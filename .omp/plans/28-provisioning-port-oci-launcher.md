# Plan 28 — Provisioning port + local OCI launcher: workers scale zero-to-N

**Status:** ⬜ not-started (flips 🔵 at the replanning gate, then 🟡)
**Depends on:** 27 as-built (generation semantics + nudge/stalled states are the launcher's signals). **Phase F leg 7 of 9.**
**On approval:** this file is the working detail design; keep the Implementation Status table current.
**Granularity:** one port + one adapter (local OCI/docker launcher). A second provisioning backend (k8s etc.) is out of scope by §18's own admission — seam + one implementation + tests.
**Questions may be asked during planning, but not during implementation** — decisions are locked below; when reality diverges, infer from code/spec per the contingencies section and prefer the simpler robust option.

## Context
Spec §18 (`docs/specs/open_facilitator_framework_architecture.md:1297`, "Workspace provisioning, retention and scale-to-zero"): the kernel asks a provisioner to bring workloads up/down; `provisioningOperationId` idempotency per §7.4. As-built there is **zero** provisioning code (`grep -i provisioning` over the kernel: one unrelated retention comment, `Entities.cs:562`), workloads are compose-fixed, and 19's verify worker-restart leg encodes today's honest reality: killed worker ⇒ `assignment.failed` ⇒ operator re-dispatch. 27 landed DAG/generation/nudge; this plan turns worker death from an operator event into a provisioned re-launch with context continuity (artifacts + memory re-hydration, 24's lineage path), flips the `workload-scale` config from stored intent to behavior, and retires the honest-failure expectation in verify.

## Known facts (as-verified 2026-10-29; code wins, divergence goes in the status table)
- **Zero provisioning state:** no port, no table, no config read. `workload-scale{minWorkers∈0..8,maxWorkers∈1..32,idleShutdownMinutes∈1..1440,min<=max}` exists as stored intent (`AdminConfigStore.cs:76,220-222` region).
- **Signals available:** nudge/stalled states + caps (27 as-built), link session liveness (`link_sessions` lease/epoch), assignment generations (27), `KernelMetrics` gauges pattern.
- **Worker lifecycle as-built:** deterministic worker + coding example connect outbound (enroll → link → receive); a relaunched process with the same enrollment re-binds by team/endpoint; 24's re-dispatch leg proves context re-hydration from branch + manifest lineage; 16's memory recall completes the picture.
- **Compose topology:** demo/coding workspaces are compose services (`compose.full.yaml`, node image command override); a launcher inside compose that starts/stops *other* compose services is fragile — the launcher therefore manages **`docker run` containers detached from the compose project** (own network attachment via `--network facilitator-demo_default` equivalent + env file), not compose lifecycle.
- **Verify law to change:** `reference/checks/verify.sh` worker-restart step asserts 1 retryable failure + operator re-dispatch (`19_SLIM_ACCEPTANCE_VERIFY_CI_PLAN.md` step 3 note (b)); 28 replaces it with auto-relaunch-expectation once the launcher is in the slim stack (slim gains the launcher service).
- **§7.4 idempotency:** `provisioningOperationId` named in spec as the idempotency key for provision operations.

## Locked decisions
1. **Port shape = outbox commands, adapter = external process.** Kernel gains a `provisioning` outbox channel: `provision.start` / `provision.stop` envelopes (additive schemas, fixtures) with `provisioningOperationId` = deterministic hash(workId, assignmentId, generation, op) — replay-safe per §7.4. A new **`src/facilitator/Kernel/Provisioning/ProvisioningProcessor`** decides (scale-up on unroutable-for-lack-of-capacity or explicit `minWorkers` shortfall; scale-down on `idleShutdownMinutes` with no live assignment) and records `facilitator.provisioning_operations` rows (state machine: requested→running→stopped, unique op id).
2. **Launcher adapter = `src/adapters/provisioning-oci` (TS sidecar, node image):** consumes provisioning commands via the link plane (same pattern as 22's adapter-as-link-client, capability-gated, team-side credentials), executes `docker run/stop/rm` against the mounted socket, reconciles by container label `ofac.workload=<id>` on restart (read-before-act, 23's connector law). No kernel docker dependency.
3. **Config activation:** `workload-scale` becomes behavior: min/max per workload-class, idle shutdown; missing row ⇒ manual mode (today's compose-fixed reality, slim keeps it opt-in via seeded config).
4. **Continuity law:** a relaunched worker binds to the **same assignment** at a **new generation** (27's fence makes the old one's stragglers typed-rejected); context = work record + manifest lineage + memory recall. The killed-container test is 29's spec §7 story — here the killed-**launcher-managed** container test belongs: SIGKILL the workload container ⇒ kernel detects lease loss ⇒ provisioner re-launches ⇒ completes with exactly one manifest (supersedes 19's operator-re-dispatch expectation).
5. **Nudge stays separate:** nudge ≠ provision (6.4/23.5); escalation is nudge-capped → stalled → provisioner scales up (only if maxWorkers allows), never the reverse.
6. **Retention (§18 half):** stopped-workload containers removed within `idleShutdownMinutes`-bounded TTL (launcher GC by label); artifact/memory retention stays 14/16's laws.
7. **Scope fence:** one launcher implementation (local OCI/docker). No k8s, no autoscaler cleverness beyond min/max/idle rules.

## Steps
0. **Research (mandatory, no code).** Spec §18 + §7.4 idempotency + §6.4 escalation; docker Engine API vs CLI (socket mount, labels, network attach as-composed); 22's adapter-as-link-client pattern as the launcher's transport; 27's stalled-state + generation as-built; verify.sh worker-restart step internals.
1. **Kernel:** migration (`provisioning_operations`), ProvisioningProcessor decisions + outbox channel, schemas + fixtures, ProblemDetails, metrics counters, admin visibility (deliveries plane + summary lane); xUnit incl. op-id replay idempotency + decision matrix (min/max/idle).
2. **Launcher adapter:** link-client + docker executor + label reconciliation + GC pass; vitest against a docker-API fake; contract fixtures.
3. **Compose:** `provisioning-launcher` slim+full service (socket mount), launcher-managed example workload (coding-workspace or a dedicated scale-demo unit), seeded `workload-scale` config in reference bootstrap.
4. **Continuity + relaunch proof:** kill -9 the launcher-managed workload mid-Work ⇒ auto re-launch on same assignment, new generation, completes, exactly one manifest; idle ⇒ scale to zero ⇒ next Work cold-starts it within budget. Replaces 19's honest-failure step in `verify.sh` (both arches local).
5. **Live proof (local gate):** full provider-e2e re-run + new provisioning legs; transcripts to status table.
6. **Docs + skills:** §18 as-built section, launcher README, runbook seed content for 30 (start the `docs/runbooks/` file with the relaunch drill), skills.

## State after
Workers are cattle: the kernel requests provision ops by idempotent command, a team-side launcher runs/stops containers, a killed mid-flight worker returns on the same assignment with continuity, idle workloads scale to zero and cold-start. §26.1 item 10's provisioning half lands; verify's restart leg asserts auto-resume reality.

## Testing
xUnit decision/idempotency matrix; launcher vitest; live kill-9 + cold-start + scale-to-zero legs (local gate); slim verify 11/11 **with the rewritten worker-restart step**; gateway-e2e regression.

## Documentation
§18 section, launcher README, runbook seed, skills.

## Architecture fit
§18 (provisioning port + one adapter), §7.4 (idempotent ops), §9 (execution authority stays team-side: the launcher holds docker creds, the kernel holds intent).

## Additional resources
Spec §18/§7.4/§6.4; Docker Engine API label/network docs; 22/24/27 as-built.

## Critical files & anchors
- `AdminConfigStore.cs` workload-scale validator — the intent this plan activates.
- 27's generation bump + stalled state — the launcher's inputs.
- `reference/checks/verify.sh` worker-restart step — the law this plan rewrites (in-place, with the 19 record cited in the status table).

## Verification
build/lint/test/docs-check; suites green; kill-9 relaunch transcript; cold-start latency observed + recorded; verify 11/11 with rewritten step.

## Assumptions & contingencies
- If socket-mounted docker in the launcher container proves permission-hostile (WSL2 docker context), run the launcher on host in the local gate (documented) — the port/adapter split stays proven either way.
- If container-name collisions appear across generations, label-only identity (never names).
- If cold-start exceeds verify's poll budgets, the check rides inbox/timeline polls (never wall-clock law) — no special-casing.
- **Replanning or optimization may be required before implementation begins.**

## Implementation Status
| Step | Status | Notes |
|---|---|---|
| 0 Research | Not started | |
| 1 Kernel provisioning | Not started | |
| 2 Launcher adapter | Not started | |
| 3 Compose | Not started | |
| 4 Continuity + relaunch proof | Not started | |
| 5 Live proof | Not started | |
| 6 Docs + skills | Not started | |

Status vocabulary: Not started → In progress → Partially implemented → Fully implemented, or Blocked (with reason). Record deviations from locked decisions with a one-line reason.
