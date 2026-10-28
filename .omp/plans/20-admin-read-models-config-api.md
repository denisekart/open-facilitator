# Plan 20 — Admin surface, leg 1: kernel admin read models and configuration API
**Status:** ⬜ not-started (flips 🔵 at the replanning gate, then 🟡)
**Depends on:** 19
**Phase E leg 4 of 5.** Operator-surface chain: 20 → 21. Independent of nothing outside itself once 19 lands (SPA needs 21).

## Objective
Build the server half of spec §17: the `/api/admin/*` read/write surface the
embedded inspector (Plan 21) consumes — universal search, health summary,
object detail + timeline, link/delivery state with dead-letter **replay**,
inline metrics roll-up, and `PATCH /api/admin/config/{resource}` with
`If-Match` optimistic revision (§17.4). Read models query the existing
Postgres ledgers; the API is the only new truth-teller, never a second store.
Dev affordances (`/api/dev/*`) are cut over to admin equivalents, not kept
alongside.

## Known facts (as-built anchors; code wins, divergence goes in the status table)
- **Zero `/api/admin/*` routes exist.** Today's operator-ish surface: `GET /api/works` list, `GET /api/works/{id}/timeline` (`WorkEventRow[]`, newest-1000 then chronological, `EfCoordinationStore.cs:197-208`), `GET /api/dead-letters` (top-50, `DeadLetterDto`, no replay), approvals pair (`GET /api/works/{id}/approvals` + respond — Plan 11: "the admin SPA later builds UI on exactly this pair", `11-approval-lifecycle.md:20`), artifacts/manifests/memory read endpoints, `GET/PUT/DELETE /api/identity/roles` (rowVersion 409 — the **existing optimistic-concurrency template** to copy for config PATCH), dev-only `GET /api/dev/deliveries` + `POST /api/dev/link-outbox` (dev-gated per hardening plan; 02 recorded them as "admin views replace" candidates).
- Metrics exist as Prometheus-shaped counters/gauges/histograms on meter `facilitator` (`KernelMetrics.cs`: delivery attempts/latency/dead-letter, inbox/outbox depth, link sessions, artifact/memory/manifest families) — §17.2's inline strip maps onto these + bounded SQL queries, not new instrumentation, unless a gap surfaces at the gate.
- Tables available for read models (§19.1 as-built): works, assignments, work_events, inbox, outbox (+ dead-letter rows via `OutboxAdminStore.ListDeadLettersAsync`), link_sessions (epoch/leaseUntil = online/stale law), approvals, artifacts, result_manifests, memory records + shares + usage, audit/decision rows.
- Plan 15 explicitly deferred **admin re-marking of result manifests** to this surface (`15-result-manifests-worker-cutover.md:14`).
- Appendix §4.3 trustMode is recorded per enrollment (Plan 10 binding rows) → belongs in the links view model.
- Route authorization is config-driven (`identity.authorization.operations` in dev config) — new admin routes join the same table with a dedicated role, no new mechanism.
- Dead-letter replay has NO kernel behavior yet (03/02 shipped visibility only); appendix §11.2 wants operator replay — replay = re-enqueue the envelope through the same outbox path with a fresh attempt + audit row, idempotency law unchanged (the ledger, not the UI, arbitrates).

## Locked decisions (from planning; replanning may only amend with recorded reason)
- **Inspector-first scope:** all §17.4 GET read models + dead-letter replay POST + config `PATCH` with `If-Match` land here. §17.3's editable-field *classes* get config resources + validation + audit, but the UI in Plan 21 exposes only a minimal safe set.
- Search is **SQL-bounded** (typed sections per entity with LIMITs, one round-trip per section), no new index until measurements show need; message-ID search covers outbox/inbox/dead-letter id columns.
- Versioned preferences copy the roles-API pattern: a `facilitator.admin_config` table (resource, payload jsonb, rowVersion, author, reason, updatedAt) with same-transaction audit + outbox event, per §17.1's "author, validation, reason and rollback link" — rollback = PATCH to a prior payload, the table keeps bounded history.
- `GET /api/admin/*` grouped under one authorization operation set; SPA later authenticates as the human via Keycloak (alice-class role in the realm).
- Clean cutover: once admin delivery views exist, the dev-only `/api/dev/deliveries` endpoint is **deleted** (its e2e consumers move to the admin route under the dev token).

## Steps
0. **Research (mandatory):** enumerate §17.2's statistic rows against existing metrics/tables and write the gap list; study `OutboxAdminStore` + `LinkLedger` for replay-safe re-enqueue; re-read §17.4 + appendix §4.3/§11.2 for view-model fields; check EF read-query patterns in `EfCoordinationStore` for bounded queries.
1. Migrations + entities: `facilitator.admin_config` (versioned, audited, bounded history).
2. Read models: `/api/admin/summary` (health strip: footprint counts, gateway online/offline/stale from leases, work/assignment by state, inbox/outbox pending/leased/dead-letter, oldest pending, approvals pending/expired/denied, artifact + memory roll-ups), `/api/admin/search?q=&type=`, per-object detail (`projects/{id}`, `work/{id}` incl. manifest + artifact refs + approvals), `work/{id}/timeline` (reuse), `links` (sessions + **trustMode** + affected assignments for stale), `deliveries?state=` (in-flight, dead-letter w/ reason), `metrics?window=` (bounded roll-up).
3. Mutations: `POST /api/admin/dead-letters/{id}/replay` (audit + outbox re-enqueue; typed conflict when envelope's work moved on), admin manifest re-mark per 15's deferral, `PATCH /api/admin/config/{resource}` + `If-Match`/409 + reason header w/ audit.
4. Delete `/api/dev/deliveries`; migrate its consumers; keep `/api/dev/link-outbox` until proven unneeded (record decision).
5. OpenAPI: extend `protocol/openapi/link-v1.yaml` companion or a new `admin-v1.yaml` per §29.1 contract-first; both-harness drift check as in Plan 01.
6. xUnit integration suite on Testcontainers Postgres (existing collections): each read model against seeded fixture state incl. stale-session and dead-letter cases; replay law tests (replay twice → one business command); PATCH conflict/audit tests.

## State after
Every §17.4 endpoint answers from a seeded DB with role-gated access; a dead-letter can be replayed through the API with audit; config changes are versioned, attributed, revertible; operators can trace an acceptance-run Work via API alone. Plan 21's SPA has a complete, frozen surface to bind to.

## Testing
- xUnit + Testcontainers: read-model correctness against seeded fixtures (state transitions reflected, bounds enforced), replay idempotency, `If-Match` 409 + audit rows, search section limits, authorization matrix (role without admin sees 403).
- Conformance fixture only if a new wire-visible payload shape justifies one (admin DTOs are kernel API, not protocol wire — default is no fixture; record the call).

## Documentation
- `docs/protocol.md` or a new `docs/admin-api.md` section documenting the surface with exact JSON; `docs/security.md` row for the admin role + what it may/may not touch (§17.3 ceilings: no bypass of team authorization, no raw secrets).
- `.omp/skills/facilitator-architecture` updated; dev-endpoint removal reflected in every referencing doc.

## Architecture fit
§17 — inspector over the kernel, bounded queries over materialized summaries, database remains sole authority. §12.2 audit law reused; §17.4's endpoint list becomes the API contract the SPA cannot outgrow.

## Assumptions & contingencies (replanning caveat)
- If summary queries exceed a sane latency budget on seeded-large fixtures, add bounded incremental counters (appendix §9 already sketches materialized counters) — recorded as a scope amendment, not silent drift.
- If `admin_config` history growth is a concern, retention = keep N versions per resource (cap decided at gate).
- Metrics histogram → inline-stat aggregation shape may force one new gauge (e.g. nudge/escalation counts); acceptable, instrument in `KernelMetrics` same change.
- Replanning or optimization may be required before implementation begins; implementation begins with additional research (Step 0).
- **Questions may be asked during planning, but not during implementation.**

## Implementation Status
| Step | Status | Notes |
|---|---|---|
| 0 Research | Not started | |
| 1 admin_config migration | Not started | |
| 2 Read models | Not started | |
| 3 Mutations (replay/PATCH/re-mark) | Not started | |
| 4 dev-endpoint cutover | Not started | |
| 5 OpenAPI + drift gate | Not started | |
| 6 xUnit suite | Not started | |

## Verification
Admin integration suite green; manual pass against the slim env: search an acceptance-run Work, read its timeline/detail, force a dead-letter (kill the gateway mid-delivery via compose), replay via API, watch it re-deliver.
