# Plan 29 — Horizontal replicas: two Facilitators, one Postgres

**Status:** ♻️ superseded by plan 35 (Phase D of `35-production-shape-milestone.md`; its locked decisions carry over there)
**Depends on:** 28 as-built (provisioning processor adds a second background loop that must survive the replica audit). **Phase F leg 8 of 9.**
**On approval:** this file is the working detail design; keep the Implementation Status table current.
**Granularity:** single plan — a correctness audit + topology + fault-injection tests; the doc set it starts continues in 30.
**Questions may be asked during planning, but not during implementation** — decisions are locked below; when reality diverges, infer from code/spec per the contingencies section and prefer the simpler robust option.

## Context
Spec §7's central claim — "connections are transient delivery optimizations, the database is the coordination authority" — is currently true by construction but tested only single-replica. As-built the plumbing is deliberately replica-ready: `Kernel:ReplicaId` identity (`KernelOptions.cs:10-15`) stamped on outbox leases (`DbEventDispatcher.cs:62,70,110`) and `link_sessions` (`Entities.cs:245-256`, upsert with epoch bump `LinkService.cs:46-73`); `LinkConnectionRegistry` is explicitly per-replica ("one live hub connection on THIS replica", `LinkConnectionRegistry.cs:5-9`); the delivery pump claims only for locally-socketed endpoints (`LinkDeliveryPump.cs:40-62,157-161`); LISTEN/NOTIFY exists but only on the outbox channel, hint-only with 2 s poll fallback (`PgWakeListener.cs:8-13,61-62`, `LinkDeliveryPump.cs:10-13`). No backplane, no affinity config, no two-replica test. This plan runs the singleton-assumption audit, boots a second replica in a compose overlay, and proves the crash/duplicate/race scenarios (slice-9 exit, §26.1 item 4).

## Known facts (as-verified 2026-10-29; code wins, divergence goes in the status table)
- **Lease/epoch law exists:** outbox lease with replicaId + attempt/nextAttemptAt + dead-letter; session lease/epoch + sweep + force-goodbye (`LinkDeliveryPump.cs:167-178`); expired-lease handover is implemented but single-process-tested only.
- **Inbox dedupe is DB-enforced:** unique `(event_source, event_id)` + `command_id` + provider pair (`InboxEntityConfiguration.cs:33-37`) — the concurrent-webhook race has a correctness floor, unproven under a 2-way race.
- **No in-process cache inventory exists:** audit required (see steps) — candidates by inspection: `LinkConnectionRegistry` (intended per-replica), metrics gauges' 5 s cache (benign), `AdminReadStores` roll-ups (read-only), config cache (If-Match law protects writes).
- **Topology:** compose slim has one `facilitator` service; `Kernel:ReplicaId` configurable (`Kernel:ReplicaId`, fallback machine-name/GUID — two containers on one image without distinct ids collide today: overlay MUST set distinct `Kernel__ReplicaId`).
- **SignalR:** no backplane; spec's own guidance (`architecture.md:359`): WebSockets-only with `skipNegotiation` behind LB **or** sticky sessions; appendix §5.6 (proposed) says the same. Cross-replica session handoff rides the DB: sessions are rows; a reconnect bound to replica B works because routing/pump key on session rows + local sockets.
- **Gateway/linkd connect outbound to one kernel URL** (env `OFAC_FACILITATOR_URL`): multi-replica needs an LB in front (or documented reconnect-to-other-replica); LB choice must be image-light (nginx/alpine, config in repo).
- **27/28 loops:** nudge sweep + provisioning processor — both must be lease/dedupe-guarded to run on every replica (the 27 note deferred exactly this to 29).

## Locked decisions
1. **Audit first, fix what it finds, change no architecture.** Every background loop must be safe N-replicate: dispatcher (already lease-based), link pump (already endpoint-scoped), nudge sweep + provisioning processor get the dispatcher's lease pattern (FOR UPDATE SKIP LOCKED claim per sweep). In-process caches allowed only if staleness-bounded and write-guarded by row version. Findings list goes in the status table; fixes ride this plan.
2. **Topology: `compose.replicas.yaml` overlay** (slim + overlay): second `facilitator-b` service (distinct `Kernel__ReplicaId`), nginx LB (`facilitator-lb`) with sticky `ip_hash` + `/hubs/link` WebSocket upgrade config, per §22's "one tested method". `referencectl up slim --replicas 2` selects the overlay; acceptance default stays single-replica.
3. **Fault injection = docker kill -9 + iptables-free method:** SIGKILL via `docker kill` (already 28's tool) + compose stop for graceful; no toxiproxy — Postgres-level and process-level injection suffice for §26.1 item 4's claims.
4. **Test matrix (live, local gate — free-tier law):**
   a. Concurrent duplicate webhook (two parallel POSTs at the LB) ⇒ exactly one business command, one consumed inbox row, loser gets `duplicate` receipt/shape per 02's law.
   b. SIGKILL replica A owning a live link session ⇒ lease expiry ⇒ SDK reconnect (its own retry law) lands on B ⇒ deliveries resume, epoch bumps, no double-delivery of acked items.
   c. Outbox lease handover: A leases rows, SIGKILL before ack ⇒ B re-leases after lease expiry ⇒ exactly-once sink effect (attempt count visible).
   d. workVersion race: parallel steers at LB hit different replicas ⇒ one wins, other gets typed conflict-retry per `ICoordinationStore` arms.
   e. Both replicas run all loops simultaneously for a full `verify` run ⇒ no duplicate side effects (dead-letters-empty + exactly-one-manifest assertions hold).
5. **SignalR scale-out decision recorded:** sticky LB (ip_hash) primary + documented `skipNegotiation` alternative; no Redis backplane (connections are transient per §7 — backplane would optimize, not correct). Guidance written to docs, config tested.
6. **Load-shed/backpressure defaults (§7.5):** minimal surface — bounded channels already exist; expose queue-depth gauges via admin metrics (already present) and document thresholds; no new limiter unless the race tests show unbounded growth.

## Steps
0. **Research (mandatory, no code).** Spec §7 fully + appendix §5.6 proposal + §26.1 item 4; inventory every `IHostedService`/periodic loop via DI registration (`KernelServiceCollection.cs`) and audit each for singleton assumptions; SignalR without-backplane semantics on multi-instance (connection affinity docs); nginx `ip_hash` + WS upgrade minimal config.
1. **Audit fixes:** lease-guard nudge + provisioning sweeps; any cache found; replicaId-distinctness enforcement (fail-fast duplicate replicaId against `link_sessions`/leases? — decide per audit, prefer warn+metrics over startup coupling).
2. **Overlay + LB:** `compose.replicas.yaml`, nginx config in `reference/`, `referencectl` flag, ports/env; `status` verb shows both replicas + LB.
3. **Race/fault suite:** script `reference/checks/verify-replicas.sh` (a, b, c, d, e above) — same step-runner law as verify.sh (polls, no wall-clock), invoked `referencectl verify --replicas`.
4. **Two-replica full pass:** slim acceptance suite re-run under `--replicas 2` green; provider-e2e legs re-run through the LB.
5. **Docs:** advanced.md replication + SignalR affinity (write it as operational fact + runbook seed for 30), reference README overlay section, skills.

## State after
Two Facilitators share one Postgres with tested claims: duplicate webhooks dedupe under race, lease handover survives SIGKILL, sessions resume on the survivor, optimistic conflicts resolve typed, and every background loop is N-replica safe. Slice-9 exit passes. §26.1 item 4 lands.

## Testing
The `verify-replicas` matrix IS the test tier (local gate); unit-level lease-guard tests where injectable; slim single-replica verify 11/11 regression mandatory.

## Documentation
Replication + affinity ops docs (runbook seeds), reference README, skills.

## Architecture fit
§7 — the claim becomes a tested claim with zero new coordination machinery (Postgres stays the only authority).

## Additional resources
Spec §7, appendix §5.6/§9; SignalR scale-out + nginx stream/http proxy docs; Postgres SKIP LOCKED docs.

## Critical files & anchors
- `KernelServiceCollection.cs` — the loop inventory to audit.
- `PgWakeListener.cs` + `LinkDeliveryPump.cs:40-62,157-178` — per-replica semantics to prove.
- `KernelOptions.cs:10-15` — replica identity wiring the overlay must exercise.
- `reference/compose.slim.yaml` + `referencectl` — topology host.

## Verification
build/lint/test/docs-check; `referencectl verify --replicas` full matrix green on clean full-stack boot; single-replica verify unchanged; transcripts in status table.

## Assumptions & contingencies
- If `ip_hash` proves insufficient for SignalR's negotiate/WS two-phase (negotiate lands on a different replica), switch that location block to cookie affinity (`$cookie_...` via nginx sticky module or hash on the connection token) — still no backplane.
- If the audit finds a loop that can't be lease-guarded cheaply, designate it a singleton-by-config role (`Kernel:Roles:Scheduler=true` on replica A only) and record — honest asymmetry over silent duplication.
- If two arm64 kernel containers + LB starve the dev host, matrix legs run sequentially (runner already dep-walks).
- **Replanning or optimization may be required before implementation begins.**

## Implementation Status
| Step | Status | Notes |
|---|---|---|
| 0 Research + loop audit | Not started | |
| 1 Audit fixes | Not started | |
| 2 Overlay + LB | Not started | |
| 3 Race/fault suite | Not started | |
| 4 Two-replica full pass | Not started | |
| 5 Docs | Not started | |

Status vocabulary: Not started → In progress → Partially implemented → Fully implemented, or Blocked (with reason). Record deviations from locked decisions with a one-line reason.
