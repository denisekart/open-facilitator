# Operational runbooks

One page per operational scene. Each scene carries a symptom, exact
diagnosis queries (psql, curl, admin surfaces), the action, and the
durable behavioral facts an operator can rely on.

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
- Proven recovery paths, re-runnable: `./referencectl verify` (slim legs incl. kernel-restart-resume and dead-letters-empty), `./referencectl verify --replicas` (two-replica matrix a–e).

## Scenes

| Scene | Start here |
|---|---|
| Link leg (gateway/adapter) down, sessions/leases, reconcile-before-work | [gateway-offline.md](gateway-offline.md) |
| Assignment silent: heartbeat vs harness progress, nudge budget, stalled | [work-stuck.md](work-stuck.md) |
| Suspected double-processing: CloudEvents dedupe, action ledgers, readback | [duplicate-event-suspicion.md](duplicate-event-suspicion.md) |
| Postgres unavailable: fail-safe ingress, lease reclaim, no phantom acks | [database-outage.md](database-outage.md) |
| Artifact store (S3) / memory backend down: degrade, don't fake | [artifact-memory-backend-outage.md](artifact-memory-backend-outage.md) |
| Provider API rejecting writes: backoff, budgets, dead-letter + replay | [provider-api-rate-limit.md](provider-api-rate-limit.md) |
| Token accepted but permissions wrong: claim pointers, role rules | [identity-claim-mapper-error.md](identity-claim-mapper-error.md) |
| Two+ replicas behaving oddly: epochs, SKIP LOCKED leases, fleet view | [horizontal-scale-anomalies.md](horizontal-scale-anomalies.md) |
| Held action denied `expired`: lazy flip, response window, re-ask law | [consent-expired.md](consent-expired.md) |
| Notice ended `undeliverable`: the egress ladder and its named reason | [notification-undeliverable.md](notification-undeliverable.md) |
| An automation left the roster: name reserved, work with the team, audit trail | [contributor-retired.md](contributor-retired.md) |
| A work id 303s: merge law, loser history stands, no unmerge | [work-merged.md](work-merged.md) |
| The verification tiers outside CI and the law that binds them | [manual-verification-tiers.md](manual-verification-tiers.md) |
