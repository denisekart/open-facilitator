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
`pending` within one dispatcher tick and redeliver exactly-once; verify
reconciliation with the counts in the drill below. Validate link leases
(`link_sessions.lease_until`) and replay any dead-letters the outage
pushed past 5 attempts (see work-stuck.md). `./referencectl verify`
includes the kernel-restart-resume and dead-letters-empty legs that prove
this recovery path end-to-end.

## Drill 1 executed 2026-11-01 (DB restart mid-drain; UTC wall-clock 04:34:32–04:34:55)

Burst of 10 `POST /api/works` through the LB (command ids
`drill1-<ts>-1..10`), then `docker restart facilitator-demo-postgres-1`
within the same second the burst finished — the restart raced the drain.
All 10 POSTs returned a `workId` (the burst completed inside the ~1 s
window before the store actually dropped; every one was durably accepted).
Postgres health `healthy` again at 04:34:39 (7 s down). Reconciliation at
04:34:55: **works persisted 10/10**; **inbox rows for the burst
commands: 10** — each command exactly once, no double-ack, no phantom ack
(every accepted POST had its inbox row; none was acked without one);
outbox for the burst works all terminal (`delivered`), `leased` rows with
`leased_until < now()`: **0** (no row stranded on a dead claim — the
kernel's own in-flight claims self-reconciled when the store returned);
link sessions all survived the outage with epoch continuity
(`mm-adapter/1 forgejo-adapter/2 gw.coding/1 oci-launcher/1
gw.team-alpha/2` — the forgejo epoch 2 predates this drill, from earlier
provider churn that evening). Kernel logs during the window show the
fail-safe path verbatim: connection-failure warnings on the drain path
and the LISTENER falling back to polling; no crashed task, no 202 without
a row. Post-restart steady state: `pending=0 leased=0`.
