# Consent expired (a held action denied at consume)

**Symptom.** A human approves a held action and the answer is a denial that
says `expired` — sometimes seconds after the request arrived. Or the work
parked with no approver ever answering.

## Law (from shipped code)

Approval rows (`facilitator.approvals`, the same store holds held actions)
carry a deadline and flip to `expired` **lazily**: the expiry sweep flips
aged rows, and every respond path re-checks the deadline inside its
conditional update, so a slow consume that races the sweep still denies
(`ApprovalLedger` — "Row is expired (lazy flip included) — respond denies").
The flip is one transaction: the approval row, the held action's outcome,
and (for a confirm-held assignment) the work parks non-failed with a
timeline row. Nobody's click is ever honoured after the deadline — a slow
process arriving late is exactly the case C5/N6 refuse.

## Diagnosis

```sql
SELECT a.state, a.decided_by, a.deadline_at, a.updated_at, w.status
FROM facilitator.approvals a
LEFT JOIN facilitator.works w ON w.id = a.work_id
WHERE a.id = :approvalId;
```

`state='expired'` with `decided_by=''` = nobody answered in time. A deny on
a respond that returned Expired wrote a `consent-expired`-shaped ProblemDetails —
check the audit row the denial left.

## Playbook

1. Confirm the deadline was humane: `deadline_at` minus the request-send
   time is the response window; a too-short window is an operator policy
   fix (the held-action creation site passes the deadline), not a code fix.
2. If a human DID answer inside the window and still got Expired, look for
   clock skew between the kernel instance that flipped the row (the sweep
   runner) and the one that served the respond — replica clock law
   (`horizontal-scale-anomalies.md`).
3. Re-asking is a new held action, never a reopen: the parked work carries
   the expired trail, and a fresh approval with a fresh digest is the honest
   path (reusing the old command bytes is fine; reusing the old row is not).
4. A pattern of expiries on one team = the consent request never reached an
   entitled approver — see `notification-undeliverable.md` (N23's law: a
   held question that no one entitled could see is a notification failure
   wearing a consent costume).
