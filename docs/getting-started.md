# Getting started

Status: Plan 02 (transactional kernel) — the Facilitator runs against PostgreSQL with HTTP ingress, Work state machines, and the outbox dispatcher. The slim reference environment (`referencectl up slim`) arrives with Plan 11; until then, this is the full contributor loop.

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
- Releases trigger on `v*` tags: the Facilitator image is pushed to GHCR for `linux/amd64` + `linux/arm64`.

## 5. Artifact registries

| Artifact | Registry | Coordinate |
|---|---|---|
| OCI images (facilitator; later gateway, workspace-link, features, examples) | GHCR | `ghcr.io/denisekart/open-facilitator/<name>` |
| npm packages `@ofac/*` | GitHub Packages npm | `https://npm.pkg.github.com/denisekart` (see repo `.npmrc`; publish flips in Plan 01) |
| Dev Container Features (Plan 06, OCI per spec §8.5) | GHCR | `ghcr.io/denisekart/open-facilitator/features/<name>` |
| NuGet | — (public deps from nuget.org; first internal package → `https://nuget.pkg.github.com/denisekart/index.json`) | — |
