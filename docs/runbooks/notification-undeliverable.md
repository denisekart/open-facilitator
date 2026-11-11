# Notification undeliverable (the ladder ran out)

**Symptom.** A notification row ends `undeliverable`, or a team says "nobody
told us" while the work carried on. The system never abandons silently: an
undeliverable row names its reason (P5).

## Law (from shipped code)

Every notice writes a ledger row and its outbox command atomically
(`NotificationLedger`). Egress resolves over the ladder
(`EgressResolver`): bound conversation → the team's channel for that
provider → an anonymized public post in the team channel → `undeliverable`
with a reason. A DM rung requires a LINKED provider identity — an unlinked
DM is skipped, never guessed at. A receipt naming a vanished conversation
flips the row `address-obsolete` and the work endures (F5). Transient
rejections never touch the ledger row; the outbox retries it.

## Diagnosis

```sql
SELECT state, reason, target, created_at, updated_at
FROM facilitator.notifications WHERE work_id = :workId ORDER BY created_at;
```

Reasons are facts, e.g. `no-ladder-left: <why each rung failed>`. Cross-read
the outbox row for the same command id, then the receipt on the delivery.

## Playbook

1. `address-obsolete` + work alive = the conversation vanished (renamed,
   deleted, left). Expected, self-healing for FUTURE notices: the next
   emission re-resolves the ladder and falls to the team channel. Nothing
   to fix unless the team wants a new bound target (re-link the
   conversation).
2. `undeliverable: ... no linked identity` = P4 working as designed: the
   person was never linked. The fix is a link (mint a challenge), not a
   config change.
3. `undeliverable: ... no team channel` = the team has no channel binding
   for the provider. Seat the team's binding, then re-emit (a re-notification
   is a new ledger row — trails never rewrite).
4. Held actions parked with nobody notified (N23): check the approval row's
   notification first — a consent request nobody could see is THIS failure
   wearing a consent costume. The approval's `consent-expired.md` leg then
   explains the aftermath.
5. A vanished provider (degraded status): `ProviderDegradedDisclosesPerStatus`
   law — the admin read discloses per provider status; the queue keeps
   notices pending, not dropped. See `provider-api-rate-limit.md` for the
   back-off side.
