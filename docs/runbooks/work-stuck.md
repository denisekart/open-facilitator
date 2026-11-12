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
# queue age: pending intents age visibly (a quiet queue is the bug)
curl -s -H "Authorization: Bearer $HUMANTOK" \
  http://localhost:8089/api/admin/queue | jq '.[] | {workId, ageSeconds, reason}'
# executor disclosure: addressed endpoint + liveness + queue reason
curl -s -H "Authorization: Bearer $HUMANTOK" \
  http://localhost:8089/api/admin/executions | jq '.[] | {assignmentId, endpointId, online, queueReason}'
```

The reads answer the disclosure law: an offline owning endpoint shows
`online: false` (nudge skips it WITHOUT burning the budget -
`OfflineOwner_SkippedWithoutBurningBudget`), a gateway window-full pause
shows its `queueReason` on the execution read, and queue rows age-count so
work waiting on capacity that never returns tells someone.

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

## Proven behavior (dead-letter storm + replay)

The drain-side failure mode of "stuck forever" is dead-letters, so this
scene carries the dead-letter path. Rehearsed against the reference stack
by seeding `facilitator.dead_letters` rows — two addressed to the live
`kernel.log` sink, one to a bogus link endpoint — then replaying each via
`POST /api/admin/dead-letters/{id}/replay` (human token):

- First replay of each id: 200 `{"replayed":true}`; an immediate second
  replay of the same id: 409 (`version-conflict`) — the
  exactly-one-replay ledger law.
- Rows addressed to live sinks go `pending → delivered` with attempts=1
  in seconds.
- A link-addressed row to a never-existing endpoint enters `pending`,
  consumes 5 charged attempts (the link sink rejects with no live
  session, which charges attempts), and **dead-letters again**. Two
  honest rules: a link row to a nonexistent endpoint dead-letters after
  5 attempts (only routing's unroutable path requeues indefinitely), and
  replay evidence lives in the audit trail + `dead_letters`/`outbox`
  rows — post-incident, verify settle with
  `SELECT status,attempts,last_error FROM facilitator.outbox WHERE command_id LIKE 'replay:%'`.
