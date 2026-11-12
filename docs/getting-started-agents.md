# Getting started (AI agents)

You are an AI agent asked to run, drive, or extend an Open Facilitator
deployment. This page is the dense, machine-friendly orientation: what to
start, how to authenticate, which endpoints answer what, where the exact
machine-readable contract lives, and how to fail well. Read
[overview.md](overview.md) first if the vocabulary is new to you; read
[protocol.md](protocol.md) when you need exact schemas.

## Orientation in one screen

- One **Facilitator** kernel (HTTP + SignalR, state in PostgreSQL) is the
  coordination authority. Everything else — Gateways, Workspaces, adapters —
  is a client of its public APIs. There is no backdoor.
- **Human-plane** calls carry an OIDC bearer to `/api/*`; **link-plane**
  endpoints (`/api/link/v1/*`, `/hubs/link`) carry a per-endpoint
  `linkToken`. The two planes never share credentials.
- Writes are **at-least-once + idempotent**: dedupe on
  `(event_source, event_id)`, `commandId`, or `(provider,
  provider_delivery_id)`. Replay is cheap; retries are safe.
- Reads of another team's data answer **404**, not 403 — absence is the
  honest disclosure.
- Approvals are durable rows with **lazy expiry**: reading one applies
  expiry first, then evaluates. Never assume a pending row is still
  pending.

## Boot a reference deployment

```bash
./referencectl up slim          # builds images, boots stack, seeds a demo Work
./referencectl status           # service table + kernel/gateway probes
./referencectl down -v          # tear down
```

Requires Docker + Compose v2 (~4 GiB RAM). The stack:
PostgreSQL 17 (`:5432`), Keycloak (`:8080`, realm `facilitator`, issuer
`http://id.localhost:8080/realms/facilitator`), the kernel (`:8088`,
`GET /health` anonymous), SeaweedFS S3 (`:8333`, bucket
`facilitator-artifacts`), Aspire dashboard (`:18888`, OTLP
`18890`), a demo team Gateway inside `demo-workspace` (diagnostics on
container-loopback `:8099`, not published). Host-side artifact downloads
need `127.0.0.1 artifacts` in `/etc/hosts` (presigned URLs embed the
in-network host). The full overlay (`FULL=1 ./referencectl up slim`, or
`make compose-full`) adds Forgejo, Mattermost, Kafka, Ollama + the provider
adapters.

## Authenticate

```bash
# service/operator token (client_credentials grant)
TOKEN=$(./referencectl token | tail -1)

# human token (password grant; kernel maps subject → audit actor)
TOKEN=$(./referencectl token --user alice --password alice | tail -1)

curl -s http://localhost:8088/api/works -H "Authorization: Bearer $TOKEN"
```

Bearers live ~5 minutes (realm default) — re-mint liberally. Missing or
invalid bearer ⇒ `401 unauthorized` (RFC 9457, camelCase). Bearer passes
the gate but the role rules don't ⇒ `403 forbidden`. Raw grant equivalent:
`POST http://localhost:8080/realms/facilitator/protocol/openid-connect/token`
with `client_id=facilitator-cli`, `client_secret=e2e-cli-secret`.

## Drive a Work over REST

| Do this | Call |
|---|---|
| Create a Work | `POST /api/works` `{"objective": "…", "projectId": "<uuid?>"}` |
| Read one / list | `GET /api/works/{id}` · `GET /api/works` (newest 50) |
| Steer a live Work | `POST /api/works/{id}/steer` `{"directive", "commandId", "expectedVersion"}` |
| Operator state walk | `POST /api/works/{id}/transitions` `{"to", "expectedVersion", "reason?"}` |
| Timeline (audit trail) | `GET /api/works/{id}/timeline` |
| Result manifests | `GET /api/works/{id}/manifests` (newest revision first) |
| Approvals ledger | `GET /api/works/{id}/approvals` · respond: `POST /api/works/{id}/approvals/{approvalId}/respond` `{"decision": "approved"|"denied"}` |
| Raw protocol ingress | `POST /api/ingress/events` (full CloudEvents 1.0 envelope) |
| Artifacts | `POST /api/artifacts/request-upload` → `PUT /api/artifacts/{id}/content` → `GET /api/artifacts/{id}[/content]` |
| Memory | `POST /api/memory/retain` · `POST /api/memory/recall` · `GET /api/memory/export?scope=…` |
| Operator surface | `/api/admin/summary|search|links|deliveries|metrics|replicas|config/{resource}[/history]`, `POST /api/admin/dead-letters/{id}/replay` |

Concurrency uses optimistic versioning: send the `version` you saw as
`expectedVersion`; a mismatch is `409 version-conflict` (the problem body
carries `currentVersion`). Illegal lifecycle moves are `422
invalid-state-transition`. The bundled CLI rides exactly this REST surface:
`dotnet run --project src/facilitator -- cli [--url U] [--token T] work
create --objective "…" | work get|timeline <id> | work steer <id>
--directive "…" --expect-version N`.

## Become a link endpoint (execute work)

A Workspace/Gateway joins with one enrollment call:

```bash
curl -s -X POST http://localhost:8088/api/link/v1/enroll -H 'content-type: application/json' -d '{
  "token": "enroll-dev",            # Link__EnrollmentToken; or a workload-OIDC token
  "endpointId": "gw.my-team",       # gw.* requires role "gateway"
  "team": "team-alpha",             # must be a kernel roster slug (Link:Teams)
  "role": "gateway",                # default; immutable across token rotation
  "trustMode": "shared-runtime"     # shared-runtime | separate-process-same-host | isolated-executor
}'
# → 201 { enrollmentId, trustMode, linkToken, expiresAt }
```

Store `linkToken` durably; enrollment is an upsert and **rotates** the
previous token immediately. Then: `POST /api/link/v1/capabilities` with
your capability manifest (`capability-manifest.v1.json`), open a session
(hub `hello` on `/hubs/link`, or via the HTTP surface below), and consume
deliveries: long-poll `GET /api/link/v1/deliveries?wait=0..60&limit=1..64`
and answer each with `POST /api/link/v1/deliveries/{deliveryId}/receipt`
(`accepted|duplicate|rejected|unsupported` referencing `deliveryId`);
send your own envelopes with `POST /api/link/v1/events`. The full frozen
fallback surface is `protocol/openapi/link-v1.yaml`. Note: a deliveries
read REQUIRES a live session row (minted by a hub `hello`; each successful
poll refreshes its lease) — a pure-HTTP client that never speaks SignalR
needs the session-keeper trick `@ofac/workspace-link` uses, so prefer the
SDK. Un-acked deliveries are re-offered after the in-flight lease
(default 120 s) — dedupe on `commandId` before acting.
`@ofac/workspace-link` implements all of this, including a durable state
file that replays after a crash.

## The contract, where to read it mechanically

| Need | Artifact |
|---|---|
| Envelope + all payload schemas | `protocol/json-schema/*.json`, `protocol/cloudevents-schemas/*.json` (`$id` = `https://schemas.openfacilitator.org/v1/<file>`) |
| Type strings | `org.facilitator.<class>.<type>.v<N>`; registry table in [protocol.md](protocol.md) |
| HTTP link surface + admin API | `protocol/openapi/link-v1.yaml`, `protocol/openapi/admin-v1.yaml` |
| Conformance fixtures (valid + invalid, machine-checkable) | `protocol/conformance-fixtures/index.json` + payload files |
| TypeScript types + validators | `@ofac/protocol` (npm) |
| Gateway config law | `docs/advanced.md` (Gateway modes and configuration) |
| Operational failure modes | `docs/runbooks/index.md` |

Limits: envelope ≤ 262144 bytes (larger content goes through the artifact
flow); closed payload schemas (`additionalProperties: false`); timestamps
RFC 3339; approval digests are RFC 8785 canonicalization + SHA-256.

## Working *in* this repo as an agent

- The nearest `AGENTS.md` is your entry point; `make docs-check` enforces
  that signposts stay true. Nested `AGENTS.md` files beat the root one.
- Daily loop: `make restore && make build && make test && make lint`
  (Docker needed for tests). Formatting: `make format`.
- The acceptance law for any change touching the reference environment:
  from a clean tree, `./referencectl down -v && ./referencectl up slim
  --no-seed && ./referencectl verify` exits 0.
- Behavior covered by a scenario manifest row
  (`docs/scenarios/scenario-bindings.json`) obliges updating the binding
  and rerunning `make scenario-gate`.
- Zero warnings is a build requirement on both stacks; do not add
  suppressions without calling them out.
- Never commit `.env` or `secrets/`; demo credentials only.
