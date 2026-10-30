# Security posture

Status: reflects Plans 09–10 (OIDC identity foundation + workload identity)
plus the artifact-store and result-manifest laws below.
The human plane federates to any OIDC IdP; roles are operator-defined claim
rules; every gate decision audits.

## Trust vs data-plane split (spec §3.3, D-10)

The Facilitator is a coordination kernel, not a privilege holder. It authorizes *admission* (accept a command, transition a Work) and never performs privileged external writes itself:

| Plane | Held by | Facilitator role |
|---|---|---|
| Coordination state (Works, events, inbox/outbox, approvals) | Facilitator PostgreSQL | Owner |
| SCM / issue-tracker / deploy credentials | Team (Gateway or direct-mode workspace) | None — the Facilitator holds **no** SCM/issue/deploy credential (D-10) |
| External communication | Narrow chat-bot identity only (thread replies/reactions) | Admits and routes; the adapter writes |
| Execution authority | Workspace/harness | Records status; does not drive execution |

Anything that writes outside the framework runs under team-owned credentials behind team policy. See `.omp/skills/facilitator-architecture/SKILL.md` (Ownership law) for the normative table.

## Three identities

| Identity | Carried by | Validated by |
|---|---|---|
| **Human/operator** (this doc) | OIDC access token (`Authorization: Bearer`) on `/api/*` except `/api/link/v1*` | JwtBearer: signature (JWKS from `identity:oidc:authority`), issuer, audience; RS256/ES256 only |
| **Workspace Link endpoint** | Per-endpoint `linkToken` from enrollment — mode (a) shared secret or mode (b) OIDC workload identity | `LinkBearerHandler` — a fully separate seam (`/api/link/v1*`, `/hubs/link`) |
| **Machine-to-machine ingress** (providers) | CloudEvents envelope identity (`event_source`/`event_id`, commandId, provider delivery id) | Dedupe + envelope validation, behind the same bearer gate |

The kernel is **vendor-neutral**: it consumes OIDC discovery, bearer tokens,
and RFC 6901 JSON pointers only. Vendors (Keycloak in dev/test) appear only
in fixtures (`reference/keycloak/`) and docs.

## Human plane: OIDC + claim pointers (spec §11)

Identity is the verified pair `(iss, sub)` — the canonical audit/timeline
actor is `oidc:<iss>|<sub>`. Display names never enter the actor. Which
token claims mean "subject", "groups", "scopes", … is operator config
(`identity:claims`, RFC 6901 pointers + a value shape per source); a
missing/misconfigured pointer yields **no** context, never a wildcard.
Extra issuers (e.g. a direct Google federation) are listed in
`identity:oidc:trustedIssuers` and matched by rules on `sub`/`amr` — no
claim mapping required beyond the pointers.

### Worked examples

Keycloak dev realm (`reference/keycloak/realm.json`): map `/sub`,
`/preferred_username`, `/groups`, `/realm_access/roles` (see
`appsettings.Development.json`) and gate on
`{"claim": "/realm_access/roles", "op": "contains", "value": "kernel-operator"}`.

Raw Google-account tokens (no group claims, `hd` only on Workspace tokens):
`trustedIssuers: ["https://accounts.google.com"]`, pointer `subject: "/sub"`,
and a rule keyed on the verified subject instead of realm roles —
`{"claim": "/sub", "op": "equals", "value": "<google-subject-id>"}` optionally
ANDed with `{"claim": "/amr", "op": "contains", "value": "mfa"}`.
Operator-verifiable against a live token; the realm does not broker Google.


### Roles: runtime-mutable claim rules

A role is an OR-list of AND-rules over arbitrary claim pointers:
`{"all": [{"claim": "/realm_access/roles", "op": "contains", "value": "kernel-operator"}]}`.
Ops: `equals` (string leaf), `contains` (string-array leaf or
space-delimited token), `exists` (pointer resolves). Malformed rules are
skipped (deny, logged) — never wildcard.

- Seed: config `identity:roles:<name>` bootstraps the `identity_roles` table
  only when it is empty; **the DB is truth thereafter**.
- Mutate: `GET/PUT/DELETE /api/identity/roles[/{name}]` — PUT carries the
  full replacement rule list + the `rowVersion` the writer saw (omit to
  force); a stale/absent version ⇒ `409 role-version-conflict`. The manage
  surface itself is gated by the `identity.roles.manage` operation binding.
- Effective within `identity:rolesRefreshSeconds` (default 5 s — the
  cross-replica staleness bound); the writing replica sees its edit instantly.

### Operation gates + audit

`identity:authorization:operations` binds `methods × route-template paths →
rolesAnyOf`. The gate middleware (after authorization, so the resolved
endpoint is known) evaluates the matched binding — or passes any
authenticated principal when no binding matches (read/dev surface). Every
binding evaluation writes a row to `facilitator.audit` (allow **and** deny):
normalized actor, action `authorization.evaluated:<operationId>`, decision,
reason (`role-match` / `no-role-match` / `audit-unavailable`), policy version
(row-version epoch), trace id, and a small `detail` jsonb — never a raw
token. An audit failure on an allow path fails closed (`503
auth-unavailable`). Role edits and the config seed also audit
(`identity.role.upsert/remove`).

Failure wire contract: missing/invalid bearer ⇒ `401 unauthorized`; gate
deny ⇒ `403 forbidden` (both RFC 9457, camelCase).

## Endpoint exposure

| Surface | Auth | Notes |
|---|---|---|
| `GET /health` | anonymous | liveness |
| `/api/works*`, `/api/works/{id}/*` | OIDC bearer; POSTs gated `work.write` | Work CRUD, steer, transitions, timeline |
| `POST /api/ingress/events` | OIDC bearer; gated `work.write` | CloudEvents 1.0 envelope ingress; fail-closed validation |
| `GET /api/works/{id}/approvals` | OIDC bearer; gated `approvals.read` (kernel-operator ∣ framework-admin) | Work's ledger rows, lazy expiry on read |
| `GET /api/works/{id}/manifests` | OIDC bearer; gated `manifests.read` (mirrors `artifacts.read`, includes `team-member`) | Work's recorded manifest chain, newest-first; work-scope 404 non-disclosure |
| `POST /api/works/{id}/approvals/{approvalId}/respond` | OIDC bearer; gated `approvals.respond` | Human decision; eligibility + single use below |
| `/api/identity/roles*` | OIDC bearer; PUT/DELETE gated `identity.roles.manage` | runtime role rules |
| `/api/admin/*` | OIDC bearer; gated `admin.read` (GETs) / `admin.mutate` (PATCH/POST) | Operator surface (Plan 20): roll-ups, search, detail, links, merged deliveries, metrics, runtime config (If-Match + `X-Change-Reason`; every PATCH audits `admin.config.patch` + a bounded history row), dead-letter replay, manifest re-mark |
| `/api/link/v1*`, `/hubs/link` | link plane (`linkToken`) | enrollment + evidence, below |

The kernel refuses to boot at all with an invalid/incomplete `identity:`
block (`IdentityStartupCheck`, fail closed in every environment).

## Ingress hard limits

- Request bodies at `/api/ingress/events` are capped at **262144 bytes**; oversize ⇒ `413` (`payload-too-large`). This matches `limits.maxEnvelopeBytes` in `protocol/conformance-fixtures/index.json` and is the baseline DoS floor — large payloads travel by artifact reference (appendix §8), never inline.
- Envelopes fail closed: schema/envelope violations ⇒ `400`, command types outside the 7-type known-command table ⇒ `422 unknown-command`, and the rejected envelope is still persisted to the `inbox` as an audit record (state `failed`).
- Duplicate admission (same `(event_source, event_id)`, `command_id`, or `(provider, provider_delivery_id)`) returns `200 {"status":"duplicate"}` and reprocesses nothing — replay is cheap and side-effect-free by construction.

## Provider ingress (Plans 22 + 23)

Provider events ride the **link plane**: the adapter authenticates with its
`linkToken` like any link endpoint, and the `AdmissionGate` provider arm
admits a central provider domain type
(`org.facilitator.event.provider.<domain>.v1`) only when a
`Providers:<provider>` section is enabled, the envelope's `data.provider`
names it, `reference.provider` matches, and the sending link endpoint is the
section's `EndpointId` — otherwise typed `unsupported` refusal + failed
audit row, never silent acceptance. Provider-specific rules:

- **Authentication before normalize.** The transport trust (Mattermost: the
  bot WebSocket credential — 11.11 webhooks sign nothing on the
  event-carrying form) is established before a normalized event exists; an
  unauthenticated frame never reaches classification.
- **Forgejo: HMAC before normalize, privileged writes behind policy.** The
  webhook receiver verifies the shared-secret HMAC-SHA256 over the raw body
  (`X-Forgejo-Signature` bare-hex; `X-Hub-Signature-256` also accepted)
  constant-time, 401 before parsing; `X-Forgejo-Delivery` is the dedupe key.
  The adapter comments as the bot so provider echo is recognizable, and it
  holds no forge-credential authority beyond replies: issue reads, branch
  pushes, and PR creates are connector ops — a Gateway child whose
  `vcs.change_request.create` class is confirm-held by team policy, so every
  privileged write rides the approval flow with actor evidence.
- **Duplicate = the delivery id.** `(provider, provider_delivery_id)` is
  unique; synthesized ids (e.g. the reaction `(user, post, emoji)`
  composite) must be stable across inbound echo and outbound receipt so
  replay can't double-fire an arm.
- **Echo is recorded, never processed.** The bot's own posts arrive as
  `message.v1` echoes: the kernel stores the provider post id in
  `communication_bindings` and runs no arm — the bot never answers itself
  into a new Work.
- **A reaction is never consent.** `reaction.v1` audits
  (`provider.reaction` timeline row) and gets the configured ack; it is not
  an approval, transition, or steer signal anywhere in the kernel (§13.3).
- **Link identity is redeemed on the human plane.** A `/link` DM mints a
  one-shot code; redemption is an authenticated REST call
  (`POST /api/link-identities/redeem`) so the linked identity is the
  verified OIDC `(iss, sub)` — chat display names never bind accounts.

## Workload plane: enrollment + delegation evidence (appendix §3.2)

**Enrollment** (`POST /api/link/v1/enroll`) minting a per-endpoint
`linkToken` comes in two modes, told apart by the shape of the presented
credential (`token` field):

| Mode | Credential | Identity source |
|---|---|---|
| (a) shared secret | `Link:EnrollmentToken` | body must name `team` (roster `Link:Teams`; absent/unknown ⇒ `403 team-unknown` — no default-team fallback); `role` defaults to gateway |
| (b) workload OIDC | an `aud`-`facilitator-link` access token | the `Link:WorkloadIdentities` binding matching `(azp, endpointId)` fixes team, role, and delegation acceptance; an unregistered pair ⇒ `403 workload-not-registered` |

Laws (both modes): the `gw.` endpointId prefix is reserved for `role:
gateway` (`403 role-mismatch` otherwise); a body `role` that contradicts the
mode-(b) binding also `403 role-mismatch`; **role is immutable across
rotation** — re-role needs revoke + re-enroll; rotation replaces the stored
hash, so the old link token stops validating immediately.

**Delegation evidence**: a command may carry `actorEvidenceRef`. Validation
is scheme-split — the deny-vs-unverified rule:

| Ref state | Outcome |
|---|---|
| absent | `workload-only` — the enrollment acts for itself |
| `oidc:<jwt>` | verify signature/iss/aud/exp/nbf against the realm JWKS + require `delegation: true`; **failure ⇒ `422 evidence-invalid` deny** (failed evidence never silently downgrades); pass + enrollment accepts ⇒ principal extends with `oidc:<iss>|<sub>` (`delegation-validated`); pass + enrollment refuses ⇒ `delegation-refused` (allowed, workload-only) |
| `kernel:<id>` | resolved through `facilitator.evidence` (pre-registered human assertions); unknown/expired/consumed ⇒ deny |
| any other scheme (e.g. `identity-evidence:`) | recorded `evidence-unverified:<scheme>`, workload-only — an opaque pointer the kernel has no oracle for neither denies nor authorizes |

Every command admission writes one `facilitator.audit` row
(`authorization.evaluated:link`, allow **and** deny): actor = the workload
principal, reason = `delegation-validated` / `delegation-refused` /
`evidence-unverified:<scheme>` / `evidence-malformed` /
`evidence-invalid:<cause>` (`signature`, `expired`, `missing-delegation`,
`wrong-audience`, `unknown-id`, `consumed`, …), and `detail` jsonb carrying
`delegatedSubject` + team/role — never a raw token. Audit failure on an allow
path ⇒ `503 auth-unavailable` (fail closed). The effective principal (always
workload + optional validated human) is what Plan 12's policy engine will
authorize against.

## Approval lifecycle (spec §12.1–12.2, appendix §7)

`facilitator.approvals` is the lifecycle authority:
`pending → approved|denied|expired|revoked → consumed`.

- **Digest law**: when `approval.required` carries an `action`, the kernel
  recomputes the appendix §7.2 digest (RFC 8785 over
  `{approvalId, action, eligibleActors, expiresAt, createdAt}`) and compares
  to the declared `actionDigest` — a mismatch denies `422
  approval-digest-mismatch` and the row is never recorded; the kernel never
  trusts a digest it didn't recompute. Events without `action` (1.0 senders)
  record the declared digest verbatim, flagged `digest-unverified` + audited.
- **Single use**: a respond decides via one conditional
  `UPDATE ... WHERE state='pending'` — exactly one concurrent decider wins,
  every replay denies `409 approval-already-consumed` even for byte-identical
  command JSON (§12.2). Consume (`approved→consumed`, Plan 12's hook) is the
  same conditional-update shape.
- **Lazy expiry**: no timer. Every read/respond first applies
  `UPDATE ... SET state='expired' WHERE state='pending' AND expires_at<=now`
  (audited `deny reason=expired`), then evaluates — an expired-but-not-yet-
  flipped row still denies, and it can never come back.
- **Eligibility (law D4)**: the deciding principal must intersect
  `eligibleActors`: `group:<path>` (case-sensitive; raw or `/teams/`-stripped
  — the token's `/groups` claim), then `user:<displayName>`, then
  `oidc:<iss>|<sub>` (canonical actor, verbatim). Deny ⇒ `403
  approval-actor-ineligible` + audit `deny reason=actor-ineligible`.
- **Non-repudiation (§12.1 step 4)**: a respond needs a verified human —
  the REST pair takes the OIDC bearer principal; the wire/Link surfaces
  require `actorEvidenceRef` verifying as `oidc:`/`kernel:`. A workload-only
  respond denies `403 approval-actor-unknown`.
- **Receipt**: a REST approve mints a `kernel:<id>` evidence row already
  `consumed` (receipt semantics: it proves the decision happened, it is not
  re-spent), rides the response as `evidenceRef`, and lands in the audit
  row + an `approval.decided` timeline note.
- **Revocation-before-apply (appendix §7.3)**: `approval.revoked` flips
  `pending→revoked` (terminal states no-op, 202 + audit); every consumer
  (respond, consume) checks the row state first, so a revoked approval denies
  `409 approval-revoked` even when the command JSON is identical to what
  would have succeeded.

Every decision/denial writes a `facilitator.audit` row
(`authorization.evaluated:approval`, allow **and** deny, `detail` carrying
approvalId/digest/decision/actor, `policy_version` `approval-v1`; gate
evaluations on the REST pair ride `authorization.evaluated:approvals.read`/
`:approvals.respond`) — never a raw token.

## Team policy engine + the confirm round-trip (spec §12, Plan 12)

The Gateway evaluates every inbound command against the team's policy
file (`policy.inline` / `policy.path` in `gateway.yaml`; the path resolves
against the config file's directory — keep it on team-owned, write-protected
storage, since its rows ARE the team's authorization). Schema:
`protocol/json-schema/policy-file.v1.json`; the loader
(`src/gateway/src/policy-file.ts`) is fail-closed: any structural error
aborts boot. `policyId = owner/sha256-prefix` of the file bytes rides every
audit line, so a policy edit is visible in the trail.

**Evaluation law** (`src/gateway/src/policy.ts`): a fleet-honesty pre-pass
first (event.forward always allowed; missing capability, vanished child
descriptor, steer/cancel of an unsupported capability ⇒ deny), then
first-match-wins over `policies[]`; no match ⇒ implicit final **deny**.
A row matches when `capability` equals the command's, a `resources[]` term
matches the derived resource, and — when `actorAnyOf` is non-empty — one
term matches the actor claims. `effect` ∈ `allow | deny | confirm`.
`conditions` are advisory metadata for connectors; the engine NEVER
evaluates them.

**Resource law** (single boring rule): the resource is
`data.inputs["resource"]` when that field is a string, else the
`capabilityId` itself. A `resources[]` term matches on exact equality, `*`
(matches anything), or a single trailing `*` prefix-glob
(`repository:svc-*`). Finer per-connector resources are later-plan
territory.

**Actor-claim law** (`src/gateway/src/claims.ts`): the Gateway trusts
exactly two identity sources — its own workload identity, and an
`oidc:<jwt>` `actorEvidenceRef` verifying against the issuer JWKS
(signature, `iss`, `aud facilitator-api`, `exp` with the kernel's 30 s
clock-skew default, `delegation: true`). Verified ⇒
`subject = oidc:<iss>|<sub>` + the token's `groups`. Anything else
(`kernel:` opaque receipts, bare `identity-evidence:*`, a failed verify) ⇒
workload-only claims: an untrusted string NEVER buys privilege. Row terms:
`group:<path>` (case-sensitive, raw or `/teams/`-stripped, verified claims
only), `user:<principal-or-preferred_username>`, `workload:<id>`.
`role:<r>` terms match nothing today — no realm mapper mints roles into
evidence — so a `role:` row denies until one lands (documented limitation).

**The §12 loop** across kernel + Gateway, wire-visible in both directions:

1. `command.work.assign` arrives over the Link; a `confirm` verdict parks a
   `awaiting-approval` row in the Gateway ledger, publishes
   `event.approval.required.v1` (with the `action` payload —
   `{capabilityId, resource, assignmentId, workId}`) to
   `/api/link/v1/events`, and ACKS the delivery `accepted`. The kernel
   recomputes the §7.2 digest (cross-language goldens:
   `canonical/approval-record-1|-2`) and stores
   `requested_by_endpoint_id` from the Link caller.
2. A human answers the REST pair (§ above). Every terminal flip
   (approved/denied/expired/revoked) enqueues
   `event.approval.decided.v1` on the `link.<endpointId>` outbox channel in
   the SAME transaction (outbox law; per-decision `commandId` dedupe), and
   the pump pushes it into the Gateway's session.
3. The Gateway re-evaluates the parked envelope against CURRENT state
   (appendix §7.3: a policy flip or a vanished capability denies even after
   a human approved; `createdAt` must match the parked digest record), then
   sends `command.approval.consume.v1` (`actorEvidenceRef:
   workload:<gatewayId>`) synchronously over the Link plane. The kernel
   flips `approved→consumed` — the EnvelopeAck IS the consume outcome.
   Denied/expired/revoked settle with one honest `assignment.failed`.
4. Crash recovery: `GET /api/works/{id}/approvals` (the Gateway SA holds
   `approvals.read` via the `gateway-workload` role claim rule) re-resolves
   parked rows on reconnect; a consumed row executes once, a terminal row
   settles once.

**Gateway audit line** (`src/gateway/src/audit.ts`): every policy decision
emits exactly one `authorization.evaluated` info line carrying the §12.2
set — `decision`, `reason`, `workId`/`assignmentId`/`capabilityId`,
`resource`, the three principals (`callerWorkload`, `gatewayWorkload`,
`humanSubject` only when claims verified), `policyId`, `actionDigest` +
`evidenceRef` (kind-only, never the raw token) on approval lines,
`traceId`/`correlationId` when present.

## Artifact store laws

- **Digest at the kernel boundary.** A declared `sha256:` digest means nothing
  until the kernel has hashed the bytes it actually received: the byte-PUT
  streams through a count+hash tee, and only a match flips
  `pending → stored`/`verified`. A mismatch rejects the slot (422, deny
  audit, row `rejected`) — the kernel never stores what it could not verify,
  and a rejected slot is spent (a fresh allocation is required, no replay
  against the old digest).
- **Non-disclosure 404.** Artifact reads and workload writes are team-scoped
  inside the ledger: a foreign-team row and a missing row answer the same
  404 `artifact-not-found` (the approvals work-scope precedent). The
  `artifacts.read` gate binding includes the `team-member` role (any
  `/groups` claim) precisely so a wrong-team human hits the ledger's 404
  rather than leaking team-ownership through a 403; a principal with no
  group claim never reaches the ledger at all.
- **External refs are screened, never fetched.** An `artifact.available`
  with a foreign `artifactId` registers metadata only (`stored`,
  `external:<url>`); the kernel never dereferences the URL, so an artifact
  declaration cannot drive SSRF. `verification` is the screen's verdict:
  an external URL is screened against operator bindings
  (`Storage:ExternalBindings`, prefix match) plus SSRF address rules
  (absolute http(s), no userinfo, host resolves and EVERY address is
  outside loopback/private/link-local/unique-local); a binding waives the
  address screen but never the syntax laws. Unbound or unsafe resolves to
  `unverified`, never fetched — a miss downgrades, it never refuses, and
  the reason rides the audit detail. A kernel-allocated id, once
  stored+verified, cannot be rewritten by a later declaration (422
  `artifact-declaration-mismatch`, deny audit).
- **Direct uploads confirm by kernel re-hash.** A presigned PUT lets bytes
  reach the object store with no kernel hop, so the confirm path re-reads
  and re-hashes the declared key itself — provider ETags (composite for
  multipart, md5-shaped on some gates) are never trusted as proof of the
  declared `sha256:`. Presigned URLs are minted ONLY for the digest-derived
  key the allocation already fixed (never a caller-supplied key), share the
  ticket's 15-min expiry, and appear only in the ticket response — never in
  a manifest or row (appendix 8.1: no long-lived bearer secrets).
- **Manifests record only kernel-verified canonical bytes.** A completed
  event's `resultManifestRef` records nothing until the ledger has the
  stored bytes: they must byte-equal their own RFC 8785 canonical form,
  re-hash to the wire `resultDigest` AND to the artifact row's
  boundary-verified digest, and every declared output must resolve to a
  verified same-team artifact — otherwise 422, zero rows, deny audit. The
  kernel never rewrites a manifest to make it canonical; the producer
  ships canonical bytes or the completion is honestly rejected. Recording
  audits `manifest.recorded` (+ timeline); supersession audits
  `manifest.superseded`.
- **Retention GC** (`RetentionGcService`, advisory-lock serialized):
  | Policy | Law |
  |---|---|
  | `permanent` | never collected by age |
  | `Nd` (N ≥ 1) | collect once `stored_at + Nd` lapses AND unreferenced — the row's `work_id` is NULL or that Work is terminal, AND no non-superseded `result_manifests` row pins it (as `artifact_id` or inside `output_artifact_ids`); a recorded manifest pins its document AND every declared output blob REGARDLESS of Work state; supersession releases |
  | `archive` | never deleted by the GC — kernel-side tombstone-free hold; cold-tiering is an ops concern |
  | expired pending slot | `deleted` + audit, NO backend call (bytes never arrived) |
  Collection = conditional-update tombstone + audit in one transaction, backend delete AFTER commit (idempotent): a crash between the two re-collects next pass, batch 100/pass.
- **No path traversal in the store.** Blob paths derive only from hex
  digests (`{hex[0..2]}/{hex}`); no client-supplied text (mediaType,
  comment, URL) ever participates in a filesystem path, and the store
  refuses any non-hex or escaping relative path before touching the disk.
- **Envelope cap ≠ artifact cap.** The 262144-byte ingress body cap bounds
  the JSON envelope only; artifact bytes travel by `PUT` — proxied as a
  stream (counted + hashed, never buffered whole) or direct to the object
  store via a presigned URL — so the cap can never become a silent
  artifact-size limit.

## Memory broker laws

- **ACL before search, always.** Recall builds the caller's readable
  predicate (`status <> 'tombstoned' AND (own team OR facilitator-visible OR
  live share)`, private rows never reachable via another team's label) and
  embeds it in the SQL `WHERE` — never a post-filter over a wider search.
  Denial discloses nothing: a foreign-team recall is `{entries: []}` with no
  count, no "N hidden".
- **Non-disclosure 404.** Entry reads and curation are team-scoped inside
  the broker: a foreign row, a tombstone, and a missing row all answer the
  same 404 `memory-not-found` (the artifact precedent).
- **Quotas + payload caps.** `Memory:Quota:MaxContentChars` (default
  16384, mirrored as the schema `maxLength`) bounds one entry;
  `Memory:Quota:DailyRetainPerTeam` (default 200) bounds a team's UTC-day —
  the breach is 422 `memory-quota-exceeded` + deny audit, never a queue
  backpressured onto readers. Duplicate content (same scope, same sha256)
  dedupes to the stored identity instead of consuming budget growth.
- **Memory is never authorization (spec §3.3).** Recall and the usage law
  inform decisions; no gate, transition, or ledger mutation reads memory
  as an authority source.
- **Injected content is data.** Recalled entries ride back as reference
  material with provenance; the delimiter discipline around untrusted text
  is the harness's law, the kernel records who said it (`provenance`,
  `accessControl`) and who consumed it (one `memory_usage` row per entry
  returned by any read, external consumers attesting through
  `event.memory.used`).
- **Workload writes land `candidate`.** A link-plane retain or a
  `memory.candidate` event can only propose: promotion, correction,
  sharing, and deletion are REST human curation (`memory.curate`), so an
  compromised workspace cannot silently seed a team's recall corpus.
- **Deletion drops content, keeps the trail.** Tombstone + content drop +
  share revocation + usage-row retention commit in one transaction; the
  emptied tsvector recomputes, so the row leaves every search while the
  audit chain (and `content_hash`) survives. The retention pass
  (`MemoryRetentionService`, advisory lock 420003) applies the same law to
  lapsed candidates: per-Project `projects.config` `memory` key first,
  `Memory:Retention:CandidateDays` (90) as the fallback. Backup-retention
  honesty: content dropped from the live table ages out of each operator's
  backup window on its own schedule.

## Deferred to later plans

| Concern | Plan |
|---|---|
| Refresh-token / session lifecycle for interactive UIs | 13+ |
