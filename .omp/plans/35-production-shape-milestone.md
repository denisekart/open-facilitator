# Plan 35 (goal) — Production-shape milestone: routing, coordination core, provisioning, replicas, conformance

**Status:** 🔵 planned (supersedes 25, 27, 28, 29, 30 — folded in as Phases A–E below; plan 26 stays open and separate)
**Depends on:** 24 as-built (steer/re-dispatch + AssignRules interim + the 17-step provider-e2e), 21 (admin surface), 34 (provider-neutral wire). **Prerequisite (first task of Phase A):** close plan 24's open live-proof defect — `coding-review-steer` fails (a `COMMENTED` review POST on a resolver-bound PR produces no adapter webhook; suspected Forgejo hook-queue stall on a freshly-resolved PR head; full run 2026-10-30: 15/16 PASS, see 24's status table) — triage and make `coding-review-steer` + `coding-redispatch` green before any 35 phase lands. **Phase F legs 4–9 of 9 as one goal.**
**Questions may be asked during planning, but not during implementation.** Every ambiguity below is resolved with an explicit decision; an implementer must never stop to ask — use the clarifying actions ("inspect X first", "infer from existing code/spec") at each phase head.
**Goal mode:** this file is the mission. "Done" = the Success criteria observable on a clean local machine, not "code merged".

## 1. Goal (one sentence)

Make the framework's §7 claim true at production shape — the kernel routes work itself, coordinates multi-execution DAGs with generation fencing and bounded nudges, scales teams' workers zero→N→zero through a team-side launcher, survives two-replica operation and SIGKILL, and proves all of it with an implementation-agnostic conformance package, executed incident drills, and release hardening — closing spec §10, §4.2/§6.4, §18, §7, §26.1–26.2, §27, §22.6 in one goal run.

## 2. Success criteria (observable, goal-mode acceptance)

1. `./referencectl down -v && ./referencectl up slim --no-seed && ./referencectl verify` → 11/11 from clean, with the worker-restart step asserting **auto-relaunch continuity** (same assignment, new generation, exactly one manifest) — the honest-failure law replaced, not annotated.
2. No hardcoded dispatch addresses remain: `grep -rn "link.gw.team-alpha" reference/bootstrap` finds none; every assign row is produced by the routing funnel; `facilitator.routing_decisions` shows the chosen candidate + allowed set + basis for every Work.
3. A two-repo Work (§23.2) runs two independent executions, gated by `dependsOn` conditions satisfied by outbox effects; a cycle is rejected at create with typed ProblemDetails; a steer during execution 2 bumps generation and a straggler message from generation 1 is rejected `stale-generation` — proven live in provider-e2e (step `two-repo-dependency`).
4. Kill -9 the worker container mid-run → the launcher relaunches it, it re-binds the same assignment at a new generation, completes, and the manifest count stays exactly one. Idle past `idleShutdownMinutes` → scaled to zero; a new Work cold-starts it. (`workload-scale` PATCHed through the admin API, ETag law.)
5. `Kernel__ReplicaId`-distinct two-replica overlay behind a sticky LB: the 5-case race matrix (`verify-replicas.sh`) is green and the whole slim suite passes with two replicas; single-replica default unchanged.
6. `referencectl verify --full` runs every leg (slim 11, full-profile 18, replicas 5 cases, drills) with a JSON report; `Conformance.Runner` passes 15-item matrix (item 14 explicitly `deferred:plan-26`) against this stack AND against a foreign link client built only on 04's public surface.
7. `docs/runbooks/` holds the eight §27 scenes, ≥5 with dated executed-drill sections; `reference/versions.env` `*_DIGEST` pins are non-empty and the CI meta job fails on an empty pin; SBOM job in `release.yml`.
8. All gates green both stacks: `make build && make lint && make test && make docs-check && pnpm -r --if-present typecheck`, zero warnings.

## 3. Architecture spine (invariants that bind every phase)

- **One dispatch choke point:** the routing funnel (`RoutingProcessor` → candidate set → `DispatchGate` → assign outbox row) is the ONLY producer of `work.assign` rows. 24's provider arms become intent submitters. DAG gating, capacity triggers, and the admin re-dispatch verb all mount on this funnel, none bypasses it.
- **DB is the coordination authority** (§7): every new background loop (routing, nudge, provisioning) uses the dispatcher's `FOR UPDATE SKIP LOCKED` + lease pattern from day one — Phase D audits, it does not retrofit.
- **Advisory-only decisions** (§10.2): only the deterministic authorization evaluator may allow/deny. `IDecisionProvider` returns preferences over a kernel-computed allowed set; out-of-set answers fall back deterministically, audited.
- **Idempotency law** (§7.4): unique `(assignmentId, commandId)`; `provisioningOperationId = hash(workId, assignmentId, generation, op)`; only the current execution generation may finalize.
- **Envelope read-back law (24):** the outbox row that carried an assign is the assignment→endpoint ledger; command-id prefixes `work-assign:`, `work-assign-coding:`, `work-redispatch:` are load-bearing for steer re-delivery and SURVIVE the routing cutover (the routing intent carries the caller's command-id base).
- **Free-tier law:** every live leg is the documented LOCAL gate; CI keeps unit/contract tiers only.
- **Plan-prose law:** no plan-number references in shipped code/docs (30's scrub applies to everything THIS plan writes, immediately — write phase names, not plan numbers, in code comments).

## 4. Phase A — Kernel routing + decision seam (was 25)

Clarifying action first: read `Kernel/Shared/Dispatch/DbEventDispatcher.cs` + `LinkSink` (requeue/backoff shape), `LinkEnrollmentStore.cs:152` (capability manifests + digest), `AdminConfigStore` validators, and the seed dispatch primitives (`reference/bootstrap/seed-lib.sh:93-138`, `bootstrap.sh:25,36`) before writing code.

1. `Kernel/Work/RoutingProcessor.cs`: consumes `work.routing.requested` outbox effects (sibling processor to the dispatcher's sinks). Candidates = live `link_sessions` whose stored capability manifest advertises the capability, filtered to the intent's team (cross-team exclusion), ordered deterministically: capability specificity → oldest-online → endpointId. Emits the assignment row + `work.assign` outbox row + timeline event + one `facilitator.routing_decisions` audit row (allowed set, chosen, basis, provider ref, null for deterministic).
2. **24's AssignRules become routing hints (minimal-deviation decision):** a matched rule contributes `{team, capabilityId, inputs, commandIdBase}` to the intent instead of a pinned endpoint. `AssignRule.EndpointId` survives as an optional preference (validated to a live session, audited `preference-satisfied|preference-ignored`); no rule match keeps today's global pair as an intent for the default capability. Rationale: removes a second addressing mechanism while keeping 24's re-dispatch read-back and provider label policy intact; §10's team-scope filter consumes the rule's Team.
3. `IDecisionProvider` (port, `Kernel/Shared/`): `EvaluateAsync(kind, facts, allowedCandidates, ct)` — kinds ContextResolution/Routing/Communication/Authorization; ships with the `DeterministicDecisionProvider` only. `routing-defaults` validator extended to `{provider: "deterministic"|"model"|"", preferTeam?}`: `"model"` is stored-accepted, falls back deterministic with audit flag (seam reserved for 26's Ollama impl, no config-schema change later). SPA `config-shapes.ts` mirror updated.
4. Ambiguity law (§10.1 step 4–5): >1 equally-ranked candidates ⇒ decision provider over the candidate list only; no provider ⇒ deterministic order stands. Low confidence ⇒ clarification note on the Work, never speculative dispatch.
5. No-route law: requeue with backoff (LinkSink pattern), `lastRoutingAttemptAt` + reason (`no-candidate` vs `no-capacity`, distinguished by an advertised-slot check) on the routing-decision row — Phase C's scale-up trigger reads exactly this.
6. Seed cutover (clean, no shim): `seed_work` posts the capability via `POST /api/works` (+ assignment intents, see B.1 shape — land the endpoint additive now, DAG fields empty); delete the raw-SQL assign insert + `link.gw.team-alpha` literals.
7. xUnit: single-candidate, two-candidate deterministic tie, no-candidate requeue, cross-team exclusion, preference-satisfied/ignored, `"model"` fallback audit, duplicate `work.routing.requested` idempotent (unique `(workId, intentCommandId)`).

Exit: slim verify 11/11 (steps rewritten for routing-created assignments), provider-e2e 17/17, grep of criterion 2.

## 5. Phase B — dependsOn DAG, generation fencing, nudge (was 27)

Inspect first: 24's `AppendSteerDeliveryAsync` (`EfCoordinationStore.cs:237-294`) — the generation bump lands inside this state machine, not beside it; the gateway's existing `connectionGeneration` pin (`src/gateway/src/child.ts:33-36`); enumerate wire carriers via LSP refs across kernel/gateway/SDK (three-runtime change).

1. Migration 1 (additive): `assignments.depends_on jsonb` = `[{assignmentId, condition ∈ completed|result_validated|approved}]`, RFC 8785-canonical. Cycle/unknown-dep ⇒ typed ProblemDetails at create.
2. `DispatchGate` in the funnel: a candidate assign is suppressed while deps unmet; satisfaction arrives as the existing outbox effects (completed / manifest-validated / approval-decided) re-triggering gated siblings deterministically. xUnit: matrix, diamond, blocked-until-validated, cycle-reject.
3. §23.2 shape: Work-create accepts N assignment intents (capability, repo context, deps) → N independent executions on one Work; context passes via artifacts/manifests/memory (16) only, no new seam.
4. Generation fencing (§7.4): steer bumps `assignments.CurrentExecutionGeneration`; every wire message/receipt carries it; stale ⇒ typed `stale-generation` + re-delivery per link law; only-current-generation finalizes. Re-dispatch ⇒ next generation. Gateway pin semantics align to assignment generation.
5. `NudgeProcessor` (§6.4): dispatcher sweep, `nudge-policy.staleAfterMinutes` (missing row ⇒ no nudge); silence ⇒ `command.work.nudge` to the owning endpoint; cap 3 ⇒ `stalled` + admin visibility; offline endpoint ⇒ no nudge; dedupe per `(assignmentId, generation, stallEpisode)`. Nudge NEVER re-runs tools or escalates authority.
6. Law preserved here: no auto-resume yet — dead worker ⇒ honest `assignment.failed` + operator re-dispatch (Phase C flips this; keep the slim step asserting exactly that until then).
7. Admin DTOs additive: dependency lanes, generation, nudge/stalled fields.
8. Live leg: provider-e2e gains step `two-repo-dependency` (§23.2 + steer at generation 2) → full profile = 18 steps; update step-count prose ONCE here (compose-full echo, getting-started, skill).

Exit: xUnit green (DAG matrix, race, fake-clock nudge matrix), 18/18 provider-e2e, slim 11/11.

## 6. Phase C — Provisioning port + local OCI launcher (was 28)

Inspect first: §18 spec lines 1297–1320, 24's unit-script worker (the thing the launcher must be able to start: node-runtime image + config mounts), `AdminConfigStore` `workload-scale` resource, WSL2 docker-socket reality (contingency below).

1. Migration 2 (additive): `facilitator.provisioning_operations` (requested→running→stopped, unique `provisioningOperationId = hash(workId, assignmentId, generation, op)`, replay-safe).
2. `Kernel/Provisioning/ProvisioningProcessor.cs` (lease-guarded from day one): scale-up on routing's `no-capacity` state or `minWorkers` shortfall; scale-down on `idleShutdownMinutes` with no live assignment; escalation order nudge-capped → stalled → scale-up (never reverse); missing `workload-scale` row ⇒ manual mode (slim stays manual; seeded config opts in). Envelopes `org.facilitator.command.provision.start|stop.v1` (additive schemas + fixtures + ProblemDetails + metrics + admin visibility).
3. Launcher `src/adapters/provisioning-oci` (TS, link-plane client like 22's adapter, capability-gated): `docker run/stop/rm` on mounted socket; reconcile-by-label `ofac.workload=<id>` read-before-act; manages its OWN detached containers, never the compose project; GC stops orphaned stopped containers within an idle-bounded TTL. vitest against a docker-API fake; Dockerfile.node deploy filter.
4. Continuity law (the flip): relaunched worker binds the SAME assignment at a NEW generation; 27's fence rejects stragglers; context = Work record + manifest lineage + memory recall. Rewrite slim `worker-restart-resume`: kill -9 ⇒ auto-relaunch ⇒ exactly one manifest rev 1. (Step id stays; assertion inverts.)
5. Compose: `provisioning-launcher` service (slim+full, socket mount); launcher-managed worker image = the runtime config already used by the demo worker units. **Contingency (decided up front):** if the WSL2 socket mount is untestable, run the launcher on the host with `DOCKER_HOST`; the compose service stays the documented production shape.
6. Live legs: kill-9 continuity, idle→zero→cold-start (provider-e2e steps `provision-kill9`, `provision-cold-start` → 20 steps total).

Exit: xUnit decision matrix + op-id replay idempotency, vitest launcher, slim 11/11 with inverted step, 20/20 provider-e2e, gateway-e2e regression.

## 7. Phase D — Horizontal replicas (was 29)

Audit first (no architecture change unless the audit demands it): every loop must be N-replicate-safe — dispatcher ✓ (lease), link pump ✓ (endpoint-scoped), routing/nudge/provisioning processors ⇒ SKIP-LOCKED lease guards (built in A/B/C; D verifies + fixes). Caches only if staleness-bounded + row-version-guarded. Contingency: a loop proven un-guardable becomes singleton-by-config (`Kernel:Roles:Scheduler`), documented as honest asymmetry.

1. `reference/compose.replicas.yaml` overlay: `facilitator-b` with distinct `Kernel__ReplicaId` (make distinctness fail-closed at startup when `ReplicaId` collides with a live session lease), nginx `facilitator-lb` sticky `ip_hash` + `/hubs/link` WS upgrade headers; `referencectl up slim --replicas 2`; default stays single.
2. `reference/checks/verify-replicas.sh` 5-case matrix: (a) concurrent duplicate webhook ⇒ one command, loser `duplicate`; (b) SIGKILL the replica owning a live session ⇒ lease expiry ⇒ reconnect lands elsewhere, epoch bump, no double-delivery; (c) outbox lease handover ⇒ exactly-once sink effect; (d) parallel steers across replicas ⇒ one wins, other typed conflict-retry; (e) full slim suite against the LB with both replicas running all loops ⇒ no duplicate side effects.
3. SignalR: sticky LB primary; `skipNegotiation` documented; NO Redis backplane (§7.3). §7.5: publish queue-depth gauges, document thresholds, no speculative limiter.
4. `referencectl verify --replicas` + `status` verb showing both replicas' leases.

Exit: matrix green on clean boot; single-replica 11/11 regression; provider-e2e through the LB.

## 8. Phase E — Conformance, runbooks, production entry (was 30)

Gap-first law: step 0 = the §26.1 15-item × proof matrix written to `protocol/conformance-fixtures/COVERAGE.md` (item → existing proof → gap → assignment to fixture/step/leg/drill). **Item 14 (Kafka at-least-once) is recorded `deferred:plan-26`, not faked.** If the gap list comes back large, split THIS phase internally (E1 conformance CLI + gap closure, E2 runbooks + drills + SBOM) — the only split allowed inside the goal, decided at step 0, recorded in the status table.

1. `protocol/tools/Conformance.Runner` (.NET, config-driven: base URL/creds/roles): fixture suite + behavioral scenarios (handshake, replay, dedupe, approval roundtrip, v2 negotiation) against ANY conformant kernel; JSON report `{item, scenario, pass|fail|deferred, evidence}`. First victims: this stack + a foreign-shape link client built only on 04's public surface (lives under `protocol/tools/` as a test artifact, honest stub-kernel transport).
2. v2 negotiation (§5.5): simulated major-mismatch hello ⇒ typed rejection; fixture + live leg.
3. `referencectl verify --full` = slim 11 + full-profile 20 + replicas 5 + drills, one JSON report. Free-tier law unchanged; `.github/workflows/README.md` honest table.
4. Runbooks §27: eight scenes seeded from B/C/D notes; ≥5 drills EXECUTED on the full stack with dated sections: DB restart mid-drain, provider outage, invalid-token replay, replica SIGKILL (reuse D's matrix transcript), dead-letter storm + replay. Symptom → diagnosis query → action → drill date.
5. Release hardening (§22.6): CycloneDX SBOM (.NET + npm) job in `release.yml`; digests written into `versions.env` `*_DIGEST`; meta job flips to enforcing non-empty pins; compose bring-up verifies digests.
6. Prose scrub: strip plan-number/phase references outside `.omp/` + `docs/specs/` (known rot: `Entities.cs:365` "Plan 13 behaviour", `docs/protocol.md:125`, `docs/security.md:104`+`:381` deferred table, `getting-started.md:130`, `verify.sh:156`); §26.2 checklist mirrored in the status table; skills sweep + roadmap Phase F complete.

## 9. Resolved conflicts (decisions, not options)

| # | Conflict | Decision |
|---|---|---|
| 1 | Three writers to the steer/dispatch state machine (24 live, B fencing, C relaunch) | ONE state machine in `EfCoordinationStore` + funnel: bump generation ONLY in the steer/re-dispatch/relaunch transitions; single xUnit matrix owns all three legs |
| 2 | 25 kills seed SQL vs 24's AssignRules interim | Rules survive as routing HINTS (team/capability/inputs/commandIdBase); endpoint preference optional + audited; prefixes preserved (§3 law) |
| 3 | `routing-defaults.provider="model"` has no impl without 26 | Stored-accepted, deterministic fallback + audit flag; 26 registers an impl with zero schema change |
| 4 | Three admin-config resources flip intent→behavior | One PR-shaped change each phase; shared If-Match PATCH/audit law; SPA mirror rides same commit |
| 5 | Migration ordering | Two additive migrations, ordered: `depends_on` (B) then `provisioning_operations` (C); one each, `Configurations/` pattern |
| 6 | Step-count prose churn (C7 precedent) | Updated exactly once per phase that changes a suite, never speculatively |
| 7 | 27's nudge + 28's provisioning defer replica-safety to 29 | Lease guards are BUILT IN B/C; D only audits — no retrofit phase |
| 8 | C flips the honest-failure law 19/27 pin | B keeps the current assertion; C rewrites the step in place (id stable, assertion inverts) |
| 9 | Plan 26 excluded | Explicit out-of-scope: §26.1 item 14 `deferred:plan-26`; slice-7 exit partially claimed (Kafka/Ollama legs stay open); roadmap keeps 26's slot; 30's dependency "21, 26, 29" becomes 35's "21, 29 + internal A–D" |

## 10. Execution order + gates

Strictly A → B → C → D → E (each phase's Exit line is its gate; a phase may not start while the prior Exit is unmet). Per-phase gate set: `make build && make lint && make test && make docs-check && pnpm -r --if-present typecheck` (zero warnings), then the phase's live legs locally. Commit per phase with its docs/skills rides.

**Problem-solving discipline (mandated):** on a failure, first try straightforward fixes consistent with this file. After three failed iterations on the same sub-problem, stop and re-evaluate that component's design for unnecessary complexity (spec's simple-solution preference wins over clever machinery); record the re-evaluation in the status table notes rather than silently rewriting the phase. Prefer clear, composable, well-bounded components: each processor is one file, one loop, one table-set.

**Internet resources:** where a package/protocol best practice is uncertain (CycloneDX SBOM tooling, nginx sticky-session config, docker HTTP API versioning, SignalR LB docs), check current docs online before inventing config — but never introduce a new dependency for a problem an existing pinned image solves.

## 11. Out of scope (fences)

Plan 26 (Kafka adapter, Ollama provider, research-worker example); k8s or non-OCI provisioners (§18's own admission); autoscaler heuristics beyond min/max/idle; Redis backplane; Temporal/IWorkCoordinator; anything Phase G.

## 12. Implementation Status

| Phase | Status | Notes |
|---|---|---|
| 0 §26.1 gap matrix (Phase E step 0 may run early, read-only) | Not started | |
| A Kernel routing + decision seam | Not started | |
| B DAG + generation + nudge | Not started | |
| C Provisioning + OCI launcher | Not started | |
| D Horizontal replicas | Not started | |
| E Conformance + runbooks + release | Not started | |

Status vocabulary: Not started → In progress → Partially implemented → Fully implemented, or Blocked (with reason). Update on every landed step; record deviations from this file's decisions with a one-line reason.
