# Link credential lifecycle: identity, not secrets

## Context

One credential does four jobs today: the `linkToken` issued at enroll is the
authorization artifact, the endpoint identity, the authentication secret and the
rotation state at once, and it is a 720-hour replayable bearer
(`Link:TokenLifetimeHours`). The gateway repeats the disease one level down:
`children:` entries authenticate forever with a static `hello.token`
(`OFAC_CHILD_SECRET`). This plan makes the credential an **Ed25519 public key**
(identity + proof of possession), demotes every secret to a first-boot pairing
artifact, and deletes the long-lived bearer in one big-bang release — no legacy
credential class, one wire.

End state: `/enroll` takes `{token, publicKey, keyAlgorithm}` and returns no
secret; the websocket authenticates with a DPoP-style detached EdDSA JWS proof
over a kernel nonce; HTTP rides a 5-minute kernel-signed session JWT; a monotonic
`seq` fence plus a revocation `epoch` make replay and revocation detectable
without a session table; endpoints self-rotate keys; gateway children pin a key
on first `hello`. The gateway is a mini-kernel for its fleet; the kernel is a
mini-kernel for its endpoints.

Decisions already taken (not open): kernel crypto provider =
**BouncyCastle.Cryptography 2.7.0** (the net10.0 ref pack has no Ed25519 —
verified: only `MLDsa*WithEd25519` OID names; the package is already in the
local NuGet cache and is pure managed, no native assets); channel binding =
**`cbh` always present, empty when the transport exposes no exporter, new
`Link:RequireChannelBinding` (default `false`) makes empty fatal**; operator
surface = **in scope for this release** (fingerprint list + revoke).

Everything else below that reads as a micro-choice (hand-rolled vs. library JWS,
hub-method vs. round-trip for the handshake proof, `admin_config` vs. a one-row
table for the session key) is **delegated to the implementer**: pick the simpler
one, keep the wire, the claim names, the audit verbs and the fail-closed posture
exactly as written.

## Layer model (the law the code must express)

| Layer | Mechanism | Lifetime |
|---|---|---|
| authorization to join | one-time enrollment code / config bootstrap token | minutes, or consumed at first hello |
| identity | OIDC workload identity **or** enrolled public key | until revoked |
| authentication | proof of possession / issuer trust | per channel or per proof |
| session | the authenticated socket, or a 5-minute kernel-signed JWT | minutes |

`AdmissionGate` stays as it is: it reads the enrollment row, not the credential
class. Credential classes on the enrollment row: `oidc | key`, closed set.

## Approach — ordered, each step leaves the tree green

### 1. Wire contract first (blocks everything)

`protocol/json-schema/enroll-request.v1.json`: keep `token`, `endpointId`,
`instanceId`, `trustMode`, `role`, `team` as-is; add **required**
`publicKey` (base64url, `minLength: 43, maxLength: 44` — one Ed25519 raw key) and
`keyAlgorithm` (`enum: ["ed25519"]`). `additionalProperties: false` stays, and the
kernel DTO's existing `contributor` field stays a known live divergence (do not
"fix" it here — it would change the fixture set).

`enroll-response.v1.json`: drop `linkToken`/`expiresAt`, add **required**
`endpointKeyId` (the fingerprint, `sha256:<hex64>`) and `seq` (integer >= 0).
`required` becomes `[enrollmentId, trustMode, endpointKeyId, seq]`.

Then `pnpm protocol:codegen` (regenerates `src/protocol/src/generated/types.ts`,
`protocol/dotnet/Ofac.Protocol/Generated/WireTypes.cs`, mirrors
`src/protocol/schemas/`). Hand-edit the fixtures — nothing regenerates them:
`valid/enroll.request.json`, `valid/enroll.request.role.json`,
`valid/enroll.response.json` gain the new fields;
`invalid/enroll.no-trustmode.json` and `invalid/enroll.bad-role.json` stay valid
failure cases (add the new required fields to their bodies so they still fail for
their own reason); add `invalid/enroll.no-publickey.json`
(`errorClass: "schema-violation"`) and register it in
`protocol/conformance-fixtures/index.json` (`class: "enroll"`,
`schemaFile: "enroll-request.v1.json"`).

Bump `protocol/openapi/link-v1.yaml` `info.version` to `2.0.0` with a rationale
paragraph in `info.description` (the precedent for 1.0.1/1.0.2 is right there),
and rewrite the `Authentication:` description block: bearer on the link plane is
now the 5-minute session JWT. The two literal version citations in
`docs/protocol.md` (currently `:113`, `:206`) ride along.

Child wire: `protocol/json-schema/adapter.v1.json` `$defs/hello` gains optional
`publicKey` (same 43/44 base64url constraint) and makes `token` still required
(it is empty-on-third-and-later-boots rather than absent — a required field with
`minLength: 0` already tolerates that). Frame `v` stays `1`.

Verification for this step: `pnpm protocol:codegen && make protocol` (the
`git diff --exit-code` gate needs the new files `git add`ed) and
`dotnet run --project protocol/tools/Validate.Fixtures -c Release --no-build`.

### 2. Kernel: enrollment row, keys, fence, epoch

`src/facilitator/Kernel/Shared/Persistence/Entities.cs` —
`LinkEnrollmentEntity` (line 629): delete `TokenSha256`; add `required string
CredentialClass` (`oidc|key`), `long Epoch` (revocation epoch, starts 0), `long
Seq` (last-seen fence, starts 0). New entity `LinkEndpointKeyEntity` mirroring
the table `facilitator.link_endpoint_keys`: `Guid EnrollmentId` (FK →
`link_enrollments.enrollment_id`, cascade), `required string KeyId`
(`sha256:` + hex of the raw 32-byte public key — the hash-only rule
`EnrollmentTokenStore` already applies to codes, extended to keys), `required
string PublicKey` (base64url raw key), `required string KeyAlgorithm`,
`required string State` (`active|retired`), `DateTime RegisteredAt`, `DateTime?
RetiredAt`; PK `(enrollment_id, key_id)`; unique index on
`(enrollment_id) where state = 'active'` — exactly one active key is a DB
constraint, not a code convention. New table
`facilitator.link_challenges`: `required string Nonce` (PK), `required string
EndpointId`, `DateTime IssuedAt`, `DateTime ExpiresAt`, `bool Consumed` — the
single-use challenge store, also the `jti` replay store for proofs
(`jti` rides in the same table under a `link-proof:` nonce prefix; no second
mechanism).

New configuration files per the existing pattern
(`Configurations/LinkEnrollmentEntityConfiguration.cs`):
`LinkEndpointKeyEntityConfiguration.cs`. Generate the migration:
`dotnet ef migrations add AddLinkCredentialLifecycle -p src/facilitator -s
src/facilitator` — never hand-edit `FacilitatorDbContextModelSnapshot.cs`.

`LinkEnrollmentStore.cs`:
- `EnrollResult` drops `LinkToken`, gains `string EndpointKeyId, long Seq`.
- `EnrollAsync(string endpointId, string instanceId, string trustMode, string?
  requestSecret, string teamSlug, string role, bool acceptsDelegatedClaims,
  (string Issuer, string Subject)? workload, string? publicKeyBase64Url,
  CancellationToken ct)`: for `key` class the guard is the code/secret exactly as
  today (mode (a) path unchanged: minted token or `LinkEnrollmentSecret`
  literal), then upsert the enrollment + swap the key list (new key `active`,
  previous key `retired` **only on a first-boot re-enroll of the same
  endpointId**, which keeps the existing rotate-on-re-enroll law), bump `Seq` to
  0 for a fresh enrollment and leave `Epoch` alone.
  For `oidc` (mode (b)) no key row is written — identity stays the issuer.
- `RevokeAsync(string endpointId, ...)`: sets `RevokedAt` **and** increments
  `Epoch`, **and** retires every key. That single statement is the
  "revoke kills the endpoint, every key, every proof, every session" law.
- New `RotateKeyAsync(string endpointId, string oldKeyId, string newKeyId,
  string newPublicKeyBase64Url, long expectedSeq, TimeSpan grace, ...)` →
  atomic swap + `key-rotated` audit; returns a result enum
  (`Rotated | UnknownKey | GraceConsumed | ReplaySuspect | Revoked`).
- New `AdvanceSeqAsync(string endpointId, long claimedSeq, ...)` → `bool`
  (`false` = `claimedSeq < Seq`, caller audits `replay-suspect`). Shared by proof
  verification and session-JWT mint.
- `internal static string Sha256(...)` and the base64url helper stay where they
  are and are reused for fingerprints.

### 3. Kernel: crypto + proof + session JWT

New `src/facilitator/Kernel/Link/LinkKeys.cs` — the only crypto surface:
`static bool TryParsePublicKey(string base64Url, out byte[] raw)` (43/44-char
base64url decode, exactly 32 bytes), `static string KeyId(byte[] raw)`
(`"sha256:" + lowercase hex`), `static bool Verify(string publicKeyBase64Url,
byte[] signingInput, byte[] signature)` and
`static string SignSessionJwt(byte[] payloadJson)`-adjacent helpers.
Ed25519 via BouncyCastle (`Org.BouncyCastle.Crypto.Parameters.Ed25519PublicKeyParameters`
/ `Ed25519Signer`) — add `<PackageReference Include="BouncyCastle.Cryptography" />`
to `src/facilitator/Facilitator.csproj` and
`<PackageVersion Include="BouncyCastle.Cryptography" Version="2.7.0" />` to
`Directory.Packages.props` (central versioning is law here).

New `LinkProof.cs`: parse + verify a **detached EdDSA JWS** (RFC 8037/7515
subset): `{"alg":"EdDSA","typ":"ofac-proof+jwt"}` protected header, payload
`{iss, sub, jti, htm, htu, iat, nbf, exp, nonce, seq, cbh}` (`iss` = endpointId,
`sub` = key id, `cbh` = channel-binding hash, **empty string when the transport
exposes no exporter**), signature = base64url 64-byte Ed25519 sig over
`b64(header) + "." + b64(payload)`. Checks, all fail-closed with an audited
denial: signature by the enrollment's **active** key, `htm`/`htu` match the
request/channel, `iat`/`exp` inside `Link:Evidence:ClockSkewSeconds`-style skew,
`nonce` present-and-unconsumed in `link_challenges`, `jti` unseen, `seq` >=
stored `Seq` (else `replay-suspect`), `epoch` claim equals the row's `Epoch`
(revocation without a session table), and `cbh` non-empty when
`Link:RequireChannelBinding` is true.

Session JWT: **Ed25519, signed by a kernel private key in `admin_config`**, so
any replica verifies from the shared row with no external CA and no DB read. New
`AdminConfigStore.Resources` entry `"link-session-key"` (registered, so PATCH
validates it) with payload `{"kid": "...", "privateKey": "<base64url seed>",
"publicKey": "<base64url raw>", "createdAt": "<rfc3339>"}`; minted on first
demand by `LinkSessionKeys` (new, next to `LinkKeys.cs`) under an insert-if-absent
write, and cached in memory per replica. Claims:
`{sub: endpointId, team, role, trustMode, auth_source: "key"|"oidc", epoch, seq,
exp}`, `exp` = now + `Link:SessionTokenLifetimeSeconds` (default **300**),
`kid` in the header. Reuse the existing
`Microsoft.IdentityModel.JsonWebTokens` stack already referenced by
`Shared/Identity/IdentityJwt.cs` for the serialization, with the
BouncyCastle-provided key material — if wiring a custom `SigningCredentials` for
`EdDSA` fights the library, sign the compact JWS by hand in `LinkKeys.cs` and
keep the same claim set; that is an implementation choice, the claims and the
`EdDSA` algorithm are not.

`LinkOptions`: delete `TokenLifetimeHours`; add `SessionTokenLifetimeSeconds`
(5), `ChallengeLifetimeSeconds` (30), `RotateGraceMinutes` (60),
`RequireChannelBinding` (false). Defaults literal, `FromConfiguration` is the
single read point.

### 4. Kernel: the two new endpoints, and the enroll change

`LinkEndpoints.Enroll.cs`: `EnrollRequestDto` gains
`string? PublicKey, string? KeyAlgorithm`; mode (a) requires them (400
`schema-violation` when absent or malformed — reuse the existing
`ProblemErrors.SchemaViolation`), mode (b) ignores them. Response
`EnrollResponseDto(result.EnrollmentId.ToString("N"), result.TrustMode,
result.EndpointKeyId, result.Seq)`. `.RequireRateLimiting("enroll")` stays.

New file `src/facilitator/Kernel/Link/LinkProofEndpoints.cs`, mounted from
`LinkEndpoints.MapLinkApi` next to `MapEnroll` (same `/api/link/v1` group):
- `POST /api/link/v1/challenge` `{endpointId}` → 200
  `{nonce, publicKeyRequired: true, algorithm: "ed25519", expiresAt}`. **No
  auth** (the challenge proves nothing, so it can't be guarded by what it's
  issuing), and it inherits the existing `"enroll"` fixed-window policy —
  add `.RequireRateLimiting("enroll")` rather than a bespoke limiter (the
  parked-item decision: same budget line, nothing bespoke). Unknown endpointId ⇒
  401 `unauthorized`, identical to a wrong proof: the no-existence-oracle law
  that `EnrollmentTests` already asserts for `capabilities` extends here.
- `POST /api/link/v1/proof` `{"proof": "<detached jws>"}` → 200
  `{sessionToken, expiresAt, seq}` (the 5-minute JWT) or 401. `htm` = `POST`,
  `htu` = this route. Audits `challenge-issued`, `proof-verified`, `proof-failed`
  and `session-issued`.
- `POST /api/link/v1/enroll/{endpointId}/rotate` `{"oldKeyId","newKid... }` —
  body `{oldKeyId, newPublicKey, proofs: [<jws by old key>, <jws by new key>]}`;
  **both proofs sign the same payload** `{oldKid, newKid, nonce, seq}` and both
  are required (400 `schema-violation` otherwise). Auth: a valid proof by the
  current active key (i.e. it arrives under the ordinary link auth path).
  A proof by a key `retired` less than `Link:RotateGraceMinutes` ago is accepted
  against a **fresh** challenge and audits `rotate-grace-consumed`; the grace
  never authorizes a *first* key, only a next rotation, and only the specific
  retired key it names. Outside the grace: 401, revoke-and-rejoin. Audit
  `key-rotated` with old/new fingerprints, `Epoch` untouched.

ProblemErrors additions (`src/facilitator/Kernel/Shared/ProblemErrors.cs`,
following the existing `Create(type,title,status,detail)` shape and
`https://errors.openfacilitator.org/v1/<code>` types): `proof-invalid` (401),
`replay-suspect` (401), `key-rotation-grace-expired` (401). Existing
`enrollment-secret-invalid`, `unauthorized`, `role-mismatch`, `team-unknown`
keep their exact strings.

### 5. Kernel: auth seams become proof/session validators

`ILinkTokenValidator` (`src/facilitator/Kernel/Shared/Auth/ILinkTokenValidator.cs`)
keeps its name and its `Task<LinkPrincipal?> ValidateAsync(string token, ...)`
signature — it is the documented clean-cutover seam and the call sites
(`LinkAuthMiddleware`, `LinkBearerHandler`) are exactly right. Its one
implementation changes: `StoreLinkTokenValidator` becomes a **session-JWT
verifier** (parse, verify `EdDSA` against the `admin_config` public key, check
`exp`, then compare the `epoch` claim against a cached enrollment row — the
cache-and-NOTIFY pattern `LinkConnectionRegistry`/`PgWakeListener` already
establish for "hint, never correctness"). Add `LinkPrincipal` fields
`long Epoch, long Seq, string AuthSource`; every construction site is the
validator, so `AdmissionGate` and the hub need no logic change.

`LinkBearerHandler`: update the XML doc only — one job now, "validate the
short-lived session JWT on the negotiate and upgrade request"; the
`?access_token=` affordance and the claim set stay, plus `facilitator.epoch` /
`facilitator.seq` claims so `LinkHub` can fence without a second query.
Registration (`KernelServiceCollection.cs:250`) unchanged.

`LinkHub` (`src/facilitator/Kernel/Link/LinkHub.cs`): the `[Authorize(...)]`
scheme stays. The **handshake proof** rides the existing `Hello(JsonElement
hello)` call rather than a new hub method — the hello frame grows `proof` and the
hub verifies it (same checks as step 4, `htm = "WS"`, `htu = "wss://<host>
/hubs/link"` as the client sees it, `cbh` from
`Context.Connection.Features`/`ITlsHandshakeFeature`) **before**
`sessions.OpenSessionAsync(...)`, and closes with the existing
`GoodbyeFrame`/`no-compatible-protocol-version` precedent using a new
`proof-failed` reason when it fails. The kernel's nonce is delivered by a new
`ProofRequest`/`Proof` hub round-trip only if the hello-carried proof turns out
unworkable in SignalR's negotiate ordering — the client already has a
`Handshaker` that owns the hello exchange either way. `WelcomeFrame` gains
`sessionKeyId` + `seq` (additive JSON fields on the existing record,
`control.v1.json` + `handshaker.ts` + `link-client-foreign/src/main.ts` together).
Revocation mid-socket needs no new machinery: the existing lease refresh
(`sessions.TouchSessionAsync` on `Heartbeat`) is where an epoch mismatch closes
the socket, so add the epoch comparison there.

Cross-replica epoch freshness: `RevokeAsync` adds a
`PostgresNotifications` sibling (`NotifyRevocationAsync`, new channel
`facilitator_link_revocation`) and the validator's per-endpoint cache
invalidates on it. NOTIFY is a hint (the file's own law), so also give the cache
a short TTL — an expired cache entry re-reads the row. Both, not either.

### 6. Endpoint side: one keystore helper, then fan out

`src/workspace-link` gains `src/core/keystore.ts` (the only Ed25519-on-disk code
in the TS fleet, so the gateway, adapters and harnesses import it instead of
copying it — `node:crypto` has Ed25519 natively, verified):
`loadOrCreateIdentity(stateDir): {keyId, publicKeyBase64Url, sign(buf)}` —
`<stateDir>/identity.key`, a 0600 file holding a JSON header
`{"v":1,"keyId":"sha256:...","algorithm":"ed25519","publicKey":"<b64url>","seed":"<b64url>"}`.
**Plaintext, no sealing tiers** (the keystore law). A 32-byte seed round-trips
through `crypto.createPrivateKey` with the PKCS#8 prefix
`302e020100300506032b657004220420` and a raw public key through the SPKI prefix
`302a300506032b6570032100` (both verified against Node) — use those constants,
do not shell out to an ASN.1 library. Missing file ⇒ generate; corrupt file ⇒
throw, never silently re-enroll. Also `src/core/proof.ts`:
`buildProof({keyId, sign, endpointId, htm, htu, nonce, seq, cbh})` → the detached
JWS string, and `signProofForGateway(...)` reused by the child side.
The keystore law to state in the file's doc comment: stateDir **is** the logical
instance — no-backup, migration is `mv`, loss is rejoin.

`src/core/enroll.ts`: `EnrollOptions` gains `publicKey: string;
keyAlgorithm: "ed25519"`; `enroll()` returns the new `EnrollResponseV1`
(`endpointKeyId`, `seq`). `src/core/token-rotation.ts` — the class is now about
the *session*: on `TransportError.unauthorized` call `token.refresh()`, which
re-runs challenge+proof instead of re-enrolling; the enrollment itself is no
longer a re-auth path (the bearer re-enroll path deletes here).
`src/core/config.ts` and `src/bin/linkd.ts`: the state dir becomes a required
concept for linkd (today it only has `--state <sqlitePath>`; make it
`--state-dir`, default `./ofac-link-state`, keep the sqlite path inside it).
`OFAC_LINK_TOKEN` stops being a credential — it is deleted from
`linkd`/`config.ts`; `OFAC_ENROLL_SECRET` stays as the first-boot-only code.
`src/node/signalr.ts` and `src/node/http.ts`: `LinkTokenProvider` keeps its
`getToken()/refresh()` shape (that is what both transports already consume, so
they need one-line changes at most), but it now hands out a session JWT obtained
by challenge→proof; `http.ts` sends `Authorization: Bearer <sessionToken>` and
refreshes on 401 exactly once via the existing `onUnauthorized` hook.
`src/node/capabilities.ts` and `src/core/handshaker.ts` follow the same two
moves. `test/e2e/harness.ts` + `test/package/fixtures.test.ts` +
`test/transport/envelope.test.ts` take the new enroll shape.

Then fan out, one owner per package, all mechanical once `keystore.ts` exists:
- **`@ofac/gateway`** (`src/gateway/src/`): `config.ts` deletes
  `tokenFilePath`/`hasStoredToken`/`linkToken` and the `OFAC_LINK_TOKEN` overlay
  (`:508`), `stateDir` stays and becomes where `identity.key` lives (the
  gateway's `children:` block is its admin surface — no new admin UI);
  `runtime.ts:acquireToken` (`~:631`) becomes `acquireSession` (challenge+proof
  against the kernel, no `writeTokenFile`/`readStoredToken`, no
  `<stateDir>/token`); `childwire.ts:HelloFrame` gains `publicKey: string` and
  keeps `token` (empty after first pin); `listen.ts:onHello`/`tokenMatches`
  split into first-hello (`token` must match the config `children[].token` or
  `OFAC_CHILD_SECRET`, then **pin the key** → `child-key-pinned` log/audit, and
  the token retires forever) and every-later-hello (verify a proof by the pinned
  key; a mismatch is the existing `denied: bad-token`
  `RegisterDenyReason`, unchanged); `ledger.ts` gains a
  `child_keys(child_id, key_id, public_key, algorithm, pinned_at, rotated_at)`
  table alongside `bindings`/`child_state`; `connect.ts` (gateway→child
  dial-out) sends the gateway's own hello with key + proof. Remote children get a
  **TLS listener mode** on the gateway (`childTransport.tls: {cert, key}`),
  which is also where a non-empty `cbh` comes from for them.
- **`adapter-sdk` + the three harness worker bins + the five communication
  adapters**: `childToken()`-style cells (`adapter-sdk/src/adapter.ts:332`,
  `artifacts.ts:78`, `media-law.ts:142`) become a session-token provider fed by
  the same keystore + proof helper. Each package already has a state dir
  (`OFAC_STATE_PATH`, `TELEGRAM_STATE_PATH`, `statePath`); point the keystore at
  it. `OFAC_CHILD_SECRET` keeps working as a first-boot-only bootstrap token.
- **`protocol/tools/Conformance.Runner`**: `KernelClient.EnrollAsync`
  (`:73`) generates a keypair in memory, posts it, then does
  challenge→proof per call instead of caching `LinkSession.LinkToken`;
  `RunnerConfig.EnrollmentToken` unchanged. **`link-client-foreign`** must
  demonstrate the wire independently: it already hand-rolls hello; it now
  hand-rolls the detached JWS too — that is the cross-language proof the
  conformance suite exists for.

### 7. Admin surface + bootstrap + docs

`AdminReadStores.LinksAsync` / `AdminLinkRow`: add `CredentialClass`,
`EndpointKeyId`, `Epoch`, `Seq`, `ReplaySuspect` (last-fence-rejection flag) so
the console shows identity and the replay story. New
`DELETE /api/admin/links/{endpointId}` in
`src/facilitator/Kernel/Admin/AdminEndpoints.cs` (next to `MapGet("/links")`
`:64`, same config-driven authz law — **no** `.RequireAuthorization`, the
`identity:authorization:operations` binding decides) calling
`LinkEnrollmentStore.RevokeAsync` + auditing `endpoint-revoked`.
`EnrollmentTokenEndpoints` gains no new route; its revoke-kills-the-enrollment
lever now retires keys too, via the same `RevokeAsync`. UI:
`LinksView.svelte` renders the new columns + a revoke button;
`EnrollmentTokensCard.svelte` is unchanged (minting law is untouched),
`lib/types.ts` mirrors the new `AdminLinkRow`.

Bootstrap law: `Link:EnrollmentToken` (and `LinkEnrollmentSecret` +
`LinkEnrollmentStartupCheck`, both unchanged in shape) is demoted in the docs
from "what enrolls endpoints" to "what authenticates the first admin" — the
static secret still enrolls, but the standing path is a minted code; a kernel
with neither a static token nor a pre-seeded code boots degraded (accepts
nothing, says so) rather than implied-open, which the existing fail-closed
startup check already does — keep the wording aligned.

Audit verbs, exactly (hash/fingerprint-only detail, never key bytes):
`enrollment-issued`, `enrollment-consumed`, `enrollment-reuse-attempt`,
`challenge-issued`, `proof-verified`, `proof-failed`, `session-issued`,
`endpoint-revoked`, `key-rotated`, `rotate-grace-consumed`, `replay-suspect`,
`child-key-pinned`. The link plane writes audits through
`AuditWriter.WriteAsync(new AuditRecord(actor, action, resource, decision,
reason, evidenceRef, policyVersion, detail, correlationId))`; follow
`admin.enroll-tokens.mint`'s dotted-namespace precedent, so the literal set
ships as `link.*`-prefixed where written by the link plane
(`link.challenge-issued`, `link.proof-verified`, `link.proof-failed`,
`link.session-issued`, `link.enrollment-issued`, `link.endpoint-revoked`,
`link.key-rotated`, `link.rotate-grace-consumed`, `link.replay-suspect`) and
`gateway.child-key-pinned` for the gateway-side one — the design's verb names are
the leaves, the repo's namespace law decides the prefix.

Docs (the gate is `make docs-check`, and AGENTS.md signposts are capped at 80
lines — anchor new facts as a clause on an existing bullet, never a new block):
`docs/security.md` (the workload-plane section at `:155-175` and the two tables
at `:26` and `:105`), `docs/protocol.md` (auth section `:214-240`, the endpoint
table at `:303`, the error table at `:322`, the config table at `:345` —
`Link:TokenLifetimeHours` row deletes, the four new `Link:*` keys appear),
`docs/getting-started-agents.md` (`:17`, `:95-104` — the curl example now posts a
public key and gets no token back), `docs/advanced.md:91` (the `linkToken` row
deletes), `src/facilitator/AGENTS.md:23-25`, `src/gateway/AGENTS.md`,
`src/workspace-link/README.md`, `src/gateway/README.md`, and the
`features/src/*/README.md` + `install.sh` credential lines. Env/config surface
that must not keep claiming a bearer credential: `reference/config/facilitator.env`
(`Link__EnrollmentToken` stays), every
`reference/config/**/*.yaml` + `examples/local/templates/**` +
`examples/full/coding-worker/src/config.ts` +
`examples/aws/lib/stacks/team.ts` that sets `OFAC_LINK_TOKEN` — that variable
deletes (and with it the AWS CDK snapshot + `cdk.out` regeneration and
`examples/local/tools/localctl` snapshot tests). `reference/.env`,
`reference/checks/verify.sh`, `tools/scenario-plane/test/chain.golden.test.ts`
and `docs/scenarios/scenario-bindings.json` get touched by the test renames
below, not by design.

### 8. Tests

`tests/Facilitator.Tests/Infrastructure/LinkTestHarness.cs` is the choke point:
`EnrollAsync` gains a generated keypair and returns a session-token provider
instead of a string; `CreateLinkClient` and `FakeLinkClient.ConnectAsync` take
the provider. **Keep the "no secrets in query strings" law** the file header
asserts — the proof goes in a header, never a query param.
`Link/EnrollmentTests.cs`: rewrite the 720-hour/4-field assertions
(`linkToken.Length >= 43`, `expiresAt > +719h`, exactly-4-fields) into the new
shape (201 has exactly `enrollmentId, trustMode, endpointKeyId, seq`; the key id
matches `sha256:<64 hex>`); `ReEnrollRotatesTokenAndInvalidatesTheOldOne`
becomes "re-enroll with a new key retires the old key and the old key's proof
stops validating **immediately**". New tests, in this file (it owns the
credential laws) and `Link/EnrollmentTokenTests.cs` (it owns the mint/consume/
revoke lever — `RevokeKillsTokenAndIssuedLinkToken` becomes
`RevokeKillsTokenAndIssuedKey`): proof required, proof by wrong key 401, proof
replay (`jti` reuse) 401, stale `seq` ⇒ 401 + `link.replay-suspect` audit,
revoked endpoint's mid-session request 401 within the 5-minute JWT window
(the epoch law, and the strongest new-behavior proof in the suite),
rotate-then-old-key-fails, rotate-under-grace audits `rotate-grace-consumed`,
rotate-outside-grace 401, session JWT expires (fake `TimeProvider` past 300 s ⇒
challenge+proof again, not re-enroll). `Link/HandshakeTests.cs`,
`HttpFallbackParityTests.cs`, `LinkIngressTests.cs`, `DeliveryFlowTests.cs`,
`HeartbeatTests.cs`, `ReceiptLossTests.cs`, `FlowControlTests.cs`,
`EvidenceAdmissionTests.cs`: mechanical, via the harness. Every
`tests/Facilitator.ScenarioWalks/*WalkTests.cs` call to `WalkKit.EnrollAsync`
(`WalkKit.cs:22`-adjacent) inherits the harness change — signature change in
`WalkKit`, no per-file edits beyond it. Gateway side: `src/gateway/test/listen`
+ `fleet` suites get first-hello-pins / later-hello-proves cases;
`test/e2e/gateway-harness.ts:gatewayToken` and `test/e2e/child-runner.mjs`
become key-based. `src/workspace-link/test/e2e/harness.ts` same.
`tests/Facilitator.E2E/AdminInspectorTests.cs` picks up the new admin columns and
the revoke route.

## Critical files & anchors

1. `src/facilitator/Kernel/Shared/Persistence/LinkEnrollmentStore.cs` —
   `EnrollResult` (`:21`), `EnrollAsync` (`:82`), `RevokeAsync` (`:197`),
   `StoreLinkTokenValidator` (`:223`). Every credential law lands here first; the
   validator's rename target keeps the `ILinkTokenValidator` seam intact.
2. `src/facilitator/Kernel/Link/LinkEndpoints.Enroll.cs` — `MapEnroll` (`:19`),
   the mode-(a)/(b) split (`:95-140`), the response construction (`:209`). The
   response-shape change and the new key guard are both here.
3. `src/facilitator/Kernel/Link/LinkHub.cs` — `[Authorize(SchemeName)]` (`:26`),
   `Hello` (`:103` `OpenSessionAsync`), `Heartbeat` (`:172` `TouchSessionAsync`).
   Handshake-proof insertion point and the epoch-on-lease-refresh point.
4. `src/gateway/src/listen.ts` — `onHello`, `tokenMatches`, the
   `RegisterDenyReason` literals. The pin-on-first-hello fork lives entirely in
   this function; keep the deny vocabulary unchanged.
5. `src/workspace-link/src/core/enroll.ts` + the new `core/keystore.ts` — the
   single TS implementation of "identity lives in the stateDir"; every other
   endpoint package imports it rather than reimplementing it.

## Verification

Run in this order; each step proves something beyond "it compiles".

1. `pnpm protocol:codegen && git add -A && make protocol` — expects a clean diff
   gate over `src/protocol/src/generated`,
   `protocol/dotnet/Ofac.Protocol/Generated`, `src/protocol/schemas`, and both
   `swagger-cli validate` calls passing on `info.version: 2.0.0`.
2. `dotnet run --project protocol/tools/Validate.Fixtures -c Release` — expects
   the new `invalid/enroll.no-publickey.json` case reported as
   `schema-violation` and every enroll fixture validating against the new
   required set.
3. `pnpm install --lockfile-only` (BouncyCastle is a `.nupkg`, so the lockfile is
   untouched, but any workspace-dep drift from the fan-out shows up here as
   `ERR_PNPM_OUTDATED_LOCKFILE` in every package's test script), then `make
   test`.
4. **New-behavior proof, kernel** — `dotnet test
   tests/Facilitator.Tests/Facilitator.Tests.csproj -c Release --no-build
   --filter "FullyQualifiedName~Link"`: the concrete inputs that must produce
   observable output are (i) enroll with a fresh public key ⇒ 201 whose body has
   exactly `enrollmentId/trustMode/endpointKeyId/seq` and whose `endpointKeyId`
   equals `sha256:` + lowercase hex of SHA-256 of the raw key you sent;
   (ii) replaying the same proof twice ⇒ second is 401 `proof-invalid`;
   (iii) a proof whose `seq` is one lower than the stored one ⇒ 401 plus a
   `link.replay-suspect` audit row; (iv) a live session JWT, then
   `DELETE /api/admin/links/{endpointId}` ⇒ the **same, still-unexpired** JWT
   gets 401 on the next request — this is the check that revocation is immediate
   without a session table, so it must not be simulated by shortening `exp`.
5. **New-behavior proof, rotation** — rotate with proofs from old+new key ⇒ 200
   and a `link.key-rotated` audit naming both fingerprints with `epoch`
   unchanged; then a proof by the old key ⇒ 401; then, after a second rotation
   whose first proof is signed by the just-retired key ⇒ 200 and a
   `link.rotate-grace-consumed` audit.
6. `pnpm -C src/workspace-link test && pnpm -C src/gateway test` — the
   first-hello-pins / second-hello-proves cases must fail if the pin is skipped:
   a child reconnecting with the bootstrap token and **no** proof must be denied
   with `denied: bad-token` after its key is pinned.
7. `./referencectl up slim && ./referencectl verify` — the unattended end-to-end
   proof that every in-repo endpoint enrolls with a key and holds a session:
   expect the slim stack to reach the same verified state as before, with
   `link.enrollment-issued` / `link.session-issued` rows in the audit table
   instead of a shared bearer, and no `OFAC_LINK_TOKEN` anywhere
   (`grep -rn OFAC_LINK_TOKEN reference examples` ⇒ no hits).
8. `make docs-check` and `pnpm lint`.

## Assumptions & contingencies

- **Ed25519 in the kernel goes through BouncyCastle** (the net10.0 ref pack has
  no Ed25519 — only ML-DSA hybrids). If the reviewer rejects the new dependency,
  the fallback is a vendored verify-only RFC 8037 implementation plus hand-signed
  session JWTs; the wire (`alg: EdDSA`, raw 32-byte keys, `sha256:` fingerprints)
  does not change, so this is a kernel-internal swap.
- **Session JWTs are signed by a kernel Ed25519 key minted into the
  `admin_config` `link-session-key` resource.** If `Microsoft.IdentityModel`
  cannot be persuaded to sign `EdDSA`, keep the algorithm and hand-build the
  compact JWS; if `admin_config` turns out to be a poor home (it is
  operator-editable and capped at 16 KiB), fall back to a dedicated one-row table
  and keep every claim name identical.
- **Channel binding is carried but not required** (`Link:RequireChannelBinding =
  false`) because the reference environment is plain HTTP
  (`OFAC_KERNEL_URL=http://facilitator:8080`, no TLS terminator) and .NET's
  `SslStream` exposes no exporter. If an operator turns the flag on, every
  endpoint must reach the kernel over TLS — the gateway's new child TLS listener
  is the same mechanism, so flip the flag and enable TLS together.
- **Handshake proof rides the existing `Hello` hub method.** If SignalR's
  negotiate ordering makes a hello-carried proof unworkable, add the
  `ProofRequest`/`Proof` round-trip described in step 6; the client's
  `Handshaker` owns the exchange either way, so this stays inside
  `LinkHub.cs` + `src/core/handshaker.ts` + the fixture copies.
- **Renaming `EnrollmentTokenTests.RevokeKillsTokenAndIssuedLinkToken`** (and any
  other renamed test) must be mirrored in
  `docs/scenarios/scenario-bindings.json`, which references test ids literally —
  `make scenario-gate` cross-checks it against the trx.
- **`link_challenges` growth** is bounded by a same-release sweep alongside the
  existing lease sweeper (`Link:LeaseSweeperEnabled`'s neighbourhood); delete
  rows past `ExpiresAt`. No new configuration key.
