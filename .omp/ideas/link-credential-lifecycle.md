# Link credential lifecycle: identity, not secrets

Status: decided design idea (not scheduled). All questions from the
refinement rounds are resolved; what remains open is parked work, not
undecided shape. Assumes the as-built enroll plane (mode (a) consumed
enrollment codes, mode (b) JWKS-verified OIDC bound to
`Link:WorkloadIdentities`, revocation killing the issued enrollment).
No external consumers exist on the old path: the swap is big-bang, one
release, no legacy credential class.

## The problem

One credential today conflates four responsibilities: authorization to
join, endpoint identity, endpoint authentication, and rotation. Mode (a)
proves it — the enrollment code is lawfully single-use, yet the link
token it issues (`Link:TokenLifetimeHours` = 720) is a permanent
replayable bearer that also serves as the re-enrollment secret. The
pairing credential became an endpoint password. The gateway has the
same disease one level down: `children:` entries authenticate with a
static shared token, a forever-password on the socket.

## The layer model

Four concerns, four separate lifecycles:

| Layer | Mechanism | Lifetime |
|---|---|---|
| Authorization to join | one-time enrollment code (kernel) / config-declared bootstrap token (gateway) | minutes, or consumed at first hello |
| Identity | OIDC workload identity **or** enrolled public key | until revoked |
| Authentication | proof of possession (key) / issuer trust (OIDC) | per channel or per proof |
| Session | the authenticated websocket, or a 5-minute kernel-signed token | minutes |

Both identity sources converge on the same authorization model —
endpoint, team, role, capabilities, audit, revocation. Authentication
source changes; authorization law does not. This is what keeps the
AdmissionGate unchanged: it reads the enrollment row, not the credential
class.

## Federated identity (unchanged, as-built — and now load-bearing)

Mode (b) already exists: JWKS-verified client-credentials JWT, bound to
a registered workload identity via `azp`. OIDC is **preferred** where an
issuer exists (AWS, Azure, GHA, Kubernetes, future providers) — those
endpoints never hold a standing secret. The v1 mapping (static
registration keyed on `azp`) stands; a subject-pattern refinement is
later work.

The split that organizes everything else: **an endpoint either has an
issuer or it doesn't.** EC2/ECS/GHA/K8s have one (AWS signs instance/
task identity documents verifiable the way mode (b) verifies OIDC
JWTs); laptops, server PCs, on-prem boxes, and edge devices do not —
they are *issuer-less*. Issuer present ⇒ re-attest per boot, zero
standing secret, a replaced instance is a fresh enroll with no human.
Issuer-less ⇒ enrolled public key, below. The dichotomy is
self-consistent: wherever local key sealing would be unreliable (fresh
task, new instance), an issuer exists; where no issuer exists, the
machine is stable enough to hold a local key.

## Enrolled identity (issuer-less endpoints)

`Enrollment code → public-key registration → proof-of-possession`.

The operator mints a code that is **authorization artifact only**:
single-use, TTL-bounded, team- and role-bound, revocable before use —
the as-built EnrollmentTokenStore law, kept verbatim. What changes is
what the exchange yields:

```
POST /enroll  { enrollmentCode, publicKey, keyAlgorithm, metadata }
```

The endpoint generates an Ed25519 keypair locally; the private key never
leaves the device. The kernel validates and consumes the code, stores
the public key, creates the enrollment. The endpoint identity is
`EndpointId + PublicKey`, never `EndpointId + Secret`. SSH
`authorized_keys` semantics — pinned key, possession proves identity —
without SSH transport.

The enrollment row stores a **key list**, each entry keyed by key id
(the fingerprint) with state `active | retired`; exactly one
`active`. Plus a monotonic `seq` fence (replay law, below). The root of
trust is possession of the private key; the kernel never stores one.

## Identity follows state (the keystore law)

**The stateDir is the logical instance.** Every endpoint already has a
persistent state directory (gateways: sockets + sqlite ledger resolve
against it; adapters: their ledger + config). The identity file is
`<stateDir>/identity.key` — a 32-byte Ed25519 seed plus a small JSON
header, mode 0600, **plaintext: no sealing tiers.** DPAPI/TPM-class
sealing derives from the machine, and exactly the machines where
sealing breaks (fresh tasks, new instances) are the ones that have an
issuer and hold no key at all. A read-only-root device that offers no
persistent path is declared stateless: it must use an issuer, or accept
re-enroll per boot.

Consequences, stated as law:

- **No-backup.** A copied stateDir is a second endpoint with the same
  identity, admitted until replay detection or revocation says
  otherwise; copying is not a backup strategy, it is re-introducing a
  replayable secret.
- **Migration is `mv`.** Move the stateDir, move the endpoint — ledger
  and identity relocate together, which is what operators already do
  when they relocate a gateway.
- **Restore discipline needs the fence, and gets it** (below):
  restoring a stale snapshot is a *detected* condition, not a silent
  split-brain.
- **Loss = rejoin.** A destroyed device is a new enrollment with a
  fresh code, one admin PATCH + one mint. Correct posture for the
  device-held root of trust; the rotation route below is what keeps
  healthy devices from needing it.

## Replay fence: identity + generation, like everything else in this repo

The kernel fences work executions at a stale
`current_execution_generation`; the enrollment gets the same pattern.
Every proof and session token carries the endpoint's monotonic `seq`;
the kernel stores last-seen per endpoint, **rejects lower**, audits
`replay-suspect`, and surfaces it on the endpoint's admin view. This
covers the two clone stories — VM snapshot/restore and stateDir
copy-promote — without trusting hardware: the stale twin announces
itself at its first proof. The DPoP `jti` law covers short-window proof
replay; the `seq` fence covers long-window state replay. Revocation
epoch and replay `seq` are distinct: epoch answers "is this identity
still alive", seq answers "is this the freshest evidence of it".

## Key rotation (first-class)

Self-service, no operator, no code, no downtime:

```
POST /enroll/{endpointId}/rotate   (authenticated like any request)
     proof by current key   over { oldKid, newKid, nonce, seq }
     proof by new key       over { oldKid, newKid, nonce, seq }
```

- **Both keys sign the same payload.** The current key proves
  authority; the new key proves possession — registering a stranger's
  public key fails closed. The challenge nonce makes the pair
  un-replayable.
- **Atomic swap.** New key becomes the single `active`; old is
  tombstoned `retired`, never authenticates again. No dual-active
  window: rotation is proactive, keys do not expire.
- **Sessions ride through.** HTTP session JWTs are kernel-signed (the
  endpoint key is not involved mid-session) and the websocket was
  authenticated at handshake. Only revocation cuts sessions.
- **Grace window closes the bricked-device hole** (decided):
  a rotation signed by a just-`retired` key is accepted within a
  bounded grace period (default 1 h) against a fresh challenge — this
  covers firmware rollbacks and rotation-crash mid-states, the
  scenarios where an endpoint is stuck with a key the model considers
  retired. The grace is a named, audited, time-boxed exception
  (`rotate-grace-consumed`), not a second trust path: it never
  authorizes a *first* key, only a next rotation, and only for the
  specific retired key it names. There is no operator key-replacement
  override: outside the grace, a stuck key is revoke-and-rejoin.
- **Compromise is not rotation's job.** Self-rotation presumes a
  healthy key. A compromised or lost key goes through revocation +
  fresh code, same as any rejoin.
- Audit: `key-rotated` with old/new fingerprints; epoch unchanged.

## Authentication

**Websocket (primary):** connect → kernel presents a nonce → endpoint
returns a **DPoP-style proof** (RFC 9449 subset: `jti` single-use,
`htm`/`htu`, `iat`, plus a channel-binding hash of the handshake's
exported keying material) → the socket *is* the session. No bearer,
no refresh, no standing secret on the primary path.

**HTTP fallback:** proof at the edge → **5-minute kernel-signed JWT**
`{sub, team, role, auth_source, epoch, seq, exp}` (the `exp` riding
short, the `seq` fenced, the `epoch` live-checked) → bearer for the
window. The token is a performance optimization, not an identity.
Per-request signing stays available for endpoints that want it —
DPoP proofs carry `htm`/`htu` naturally — but is not the law, because
the delivery plane is already ledger-idempotent and the replay blast
radius is small by construction.

**Standards law, decided:** the proof is a **DPoP subset (RFC 9449)**
carried as a detached EdDSA JWS (RFC 8037/7515). The spec already *is*
proof-of-possession with channel binding; the kernel plane simply
doesn't carry the OAuth parts. The spike may only replace it with
another standard on the closed list (COSE Sign1), and only with
evidence. Boring, citable crypto is a feature of the plane's
credibility.

**Revocation stays immediate without a session table** (decided):
revocation bumps the enrollment's `epoch`; the AdmissionGate — which
already caches enrollment state per singleton — compares the claim
against the cache. No session table, no cleanup job, no per-request
lookup. Cross-replica freshness rides the existing Postgres NOTIFY.
The session signing key is a kernel asymmetric key in `admin_config`
(shared across replicas, no external CA, verifiable by any replica
without the DB). Epoch-per-enrollment is all-or-nothing by design;
per-session kills are parked until an operator story demands the
granularity.

## The gateway is the principal — and a mini-kernel for its fleet

Workers are execution resources, not security principals; the kernel
trusts gateways, worker names (`gw/name`) are gateway-owned metadata.
This ratifies as-built topology: the gateway holds one link enrollment,
runs the child fleet over its own socket law (fleet-of-one, per-child
quotas, lease/heartbeat, `goaway`).

**The recursive law (decided): the gateway is a mini-kernel for its
fleet, and no runtime minting is required.** The auth stack repeats one
level down with the *same* shapes:

- **Authorization:** the `children:` config block is the declaration —
  a child listed with a bootstrap token (the as-built `token` /
  `OFAC_CHILD_SECRET`) is exactly a minted code: authorization artifact
  only. The operator spoke when writing the config, the same moment a
  kernel code gets minted, just declaratively.
- **Identity:** first `hello` from a declared child carries
  `{name, token, publicKey}` — the token pins the key and retires
  forever. Pinned key = child identity.
- **Authentication:** every later `hello` is a DPoP-style proof by the
  pinned key. The bootstrap token never authenticates anything again —
  the forever-password is gone, demoted to first-boot pairing secret.
- **Session:** the socket, under the unchanged lease/heartbeat law.

Local children keep the unix-socket (and peer-uid pin) as physical
reinforcement *under* the proof; remote children — already wire-legal
on the identical-framing TCP fallback — gain a TLS listener mode on the
gateway and lean entirely on the proof. Loss/rebuild: fresh token in
the config, first hello re-pins — re-enrollment with a fresh code, the
same story one level up. A gateway therefore needs no admin surface of
its own for identity: config is its admin surface.

## The big-bang swap

No consumer ships against the old path: one release, one wire.

- Enrollment token store, admin mint/revoke surface, OIDC leg:
  unchanged.
- `/enroll` mode (a) stops returning a bearer secret and starts taking
  `{publicKey, keyAlgorithm}`; the bearer re-enroll path, the issued
  link token, and `Link:TokenLifetimeHours` delete in the same
  release. Credential classes on the enrollment row: `oidc | key`,
  closed set.
- `LinkBearerHandler` keeps one job: validating the 5-minute session
  JWT. No long-lived-bearer branch survives.
- Every in-repo endpoint — the five communication adapters, the
  harnesses, the gateway — gains a stateDir keystore (law above) and a
  first-boot flow: present the minted code, register the generated
  public key. The self-enrollment law survives unchanged in shape:
  enroll on boot, degraded-loud until enrolled.
- Child wire: `hello` grows `publicKey`; `hello.token` becomes
  first-boot-only; the gateway ledger grows the pinned-key table.
- Conformance fixtures gain the new enroll shape; the old ones go.
  Wire version bumps once.
- Bootstrap: the static `Link:EnrollmentToken` retires from "what
  enrolls endpoints" to "what authenticates the first admin", and the
  first code is minted through the admin surface like every code after
  it. A kernel with neither a static token nor a pre-seeded code boots
  degraded (accepts nothing, says so) rather than implied-open.

## Audit

Lifecycle verbs, fingerprints only (key = SHA-256 of the public key,
base64) — the hash-only rule the EnrollmentTokenStore already applies
to codes extends to keys. No private material, no literal secrets,
ever:

`enrollment-issued`, `enrollment-consumed`, `enrollment-reuse-attempt`,
`challenge-issued`, `proof-verified`, `proof-failed`, `session-issued`,
`endpoint-revoked`, `key-rotated`, `rotate-grace-consumed`,
`replay-suspect`, `child-key-pinned`.

Revocation semantics are unchanged in force: revoke the endpoint and
the enrollment, every active key, every derived proof, every issued
session — and, for a gateway, its whole pinned fleet — die together;
rejoin requires a fresh code (or a fresh config token, one level down).

Everything the old plane earned stays earned: fail closed,
audit-everything, no external CA, no mTLS requirement, self-hostable,
immediate revocation, OIDC leg, versioned wire, operator-controlled
enrollment.

## Parked, not open

Each of these has a decided default; a real deployment can reopen with
evidence:

- **OIDC subject-pattern binding** — the `azp` registration is the v1
  mapping; narrower matching waits for a federation that needs it.
- **Per-session revocation granularity** — epoch-per-enrollment is the
  law; a jti deny-list appears only if an operator story demands
  killing one session while sparing the endpoint.
- **Challenge-endpoint DoS posture** — inherits the AdmissionGate's
  existing per-endpoint budget law; the challenge leg gets the same
  budget line, nothing bespoke.
- **DPoP-subset → COSE Sign1** — allowed by the standards law only via
  spike evidence.
