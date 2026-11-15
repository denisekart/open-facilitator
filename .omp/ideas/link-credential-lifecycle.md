# Link credentials after the join: one-time pairing, no standing secrets

A design idea, not a decision — nothing below is chosen, and the numbers are
proposals to argue with. The trigger is a contradiction we keep living with:
the join ceremony is short-lived and console-gated, but what it mints becomes a
permanent credential, and the laptop-fleet shape (`examples/local`,
`examples/aws`) makes "standing bearer token on a laptop" the weakest link in
an otherwise law-abiding posture.

## Law today (shipped)

`POST /api/link/v1/enroll` takes its credential in the body (`token`), and the
kernel tells two modes apart by shape:

- **Mode (a) shared secret** — `token` = `Link:EnrollmentToken`, one literal
  trusted by every endpoint (`401 enrollment-secret-invalid`, fail-closed
  startup gate outside Development, dev literal `enroll-dev`). The body names
  the team; `role` defaults to `gateway`.
- **Mode (b) workload OIDC** — an access token with `aud: facilitator-link`,
  whose `(azp, endpointId)` binding in `Link:WorkloadIdentities` fixes team,
  role, and delegation acceptance (`403 workload-not-registered`).
- **Admin-minted tokens** (`/api/admin/enroll-tokens`, gated
  `enrollment-tokens.manage`, TTL default 60 min, max 7 days) are a third
  entry: hashed at rest, bound to label/team/role, consumed on first enroll.

The parts that bother me:

- **The pairing credential doesn't die.** `EnrollmentTokenEntity` marks
  `ConsumedAt`/`ConsumedEndpointId` and then says the quiet part out loud:
  *"afterwards the token is that endpoint's standing re-enroll secret."* The
  console-gated, hour-scoped join code doubles as the endpoint's permanent
  password.
- **The minted credential is long-lived.** `linkToken` is an opaque 256-bit
  bearer, expires at now + `Link:TokenLifetimeHours` = **720 h**, and re-enroll
  (an upsert by `endpointId`) is the only rotation path.
- **Rotation needs the old secret.** Enroll presents the mode-(a) secret again,
  so an endpoint can rotate only for as long as it keeps that file. The
  gateway's own rule encodes the dependency: a mounted `OFAC_LINK_TOKEN` with
  no secret configured gets *no* rotation path at all (`runtime.ts`, the
  linkd rule).
- **A leaked token is full impersonation** until it expires or an operator
  notices: bearer, replayable from anywhere that can reach the hub, with `401
  unauthorized` leaking nothing about whether the enrollment exists.
- **Every box in the fleet holds the shared secret** — the container unit
  scripts, all nine adapter `config.ts` files, `features/src/*/install.sh`,
  `examples/aws/fleet-launcher`. Leaking one worker leaks the thing that mints
  new workers.

Mode (b) already has the shape we want: a short, re-federated credential. This
idea is about the other half.

## The laws the owner actually demands

1. **One-time join token, only.** The pairing credential authenticates exactly
   one enroll. Reuse after consumption is an anomaly, not a re-enrollment.
2. **No long-lived secrets in steady state.** Whatever sits on an endpoint
   disk-side is either a key that never leaves the box and is not replayable,
   or a credential the kernel considers stale within hours.
3. **Rejoin after arbitrary downtime, no human.** Laptop closed for six weeks
   (past any TTL you can name) must reconnect using only on-disk material.
   "Ask the operator to re-mint" is a *diagnosis*, not the rejoin path.
4. **Proven mechanisms only.** We may adopt a documented scheme wholesale; we
   may not invent a bespoke handshake.
5. **No external CA, no mTLS authority.** No new PKI to operate, no cert
   issuance in the reference stack.
6. **Revocation semantics unchanged.** Revoke kills the enrollment (and the
   issued enrollment of a minted token); rejoining after revoke needs a fresh
   console mint. No new revocation surface.
7. **Console-only control plane, two scopes.** Mint/revoke lives in the
   console: an operator (`kernel-operator`/`framework-admin`) mints for any
   team; a **team-operator** only for their own team, with credentials that
   cannot out-rank them — no `gw.` binding for another team, no mint that
   accepts delegated claims. Config-file fleet secrets are bootstrap
   affordances, not the steady state.

## Candidates

**A. One-time pairing + a rotating credential chain** (OAuth 2.0 refresh-token
rotation, RFC 6749 §10.4 + the reuse-detection rule of the OAuth Security BCP).
Keep the bearer link token, change its semantics: the pairing token is single
use and never authenticates anything again; the minted credential rotates with
a short TTL, each re-enroll replacing it, superseded immediately. *Cost:* no
new crypto, no new wire fields, fixtures barely move (`expiresAt` shrinks).
*Weakness:* still a bearer secret at rest — requirement 2 holds only by TTL.

**B. Short access token + refresh chain, OAuth-shaped end to end.** Enroll
returns `{accessToken (5–15 min), refreshToken}`; the link plane validates the
access token, the endpoint silently renews (the `oidc-token-provider.ts` cache
is the same shape, so the client-side pattern exists). *Cost:* two credential
kinds, and both the HTTP fallback and the WS handshake need a fresh token at
connect time. *Weakness:* most moving parts, and the at-rest problem is merely
moved to the refresh token unless A's reuse rules apply to it too.

**C. Sender-constrained credentials** — DPoP (RFC 9449) or the SSH
`authorized_keys` model: pin a public key at enroll, sign a server nonce per
session. The endpoint holds a private key (useless alone to an attacker) and
proves possession per connection; the kernel stores the pinned key the way it
stores `TokenSha256` today. *Cost:* one signature per link session, a proof
transport for both surfaces, a key-at-rest + fingerprint-attestation story.
*Weakness:* per-*request* proof (true DPoP) is more than we need, and
per-session is a deviation from the RFC to be honest about — but this is the
only candidate that satisfies requirement 2 instead of managing it.

Excluded by rule 5, for the record: mTLS, a kernel-internal CA, and anything
needing an out-of-band channel.

## Proposed steady state (laws, not an implementation)

- **Join law:** a pairing credential authenticates one successful enroll from
  the endpoint that first consumed it. Any later present is `403
  enrollment-token-consumed`, audited as a **reuse signal** against
  `ConsumedEndpointId`; a reuse from a *different* source means someone else
  has the code and is strong enough to grey out the enrollment.
- **Pairing law:** console-initiated, TTL-bounded (60 min default, 7-day cap)
  and team-/role-bound at mint as today, single-use, and it may not change the
  team **or** the role of an existing endpoint.
- **Credential law:** the steady-state credential is either a pinned key or a
  credential with a server-side TTL in hours; `Link:TokenLifetimeHours: 720`
  should stop existing.
- **Rotation law (mostly unchanged):** rotation replaces the stored hash and
  the previous token stops validating immediately; role is immutable across
  rotation, re-role is revoke + re-enroll; team binding and stored capability
  manifest survive an upsert.
- **Rejoin law:** a credential may be replaced by an endpoint holding only
  on-disk material, at any age, without an operator. Idempotency rides
  machinery that exists — one `link_sessions` row per logical endpoint,
  `session_epoch` bumped per re-open, lease reconciliation, outbox backoff —
  so a rejoin after six weeks is a long outbox drain, not a new state.
- **Reuse-detection law:** a superseded generation presenting itself denies,
  audits `enrollment-token-reuse`, and flags rather than auto-revokes (two
  gateway processes racing on one token file must be diagnosable, not bricked);
  one generation of grace for in-flight requests.
- **Revocation law (unchanged):** revoke kills the enrollment and the
  enrollment its minted token issued; re-pairing needs a fresh mint.
- **Audit law (unchanged in kind):** every enroll, rotation, denial and reuse
  signal writes one `authorization.evaluated:link` row, hashes only, never a
  literal; audit failure on an allow path still fails closed.

## What this breaks / open questions

- **Bootstrap chicken-and-egg.** Rule 7 deletes the steady-state use of
  `Link:EnrollmentToken`, so what mints the *first* credential on a headless
  kernel whose console needs an OIDC login? `StartupChecks.cs`, `enroll-dev`
  and the fail-closed gate need a replacement story (a first-run code printed
  to the log is the obvious answer, and the obvious thing to argue about).
- **Gateway child fan-out (biggest surface).** Today every child enrolls
  itself with the shared secret. Under a one-time credential either a gateway
  may mint child credentials (a delegated-mint verb scoped to `child` + own
  team, hence a capability the operator can withhold) or each machine is
  paired by hand — what `examples/local`'s per-box `localctl enroll` assumes,
  and what does not scale to per-worker ephemerals.
- **Client seams.** `token-rotation.ts` (401 ⇒ rotate ⇒ retry) splits into
  rotate vs needs-repair; `reconnect-supervisor`'s unbounded retry becomes a
  terminal state worth surfacing; the gateway's enroll-or-mount "static token,
  no secret" mode has no rotation story today and would be illegal.
- **HTTP fallback asymmetry.** The fallback has no WelcomeFrame and no lease
  row — enrollment plus bearer is the whole bootstrap, so a proof-of-possession
  scheme needs a home there (header) and on the hub (subprotocol or first
  frame), plus a rule for which is authoritative.
- **Blast radius.** Every artifact carrying the secret: adapter/harness
  `config.ts`, `reference/compose.full.yaml` + `reference/config/facilitator.env`,
  the container unit scripts, `features/src/*/install.sh`,
  `examples/aws/fleet-launcher`, every `worker.e2e.test.ts` that enrolls as
  setup. On the contract side: the `enroll.*` fixtures + `COVERAGE.md`, the
  1.0.1 additive-field promise on `EnrollResponse` (new fields stay additive;
  new error URIs — `enrollment-token-consumed`, `enrollment-token-reuse`,
  `proof-invalid` — need rows in `docs/protocol.md`), `tools/scenario-plane`,
  `docs/runbooks/gateway-offline.md` (401-storm diagnosis), `referencectl verify`.
- **Should mode (b) just win?** Anything with an IdP already has a credential
  that expires. The honest minimum may be: one-time pairing code as the only
  mode-(a) form, workload OIDC as the default elsewhere, C reserved for laptops.
- **Console surface.** The Links and EnrollmentTokens views
  (`console-and-site-ux-overhaul`'s Team & Access group) gain a generation
  lineage, a "needs re-pairing" state distinct from "revoked", a reuse badge,
  and a mint dialog meaning different things to operator than team-operator.
- **Doc + dev ergonomics.** `docs/security.md` §Workload plane gets rewritten
  around the join/credential/rotation split, and the Deferred row
  *"refresh-token / session lifecycle"* stops being a deferral for this plane.
  Meanwhile HA, the devcontainer shape and every demo script hardcode one
  secret for eight endpoints — the dev path must stay one-line after the
  literal is gone.

None of the above is decided. The decision worth having first is narrow: is
the steady-state endpoint credential a short rotating bearer (A/B) or a pinned
key with a proof (C)? The fan-out question, the fixtures, the console views and
the runbook all follow from that answer, and are worth sequencing only after it.
