# Duplicate event suspicion

**Symptom.** The same provider message looks processed twice (double
reply, double work), or a retry storm makes it *look* that way. The kernel
dedupes on CloudEvents `(source, id)` and, for commands, on `commandId`;
external side effects dedupe on the provider delivery id + action ledgers.
Never "just clear the queue" — an ambiguous external write stays
`uncertain` until reconciled by provider readback.

## Law (from shipped code)

`POST /api/ingress/events`: validate → `INSERT ON CONFLICT` on inbox
`(event_source, event_id)` — a conflicting row returns HTTP 200
`{"status":"duplicate"}` with the *original* `inboxId`, zero new rows,
zero re-processing. Commands carry `commandId` into the inbox row and the
outbox `command_id` uniqueness law; downstream effects ride action ledgers
(link delivery receipts, `communication_bindings` idempotency keys,
provisioning op-id `hash(work,assignment,gen,op)`). Provider-side
readback (e.g. list thread posts) is the arbiter for "did the outside
world see it twice".

## Diagnosis

```sql
-- the canonical triple: provider delivery -> inbox row -> effects
SELECT provider, provider_delivery_id, state, received_at, processed_at
FROM facilitator.inbox
WHERE event_id = '<cloudevents-id>';             -- expect exactly 1
-- did it produce two commands / two actions?
SELECT command_id, count(*) FROM facilitator.outbox
WHERE work_id = '<work-id>' GROUP BY 1 HAVING count(*) > 1;
-- conversation replies carry their idempotency key
SELECT idempotency_key, created_at FROM facilitator.communication_bindings
WHERE work_id = '<work-id>';
```

```bash
# timeline dedupes by (event id, work); two rows = two real events
curl -s -H "Authorization: Bearer $HUMANTOK" \
  http://localhost:8089/api/admin/work/<work-id>/timeline | jq
# provider readback, mattermost: count bot posts in the thread
```

## Action

If inbox has one row: the duplicate was rejected at the door — look for
the second *effect* elsewhere (an adapter retrying before its own ledger
checkpoint, or a replay). If two inbox rows exist, the events genuinely
differ (different `source` or `id` — e.g. provider regenerated the id on
retry): treat external writes as `uncertain`, reconcile via readback, and
fix the *producer's* id law, don't hand-delete rows. Replay dedupe proof
is covered in
[identity-claim-mapper-error.md](identity-claim-mapper-error.md#proven-behavior-auth-failures--dedupe-end-to-end)
(same CloudEvents id posted twice: 202 accepted then 200 duplicate, one
`processed` inbox row).
