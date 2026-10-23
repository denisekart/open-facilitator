# Getting started

Status: Plan 00 (skeleton + build + CI). The slim reference environment (`referencectl up slim`) arrives with Plan 11; until then, this is the full contributor loop.

## 1. Environment

**Recommended: devcontainer.** Open the repository in the included `.devcontainer` configuration — Docker-in-Docker, .NET SDK 10, Node 24, and pnpm 12 are preinstalled.

**Manual path:**

- .NET SDK `10.0.40x` (per `global.json`, `rollForward: latestFeature`)
- Node.js ≥ 22
- `corepack enable && corepack install` — installs the exact pnpm from `packageManager: pnpm@12.5.1`
- Docker (optional; only needed for image builds)

## 2. Build & test loop

```bash
make restore   # dotnet restore + pnpm install
make build     # dotnet build -c Release + pnpm -r build
make test      # dotnet test -c Release --no-build + pnpm -r test
make lint      # dotnet format --verify-no-changes + eslint + prettier --check
make format    # auto-fix .NET + TS/JSON/YAML formatting
```

`make compose-slim` / `make compose-full` intentionally exit 2 until Plan 11.

### Running individual tests

- .NET: `dotnet test OpenFacilitator.slnx -c Release --filter FullyQualifiedName~HealthEndpointTests`
- TypeScript (one package): `pnpm --filter @ofac/gateway test`
- TypeScript (one file): `pnpm --filter @ofac/workspace-link exec vitest run test/package.test.ts`

Integration/behavior tests (Testcontainers Postgres, dockerized Facilitator) appear from Plan 02 and will be listed here as they land.

## 3. CI and releases

- CI (`ci.yml`) is the merge gate: .NET build+test, TS lint+build+test, workflow/meta validation, and a Facilitator image build. Job-by-job scope: [`.github/workflows/README.md`](../.github/workflows/README.md).
- Releases trigger on `v*` tags: the Facilitator image is pushed to GHCR for `linux/amd64` + `linux/arm64`.

## 4. Artifact registries

| Artifact | Registry | Coordinate |
|---|---|---|
| OCI images (facilitator; later gateway, workspace-link, features, examples) | GHCR | `ghcr.io/denisekart/open-facilitator/<name>` |
| npm packages `@ofac/*` | GitHub Packages npm | `https://npm.pkg.github.com/denisekart` (see repo `.npmrc`; publish flips in Plan 01) |
| Dev Container Features (Plan 06, OCI per spec §8.5) | GHCR | `ghcr.io/denisekart/open-facilitator/features/<name>` |
| NuGet | — (public deps from nuget.org; first internal package → `https://nuget.pkg.github.com/denisekart/index.json`) | — |
