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

## Proven behavior (provider outage end to end)

Rehearsed against the reference stack as a full provider outage
(`docker stop` of Mattermost **and** its adapter — the shape where the
kernel's bounded-retry law is exercised end to end), with a completion
reply queued against the dead provider:

- The queued reply row consumes its 5 charged attempts at the
  dispatcher's few-second requeue ladder and **dead-letters ~74 s into
  the outage**: an offline provider longer than ~1 minute is
  over-budget for queued `communication.*` commands. This is the shipped
  law, not a bug. (No `link_deliveries` row materializes for an offline
  endpoint — the budget is spent at the sink.)
- An adapter configured `restart: on-failure` can crash-loop on a
  not-yet-ready provider and stay stopped — after restoring the provider,
  check the *adapter process* too and restart it if needed. The session
  returns with an epoch bump.
- `POST /api/admin/dead-letters/{id}/replay` → 200 `{"replayed":true}`;
  the replay outbox row (`command_id` `replay:<uuid>`) delivers within
  seconds, the delivery ledger acks, and provider readback shows exactly
  one post — the surface restores exactly-once. The replay is audited
  (`admin.dead-letter.replay`).

Operational take-aways: keep the *adapter process* up through a provider
blip (a live link queues at the delivery ledger indefinitely; a dead link
spends the kernel's 5-attempt budget), and after any provider outage
>~1 min, check `dead_letters` and replay.
