# Work merged (one item was the same ask)

**Symptom.** A work id answers 303 to a GET, an automated follower reports a
redirect on an id it recorded, or "my item disappeared" while its twin
carries both histories.

## Law (from shipped code)

Merge (`POST /api/works/{id}/merge`, the loser's id in the path) is the
dedup end for duplicate-intake twins: the LOSER keeps its full history
(timeline, approvals, artifacts, links), gains a `merged` terminal state and
a redirect to the SURVIVOR, and reads of the loser 303 to the survivor —
never a rewritten id, never deleted rows (`WorkEndpoints`: "A merged work
still resolves"). Merging a work into itself refuses. The merge records one
`work-merged` timeline row on each side naming the other.

## Diagnosis

```sql
SELECT id, status, merged_into, objective FROM facilitator.works
WHERE id = :loserId OR merged_into = :loserId;
```

`status='merged'` + `merged_into` set. The survivor's timeline shows the
merge row with actor and reason; the audit export shows the API call.

## Playbook

1. An id is 303-ing forever: expected — the redirect is stable and the
   loser's history stays readable THROUGH the survivor's view of the link
   (`WorkRecordTests.Merge_KeepsLoserHistory_RedirectsToSurvivor`).
2. An automation holding the loser id should follow the 303 location and
   re-record the survivor id; nothing it already did rolls back.
3. Wrong merge (the "twins" were different asks): there is no unmerge —
   opening fresh work for the misjudged ask and merging a note into the
   trail is honest history; a tombstone dance would rewrite the record.
4. Post-merge transitions on the loser answer the terminal-work law
   (steer/completed are 422 illegal-transition, not silence): an executor
   still reporting on a merged item is a harness that missed the
   cancellation, see `work-stuck.md`'s disclosure leg.
