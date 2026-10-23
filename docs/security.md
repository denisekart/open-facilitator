# Security posture

Status: reflects Plan 02 (transactional kernel). The credential model is final; the authentication mechanism is interim and cuts over in Plan 07.

## Trust vs data-plane split (spec §3.3, D-10)

The Facilitator is a coordination kernel, not a privilege holder. It authorizes *admission* (accept a command, transition a Work) and never performs privileged external writes itself:

| Plane | Held by | Facilitator role |
|---|---|---|
| Coordination state (Works, events, inbox/outbox, approvals) | Facilitator PostgreSQL | Owner |
| SCM / issue-tracker / deploy credentials | Team (Gateway or direct-mode workspace) | None — the Facilitator holds **no** SCM/issue/deploy credential (D-10) |
| External communication | Narrow chat-bot identity only (thread replies/reactions) | Admits and routes; the adapter writes |
| Execution authority | Workspace/harness | Records status; does not drive execution |

Anything that writes outside the framework runs under team-owned credentials behind team policy. See `.omp/skills/facilitator-architecture/SKILL.md` (Ownership law) for the normative table.

## Ingress authentication (interim: bootstrap token)

Every `/api/*` endpoint requires `Authorization: Bearer <token>`, checked by middleware in `src/facilitator/Kernel/Auth` against config `Auth:BootstrapToken` — a shared secret, appropriate for single-operator development only.

- `Auth:BootstrapToken` unset + Development environment: the literal `dev-token` is accepted, so the dev loop needs no secret management.
- `Auth:BootstrapToken` unset + non-Development: fail-closed — the process refuses to serve (`503`).
- Missing or wrong bearer token: `401` with type `https://errors.openfacilitator.org/v1/unauthorized` (RFC 9457 ProblemDetails).

**Plan 07 cutover:** token validation sits behind a single seam, `IIngressTokenValidator`. Plan 07 replaces the implementation with Keycloak OIDC validation (signature/issuer/expiry + claims) without touching any call site or endpoint. Until then, treat any network that can reach `/api` as trusted-equal.

`GET /health` stays anonymous — liveness only, no data-plane exposure.

## Endpoint exposure

| Surface | Auth | Notes |
|---|---|---|
| `GET /health` | anonymous | liveness |
| `/api/works*`, `/api/works/{id}/*` | bearer | Work CRUD, steer, transitions, timeline |
| `POST /api/ingress/events` | bearer | CloudEvents 1.0 envelope ingress; fail-closed validation |
| `GET /api/dev/deliveries` | bearer | dev-time outbox ledger dump; Plan 12 replaces with admin views |

## Ingress hard limits

- Request bodies at `/api/ingress/events` are capped at **262144 bytes**; oversize ⇒ `413` (`payload-too-large`). This matches `limits.maxEnvelopeBytes` in `protocol/conformance-fixtures/index.json` and is the baseline DoS floor — large payloads travel by artifact reference (appendix §8), never inline.
- Envelopes fail closed: schema/envelope violations ⇒ `400`, command types outside the 7-type known-command table ⇒ `422 unknown-command`, and the rejected envelope is still persisted to the `inbox` as an audit record (state `failed`).
- Duplicate admission (same `(event_source, event_id)`, `command_id`, or `(provider, provider_delivery_id)`) returns `200 {"status":"duplicate"}` and reprocesses nothing — replay is cheap and side-effect-free by construction.

## Deferred to later plans

| Concern | Plan |
|---|---|
| Keycloak OIDC replacing the bootstrap token (same `IIngressTokenValidator` seam) | 07 |
| Principal mappings, per-team authorization, approval nonces/digests enforced on transitions | 07/08 |
| Link-session authentication (fencing epochs, `link_sessions` table exists schema-only since Plan 02) | 03 |
| Admin surface authorization (replacing `/api/dev/*`) | 12 |
