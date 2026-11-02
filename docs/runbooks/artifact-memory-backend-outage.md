# Artifact / memory backend outage

**Symptom.** Artifact uploads or recall calls fail or hang while works
keep running: `artifacts` rows park in `requested`, `POST`/`GET` on the
artifact endpoints 5xx or 404-absent, `/api/memory/recall` returns empty
or a degraded status. Works that don't touch the artifact/memory plane
must be unaffected — if they are, this is a different scene.

## Law (from shipped code)

Artifact state is a row in `facilitator.artifacts`
(`state, storage_uri, digest, size, verification, expires_at`) with the
blob in the S3-compatible store (SeaweedFS `:8333`, bucket
`facilitator-artifacts`). The kernel claims a slot (`requested`), the
gateway/worker PUTs the blob, finalize moves the row to `stored` after
verification; a store that is down surfaces as a failed request or a row
stuck in `requested` — never as a `stored` row with a missing blob
(artifact reads 404-absent honestly — degrade, don't fake).
Memory recall goes through the kernel's brokered scopes
(`memory_scopes`/`memory_usages` ledger); a backend failure returns a
degraded response, and assignment context assembly proceeds without the
entries. Work operations not requiring the degraded capability continue.

## Diagnosis

```sql
-- artifact slots that never got a blob
SELECT state, count(*), min(created_at)
FROM facilitator.artifacts GROUP BY state;
-- stuck slots with their work (older than a few minutes = backend problem)
SELECT artifact_id, work_id, state, created_at
FROM facilitator.artifacts WHERE state = 'requested' AND created_at < now() - interval '5 minutes';
```

```bash
# store liveness + bucket readability (SeaweedFS S3 port)
docker inspect -f '{{.State.Health.Status}}' facilitator-demo-artifacts-1
curl -s -o /dev/null -w '%{http_code}\n' http://localhost:8333/facilitator-artifacts
# kernel-side: memory plane health rides the summary
curl -s -H "Authorization: Bearer $HUMANTOK" http://localhost:8089/api/admin/summary | jq
# a recall that degrades instead of erroring
curl -s -X POST -H "Authorization: Bearer $HUMANTOK" -H 'content-type: application/json' \
  -d '{"query":"smoke","purpose":"assignment.context","workId":"<work-id>"}' \
  http://localhost:8089/api/memory/recall | jq '.entries | length'
```

## Action

Restore the store (`docker compose ... up -d --no-deps artifacts` in
`reference/`); in-flight `requested` slots either finalize when the
PUT/retry resumes or expire per `expires_at` — no queue clearing, the
digest-verified finalize is the commit point. Verify with the
`stored`-state count and a download round-trip through
`GET /api/works/<work-id>/artifacts/<artifact-id>`. For the memory
backend, recall scope grants (`memory_scopes`) and usage audit
(`memory_usages`) survive the outage because they're in the kernel's own
store; only recall *content* degrades. If works *did* stall during the
artifact outage, that is a bug worth a defect note — this plane is
required to be non-blocking for work paths that don't reference it.
No drill executed for this scene yet (the conformance env has not been
cut over on this plane); the queries above are the standing entry point.
