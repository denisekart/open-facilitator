# Getting started

Status: Plans 02–03 (transactional kernel + Link server) — the Facilitator runs against PostgreSQL with HTTP ingress, Work state machines, the outbox dispatcher, and the Workspace Link transport (SignalR hub + HTTP fallback). The slim reference environment (`referencectl up slim`) arrives with Plan 11; until then, this is the full contributor loop.

## 1. Environment

**Recommended: devcontainer.** Open the repository in the included `.devcontainer` configuration — Docker-in-Docker, .NET SDK 10, Node 24, and pnpm 12 are preinstalled.

**Manual path:**

- .NET SDK `10.0.40x` (per `global.json`, `rollForward: latestFeature`)
- Node.js ≥ 22
- `corepack enable && corepack install` — installs the exact pnpm from `packageManager: pnpm@12.5.1`
- Docker (required for `make test` — the integration suite runs PostgreSQL 17 via Testcontainers; also needed for image builds)

## 2. Build & test loop

```bash
make restore   # dotnet restore + pnpm install
make build     # dotnet build -c Release + pnpm -r build
make test      # dotnet test -c Release --no-build + pnpm -r test
make lint      # dotnet format --verify-no-changes + eslint + prettier --check
make format    # auto-fix .NET + TS/JSON/YAML formatting
```

`make compose-slim` / `make compose-full` intentionally exit 2 until Plan 11.

`dotnet test` requires a running Docker daemon: the Plan 02 integration suite boots `postgres:17-alpine` via Testcontainers (image tag pinned in `reference/versions.env`).

### Running individual tests

- .NET: `dotnet test OpenFacilitator.slnx -c Release --filter FullyQualifiedName~HealthEndpointTests`
- TypeScript (one package): `pnpm --filter @ofac/gateway test`
- TypeScript (one file): `pnpm --filter @ofac/workspace-link exec vitest run test/package.test.ts`

## 3. Running the Facilitator locally

```bash
# 1. PostgreSQL 17 (matches reference/versions.env POSTGRES_TAG)
docker run -d --name ofac-pg -e POSTGRES_USER=demo -e POSTGRES_PASSWORD=demo \
  -e POSTGRES_DB=facilitator -p 5432:5432 postgres:17-alpine

# 2. Start the kernel — auto-migrates on boot (Database:AutoMigrate, default true);
#    with Auth:BootstrapToken unset in Development the literal dev-token is accepted
ConnectionStrings__Facilitator="Host=localhost;Database=facilitator;Username=demo;Password=demo" \
  dotnet run --project src/facilitator

# 3. Create a Work and walk it through the state machine
curl -s -X POST localhost:8088/api/works \
  -H 'Authorization: Bearer dev-token' -H 'Content-Type: application/json' \
  -d '{"objective":"kernel check"}'
curl -s -X POST localhost:8088/api/works/<workId>/transitions \
  -H 'Authorization: Bearer dev-token' -H 'Content-Type: application/json' \
  -d '{"to":"planning","expectedVersion":1}'
```

Responses and errors follow the kernel error contract ([`docs/protocol.md`](protocol.md#kernel-ingress-implemented-plan-02)); a wrong bearer token yields a `401` ProblemDetails.

The Link server (Plan 03) rides the same process — enroll an endpoint with the dev enrollment secret, then use its `linkToken` as a bearer (contract: [`docs/protocol.md`](protocol.md#link-transport-implemented-plan-03); the scripted fake client lives in `tests/Facilitator.Tests/LinkTestHarness.cs`):

```bash
curl -s -X POST localhost:8088/api/link/v1/enroll -H 'Content-Type: application/json' \
  -d '{"token":"enroll-dev","endpointId":"gw-dev","instanceId":"i1","trustMode":"isolated-executor"}'   # 201 → linkToken
curl -s -X POST localhost:8088/api/link/v1/capabilities -H "Authorization: Bearer <linkToken>" \
  -H 'Content-Type: application/json' -d '{"apiVersion":"facilitator.framework/v1","kind":"CapabilityManifest","ownerTeam":"default","capabilities":[{"id":"research.analyze","version":1,"execution":"asynchronous"}]}'   # 204
curl -s "localhost:8088/api/link/v1/deliveries?wait=2&limit=16" -H "Authorization: Bearer <linkToken>"  # 404 session-not-found until a hub hello opens a session
```

The client half (Plan 04) is the `@ofac/workspace-link` SDK + the `ofac-linkd` supervisor — one command does enroll, capabilities POST, connect, and serve (echo-mode receipts); the state file replays un-acked work after a crash:

```bash
pnpm -r build   # once, builds dist/bin/linkd.js
node src/workspace-link/dist/bin/linkd.js run \
  --url http://localhost:8088 --secret enroll-dev \
  --endpoint gw-dev --team default --manifest manifest.json   # prints "linkd ready ..."
```

The same assembly doubles as its own client, riding the public HTTP API only (defaults `--url http://localhost:8088`, token from env `FACILITATOR_TOKEN` else `dev-token`):

```bash
FACILITATOR_TOKEN=dev-token dotnet run --project src/facilitator -- cli \
  work create --objective "kernel check"
dotnet run --project src/facilitator -- cli work get <workId>
dotnet run --project src/facilitator -- cli work steer <workId> --directive "focus on tests" --expect-version 1
dotnet run --project src/facilitator -- cli work timeline <workId>
```

Migration management: `make db-update` (`dotnet tool restore && dotnet ef database update -p src/facilitator`, using `ConnectionStrings:Facilitator` from the environment). `GET /api/dev/deliveries` dumps the outbox delivery ledger so you can watch the dispatcher drain.

## 4. CI and releases

- CI (`ci.yml`) is the merge gate: .NET build+test, TS lint+build+test, workflow/meta validation, and a Facilitator image build. Job-by-job scope: [`.github/workflows/README.md`](../.github/workflows/README.md).
- Releases trigger on `v*` tags: the Facilitator image is pushed to GHCR for `linux/amd64` + `linux/arm64`, and the Dev Container Features are published as OCI artifacts under `…/features/<name>`.

## 5. Artifact registries

| Artifact | Registry | Coordinate |
|---|---|---|
| OCI images (facilitator) | GHCR | `ghcr.io/denisekart/open-facilitator/<name>` |
| npm packages `@ofac/*` | GitHub Packages npm | `https://npm.pkg.github.com/denisekart` (see repo `.npmrc`; publish flips in Plan 01) |
| Dev Container Features (OCI per spec §8.5; `gateway`, `workspace-link`, `harness-adapter`) | GHCR | `ghcr.io/denisekart/open-facilitator/features/<name>:1` |
| NuGet | — (public deps from nuget.org; first internal package → `https://nuget.pkg.github.com/denisekart/index.json`) | — |

## 6. Connect an existing workspace: add one Feature

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
