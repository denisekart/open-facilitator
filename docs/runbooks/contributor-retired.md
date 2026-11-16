# Contributor retired (an automation left the roster)

**Symptom.** Work that a contributor (bot, automation, coding agent seated
as a team member) used to drive now points at the team, or a provider event
from a familiar handle is refused as unknown.

## Law

Contributors (`facilitator.team_members` rows of contributor kind) are
process members with a handle unique across teams and an accountable human
owner. Retirement sets `retired_at` — the NAME stays reserved (the trail
never gets a new actor), open work moves to the team (the accountable party
is a person, never a machine), and every contributor write — seat, retire,
remit change — requires a change reason and lands as an audited operator
action. A capability claim from a human against a contributor-only
capability denies (`refused-not-routed`), and a contributor self-report
without admitted evidence denies with an audit row — retirement changes
nothing about that.

## Diagnosis

```sql
SELECT handle, retired_at, changed_by, change_reason
FROM facilitator.team_members WHERE team_id = :teamId ORDER BY handle;
```

A retired contributor still resolves on reads (410-shaped honesty on
writes, history intact). Work items formerly owned by it show
`owner_team_id` with the trail naming the retired handle.

## Action

1. Work parked "with the team" is the design, not a loss: a human owner from
   the roster picks it up. Assignment to a NEW contributor is a fresh seat
   plus a fresh assignment, never a silent re-ownership.
2. Provider events from the retired handle refuse as unknown contributor. If
   the automation is BACK under the same handle, re-seating the name requires
   a seat write with a reason — the handle's history makes a reuse visible,
   not invisible.
3. If a remIT change looked like retirement: remit is an audited field
   (`remit` + `changed_by` + `change_reason`), the audit export shows the
   who/when/why, and a remit narrowed to nothing behaves like retirement for
   routing.
4. Run the end-of-trail check: every action naming the contributor must still
   terminate at a named human (the owner or a recorded gap). A trail that
   ends at the machine is a data bug — file it with the audit export rows.

## Durable facts

- Reads of a retired contributor never 404; writes answer 410-shaped
  honesty, history intact.
- The audit trail is the authority on who did what, when, and why — seat,
  retire, and remit changes are all in it.
