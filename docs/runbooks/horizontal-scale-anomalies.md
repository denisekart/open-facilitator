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
degrading. Boot-floor re-arm: the provisioning elastic arm is driven by
ledger rows in `facilitator.provisioning_operations`, and a `stopped` row
permanently disarms that arm — only a NEW work-keyed trigger re-launches.
A drained-then-restarted environment therefore never re-boots its
min-workers floor until the ledger is cleared:
`DELETE FROM facilitator.provisioning_operations` plus the matching
hash-commanded `facilitator.outbox` rows (orphan hash rows otherwise make
every sweep die on the outbox command-id unique key). Verified live: with
6 stale rows wiped, the min-arm re-armed, provisioned the elastic, and all
elastic legs went green across two consecutive full runs.

## Proven behavior (replica SIGKILL — two-replica shape)

Kernel-side failover laws verified on a live two-replica stack behind the
LB: SKIP LOCKED lease handover, cross-replica dedupe, fleet view,
steer-conflict handling — none of them lose state when a replica dies.

Two structural facts an operator should expect:

- **Failover is real but not instant.** The eviction-plus-reconnect
  composition (nginx `max_fails=1 fail_timeout=30s` + SignalR reconnect
  ladder 0/2/5/10/30 s + gateway backoff) can take ~76 s end to end
  when the gateway's ladder starts cold. Fixed-timeout probes (including
  parts of the acceptance matrix) should budget for this, not for the
  single-kernel timings they were written against.
- **Epoch is per session row.** A swept-and-recreated `link_sessions` row
  lawfully restarts at epoch 1. An epoch bump across a failover implies
  the old row survived; it is row-identity-dependent, not a failover
  invariant — don't alert on "epoch didn't increase".
