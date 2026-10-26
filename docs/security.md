# Security posture

Status: reflects Plans 09–10 (OIDC identity foundation + workload identity).
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
| `/api/identity/roles*` | OIDC bearer; PUT/DELETE gated `identity.roles.manage` | runtime role rules |
| `/api/dev/*`, `/api/dead-letters` | OIDC bearer (read surface) | dev-time ledger dumps; Plan 12 replaces with admin views |
| `/api/link/v1*`, `/hubs/link` | link plane (`linkToken`) | enrollment + evidence, below |

The kernel refuses to boot at all with an invalid/incomplete `identity:`
block (`IdentityStartupCheck`, fail closed in every environment).

## Ingress hard limits

- Request bodies at `/api/ingress/events` are capped at **262144 bytes**; oversize ⇒ `413` (`payload-too-large`). This matches `limits.maxEnvelopeBytes` in `protocol/conformance-fixtures/index.json` and is the baseline DoS floor — large payloads travel by artifact reference (appendix §8), never inline.
- Envelopes fail closed: schema/envelope violations ⇒ `400`, command types outside the 7-type known-command table ⇒ `422 unknown-command`, and the rejected envelope is still persisted to the `inbox` as an audit record (state `failed`).
- Duplicate admission (same `(event_source, event_id)`, `command_id`, or `(provider, provider_delivery_id)`) returns `200 {"status":"duplicate"}` and reprocesses nothing — replay is cheap and side-effect-free by construction.

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

## Deferred to later plans

| Concern | Plan |
|---|---|
| Refresh-token / session lifecycle for interactive UIs | 12 |
| Admin surface authorization (replacing `/api/dev/*`) | 12 |
| Scope-level evidence enforcement (groups→policy) | 12 |
