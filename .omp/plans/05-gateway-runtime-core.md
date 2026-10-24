# Plan 05 — Gateway runtime core: single embedded child (`@ofac/gateway`)

**Status:** ⬜ not-started — implementation begins ONLY after its replanning pass flips this to 🔵 (see "Replanning gate").
**Phase:** B — team-owned execution side. **Depends on:** 04 (🟢: SDK + `linkd` on `main`; `link-e2e` CI job green).
**Supersedes:** roadmap entry "Plan 05 — Gateway runtime: modes, ledger, multiplexing", split into Plans 05–08 so each plan is one autonomous unit. This plan is the first, smallest cut: a Gateway that serves exactly ONE embedded child, in-process.

## Normalization from Phase A (what changed since the roadmap entry was written)

- The Gateway is a **composition layer over `@ofac/workspace-link`**, not a reimplementation: reuse `createLinkClient`, the `LinkTransport` seam, `enroll()`, `buildEvent()`, and the SQLite ledger from `@ofac/workspace-link/node` (as-built: `src/workspace-link/src/core/client.ts`, `src/node/`). No second Link client, no forked reconnect supervisor.
- Enrollment reality: one endpoint = one `endpointId` via `POST /api/link/v1/enroll` (secret `Link:EnrollmentToken`; dev literal `enroll-dev`), token rotation on re-enroll, `trustMode` recorded and logged server-side. The Gateway enrolls **itself**; children never touch the Facilitator wire.
- The kernel already routes `link.<endpointId>` outbox rows via `LinkSink` liveness + pump materialization — the Gateway needs zero server changes to receive deliveries; it picks its own `endpointId` namespace (`gw.<slug>`) at config level only.
- Dev/e2e affordances exist and are the test hooks: `POST /api/dev/link-outbox`, `GET /api/dev/deliveries`, kernel token `dev-token`. The `link-e2e` CI job (self-spawned Facilitator + postgres:17-alpine service) is the pattern the Gateway e2e tier copies.
- Trust/policy: Plan 08-era (roadmap 08 → renumbered 10) OIDC + policy engine is still unbuilt; the policy port stays a deny-by-default stub, same precedent as 02's bootstrap token and 03's `ILinkTokenValidator`.

## Objective

Implement the `@ofac/gateway` package as a Node runtime that wraps a Link client session on behalf of one embedded child worker: it owns enrollment + connection lifecycle, holds the accepted-command/execution ledger (§6.3 reconcile), binds every accepted command to `(gatewayId, childWorkspaceId, assignmentId, executionId, connectionGeneration)` (§6.1), routes commands to an injected `ChildExecutor` interface, forwards child-published events upstream, and reports honest status after restart. One codebase; the three operational modes (§9.1) collapse to `embedded` here — `shared`/`standalone` (multi-child over a child-socket transport) are Plan 06's, deliberately not stubbed here.

## Replanning gate (mandatory before any code — flips ⬜→🔵)

Re-verify against as-built reality, then produce the detail design as a replacement of this file (same format as Plans 02–04: locked decisions + numbered steps + Implementation Status table). Mandatory initial research:

1. Spec §6.1–6.3 (multiplexing tuple, reconcile law), §9.1–9.2 (modes, trustMode semantics, team authority), §8.4 (team-side config layout); appendix §4 (trustMode machine-readable record), §5.4–5.5 (connector idempotency, consumed later but ledger shape must not preclude it).
2. As-built SDK surface: `src/workspace-link/src/index.ts` + `src/node/` — what is reusable vs what must be wrapped (e.g. is `SqliteLedger` reusable for the Gateway execution ledger, or does the Gateway own a second DB file with a `bindings` table; decide at the gate, don't assume).
3. As-built server behavior relevant to Gateway identity: `src/facilitator/Kernel/Link/LinkEnrollmentStore.cs` (endpoint rotation semantics — a Gateway restart re-enrolling rotates the token; reconcile with SDK 401-refresh path), heartbeat/lease config keys in `LinkOptions.cs`.
4. `.omp/skills/facilitator-workspace-packaging/SKILL.md` + `facilitator-protocol/SKILL.md` implemented-reality sections.
5. Decide (locked at gate, no implementer choice after): config file format + location (§8.4-aligned YAML, `--config` flag), Gateway state dir layout (ledger + config + pid), capability-manifest handling — Gateway POSTs its own aggregated manifest (here: the single child's manifest, verbatim plus `ownerTeam`), heartbeat/status reconcile cadence, and how `connectionGeneration` maps onto the Link `leaseEpoch` + Gateway-side execution counter.

## Scope

- `src/gateway/`: replace the Plan 00 stub with the real package. Deps via pnpm catalog: `@ofac/workspace-link` (`workspace:*`), `@ofac/protocol`. ESM-only, tsup, exports `.` (library) + `bin: ofac-gateway`.
- `GatewayRuntime`: load config → enroll (or reuse `OFAC_LINK_TOKEN`) → POST aggregated capabilities → `createLinkClient` → ready. SIGTERM ⇒ graceful `Goodbye` + ledger flush.
- `ChildExecutor` interface (in-process today, socket-compatible shape for Plan 06): `register(manifest)`, `start(command)`, `steer(command)`, `status(executionId)`, `stop(executionId)` — this is the HarnessAdapter lifecycle preview; Plan 07 freezes it as the cross-process contract, so define it as serializable messages here, not direct method calls on live objects.
- Execution ledger (SQLite, own file): accepted commands, binding tuple, per-execution state (`running|completed|failed|lost`), reconcile-on-restart law (§6.3): after reconnect, un-completed executions are reconciled by querying the child (here: the embedded executor) before reporting anything to the Facilitator; stale generations cannot ack.
- Policy port: `IPolicyEvaluator.evaluate(action, context) => deny|allow`, default **deny everything except** the embedded child's advertised capabilities (typed JSON predicates now, Plan 10-era engine later — single seam, no call-site churn).
- Heartbeat/status: forward child status as `workspace.heartbeat` events with real `activeAssignments`; expose local diagnostics HTTP endpoint (`GET /status`, localhost, no auth, dev-tier only — mirror the kernel's `/api/dev` honesty).
- `linkd` relationship: `linkd` stays the single-worker minimal runtime (Plan 04); Gateway is the team-side multiplexer. Document the split; do not fold `linkd` into the Gateway.

## State after implementation

One Gateway process, one embedded child (echo or stub handler reused from the SDK), pointed at a dev Facilitator: receives a real `work.assign` via `/api/dev/link-outbox`, executes, publishes `assignment.completed` with a result manifest, receipt terminates the delivery. Killing the Gateway mid-execution and restarting reconciles: ledger truth is reported, no phantom completion, no re-execution of a completed command (dedupe answers `duplicate`). §26.1 item 7 basis lands here.

## Testing

- vitest unit tier: config load, policy deny-by-default matrix, binding-tuple construction, reconcile state machine with a mock `ChildExecutor` + mock `LinkTransport` (copy the SDK's mock-transport pattern), ledger reopen-persistence.
- e2e tier (`*.e2e.test.ts`, env-gated on `FACILITATOR_URL`): copy the `link-e2e` job into a `gateway-e2e` CI job — self-spawned Facilitator + postgres service; concrete proofs: seeded `work.assign` → completed + outbox row `delivered`; SIGKILL mid-assignment → restart → honest reconcile (observable via `GET /status` + psql on `link_deliveries`/outbox); re-enroll rotation handled via SDK 401-refresh.
- Shared conformance fixtures: Gateway consumes `@ofac/protocol` validators; no new wire types, so no new fixtures required unless replanning finds a gap (then register in `index.json`, both harnesses).

## Documentation

- `docs/advanced.md`: Gateway-modes section (embedded as-built; shared/standalone marked as Plan 06 scope).
- `src/gateway/README.md`: quickstart (config sample → enroll → run), lifecycle diagram, diagnostics endpoint.
- `.omp/skills/facilitator-workspace-packaging/SKILL.md`: Gateway implemented reality (config keys, modes state, policy seam) — AGENTS.md maintenance rule, same change.
- This file: Implementation Status table updated per step; roadmap entry flipped ⬜→🟡→🟢.

## Architecture fit

§6.1–6.3 + §9: the Gateway is the team-owned execution authority — the Facilitator addresses one endpoint (`gw.<slug>`), the Gateway owns child fan-out, execution truth, and policy. It is the composition proof that the Plan 04 SDK is a reusable library (spec S:471 "reusable composition"), and the anchor the later plans hang from: child sockets (06), adapters (07), Features that package it (08), approvals that gate its privileged actions (roadmap-era Plan 08, renumbered 10).

## Additional resources

- Spec §6, §9, §8.4; appendix §4, §5.4–5.5; `.omp/skills/facilitator-workspace-packaging` + `facilitator-protocol`.
- As-built: `src/workspace-link/` (SDK surface + `linkd`), `src/facilitator/Kernel/Link/` (server semantics it must obey), `tests/Facilitator.Tests/LinkTestHarness.cs`, `.github/workflows/ci.yml` (`link-e2e` job to clone).
- External: better-sqlite3 WAL/txn docs (already proven in SDK), outbox-pattern prior art for worker-side ledgers.
