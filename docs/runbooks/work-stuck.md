# Work stuck (assignment silent)

**Symptom.** A work is assigned but nothing moves: no timeline rows, no
heartbeat, no completion. Distinguish the execution heartbeat (link-session
liveness — see gateway-offline) from native harness progress (timeline
events from the workspace); a live link with a silent harness is *this*
scene, a dead link is the other one.

## Law (from shipped code)

The bounded nudge policy (`NudgeSweeper`) sweeps live assignments at
`SweepIntervalSeconds`, silence window and cap from admin_config row
`nudge-policy` (`{"staleAfterMinutes": N}`; absent row = 24 h; host fires
on `NudgeSweep:IntervalSeconds`). Each nudge is one
`command.work.nudge.v1` outbox row (idempotent command id
`work-nudge:{assignment}:{gen}:{episode}:{count}`) plus a `nudges` ledger
row; the steer-arm law means a gateway without a nudge handler answers
`{}` and the receipt settles the row either way. After `NudgeCap = 3`
nudges with no progress in the episode the assignment is escalated
`stalled` — order is always nudge-capped → stalled → scale-up, never
reverse. Restart only with an explicit generation increment: a finalize at
a stale `executionGeneration` is 422 `stale-generation` with zero ledger
effect, so a restarted worker at the old generation cannot corrupt state.

## Diagnosis

```sql
-- assignment state + heartbeat age
SELECT a.status, a.current_execution_generation, a.updated_at,
       w.status AS work_status, w.objective
FROM facilitator.assignments a JOIN facilitator.works w USING (work_id)
WHERE a.work_id = '<work-id>';
-- nudge budget spent on this assignment
SELECT * FROM facilitator.nudges WHERE assignment_id = '<assignment-id>';
-- is the delivery of the nudge itself stuck?
SELECT status, attempts, last_error FROM facilitator.outbox
WHERE work_id = '<work-id>' ORDER BY created_at;
```

```bash
# timeline (also the SPA Work inspector)
curl -s -H "Authorization: Bearer $HUMANTOK" \
  http://localhost:8089/api/admin/work/<work-id>/timeline | jq '.[-8:]'
```

## Action

Send one `status.request` (SPA inspector or provider-plane ask); if the
endpoint supports nudge, exactly one bounded nudge — or arm the sweeper by
PATCHing `nudge-policy` (If-Match ETag + `X-Change-Reason` header; history
at `/api/admin/config/nudge-policy/history`). Then escalate in the
original conversation; a third silent sweep marks it `stalled` on the
ledger. Restart the worker only with a generation increment (the
ProvisioningProcessor does exactly this for a lost worker: START at
gen+1 on the same assignment). Never clear the queue to "unstick" it —
outbox/backoff handles re-delivery; see
[provider-api-rate-limit.md](provider-api-rate-limit.md) for the
dead-letter path.

## Drill 5 executed 2026-11-01 (dead-letter storm + replay; UTC wall-clock 04:41–04:45)

The drain-side failure mode of "stuck forever" is dead-letters, so this
scene carries the dead-letter drill. Setup, honest about what was seeded:
three rows INSERTed straight into `facilitator.dead_letters` (reason
`runbook drill 5: seeded provider outage`) — two addressed to the live
`kernel.log` sink, one to a bogus link endpoint `link.nope` — because an
unroutable route raises `RoutePendingException` which requeues *forever*
and cannot produce a dead letter from config alone. With the 3 seeded rows
plus drill 2's genuine one, `GET /api/admin/deliveries?state=dead_letter`
listed 4. Replaying each seeded row via
`POST /api/admin/dead-letters/{id}/replay` (human token): 3× HTTP 200
`{"replayed":true}`; an immediate second replay of the same id returned
409 `version-conflict` (exactly-one-replay ledger law). The two
`kernel.log` replay rows went `pending → delivered` with attempts=1 in
under 5 s (row settles delivered). The `link.nope` replay row entered
`pending` and stayed there with backoff — then, over the following
minutes, consumed 5 charged attempts (the link sink rejects with no live
session, which charges attempts) and dead-lettered at 04:45:24. Two
honest rules learned: **a link-addressed row to a never-existing endpoint
dead-letters after 5 attempts** (only routing's `RoutePendingException`
path is immortal), and replay evidence stays in the audit trail +
`dead_letters`/`outbox` rows, so post-incident the operator verifies
settle via `SELECT status,attempts,last_error FROM facilitator.outbox
WHERE command_id LIKE 'replay:%'`.
