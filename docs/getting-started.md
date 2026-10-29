# Getting started

Status: the slim reference environment runs the whole demo path — kernel,
Postgres, Keycloak, S3 artifacts, OTLP telemetry, a linked team workspace
with a deterministic worker — and `./referencectl verify` proves every
spec §21.1 hop unattended. The walkthrough below is a real session against
a fresh stack.

## 1. Environment

**Recommended: devcontainer.** Open the repository in the included
`.devcontainer` configuration — Docker-in-Docker, .NET SDK 10, Node 24, and
pnpm 12 are preinstalled.

**Manual path:**

- Docker + Compose v2 (the reference environment needs ~4 GiB free RAM)
- `curl`, `jq`, `git`
- .NET SDK `10.0.40x` (per `global.json`, `rollForward: latestFeature`) and
  Node.js ≥ 22 only for the build/test loop below; the walkthrough itself
  needs just Docker

## 2. Build & test loop

```bash
make restore   # dotnet restore + pnpm install
make build     # dotnet build -c Release + pnpm -r build
make test      # dotnet test -c Release --no-build + pnpm -r test
make lint      # dotnet format --verify-no-changes + eslint + prettier --check
make format    # auto-fix .NET + TS/JSON/YAML formatting
```

`dotnet test` requires a running Docker daemon: the integration suite boots
PostgreSQL 17 via Testcontainers (tag pinned in `reference/versions.env`).

### Running individual tests

- .NET: `dotnet test OpenFacilitator.slnx -c Release --filter FullyQualifiedName~HealthEndpointTests`
- TypeScript (one package): `pnpm --filter @ofac/gateway test`
- TypeScript (one file): `pnpm --filter @ofac/workspace-link exec vitest run test/package.test.ts`

## 3. Bring up the reference environment

```bash
./referencectl up slim
```

Builds the kernel + node-runtime images, boots Postgres, Keycloak (realm
`facilitator`), SeaweedFS (S3), the Aspire dashboard, the Facilitator
container, and `demo-workspace` (standalone Gateway `gw.team-alpha` +
deterministic worker), waits for every healthcheck, then seeds one
`work.assign` that dispatches end-to-end. `./referencectl status` probes the
stack (service table + kernel `/health` + Gateway `/status` via `compose
exec` — diagnostics bind the workspace container's loopback); the ports
table lives in [`reference/README.md`](../reference/README.md). First run
generates `reference/.env` from `.env.example` — rotate the placeholder
secrets for anything beyond a smoke run. `./referencectl down -v` tears it
all down.

The presigned artifact redirects name the in-network host `artifacts`
(SigV4 forbids rewriting them), so for host-side downloads add
`127.0.0.1 artifacts` to `/etc/hosts` (or use `curl --resolve
artifacts:8333:127.0.0.1`, shown below).

## 4. Log in: OIDC end to end

The realm fixture (auto-imported) carries users `alice`/`alice` (and
`demo-admin` for the admin console at
`http://localhost:8080/keycloak/admin`). Every kernel API needs a bearer;
two grants matter:

```bash
# operator/service token (client_credentials)
./referencectl token
# eyJhbGciOiJSUzI1NiIsInR5cCIgOi...   (JWT)

# human token, password grant — the kernel maps the subject to a
# kernel-side identity for audit + policy claims
./referencectl token --user alice --password alice
# eyJhbGciOiJSUzI1NiIsInR5cCIgOi...
```

Bearers live ~5 minutes (realm default) — re-mint liberally.

## 5. Run a Work

```bash
./referencectl work --objective "survey the reference environment"
# workId=6f0c1a67-... 
# [ { "kind": "created", ... } ]
```

The seed's assign rides kernel → gateway → worker: progress events, a
Markdown report uploaded through the artifact flow, and an
`assignment.completed` receipt come back through the link plane. Watch the
read models (pair from a real run — `W` = the work, tokens as above):

```bash
W=e5c6c58d-1240-45e3-87a6-f46e70b399a9
ATOK=$(./referencectl token --user alice --password alice | tail -1)

curl -s http://localhost:8088/api/works/$W/timeline -H "Authorization: Bearer $ATOK" \
  | jq '[.[].kind]'
# ["created","artifact.stored","artifact.stored","manifest.recorded","memory.candidate"]

curl -s http://localhost:8088/api/works/$W/manifests -H "Authorization: Bearer $ATOK" \
  | jq '.[0] | {revision, revisionOf}'
# {"revision":1,"revisionOf":null}

# the manifest doc names its report artifact; content redirects to a
# presigned S3 URL (see the /etc/hosts note above)
MID=$(curl -s http://localhost:8088/api/works/$W/manifests -H "Authorization: Bearer $ATOK" | jq -r '.[0].artifactId')
AID=$(curl -sL --resolve artifacts:8333:127.0.0.1 \
  "http://localhost:8088/api/artifacts/$MID/content" -H "Authorization: Bearer $ATOK" \
  | jq -r '.outputs[0].artifactId')
curl -sL --resolve artifacts:8333:127.0.0.1 \
  "http://localhost:8088/api/artifacts/$AID/content" -H "Authorization: Bearer $ATOK" | head -2
# # Research report e5c6c58d-1240-45e3-87a6-f46e70b399a9
# Objective: slim verify: analyze the reference environment
```

Traces: open the Aspire dashboard at `http://localhost:18888`, resource
`facilitator`. Every kernel span carries the work's trace id — the W3C
`traceparent` the dispatcher stamped onto the assign envelope, which the
worker echoes verbatim on its progress/completion envelopes, so
`outbox.envelope->>'traceparent'` == event envelope == dashboard trace.

## 6. Admin tour: the embedded inspector

Plan 21 embeds the operator SPA in the kernel itself — no separate portal.
With the slim stack up (section 3), open `http://localhost:8088`: the SPA
redirects to Keycloak, sign in `alice`/`alice`, and you are in the
inspector. Trace the acceptance-run Work without psql:

1. Paste the work id (or a word from its objective) into the search bar →
   the hit list filters by kind; open the Work hit.
2. The inspector shows assignments (attempts table), manifests/artifacts
   (content links ride the same presigned redirect as section 5 — add the
   `127.0.0.1 artifacts` host entry), and the full timeline
   (`/api/admin/work/{id}/timeline`).
3. A `CorrelationId` chip copies the trace handle; the dashboards' trace
   link opens the Aspire resource with that id (the kernel stores no
   telemetry time-series — §17.4).
4. **Links** view: gateways online/offline (a stale session collapses to
   offline — the DB lease is the sole authority) + trust tier.
   **Deliveries**: filter poisoned rows and replay a dead-letter once —
   the second replay reports the ledger's `already replayed`.
   **Config**: the five stored-intent resources (e.g. `nudge-policy`) edit
   via versioned PATCH (`If-Match`); a concurrent writer's 409 reloads and
   names the other author + reason. Writes are audited intent — a feature
   reads a value through only when it opts in.

The browser tier runs in CI as the `admin-e2e` job
(`make admin-e2e` locally; chromium downloads on first run).

## 7. Acceptance run: `referencectl verify`

The 11-step acceptance script (`reference/checks/verify.sh`) proves every
spec §21.1 hop through public read models, the Postgres planes, S3, and the
dashboard — no product hooks, no dev-only routes. Run it against a
booted-but-unseeded stack (`up slim --no-seed`): it seeds its own works.
Exit 0 iff every step passes; `--json` is the machine report, `--only <ids>`
reruns a leg and its prerequisites. Transcript (clean stack, exit 0):

```
PASS idp-login (313ms) — both grants minted and accepted by the kernel
PASS work-create (559ms) — work created + assigned
PASS dispatch-link (306ms) — assign delivered over the link plane + gateway ready with a connected child
PASS progress (128ms) — worker progress reached the kernel inbox
PASS artifact-stored (3920ms) — manifest doc + report stored/verified + blob in S3 + Markdown bytes served
PASS memory-candidate (235ms) — candidate ingressed + timeline row + 1 recall hit(s)
PASS kernel-restart-resume (3444ms) — approval parked across a facilitator restart
PASS approval-roundtrip (23187ms) — parked durably, resumed exactly: approved after restart, decided, completed
PASS worker-restart-resume (6346ms) — worker killed mid-run: honest retryable failed, retry completed with exactly 1 manifest
PASS trace-correlation (288ms) — facilitator stamp echoed by worker envelopes + dashboard spans
PASS dead-letters-empty (26ms) — no dead letters
```

|Step|Proves|
|---|---|
|`idp-login`|Both grants mint and the kernel accepts them (roles + claim mapping wired)|
|`work-create`|Work creation + assign outbox row via the seed primitives|
|`dispatch-link`|Dispatcher → LinkSink → gateway session (row `delivered`, child connected)|
|`progress`|Worker event rode the link back into the kernel inbox|
|`artifact-stored`|Manifest doc + report stored/verified, blob in the bucket, bytes served|
|`memory-candidate`|Candidate event ingressed, timeline row, recall hits|
|`kernel-restart-resume`|A parked approval survives a `restart facilitator` (durable await)|
|`approval-roundtrip`|Confirm → approval.required → respond → consumed → completion, exactly once|
|`worker-restart-resume`|Mid-execution kill = ONE honest retryable `assignment.failed`; re-dispatch completes with exactly 1 manifest|
|`trace-correlation`|Dispatcher-stamped `traceparent` == worker-echoed envelope traceparent == dashboard span trace|
|`dead-letters-empty`|No hop failed hard anywhere in the run|

This run is part of every task's acceptance criteria — see
[`docs/contributing.md`](contributing.md).

## 8. CI and releases

- CI (`ci.yml`) is the merge gate: .NET build+test, TS lint+build+test, workflow/meta validation, and a Facilitator image build. Job-by-job scope: [`.github/workflows/README.md`](../.github/workflows/README.md).
- The slim reference environment is part of every task's acceptance criteria — and locally IS the gate: the hosted `ref-env-bringup`/`ref-env-verify` jobs are commented out in `ci.yml` (free-tier GitHub compute). Before calling anything done: `./referencectl down -v && ./referencectl up slim --no-seed && ./referencectl verify` exits 0.
- Releases trigger on `v*` tags: the Facilitator image is pushed to GHCR for `linux/amd64` + `linux/arm64`, and the Dev Container Features are published as OCI artifacts under `…/features/<name>`.

## 9. Artifact registries

| Artifact | Registry | Coordinate |
|---|---|---|
| OCI images (facilitator) | GHCR | `ghcr.io/denisekart/open-facilitator/<name>` |
| npm packages `@ofac/*` | GitHub Packages npm | `https://npm.pkg.github.com/denisekart` (see repo `.npmrc`; publish flips in Plan 01) |
| Dev Container Features (OCI per spec §8.5; `gateway`, `workspace-link`, `harness-adapter`) | GHCR | `ghcr.io/denisekart/open-facilitator/features/<name>:1` |
| NuGet | — (public deps from nuget.org; first internal package → `https://nuget.pkg.github.com/denisekart/index.json`) | — |

## 10. Connect an existing workspace: add one Feature

An existing team container becomes a linked, headless workspace by adding
ONE Feature to its `devcontainer.json` (spec §28's onboarding law) — no base
image change, no build-time secrets:

```jsonc
// .devcontainer/devcontainer.json of any Node >= 22 workspace container
{
  "image": "mcr.microsoft.com/devcontainers/base:ubuntu",
  "features": {
    "ghcr.io/devcontainers/features/node:1": { "version": "22" },
    "ghcr.io/denisekart/open-facilitator/features/gateway:1": {
      "configPath": ".facilitator/gateway.yaml"
    }
  },
  // Secrets arrive at runtime via env — never as Feature options:
  "containerEnv": { "OFAC_ENROLL_SECRET": "${localEnv:OFAC_ENROLL_SECRET}" }
}
```

The Feature's supervisor entrypoint starts the Gateway headless (IDE and
`devcontainer up` alike); the container log then carries the ready line:

```
gateway ready endpointId=gw.demo epoch=1 children=0
```

Per-Feature options, the 1:1 env surface, state law, and troubleshooting:
[`features/src/gateway/README.md`](../features/src/gateway/README.md) (and
the sibling `workspace-link` / `harness-adapter` READMEs). A fully composed
three-Feature example with an end-to-end walkthrough:
[`examples/slim/deterministic-research-worker`](../examples/slim/deterministic-research-worker/README.md).

The `up full` providers (the chat/VCS legs of spec §28: Mattermost, Forgejo,
Kafka, Ollama) land in a later phase; today the overlay boots their
containers but no seeded flows ride them.

## 11. Tear down

```bash
./referencectl down -v   # FULL=1 includes the overlay
```
