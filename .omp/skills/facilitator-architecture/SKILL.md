---
name: facilitator-architecture
description: Domain model, ownership boundaries, state machines, durable coordination (Postgres outbox/inbox, SKIP LOCKED leases, session placement), and design-gate questions for the Open Facilitator Framework.
---

> **Status:** the transactional kernel is IMPLEMENTED (Plan 02) — see "Implemented (Plan 02)" below. Links, approvals behavior, memory, and artifacts remain spec-proposed targets. Update this skill the moment a slice lands so implemented reality replaces spec proposals.

# Facilitator Architecture

Normative source: `docs/specs/open_facilitator_framework_architecture.md` §2–§4, §7, §9–§12, §15, §19–§20; appendix §2–§4. Read the relevant chapter before changing a boundary — component ownership is prescriptive, not stylistic.

## Implemented (Plan 02)

Kernel code lives in `src/facilitator` (single deployable), folders `Kernel/Domain`, `Kernel/Persistence`, `Kernel/Ingress`, `Kernel/Dispatch`, `Kernel/Auth`; namespace root `Facilitator.Kernel.*`.

- **Schema** (Postgres `facilitator` schema, EF Core baseline migration `InitialCreate`, auto-applied on startup behind `pg_advisory_lock(420001)`): `teams`, `projects`, `works`, `work_events`, `inbox`, `outbox`, `dead_letters`, `provider_events` are written by Plan 02. `assignments`, `execution_attempts`, `link_sessions`, `approvals`, `communication_bindings` exist schema-only for Plans 03/07/08/09/13.
- **HTTP surface** (all under `/api`, bearer token, RFC 9457 ProblemErrors with type URIs `https://errors.openfacilitator.org/v1/<code>`): `POST /api/works` (201), `GET /api/works`, `GET /api/works/{id}`, `POST /api/works/{id}/steer`, `POST /api/works/{id}/transitions`, `GET /api/works/{id}/timeline`, `POST /api/ingress/events` (raw CloudEvents 1.0 envelope, ≤262144 bytes), `GET /api/dev/deliveries`. Error codes: `invalid-state-transition` 422, `version-conflict` 409 (carries `currentVersion`), `unknown-command` 422, `schema-violation` 400, `envelope-violation` 400, `payload-too-large` 413, `unauthorized` 401, `work-not-found` 404; duplicate ⇒ 200 `{"status":"duplicate"}` (not an error). Endpoint detail: `docs/protocol.md` (Kernel ingress).
- **Dedupe**: `inbox UNIQUE (event_source, event_id)`, `inbox.command_id` unique for business commands, `provider_events UNIQUE (provider, provider_delivery_id)`. Duplicate ⇒ 200 duplicate, zero reprocessing.
- **Per-Work serialization**: `pg_advisory_xact_lock(hashtext(work_id::text))` inside each processing transaction + optimistic `workVersion` fencing (spec §7.4). Illegal transitions fail closed 422 naming the edge; the §4.1 guard tables (`Kernel/Domain/StateGuard`) are the single authority — no shortcuts.
- **Outbox dispatcher** (`OutboxDispatcher : BackgroundService`, in-process, no new container): state+outbox rows commit in one tx; claim `FOR UPDATE SKIP LOCKED`; `NOTIFY facilitator_outbox` is a wake hint only (§7.3) — correctness never depends on it; sinks invoked outside any tx; backoff `min(2^attempts*2s, 5m)`; `attempts >= 5` ⇒ `dead_letters` row (reason + full envelope) + status `dead_letter`. Channel-prefix sinks: `kernel.log` (stub ledger, exposed via `GET /api/dev/deliveries`); `link.*` requeues until Plan 03.
- **Ingress validation**: shared .NET `EnvelopeValidator` in `Ofac.Protocol` (schemas embedded as resources); fail-closed 7-type `KnownCommands` table mirroring `src/protocol/src/validate.ts`; kernel tests consume `protocol/conformance-fixtures` files as live inputs.
- **Auth interim**: bootstrap shared token (`Auth:BootstrapToken`; unset + Development ⇒ literal `dev-token`, unset + non-Development ⇒ fail-closed). Seam: `IIngressTokenValidator` — Plan 07 swaps in Keycloak OIDC, call sites unchanged.
- **CLI**: same assembly, rides the public HTTP API only — `dotnet run --project src/facilitator -- cli [--url URL] [--token TOKEN] work create|steer|get|timeline ...` (defaults `http://localhost:8088`, token env `FACILITATOR_TOKEN` else `dev-token`).
- **Config keys**: `ConnectionStrings:Facilitator`, `Database:AutoMigrate` (default true), `Auth:BootstrapToken`, `Kernel:ReplicaId`, `Otel:Endpoint` (OTLP exporter when set).
- **Observability**: meter `facilitator` — counters `facilitator.delivery.attempts`, `facilitator.inbox.processed`; histograms `facilitator.delivery.latency`, `facilitator.ingress.processing.latency`; gauges `facilitator.outbox.queue_depth`, `facilitator.inbox.queue_depth`, `facilitator.delivery.dead_letter_count`; activity source `Facilitator.Kernel` (spans `ingress.process`, `outbox.dispatch`); log correlation `workId`/`deliveryId` (appendix §9).
- **Still target, not reality**: Link sessions/placement (`link_sessions` behavior, Plan 03), approvals enforcement (Plan 08), memory (`postgres-fts`, Plan 09+), artifacts (Plan 09), admin SPA (Plan 12), OIDC (Plan 07).

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
