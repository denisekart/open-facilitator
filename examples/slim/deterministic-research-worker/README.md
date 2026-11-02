# Deterministic research worker — no-Docker walkthrough

The smallest real deployment: dev Facilitator + standalone Gateway + the
deterministic harness worker, driven end-to-end by hand. This is the
third-party implementability proof for the HarnessAdapter contract
(`protocol/json-schema/adapter.v1.json`). The `.devcontainer/` in this
directory composes the same wiring from the OFAC Features — see "Devcontainer
path" at the bottom; this file stays as the manual no-Docker path.

All commands run from this directory and the repo root as marked. Prereqs:
PostgreSQL 17 + .NET 10 + Node 22 (as `docs/getting-started.md`).

## 1. Kernel

```bash
# repo root — dev Postgres + dev IdP per getting-started section 3
docker run -d --name ofac-pg -e POSTGRES_USER=demo -e POSTGRES_PASSWORD=demo \
  -e POSTGRES_DB=facilitator -p 5432:5432 postgres:17-alpine
ConnectionStrings__Facilitator="Host=localhost;Database=facilitator;Username=demo;Password=demo" \
  dotnet run --project src/facilitator   # Development: OIDC IdP + enroll-dev
```

## 2. Build the worker + Gateway, then boot both

```bash
# repo root
pnpm -r build

# this directory — relative paths below resolve from here (the Gateway
# resolves stateDir against CWD; the worker resolves against worker.yaml)
cd examples/slim/deterministic-research-worker

OFAC_CHILD_SECRET=child-secret-demo \
  node ../../../src/gateway/dist/bin/gateway.js run --config gateway.yaml
# prints "gateway ready ..." — the child listener already owns .ofac-gateway/child.sock

node ../../../src/adapters/harnesses/deterministic/dist/bin/worker.js run --config worker.yaml
# prints "worker ready childId=gw.demo/deterministic socket=.../child.sock"
```

The Gateway self-enrolls (`enrollmentSecret: enroll-dev`), POSTs the worker's
manifest once the hello/register handshake lands, and rewrites `ownerTeam` to
its own team. `OFAC_CHILD_SECRET` must match `token:` in `worker.yaml`;
spawn order doesn't matter — the worker retries with backoff until the socket
exists, and the Gateway leases + re-probes a child that drops.

## 3. Verify registration

```bash
curl -s http://127.0.0.1:8099/status | jq .children
# → [{"childWorkspaceId":"gw.demo/deterministic","state":"online","inFlight":0,...}]
```

## 4. Seed an assignment

The kernel's assignment slice dispatches `work.assign` itself once a Work is
planned; for a direct worker smoke, the Development-only dev affordance
inserts a link-addressed outbox row (exactly what the e2e tier seeds):

```bash
KERNEL_TOKEN=$(curl -s localhost:8480/realms/facilitator/protocol/openid-connect/token \
  -d grant_type=client_credentials -d client_id=facilitator-cli -d client_secret=e2e-cli-secret | jq -r .access_token)
curl -s -X POST localhost:8088/api/dev/link-outbox \
  -H "Authorization: Bearer $KERNEL_TOKEN" -H 'Content-Type: application/json' \
  -d '{
    "endpointId": "gw.demo",
    "envelope": {
      "specversion": "1.0",
      "id": "demo-cmd-1",
      "source": "urn:facilitator:instance:demo",
      "type": "org.facilitator.command.work.assign.v1",
      "subject": "work/work-demo-1/assignment/asg-demo-1",
      "time": "'"$(date -u +%Y-%m-%dT%H:%M:%SZ)"'",
      "datacontenttype": "application/json",
      "dataschema": "https://schemas.openfacilitator.org/v1/command.work.assign.v1.json",
      "data": {
        "commandId": "demo-cmd-1",
        "actorEvidenceRef": "identity-evidence:demo",
        "workId": "work-demo-1",
        "targetId": "demo",
        "capabilityId": "research.analyze",
        "expectedContextVersion": 1,
        "inputs": { "objective": "cost model", "sourceRefs": [] },
        "expectedDeliverables": [{ "kind": "analysis.report", "mediaType": "text/markdown" }],
        "deadline": "2036-10-23T18:00:00Z",
        "assignmentId": "asg-demo-1"
      }
    }
  }'
# → 202 {"outboxId":"..."}
```

## 5. Watch it run

The worker logs each lifecycle step (`worker-step` ×2, `worker-completed`
with the report path); the two `work.progress` events and the
`assignment.completed` event flow child → Gateway → kernel, deduped into the
kernel's `facilitator.inbox` table:

```bash
curl -s http://127.0.0.1:8099/status | jq .bindings   # while running: the execution row, state "running"
# the Gateway's linkStats (same document) count the relayed receipts
```

With `stepMs: 800` the run completes ~1.6s after the assign.

## 5b. The confirm round-trip (Plan 12)

`gateway.yaml` carries `policy: { path: ./policy.yaml }`: `research.analyze`
stays `allow`, `research.publish` is `confirm` for team-alpha actors. Re-seed
section 4's curl with `"capabilityId": "research.publish"`, a fresh
`commandId`/`subject`, a **GUID** `workId` (`uuidgen` — the approvals row
scopes by work id, so the respond route needs one),
`"actorEvidenceRef": "oidc:<EVIDENCE_JWT>"` (an `ofac-evidence`
password-grant token for `alice`, below), and
`inputs: { "objective": "ship it", "sourceRefs": [], "resource":
"repository:svc-core" }`. The Gateway now parks the assignment
(`curl -s 127.0.0.1:8099/status | jq .awaitingApproval` → 1) and the kernel holds
a `pending` row in `facilitator.approvals`. A human answers over REST:

```bash
EVIDENCE=$(curl -s localhost:8480/realms/facilitator/protocol/openid-connect/token \
  -d grant_type=password -d client_id=ofac-evidence -d client_secret=evidence-secret \
  -d username=alice -d password=alice -d 'audience=facilitator-api' | jq -r .access_token)
APPROVAL_ID=$(docker exec ofac-pg psql -U demo -d facilitator -tA -c \
  "SELECT approval_id FROM facilitator.approvals WHERE state='pending'")
USER=$(curl -s localhost:8480/realms/facilitator/protocol/openid-connect/token \
  -d grant_type=password -d client_id=facilitator-api \
  -d username=alice -d password=alice | jq -r .access_token)
WORK_ID=$(docker exec ofac-pg psql -U demo -d facilitator -tA -c \
  "SELECT work_id FROM facilitator.approvals WHERE approval_id='$APPROVAL_ID'")
curl -s -X POST "localhost:8088/api/works/$WORK_ID/approvals/$APPROVAL_ID/respond" \
  -H "Authorization: Bearer $USER" -H 'Content-Type: application/json' \
  -d '{"decision":"approved","comment":"ship it"}' | jq .   # 200, evidenceRef kernel:...
```

The kernel's decided-push (`link.gw.demo` outbox row → the Gateway's
session) wakes the Gateway: it re-evaluates, consumes the approval over the
Link plane (row flips `consumed`), and the child runs the SAME deterministic
script — the artifact lands as `*-published.md`. `/status`
`executions.completed` ticks to 1; a second respond answers `409
approval-already-consumed`.

## 6. Verify the artifact digest

Completion truth rides the kernel artifact path: the worker uploaded the
report AND a canonical Result Manifest document, and the completion event
carries `resultManifestRef` (an `artifact://` URI whose last segment is the
kernel artifact GUID) + `resultDigest`. Fetch the manifest back through the
kernel and rehash the served bytes:

```bash
REF=$(docker exec ofac-pg psql -U demo -d facilitator -tA -c \
  "SELECT payload->'data'->>'resultManifestRef' FROM facilitator.inbox WHERE payload->>'type' = 'org.facilitator.event.assignment.completed.v1'")
MAN_ID=${REF##*/}   # the kernel artifact GUID
curl -s "localhost:8088/api/artifacts/$MAN_ID/content" \
  -H "Authorization: Bearer $USER" > manifest.json
sha256sum manifest.json | cut -d' ' -f1   # the hex
docker exec ofac-pg psql -U demo -d facilitator -tA -c \
  "SELECT payload->'data'->>'resultDigest' FROM facilitator.inbox WHERE payload->>'type' = 'org.facilitator.event.assignment.completed.v1'" \
  # must equal "sha256:<the hex>" — the kernel verified the same bytes at the boundary
docker exec ofac-pg psql -U demo -d facilitator -c \
  "SELECT revision, digest, state FROM facilitator.result_manifests"   # revision 1, recorded
```

The `manifest.json` bytes are canonical RFC 8785 — rehashing the file IS
the digest check; the report's own digest is the manifest's
`outputs[0].digest`, verifiable the same way through
`/api/artifacts/{outputs[0].artifactId}/content`.

## Troubleshooting

- `register-ack denied: bad-token` — `token:` in `worker.yaml` ≠ the
  Gateway's `OFAC_CHILD_SECRET`.
- Worker stuck retrying `connect ENOENT` — Gateway not up / wrong socket
  path (both configs must point at `.ofac-gateway/child.sock` under *this*
  directory; the Gateway resolves it against its CWD, so start it here).
- Capabilities POST 403 in the Gateway log — `team:` ≠ the kernel's team
  slug (the dev kernel seeds `team-alpha` and `team-beta`).
- Seed answered 202 but nothing ran, Gateway `/status` shows
  `linkStats.unsupported: 1` — the assign arrived before the worker's
  register landed (capabilities unknown ⇒ honest `unsupported`, retry the
  seed after the `worker ready` line).

## Devcontainer path (Features)

Same wiring, automated: the `.devcontainer/` adds the three OFAC Features
(gateway, harness-adapter, workspace-link) from the repo source
(`../../features/src/…` — teams use the published
`ghcr.io/denisekart/open-facilitator/features/<name>:1` coordinates). The
supervisor entrypoint boots a standalone Gateway + deterministic worker (the
`.facilitator/` configs) **and** a direct `linkd` endpoint (`ws.demo`, echo)
beside it — the coexistence proof.

```bash
# repo root — host kernel per section 1 (dev mode: OIDC IdP + enroll-dev)
ConnectionStrings__Facilitator="Host=localhost;Database=facilitator;Username=demo;Password=demo" \
  dotnet run --project src/facilitator &

# Feature payloads (runtime tarballs) must exist before `devcontainer up`
node features/build.mjs

devcontainer up --workspace-folder examples/slim/deterministic-research-worker
# or: make example-slim-up
```

The harness-adapter unit self-enrolls before booting the worker: with
`OFAC_KERNEL_URL` + `OFAC_ENROLL_SECRET` set (and no `OFAC_KERNEL_TOKEN`),
it POSTs `/api/link/v1/enroll` for endpoint `demo-worker-child` (role
`child`) until the host kernel answers, then exports the returned linkToken
as `OFAC_KERNEL_TOKEN` — the worker's kernel credential for the artifact
byte path.

Expect in the workspace-mounted logs (`OFAC_LOG_DIR=/workspace/.ofac-logs`):

```bash
grep 'gateway ready'  .ofac-logs/10-gateway.sh.log   # endpointId=gw.demo
grep 'worker ready'   .ofac-logs/30-harness-adapter.sh.log
grep 'linkd ready'    .ofac-logs/20-linkd.sh.log     # endpointId=ws.demo
```

Seed an assignment with section 4's curl (it targets the host kernel on :8088),
then:

```bash
devcontainer exec --workspace-folder examples/slim/deterministic-research-worker \
  curl -s 127.0.0.1:8099/status | jq '.children[] | select(.state=="connected")'
# → the gw.demo/deterministic child, online
```

The completion row lands in `facilitator.inbox` exactly as section 6 verifies.
Cleanup: `devcontainer rm --workspace-folder examples/slim/deterministic-research-worker`
(state lives in the workspace mount: `.ofac-gateway/`, `.ofac-worker/`,
`.ofac-logs/`, `.ofac-linkd.sqlite3` — all gitignored).
