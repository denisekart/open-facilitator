# Provider API rate limit (provider rejecting writes)

**Symptom.** Kernel is healthy, link is live, but provider-side actions
(replies, comments) stop landing: outbox rows for
`command.communication.*` / `command.vcs.*` churn `pending → leased →
pending` with rising `attempts`, `last_error` carrying the provider's
rejection, and eventually rows arrive in `dead_letters`.

## Law (from shipped code)

The kernel's response to a rejecting downstream is a **bounded, visible
retry budget**: each charged failure requeues with capped exponential
backoff `min(2^attempts·2, 300)` s, five charged attempts harden the row
into `facilitator.dead_letters` with the full original envelope snapshot
and the operator-visible `reason`. `RoutePendingException` (no route at
all) is the one immortal case — requeue-forever, no dead-letter.
Honoring provider `Retry-After` precisely is the adapter's job; the
kernel's contract is: never drop, never fake success, leave an audit
trail, and give ops a replay surface. Recovery is
`POST /api/admin/dead-letters/{id}/replay` — one replay per dead-letter
(a second is 409 `version-conflict`), replay row inherits the envelope
unmodified (contract-validated), and the adapter's own post-ledger makes
the provider-side write idempotent, so a replay cannot double-post.

## Diagnosis

```sql
SELECT recipient_endpoint_id, event_type, status, attempts, last_error
FROM facilitator.outbox
WHERE status IN ('pending','leased') AND attempts > 0
ORDER BY attempts DESC LIMIT 10;
SELECT dead_letter_id, reason, created_at
FROM facilitator.dead_letters ORDER BY created_at DESC;
```

```bash
curl -s -H "Authorization: Bearer $HUMANTOK" \
  'http://localhost:8089/api/admin/deliveries?state=dead_letter' | jq
```

## Action

Fix/back off at the provider (rate windows reset; adapters resume).
Backoff-requeued rows self-heal; dead-lettered ones need the admin replay
(SPA Deliveries view or the curl above per id). Do not bump attempts
globally or wipe the outbox — the 5-attempt budget is the anti-storm
fuse, and each replay is individually audited
(`audit.action='admin.dead-letter.replay'`).

## Drill 2 executed 2026-11-01 (provider outage; UTC wall-clock 04:24–04:33)

Deviation recorded honestly: the reference env has no 429 injector, so
the rate-limit manifestation chosen was a full provider outage —
`docker stop` of `mattermost` **and** its adapter, the only shape where
the kernel's bounded-retry law is exercised end to end. Setup: fresh
thread (`t7waumb3z3gs3nuf45a684c3aw`) bound to a fresh work
(`ce8d289a-8d1b-4a0a-a65f-c7a6186cf962`) via a
`communication_bindings` row, then the work was walked to `completed` so
the completion reply command queued against the dead provider. At
04:27:22 (5 s after stop): reply row `pending`, `attempts=1`,
`last_error="sink refused the delivery"` (link sink: no live session —
note *no* `link_deliveries` row materializes for an offline endpoint),
session lease lapsed with the adapter down. The row consumed its 5
charged attempts at the dispatcher's few-second requeue ladder and
**dead-lettered at 04:28:31 — ~74 s into the outage**: an offline
provider longer than ~1 minute is over-budget for queued
`communication.*` commands, this is the shipped law, not a bug.
Recovery: mattermost restarted 04:28:17, but the adapter — configured
`restart: on-failure` — had crash-looped on a not-yet-ready provider
("adapter failed to start: fetch failed" ×5) and **stayed stopped**:
operator action `docker restart facilitator-demo-mattermost-adapter-1`
at 04:33:40 brought the session back (epoch bump visible in
`link_sessions`). 04:33:43 `POST /api/admin/dead-letters/<id>/replay` →
200 `{"replayed":true}`; replay outbox row (`command_id
replay:26c16a66-0783-…`) `delivered` attempts=1 within 4 s; delivery
ledger `acked`; **provider readback: exactly one bot reply on the
thread** — no double post; audit row `allow admin.dead-letter.replay`.
Two operational take-aways: keep the *adapter process* up through a
provider blip (a live link queues at the delivery ledger indefinitely; a
dead link spends the kernel's 5-attempt budget), and after any provider
outage >~1 min, check `dead_letters` and replay — the surface restores
exactly-once.
