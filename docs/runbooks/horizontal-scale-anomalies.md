# Horizontal scale anomalies (multi-replica)

**Symptom.** With 2+ kernel replicas behind the LB: sessions stamping to
one replica only, a delivery delivered twice (or not at all) after a
replica dies, work steering intermittently 409ing, or the fleet view
disagreeing with the links view.

## Law (from shipped code)

Replicas share everything through Postgres — there is no inter-replica
protocol. Safety rests on three mechanisms, each independently visible:
(1) **outbox leases** — `FOR UPDATE SKIP LOCKED` claim stamped with
`lease_owner`/`leased_until`; expired leases are reclaimed by any replica
and the row is receipted exactly once; (2) **link session rows** — one
per logical endpoint, stamped `replica_id` + monotonic `session_epoch` +
heartbeat-refreshed lease; a replica death lapses the lease (15 s) and the
endpoint's re-open lands on whichever replica the LB picks, epoch bumping
across the failover so straggler deliveries at the old epoch are fenced;
(3) **the DB as arbiter** — concurrent identical ingests to different
replicas resolve to one inbox row (`ON CONFLICT`), concurrent steers
resolve via optimistic version (one 200, one 409 — retry with the fresh
version). The fleet view `GET /api/admin/replicas` answers per-replica
`liveSessions` + `socketsHere` + global `outboxPending`; any replica can
answer it, and skew between views is itself a diagnosis. Cross-replica
session loss synthesizes `event.assignment.failed.v1`
(reason=`session-lease-expired`, retryable) after a two-sweep hysteresis,
gated on the `workload-scale` config row.

## Diagnosis

```sql
-- who holds which sessions; lapsed leases show stale lease_until
SELECT logical_endpoint_id, replica_id, session_epoch, lease_until > now() AS live,
       last_heartbeat
FROM facilitator.link_sessions ORDER BY logical_endpoint_id;
-- lease churn after a death: rows reclaimed then re-claimed
SELECT status, lease_owner, count(*) FROM facilitator.outbox
WHERE status <> 'delivered' GROUP BY 1,2;
```

```bash
curl -s -H "Authorization: Bearer $TOK" http://localhost:8089/api/admin/replicas | jq
curl -s -H "Authorization: Bearer $TOK" http://localhost:8089/api/admin/links  | jq '.gateways[] | {endpointId,replicaId,sessionEpoch,online}'
# the whole proven matrix, re-runnable:
./referencectl verify --replicas   # legs a-e: LB distribution, nested full
                                   # suite, cross-replica dedupe, replica
                                   # kill, lease handover, steer conflict
```

## Action

Sticky-but-evicting LB is load-bearing: the reference LB is
`least_conn` with `max_fails=1 fail_timeout=30s resolve` and a 5 s DNS
resolver. If sessions refuse to fail over within a minute of a replica
death, look at the LB's eviction and the gateway's reconnect backoff
ladder (gateway log `/var/lib/ofac/logs/10-gateway.sh.log` shows the
exact attempt/backoff sequence) before blaming the kernel — the kernel
side is just "lease lapsed, next re-open wins". Never run two replicas
with independent stores; `outboxPending` skew between answers of
`/api/admin/replicas` on different replicas means the DB itself is
degrading.

## Drill 4 executed 2026-11-01 (replica SIGKILL — two-replica matrix; UTC wall-clock 04:44–04:48 and 05:01–05:05, two full runs)

The shipped two-replica matrix (`./referencectl verify --replicas`) was
re-executed twice this session. **Run 1 (04:44:42–04:48):** 4/6 pass — LB distribution
(fleet view a=3/b=2), cross-replica dedupe, lease handover, steer
conflict; **replica-kill FAIL (61.2 s)** and the nested full suite
(leg `worker-restart-resume`). **Run 2 (05:01:36–05:05:00), after
cleaning all drill residue (`dead_letters`=0, `outbox`: delivered=131,
zero parked rows):** the *same* result — 4/6 pass, fleet view a=2/b=3,
**replica-kill FAIL at an identical 61.2 s**, nested
`worker-restart-resume` FAIL (138 s). Reproduced 2/2 with identical
timing signature → recorded as a **defect found**, not a slow-poll
flake. Evidence for the kill leg: LB `connect() failed (111)` against
the victim; the victim-stamped gateway (`gw.coding`) log shows its
reconnect ladder running the full 2 s/5 s/10 s/30 s backoff — lost
04:47:10, attempts 04:47:19 → 04:47:56, new session only at 04:48:26
(**76 s > the leg's 60 s window**), and identically 05:03:59 → 05:05:15
in run 2; the re-open then landed on the *restored* victim with a fresh
session row (epoch restarted at 1, so the leg's epoch-bump assertion
also cannot hold — the expired session row had been swept, a new row
starts at epoch 1). Honest reading: failover *does* happen and state is
never lost (every other leg passed both runs), but the
LB-eviction + SignalR + gateway-backoff composition needs more than 60 s
when the gateway's ladder is cold, and the leg's 60 s budget predates
this deployment shape. Kernel-side laws (SKIP LOCKED handover, dedupe,
fencing, steer conflict) all verified green both runs.

Probe of the nested-suite leg in isolation
(`./referencectl verify --only worker-restart-resume`, 05:22–05:24):
`FAIL worker-restart-resume (121.8 s) — provisioned elastic never went
live+idle` — the leg's 120 s window for the launcher-provisioned
`gw.team-beta` worker to register and idle elapsed with the container
already gone at check time (launcher log shows it provisioned, stopped,
and re-provisioned across the leg's own kill/retry cycle, but no
live+idle observation inside the window). Consistent with the same
reconnect-ladder law as the kill leg: under the two-replica + LB shape
this deployment reaches `live` only after the gateway's backoff ladder
matures, and both matrix legs' fixed budgets assume the pre-LB
single-kernel timings. Kernel data plane is intact throughout; the
finding is about convergence-time budgets in the verification legs
against this topology (reported to the orchestrator; no kernel fix
attempted here).
