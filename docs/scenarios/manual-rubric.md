# Manual scenario rubric (T4 lane)

These scenarios are `unexpressible` in the automated gate by conviction, not
by laziness: the check is a human judgment about experience, and no
in-process test can render it. The T4 lane is **recorded only and never a
gate** (plan 11's lane law). Score each run 0-3:

- **0** absent - the behaviour does not exist
- **1** silent-absent / divergent - exists but misleads, or fails the check
- **2** partial - passes with friction
- **3** covered - passes as the scenario expects

Record date, build SHA, tester, score, and one line of evidence per
scenario in `audit-results.md`'s manual section (free text is allowed here -
this lane is prose by nature).

## H1 — Context scopes and self-correction

**Procedure:** hold a multi-turn session with the system across two
unrelated work items in different teams. Ask a question whose answer only
exists in item A's context while talking about item B; then correct the
system mid-session ("that was about the other item").
**Expected:** the answer stays inside the team's scope (foreign context
answers absent, not forbidden); after the correction, the system re-anchors
on the right item without being re-briefed; nothing from team B's context
leaks into team A's reply.
**Score:** __ / 3 — notes: ____

## H2 — Ask later

**Procedure:** leave a question the system cannot answer (no evidence, no
endpoint). Walk away for a day. Answer it later through a different channel
than the one that asked (e.g. the question surfaced in chat, the answer
arrives as a ticket comment).
**Expected:** the question parks (non-failed), the ask does not re-fire
spam-style, and the later answer from a different channel unparks the same
item once.
**Score:** __ / 3 — notes: ____

## I3 — "Why did it do that?"

**Procedure:** pick any three automated actions from the last week's audit
export. For each, reconstruct from the exported trail alone: which rule
allowed it, who consented, when, and which evidence was admitted - without
asking a person who was there.
**Expected:** all three reconstruct fully from the export; no "the system
just did it" gaps; the actor identity resolves to a person or a named
contributor with an owner.
**Score:** __ / 3 — notes: ____

## H5 — Absent vs pruned

**Procedure:** request an artifact that was never uploaded, and one that was
collected by the retention rule. Compare the answers and what a teammate
sees who has access.
**Expected:** the never-existed artifact and the pruned one read the same
from the outside (absent, not forbidden, no tombstone leak); the audit trail
records the pruning itself; a quote of the content written before pruning is
not contradicted by the system claiming it never existed while the audit
still shows the prune.
**Score:** __ / 3 — notes: ____
