# src/facilitator/Kernel/Notifications

## Purpose
The single notification path: every unsolicited disclosure the kernel makes
leaves through here, and the ledger row is its durable receipt.

## Truth anchors
- `NotificationLedger.cs` — `facilitator.notifications`: emit (dedupe key,
  ladder addressing, outbox row `notify:<id>`), the one cadence resolver
  (channel → team → system, reusing the Teams effective-config law), and the
  receipt hook: a permanent rejection (`target-missing-channel`) retires the
  address (`obsolete` + an `address.obsolete` timeline row) while the work
  record endures.
- `EgressResolver.cs` — the address ladder: explicit target → the work's
  binding → a linked DM → the team channel fallback (anonymized, with a
  correction window) → `undeliverable` naming the reason. An unlinked DM is
  never guessed.
- `ActorDirectory.cs` — name → team actor: exact account match resolves, a
  prefix hit on several accounts ASKS on the team channel (B5), a miss is
  undeliverable with that reason.

## Invariants
- Replies and acks to direct requests keep their request's addressing and do
  NOT enter the ledger — the ledger is for notices.
- Every ladder outcome is visible: `sent`, `fell-back`, `undeliverable` (with
  a reason), `obsolete`. No silent drop.
- Audience is explicit: a 403-class provider degradation is a team notice, a
  429-class one reads `operators` only.
