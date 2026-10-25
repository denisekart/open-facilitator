# Deterministic research worker — no-Docker walkthrough

The smallest real deployment: dev Facilitator + standalone Gateway + the
deterministic harness worker, driven end-to-end by hand. This is the
third-party implementability proof for the HarnessAdapter contract
(`protocol/json-schema/adapter.v1.json`); Plan 08's devcontainer Features
automate the same wiring, and this file stays as the manual path.

All commands run from this directory and the repo root as marked. Prereqs:
PostgreSQL 17 + .NET 10 + Node 22 (as `docs/getting-started.md`).

## 1. Kernel

```bash
# repo root — dev Postgres per getting-started
docker run -d --name ofac-pg -e POSTGRES_USER=demo -e POSTGRES_PASSWORD=demo \
  -e POSTGRES_DB=facilitator -p 5432:5432 postgres:17-alpine
ConnectionStrings__Facilitator="Host=localhost;Database=facilitator;Username=demo;Password=demo" \
  dotnet run --project src/facilitator   # Development: dev-token + enroll-dev
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
curl -s -X POST localhost:8088/api/dev/link-outbox \
  -H 'Authorization: Bearer dev-token' -H 'Content-Type: application/json' \
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

## 6. Verify the artifact digest

The completion event's `resultManifest` carries the report's absolute
`file://` URL and `sha256:` digest; rehash the bytes on disk:

```bash
REPORT=$(ls .ofac-worker/artifacts/*-report.md)
sha256sum "$REPORT" | cut -d' ' -f1   # the hex
docker exec ofac-pg psql -U demo -d facilitator -tA -c \
  "SELECT payload::text FROM facilitator.inbox WHERE payload->>'type' = 'org.facilitator.event.assignment.completed.v1'" \
  | grep -oE 'sha256:[a-f0-9]{64}'   # jsonb pretty-prints key: value — match the hex, not the raw JSON
# must equal "sha256:<the hex>"
```

A mismatch means something rewrote the file between publish and read —
the digest is the verified-external-ref interim until the artifact store
era lands.

## Troubleshooting

- `register-ack denied: bad-token` — `token:` in `worker.yaml` ≠ the
  Gateway's `OFAC_CHILD_SECRET`.
- Worker stuck retrying `connect ENOENT` — Gateway not up / wrong socket
  path (both configs must point at `.ofac-gateway/child.sock` under *this*
  directory; the Gateway resolves it against its CWD, so start it here).
- Capabilities POST 403 in the Gateway log — `team:` ≠ the kernel's team
  slug (the dev kernel seeds `default`).
- Seed answered 202 but nothing ran, Gateway `/status` shows
  `linkStats.unsupported: 1` — the assign arrived before the worker's
  register landed (capabilities unknown ⇒ honest `unsupported`, retry the
  seed after the `worker ready` line).
