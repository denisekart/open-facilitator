---
name: facilitator-architecture
description: Domain model, ownership boundaries, state machines, durable coordination (Postgres outbox/inbox, SKIP LOCKED leases, session placement), and design-gate questions for the Open Facilitator Framework.
---

> **Status:** the transactional kernel is IMPLEMENTED (Plan 02) and the Link server is IMPLEMENTED (Plan 03) — see "Implemented (Plan 02)" and "Implemented (Plan 03)" below. Approvals behavior, memory, and artifacts remain spec-proposed targets. Update this skill the moment a slice lands so implemented reality replaces spec proposals.

# Facilitator Architecture

Normative source: `docs/specs/open_facilitator_framework_architecture.md` §2–§4, §7, §9–§12, §15, §19–§20; appendix §2–§4. Read the relevant chapter before changing a boundary — component ownership is prescriptive, not stylistic.

## Implemented (Plan 02)

Kernel code lives in `src/facilitator` (single deployable), grouped by feature: `Kernel/Work` (lifecycle + its coordination store), `Kernel/Link` (workspace-link server), `Kernel/Shared` (Auth, Identity, Dispatch, Persistence, CLI, metrics — not a feature). Namespace root `Facilitator.Kernel.*` follows folders. Composition: `KernelServiceCollection` exposes one extension method per folder (`AddSharedKernel`/`AddIdentityFeature`/`AddWorkFeature`/`AddLinkFeature`); new services register in their folder's method. Raw SQL lives only inside `Kernel/Shared/Persistence` classes and only for advisory locks, `pg_notify`/`LISTEN`, `FOR UPDATE SKIP LOCKED` claims, and version-fenced UPDATEs; no endpoint lambda injects a DbContext.

- **Schema** (Postgres `facilitator` schema, EF Core migrations `InitialCreate` + `AddLinkServer` + `AddIdentityFoundation`, auto-applied on startup behind `pg_advisory_lock(420001)`): `teams`, `projects`, `works`, `work_events`, `inbox`, `outbox`, `dead_letters`, `provider_events` are written by Plan 02; `link_enrollments` and `link_deliveries` by Plan 03 (see below); `audit` and `identity_roles` by Plan 09 (identity, above). `assignments`, `execution_attempts`, `approvals`, `communication_bindings` exist schema-only for Plans 07/08/13; `link_sessions` is Plan 02's table now driven by Plan 03.
- **HTTP surface** (all under `/api`, bearer token, RFC 9457 ProblemErrors with type URIs `https://errors.openfacilitator.org/v1/<code>`; `/api/link/v1/*` is the exception — link-token auth, see Plan 03 below): `POST /api/works` (201), `GET /api/works`, `GET /api/works/{id}`, `POST /api/works/{id}/steer`, `POST /api/works/{id}/transitions`, `GET /api/works/{id}/timeline`, `POST /api/ingress/events` (raw CloudEvents 1.0 envelope, ≤262144 bytes), `GET /api/dev/deliveries`. Error codes: `invalid-state-transition` 422, `version-conflict` 409 (carries `currentVersion`), `unknown-command` 422, `schema-violation` 400, `envelope-violation` 400, `payload-too-large` 413, `unauthorized` 401, `work-not-found` 404; duplicate ⇒ 200 `{"status":"duplicate"}` (not an error). Endpoint detail: `docs/protocol.md` (Kernel ingress).
- **Dedupe**: `inbox UNIQUE (event_source, event_id)`, `inbox.command_id` unique for business commands, `provider_events UNIQUE (provider, provider_delivery_id)`. Duplicate ⇒ 200 duplicate, zero reprocessing.
- **Per-Work serialization**: `pg_advisory_xact_lock(hashtextextended(work_id::text, 0))` inside each processing transaction + optimistic `workVersion` fencing (spec §7.4). Illegal transitions fail closed 422 naming the edge; the §4.1 guard tables (`Kernel/Work/WorkState.cs`) are the single authority — no shortcuts.
- **Outbox dispatcher** (`OutboxDispatcher : BackgroundService`, in-process, no new container): state+outbox rows commit in one tx; claim `FOR UPDATE SKIP LOCKED`; `NOTIFY facilitator_outbox` is a wake hint only (§7.3) — correctness never depends on it; sinks invoked outside any tx; backoff `min(2^attempts*2s, 5m)`; `attempts >= 5` ⇒ `dead_letters` row (reason + full envelope) + status `dead_letter`. Channel-prefix sinks: `kernel.log` (stub ledger, exposed via `GET /api/dev/deliveries` — dev endpoints map only in Development); `link.*` resolves to `LinkSink` (Plan 03, below). Outbox state transitions are owned by `Shared/Persistence/OutboxRepository`; wake listeners share `PgWakeListener`, notifies share `PostgresNotifications`.
- **Ingress validation**: shared .NET `EnvelopeValidator` in `Ofac.Protocol` (schemas embedded as resources); fail-closed 7-type `KnownCommands` table mirroring `src/protocol/src/validate.ts`; kernel tests consume `protocol/conformance-fixtures` files as live inputs.
- **Identity (human plane, Plan 09)**: OIDC bearer on every `/api/*` route except `/api/link/v1*` — JwtBearer scheme `"oidc"` (`Shared/Identity/IdentityJwt`), vendor-neutral (issuer/JWKS/audience, RS256/ES256). Identity = verified `(iss, sub)`; actor string `oidc:<iss>|<sub>`. Operator claim pointers (`identity:claims`, RFC 6901 + shape) map principal context; roles = runtime-mutable claim rules (`identity_roles`, config-seeded when empty, `RoleStore` snapshot ≤`rolesRefreshSeconds` stale, gated API `PUT/DELETE /api/identity/roles/{name}` rowVersion-guarded 409). `IngressRoleGateMiddleware` (after UseAuthorization) matches the resolved endpoint against `identity:authorization:operations`; no binding ⇒ any authenticated principal; matched ⇒ rolesAnyOf or 403 `forbidden`; every evaluation audits `facilitator.audit` (allow+deny, `AuditWriter`), audit failure on allow ⇒ 503 `auth-unavailable`. `IdentityStartupCheck` fail-closed boot on any invalid `identity:` block. Link plane untouched (`LinkBearerHandler`).
- **CLI**: same assembly, rides the public HTTP API only — `dotnet run --project src/facilitator -- cli [--url URL] [--token TOKEN] work create|steer|get|timeline ...` (defaults `http://localhost:8088`, bearer REQUIRED from env `FACILITATOR_TOKEN` or `--token`, an OIDC access token).
- **Config keys**: `ConnectionStrings:Facilitator`, `Database:AutoMigrate` (default true), `identity:*` (oidc/claims/roles/authorization, see docs/security.md), `Kernel:ReplicaId`, `Otel:Endpoint` (OTLP exporter when set).
- **Observability**: meter `facilitator` — counters `facilitator.delivery.attempts`, `facilitator.inbox.processed`; histograms `facilitator.delivery.latency`, `facilitator.ingress.processing.latency`; gauges `facilitator.outbox.queue_depth`, `facilitator.inbox.queue_depth`, `facilitator.delivery.dead_letter_count`; activity source `Facilitator.Kernel` (spans `ingress.process`, `outbox.dispatch`); log correlation `workId`/`deliveryId` (appendix §9).
- **Still target, not reality**: approvals enforcement (Plan 08), memory (`postgres-fts`, Plan 09+), artifacts (Plan 09), admin SPA (Plan 12), link-token OIDC rotation (Plan 10).

## Implemented (Plan 03)

Link server code lives in `src/facilitator/Kernel/Link` (namespace `Facilitator.Kernel.Link`). Coordination/ownership rules for the transport edge:

- **DB lease = liveness truth.** `link_sessions.lease_until` (hello/heartbeat + max(15 s, heartbeatSeconds×3)) decides whether an endpoint is live — never socket state. The pump's sweeper deletes expired rows and closes tracked sockets; a link being down leaves its outbox rows non-`delivered` (nothing is tied to a dead socket, §7.3).
- **The pump owns link delivery-state transitions** (`link_deliveries`: materialize pending → claim in_flight under the caller's epoch → redeliver past `redeliver_after`) **except the receipt ack**, which is its own transaction flipping ledger row to `acked` AND outbox row to `delivered` together — the dispatcher never double-charges a link row. `LinkSink` (`Channel="link"`) is only a routing liveness probe; ordering/redelivery/attempts live in the ledger, not the dispatcher.
- **The connection registry is per-replica sockets only**; cross-replica fencing is the DB `session_epoch` (stale epoch ⇒ refuse + supersede goodbye). NOTIFY `facilitator_outbox` remains hint-only for the link pump too (LISTEN + 2 s safety poll + inline pumps).
- **Ledger is truth, resume cursor is a hint** (S:291): replay recomputes un-acked rows from `link_deliveries`; `lastKnownAck` is logged, never trusted. Delivery identity = outbox id, so dropped-receipt → redelivery → duplicate terminates; a `duplicate` receipt ends redelivery like `accepted`.
- **Enrollment/auth**: `link_enrollments` (token hash, unique per endpoint, rotation on re-enroll, team `default` seeded on demand) behind the `ILinkTokenValidator` seam — Plan 07 swaps OIDC + `principal_mappings` at the same seam. Config `Link:EnrollmentToken` (fail-closed outside Development). `POST /api/link/v1/enroll` is rate-limited per remote IP (30/min fixed window, 429 on reject); `endpointId`/`instanceId` carry the schema's 128-char idToken bound server-side. Link-token query strings (`?access_token=`) are accepted only on WebSocket upgrade requests. New config keys: `Link:TokenLifetimeHours` 720, `Link:MaxInFlight` 16, `Link:HeartbeatSeconds` 30, `Link:InFlightLeaseSeconds` 120.
- **Observability additions**: span `link.handshake`; counter `facilitator.link.received`; gauge `facilitator.link.sessions`; link claims reuse `facilitator.delivery.attempts`. Wire-level endpoint/error detail: `docs/protocol.md` (Link transport).

## When to use

- Placing new code: deciding Facilitator vs Gateway vs Workspace vs adapter responsibility.
- Designing schema/state changes, routing, memory, artifact, or approval features.
- Reviewing PRs that touch coordination or privilege placement.

## Ownership law (spec §2.1)

| Concern | Owner |
|---|---|
| Normalize events, resolve Work, communicate externally | Facilitator |
| Execute implementation/research/testing; pick models & skills | Workspace/harness |
| Authorize privileged writes (SCM/issues/deploy) | Gateway (or workspace in direct mode) — Facilitator does admission only |
| Authoritative work state | Facilitator PostgreSQL |
| Team-private memory | Team; Facilitator only when explicitly delegated |
| Start/stop workers | Gateway provisioner / external scheduler; Facilitator records status only |

The Facilitator holds **no general SCM/issue/deploy credential** (D-10). It may hold only a narrow chat-bot identity for thread replies/reactions. Anything that writes outside the framework runs under team-owned credentials behind team policy.

## State machines (spec §4.1) — do not invent shortcuts

- Work: `open → planning → active → review → completed` (+ `blocked|cancelled|failed`); closure independent of execution completion.
- Assignment: `proposed → routed → accepted → running → input_required|completed|failed|cancelled|expired`.
- Execution: `starting → ready → working → stopping → stopped|failed|lost`.
- Result: `missing → submitted → validated|rejected → superseded`.
- Approval: `pending → approved|denied|expired|revoked|consumed`.
- Delivery: `pending → leased → sent → durably_acknowledged|rejected|dead_letter`.

Invariants: one *current* execution generation per assignment; assignment-scoped events carry `workId`+`assignmentId`; completion event never publishes its artifact; parent Work closes only per its completion policy. Four output classes are distinct states: execution status, result state, review status, delivery/publication state — never conflate "agent finished" with "business accepted".

## Coordination pattern (PostgreSQL-first)

- Ingress: verify signature/origin → insert `provider_events`/inbox transactionally (dedupe = success, no processing).
- Claim due rows `FOR UPDATE SKIP LOCKED` with `leaseOwner/leaseUntil/attempt/nextAttemptAt`; serialize per-Work via row lock or advisory lock keyed by Work ID in a **short** transaction; persist new state + outbox rows **atomically**; call providers only after commit, outside the transaction.
- Session placement: `link_sessions(endpointId, connectionInstanceId, replicaId, sessionEpoch, leaseUntil)`; only the session owner (epoch match) may deliver; LISTEN/NOTIFY is a wake hint, never the event store. No Redis backplane, no Kafka, no Temporal in the slim reference — those are adapter seams (D-16).
- Memory default = `postgres-fts` (GIN/tsvector). Memory is a context supplement: never consulted for authn/authz/approval; recall budget-bounded; injection framed as untrusted reference material; missing authorization excludes the whole scope (no post-filter-by-LLM).

## Extension seams (implement one of these, or don't exist)

`LinkTransport`, `HarnessAdapter`, `CapabilityProvider`, `ExecutionProvisioner`, `MemoryProvider`/`IMemoryBroker`, `ArtifactStore`, `CommunicationAdapter` (receive/read/reply/react/discover — provider ops are OPTIONAL per adapter, discovered not assumed), `IDecisionProvider` (only the **authorization** evaluator may allow/deny, and it must be deterministic), `IWorkCoordinator` (Temporal/Orleans behind it, optional).

## Design gate for any new component (spec §25.3)

Answer all: which contract does it implement; does it change team onboarding; does it add an always-on container; what identity/data can it touch; does it degrade offline/local; version compatibility path; at-least-once/idempotency behavior; recovery with a transient workspace; would a thin provider adapter have sufficed.

## Capability routing (spec §10)

Deterministic first: verified resource bindings → authorized candidate set (capability id/version, schemas, resources, availability, team admission) → decision table. Model sees **only** the candidate list + bounded `llmContext` excerpts and may return only candidate IDs; the selection is re-validated independently. Descriptive text never expands permissions; ambiguity ⇒ clarification, never speculative execution.
