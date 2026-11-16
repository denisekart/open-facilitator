# Operational runbooks

One page per operational scene, one shape on every page: what you observe,
the law that decides it, verbatim diagnosis queries (psql, curl, admin
surfaces), concrete action, and the durable facts you can always rely on.

**The reference environment is a conformance environment, not proof of
production hardening.** Backup/restore, workload token rotation,
upgrade/rollback, and alert ownership are deployment-site obligations
before a shared deployment is production-eligible.

## Reference env handles

- Kernel: `http://localhost:8088` (replica `facilitator-a`), `http://localhost:8090` (`facilitator-b`), LB `http://localhost:8089` (two-replica shape). Health: `GET /health`.
- Store: `docker exec facilitator-demo-postgres-1 psql -U demo -d facilitator -tAc '<sql>'` (schema `facilitator`).
- Tokens: `./referencectl token` (client-credentials, `kernel-operator`) and `./referencectl token --user alice --password alice` (human, framework-admin) from the repo root.
- Admin API (bearer): `/api/admin/summary|search|links|deliveries|provisioning|metrics|replicas|config/{resource}[/history]|dead-letters/{id}/replay` and `/api/identity/roles`. Admin SPA (Work inspector, Links, Deliveries, Config): the kernel root at `http://localhost:8088/` (OIDC login, alice/alice).
- Gateway diagnostics live in the gateway container: `node -e "fetch('http://127.0.0.1:8099/status').then(r=>r.json()).then(s=>console.log(JSON.stringify(s)))"` inside `facilitator-demo-demo-workspace-1` (coding fleet gateway: port 8100).
- Proven recovery paths, re-runnable: `./referencectl verify` (slim legs incl. kernel-restart-resume and dead-letters-empty), `./referencectl verify --replicas` (two-replica matrix a–f).

## Scenes

| Scene | When to reach for this |
|---|---|
| [gateway-offline.md](gateway-offline.md) | Assignments unassigned or intents unrouted, an endpoint greyed in Links, deliveries piling up `pending` — reconcile before accepting work. |
| [work-stuck.md](work-stuck.md) | Live link but a silent harness: heartbeat vs harness progress, nudge budget spent, when `stalled` is honest. |
| [duplicate-event-suspicion.md](duplicate-event-suspicion.md) | A message looks processed twice: prove it against the inbox row, the ledgers, and provider readback. |
| [database-outage.md](database-outage.md) | Postgres unavailable: fail-safe ingress (no phantom acks), then lease reclaim does the rest. |
| [artifact-memory-backend-outage.md](artifact-memory-backend-outage.md) | Uploads/recall fail while works run: the artifact/memory plane degrades, it never fakes. |
| [provider-api-rate-limit.md](provider-api-rate-limit.md) | Provider rejecting writes: the 5-attempt budget, what lands in `dead_letters`, replay per row. |
| [identity-claim-mapper-error.md](identity-claim-mapper-error.md) | Token validates but permissions are wrong: 401 vs 403, claim pointers, role rules. |
| [horizontal-scale-anomalies.md](horizontal-scale-anomalies.md) | 2+ replicas behaving oddly: epochs, SKIP LOCKED leases, failover timing, fleet view. |
| [consent-expired.md](consent-expired.md) | A held action denied `expired`: lazy flip, the response window, why re-ask is a new row. |
| [notification-undeliverable.md](notification-undeliverable.md) | A notice ended `undeliverable`: read the reason, find the rung that failed. |
| [contributor-retired.md](contributor-retired.md) | An automation left the roster: name reserved, work with the team, every trail ends at a human. |
| [work-merged.md](work-merged.md) | A work id 303s: merge law, loser history stands, there is no unmerge. |
| [manual-verification-tiers.md](manual-verification-tiers.md) | About to land a change that touches reference-env inputs: the legs outside CI and how to record them. |
