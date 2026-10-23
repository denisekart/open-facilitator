# Plan 02 — Transactional kernel: Postgres schema, state machines, inbox/outbox, ingress

**Status:** ⬜ not-started
**Depends on:** 01

Status legend: ⬜ `not-started` → 🔵 `replanning` (mandatory gate) → 🟡 `in-progress` → 🟢 `done`. This plan starts with a replanning pass (flip to 🔵): re-verify assumptions against the specs and prior plans' state, run the research below, finalize this file's detail design, then flip to 🟡.

## Objective

Implement the durable coordination core (spec slice 1): EF Core + PostgreSQL 17 schema (§19.1 tables: projects, works, assignments, inbox, outbox, provider_events, link_sessions, approvals, communication_bindings), Work/Assignment state machines (§4.1), transactional inbox/outbox with leases (§7.1–7.2, appendix §5), optimistic `workVersion` fencing (§7.4), the database-backed event loop (§20 `ICoordinationStore`/`IEventDispatcher`), and HTTP + CLI ingress for create-Work/steer/status.

## Replanning gate

- Reconcile §19.1 table sketch against Plan 01 schemas.
- Decide migration tooling (EF Core migrations, one baseline).
- Decide dispatcher background-service architecture (`IHostedService` + `FOR UPDATE SKIP LOCKED`).
- Decide how CLI (`dotnet run --project src/facilitator -- work create ...` vs separate `referencectl` seed) authenticates locally pre-Keycloak (bootstrap shared-token mode, replaced in Plan 07).

## Scope

- `framework/src/facilitator/` ASP.NET Core app: minimal-API endpoints `POST /api/works`, `GET /api/works/{id}`, `POST /api/works/{id}/steer`, `GET /api/works/{id}/timeline`.
- State-machine guard table as explicit transitions (no implicit jumps).
- Inbox dedupe on `(source,id)` + `commandId`.
- Outbox same-transaction writes + leased dispatcher with attempt/nextAttemptAt/dead-letter.
- Per-Work advisory-lock ordering.
- Local testcontainers fixture.

## State after

Accepted event survives process restart; duplicate business commands are idempotent; a Work can be driven `open→planning→active→review→completed` purely via HTTP with the outbox draining to a stub sink. Spec slice-1 exit test passes.

## Testing

xUnit + Testcontainers(Postgres 17): restart-after-accepted-event test (kill dispatcher mid-drain, restart, no loss/no duplicate), duplicate webhook → single business command, concurrent steer on same Work → version conflict retry, invalid state transition rejected with typed error. §26.1 item 3 (duplicated command) basis.

## Documentation

`docs/security.md` stub for trust/data-plane split (§3.3); ADR note in-code for lock strategy; `.omp/skills/facilitator-architecture` + `facilitator-protocol` updated to implemented reality.

## Architecture fit

§4, §7.1–7.4, §19, §20 — "the database is the coordination authority." Kernel is the hub; Link/Gateway/adapters later attach at its edges.

## Additional resources

Spec §4/§7/§19/§20, appendix §5.2–5.3; `Npgsql` docs on `FOR UPDATE SKIP LOCKED`; EF Core 10 + `Testcontainers.PostgreSql`; OTLP wiring baseline (`OpenTelemetry.AspNetCore`) so Plan 11's Aspire step is config-only.
