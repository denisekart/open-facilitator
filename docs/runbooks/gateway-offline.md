# Gateway offline (link leg down)

**Symptom.** Work is accepted but assignments sit unassigned or routed
intents never reach their endpoint; the admin SPA Links view shows an
endpoint greyed out, or `link_sessions` has no live lease for it. Deliveries
meant for that endpoint pile up in `outbox` as `pending`.

## Law (from shipped code)

A link session is a row in `facilitator.link_sessions`: one per
`logical_endpoint_id`, stamped with the serving `replica_id`, a monotonic
`session_epoch` (bumped on every re-open against the same row), a
`lease_until` refreshed by each heartbeat (15 s TTL = 5 s heartbeat × 3),
and a `capabilities_digest`. The kernel only delivers to a *live* session:
`LinkLedger` materializes `link_deliveries` rows at claim time under the
session epoch, holds an in-flight lease, and redelivers with backoff
(`redeliver_after`, capped at 5 receipt attempts) — an offline endpoint
means the *outbox* row requeues with capped exponential backoff
(`min(2^attempts·2, 300)` s) and dead-letters after 5 charged attempts.
`RoutePendingException` (no route at all — unknown capability) requeues
forever and never dead-letters. Do not replay side effects blindly:
delivery exactly-once rides the delivery ledger + the gateway's local
ledger, so reconcile before accepting new work.

## Diagnosis

```sql
-- who is connected, to which replica, lease liveness
SELECT logical_endpoint_id, replica_id, session_epoch, lease_until,
       last_heartbeat, capabilities_digest
FROM facilitator.link_sessions ORDER BY last_heartbeat DESC;
-- what is queued for the dead endpoint
SELECT recipient_endpoint_id, status, count(*), max(attempts), max(last_error)
FROM facilitator.outbox WHERE status IN ('pending','leased')
GROUP BY 1,2 ORDER BY 3 DESC;
-- in-flight deliveries stranded at a dead epoch
SELECT endpoint_id, session_epoch, state, count(*)
FROM facilitator.link_deliveries WHERE state <> 'acked' GROUP BY 1,2,3;
```

```bash
# fleet view + per-replica socket counts
curl -s -H "Authorization: Bearer $TOK" http://localhost:8089/api/admin/links | jq
curl -s -H "Authorization: Bearer $TOK" http://localhost:8089/api/admin/replicas | jq
```

Gateway-side truth lives in the gateway container (`gw.coding` here; demo
gateway uses port 8099): `docker exec facilitator-demo-coding-workspace-1
node -e "fetch('http://127.0.0.1:8100/status').then(r=>r.json()).then(s=>
console.log(JSON.stringify({state:s.state,session:s.session})))"` — plus
the reconnect log at `/var/lib/ofac/logs/10-gateway.sh.log` inside the
container, which records every `link session lost` / `retrying` with
backoff. Token/validation failures show as repeated 401-shaped WebSocket
connect errors there; check the enrollment secret and team launcher when
the gateway never reaches `ready`.

## Action

Restore the link (start/repair the gateway or adapter; `docker compose
... up -d --no-deps <svc>` in `reference/`). Outbox rows self-heal: backoff
requeues resume delivery on the next heartbeat. For rows that crossed the
5-attempt budget while offline, use the admin replay surface per row
(`POST /api/admin/dead-letters/{id}/replay`, human token, audit-logged,
one replay per dead-letter — a second replay is 409). Reconcile current
assignments via the SPA Work inspector (`GET /api/admin/work/{id}/timeline`)
before accepting new work; a session-lease-expired failure is synthesized
by the SessionLossSweeper (`event.assignment.failed.v1`,
reason=`session-lease-expired`, retryable) only when `workload-scale`
admin config is present.
