# Database outage (Postgres unavailable)

**Symptom.** Ingress starts failing or hanging; `/health` flips unhealthy
or admin reads 500; dispatch drains stall. Kernel law: nothing is claimed
`accepted` until persisted — a request that cannot write the inbox fails
(4xx/5xx) instead of being buffered in memory, and persistent-socket
sessions may stay connected but cannot acknowledge new durable commands.

## Law (from shipped code)

Every ingress write is `INSERT ... ON CONFLICT` into `facilitator.inbox`
inside the request path; a database failure surfaces as a failed request,
never a phantom 202. Dispatchers (`OutboxDispatcher`, `DbEventDispatcher`)
catch listener/drain failures, log, and degrade to polling — a drain
failure never crashes the host. Leases are the crash-consistency
mechanism: an outbox row claimed by a replica that dies mid-drain sits
`leased` until `leased_until`, then any replica's `ReclaimExpiredLeases`
flips it back to `pending` (no double delivery: delivery is claim-guarded
and receipted). Link-session leases lapse the same way (15 s TTL), after
which gateways re-open and epoch/ledger state reconciles.

## Diagnosis

```bash
docker inspect -f '{{.State.Health.Status}}' facilitator-demo-postgres-1
docker logs facilitator-demo-facilitator-1 --since 10m 2>&1 | grep -ciE "connection|Npgsql|drain"
```

```sql
-- after restore: leases that were held across the outage
SELECT status, count(*), min(leased_until)
FROM facilitator.outbox WHERE status = 'leased' GROUP BY 1;
-- rows parked leased beyond their deadline = awaiting reclaim
SELECT count(*) FROM facilitator.outbox
WHERE status = 'leased' AND leased_until < now();
```

## Action

Restore the store (tested backup / restart; validate migrations apply on
start — `DatabaseInitializer` runs them and the kernel refuses to serve
until ready). Then let the reclaimers work: expired outbox leases flip to
`pending` within one dispatcher tick and redeliver exactly-once. Validate
link leases (`link_sessions.lease_until`) and replay any dead-letters the
outage pushed past 5 attempts (see work-stuck.md).

## Proven behavior (DB restart mid-drain)

Rehearsed against the reference stack: a burst of 10 `POST /api/works`
racing a `docker restart` of Postgres (~7 s down).

- Every accepted POST persisted exactly once: 10/10 works, 10 inbox rows,
  no double-ack and no phantom ack (no accepted request without its inbox
  row).
- No row stranded on a dead claim: in-flight outbox leases self-reconcile
  when the store returns (`leased` rows with a past `leased_until`: 0).
- Link sessions survive the outage with epoch continuity.
- The kernel's fail-safe path is visible in logs (drain-path warnings,
  LISTENER falling back to polling); no crashed task, and no `202`
  returned without a durable row behind it.
- Post-restart steady state: `pending=0 leased=0`. If the outage outlasted
  link-session leases, endpoints re-hello and the ledger recomputes the
  un-acked set — the resume cursor is a hint, the receipt ledger is truth.

`./referencectl verify` re-proves this recovery path end-to-end
(kernel-restart-resume, dead-letters-empty legs).
