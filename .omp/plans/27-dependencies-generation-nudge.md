# Plan 27 — Multi-work: dependsOn DAG, steer generation fencing, nudge consumption

**Status:** ♻️ superseded by plan 35 (Phase B of `35-production-shape-milestone.md`; its locked decisions carry over there)
**Depends on:** 25 as-built (routing creates assignments — the DAG gates its dispatch); 24 (steer-consumption counterparties). **Phase F leg 6 of 9.**
**On approval:** this file is the working detail design; keep the Implementation Status table current.
**Granularity:** one kernel-semantics plan (DAG + generation + nudge = one coherent state-machine widening). Provisioning (§18) is deliberately **not** here — it's 28. Split first if the nudge policy loop grows its own scheduler.
**Questions may be asked during planning, but not during implementation** — decisions are locked below; when reality diverges, infer from code/spec per the contingencies section and prefer the simpler robust option.

## Context
Spec slice 8 + §4.2 (assignment dependencies with conditions), §6.4/§23.5 (nudge semantics + rate caps), §7.4 (generation fencing), §23.2 (one Work across two repositories/two independent executions). As-built: no `dependsOn` field anywhere (`AssignmentEntity` = id/work/team/capability/status/`CurrentExecutionGeneration`/`ExpectedContextVersion`/timestamps, `Entities.cs:59-84`); steer bumps the **work** version but never `CurrentExecutionGeneration` (`EfCoordinationStore.ApplySteerAsync:123-186`); `ExecutionAttemptEntity.Generation` exists (`:86-99`) unused as a fence; nudge is a known command that settles as a processed no-op on both planes (`LinkIngress.cs:40-42,307`) and the `nudge-policy` config (`staleAfterMinutes`) is stored intent. 25 makes assignment creation kernel-owned, which is the hook point for DAG-gated dispatch.

## Known facts (as-verified 2026-10-29; code wins, divergence goes in the status table)
- **State machines (02):** explicit guard table, `workVersion` optimistic fencing (`BumpVersionAsync`, `EfCoordinationStore.cs:152-160`), steer Conflict/Duplicate/Terminal arms (`ICoordinationStore.cs:41-57,73-74`).
- **Manifest validation hook (15):** manifests have `resultState` incl. `validated` + admin re-mark; `result_validated` (§4.2 condition) has a real trigger — the manifest record/validated transition, outbox-effectable.
- **Approval hook (11):** approvals ledger with decided outbox effects — the `approved` condition has a real trigger.
- **Generation plumbing exists unused:** gateway child wire carries `connectionGeneration` (`src/gateway/src/child.ts:33-36`); attempts carry `Generation`; nothing bumps the assignment's current generation.
- **Nudge schema/fixture frozen (01):** `command.work.nudge` known-command table (`src/protocol/src/validate.ts:53-60,100-103`); `nudge-policy{staleAfterMinutes∈1..1440}` config exists un-read.
- **Routing (25 as-built):** assignment + `work.assign` outbox row created by `RoutingProcessor` — the single dispatch funnel a DAG gate can hold shut.
- **Verify interim:** 19's worker-restart leg asserts the honest-failure law (failed → operator re-dispatch) precisely because auto-resume/multi-work orchestration doesn't exist; 28's provisioning rewrites that expectation once both land — this plan alone changes steer/DAG, the honest-failure law stands until 28.

## Locked decisions
1. **DAG = assignment columns + pure kernel evaluation.** Additive migration: `assignments.depends_on` jsonb (`[{assignmentId, condition∈completed|result_validated|approved}]`, RFC 8785-canonical for digests) set at creation (Work-create request gains optional per-assignment deps). A `DispatchGate` step (in 25's routing processor / dispatcher pipeline) suppresses the `work.assign` outbox write until every condition is met; condition satisfaction arrives as outbox effects (assignment completed, manifest validated, approval decided) that re-trigger routing for gated siblings. Evaluation is deterministic, no AI (§4.2); cycle + missing-dependency rejected at create with typed ProblemDetails.
2. **Two-repo §23.2 shape:** Work create accepts N assignment intents (capability + repo context payload + deps); the coding example (24) + a second deterministic leg prove two independent executions on one Work; context flows via artifacts/manifests + memory (16), no new seam.
3. **Steer = execution generation bump with old-generation fencing.** `ApplySteerAsync` additionally bumps `assignments.CurrentExecutionGeneration` for steered live assignments; every child-wire message and receipt must carry the generation the assignment held at delivery; a stale-generation receipt/command is rejected typed (`stale-generation`) and re-delivered per link law (7.4). The gateway/child already transport a generation field — pin the semantics to the **assignment** generation (step 0 reconciles `connectionGeneration`'s current meaning; rename/additive field if it conflates transport vs assignment generations).
4. **Nudge becomes behavior:** a `NudgeProcessor` (dispatcher sweep, interval from `nudge-policy`) finds live assignments with no progress envelope for `staleAfterMinutes`; sends `command.work.nudge` on the link plane to the owning endpoint; per-assignment rate cap (max 3 nudges, then stalled state + admin visibility; §23.5). Stalled-vs-offline reconciliation: offline endpoint ⇒ no nudge, link-state law stands (6.3). Nudge consumes the 25-recorded routing state; counters + audit follow existing patterns.
5. **No auto-resume yet:** a dead worker still surfaces as honest `assignment.failed` + operator re-dispatch until 28 provides provisioning; this plan's steering test proves a **live** re-hydration (steer → new generation on the running child) only.
6. **Admin surface:** assignments view gains dependency lanes + generation + nudge/stalled state (additive DTO fields; SPA renders the lanes if cheap, else status text — inspector correctness over polish).

## Steps
0. **Research (mandatory, no code).** Spec §4.2/§6.4/§7.4/§23.2/§23.5; `EfCoordinationStore` transition paths + outbox-effect catalog; 25's routing processor funnel; gateway child-generation wire as-built (`child.ts`) + SDK receipt validation; manifest-validated + approval-decided effect names.
1. **Migration + create-path DAG:** columns, canonicalization, create-time validation (cycles, unknown deps), ProblemDetails codes; xUnit create-path suite.
2. **DispatchGate + condition effects:** suppression + re-trigger on completed/result_validated/approved; DAG evaluation tests: blocked-until-validated, diamond, cycle-reject.
3. **Generation fencing:** steer bump + wire semantics + stale rejection across kernel/gateway/SDK (three-runtime change — enumerate consumers via LSP references); tests: steer-during-run fences the old generation's late receipt, re-delivery at new generation completes.
4. **Nudge loop:** processor + policy read + caps + stalled state + admin fields; tests: silence ⇒ nudge (fake clock), cap ⇒ stalled, offline ⇒ no-nudge.
5. **§23.2 live proof:** full stack, two-assignment Work (coding + research legs, one gated on `result_validated`) completes with both outputs; steer mid-run updates the live coding PR at generation 2; nudge fires against a scripted silent child. Local gate script extends `provider-e2e`.
6. **Docs + skills:** §4.2 as-built section in docs, nudge law, admin DTO notes, skills (architecture + protocol), getting-started unaffected.

## State after
Assignments carry typed dependencies the kernel gates deterministically; steering a live assignment fences its old execution generation; nudge is a capped liveness prompt, not a no-op. §26.1 items 10 + 13 basis lands. §23.2 runs.

## Testing
xUnit: DAG matrix, generation race, nudge clock matrix; live two-repo leg; slim verify 11/11 (worker-restart leg unchanged until 28).

## Documentation
DAG + nudge + generation sections; skills same-change.

## Architecture fit
§4.2/§7.4/§23.2 — kernel-first coordination: DAG as rows + deterministic evaluation, fencing as data. The dispatch funnel from 25 stays the single choke point.

## Additional resources
Spec §4.2/§6.4/§7.4/§18 (read for 28), §23.2/§23.5.

## Critical files & anchors
- `Entities.cs:59-99` — assignment/attempt rows the migration widens.
- `EfCoordinationStore.cs:123-186` — steer path gaining the generation bump.
- 25's routing processor — the gate host.
- `src/gateway/src/child.ts:33-36` — generation wire to pin.

## Verification
build/lint/test/docs-check; new suites; live two-repo + steer-fencing transcripts; slim verify unchanged.

## Assumptions & contingencies
- If `connectionGeneration` already means assignment generation, this is semantics-pinning + tests, not a wire change (verify at step 0 before touching three runtimes).
- If per-condition re-trigger storms appear (diamond DAG), coalesce re-evaluation per Work per effect-batch — keep it in the dispatcher loop, no new queue.
- If nudge sweeps contend across future replicas, the sweep rides 29's replica story (lease-guard like the dispatcher); note, don't pre-build.
- **Replanning or optimization may be required before implementation begins.**

## Implementation Status
| Step | Status | Notes |
|---|---|---|
| 0 Research | Not started | |
| 1 Migration + create DAG | Not started | |
| 2 DispatchGate | Not started | |
| 3 Generation fencing | Not started | |
| 4 Nudge loop | Not started | |
| 5 §23.2 live proof | Not started | |
| 6 Docs + skills | Not started | |

Status vocabulary: Not started → In progress → Partially implemented → Fully implemented, or Blocked (with reason). Record deviations from locked decisions with a one-line reason.
