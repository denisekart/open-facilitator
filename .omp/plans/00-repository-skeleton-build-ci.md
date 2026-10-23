# Plan 00 — Repository skeleton, build system, and CI spine (refined detail design)

**Status:** 🟡 in-progress — implemented on branch `plan-00-skeleton`; flip to 🟢 when CI is green on `main`.
**Depends on:** — (first slice)

## Context

Repo is spec-only (`docs/specs/*`, README, AGENTS.md, `.omp/`, `.devcontainer/`). Remote is public `github.com/denisekart/open-facilitator`, branch `main`, 2 commits. Plan 00 turns it into a buildable monorepo per spec §22.1 with a green GitHub Actions spine, so every later plan (01+) commits into a tree where `make build` / `make test` / CI already work.

Locked layout decisions (override the roadmap's gate wording):
- **Repo root = the §22.1 `framework/` tree.** `src/`, `protocol/`, `features/`, `examples/`, `reference/`, `Makefile` at repo root; existing root `docs/specs/` serves as the spec §22.1 `docs/` node. No `framework/` subdir, no second docs tree.
- **Admin SPA deferred to Plan 12** — skeleton only creates `src/facilitator/wwwroot/.gitkeep` (SPA embeds into Facilitator wwwroot then; §22.1 has no SPA path).
- **npm scope `@ofac/*`** — `@ofac/gateway`, `@ofac/workspace-link` now; `@ofac/protocol` arrives in Plan 01.
- **`/scaffold` is skipped.** The `dotnet-aspire-svelte` preset would generate an Aspire AppHost + Svelte app we don't want yet (AppHost arrives with Plan 11's Aspire wiring; SPA with Plan 12). Tree is created manually per §22.1.
- Verified local toolchain: .NET SDK `10.0.401`, Node `v24.21.0`, pnpm `12.5.1`, Docker `29.9.0`.

## Step 1 — .NET root infrastructure (repo root)

Create exactly:

**`global.json`**
```json
{
  "sdk": {
    "version": "10.0.401",
    "rollForward": "latestFeature"
  }
}
```

**`nuget.config`** — explicit single source; no private NuGet feed yet (all dependencies are public until an internal package like a published protocol DTO lib exists, then add the GH feed below as a real source):
```xml
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <packageSources>
    <clear />
    <add key="nuget.org" value="https://api.nuget.org/v3/index.json" />
    <!-- Private feed (enable when an internal package is pushed; scoped PAT or GITHUB_TOKEN in CI):
    <add key="github" value="https://nuget.pkg.github.com/denisekart/index.json" />
    -->
  </packageSources>
  <!--
  <packageSourceCredentials>
    <github><add key="Username" value="denisekart" /><add key="ClearTextPassword" value="<PAT with read:packages>" /></github>
  </packageSourceCredentials>
  -->
</configuration>
```

**`Directory.Build.props`** — shared for every C# project:
```xml
<Project>
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <LangVersion>latest</LangVersion>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <TreatWarningsAsErrors>true</TreatWarningsAsErrors>
    <AnalysisLevel>latest</AnalysisLevel>
    <Deterministic>true</Deterministic>
  </PropertyGroup>
</Project>
```

**`Directory.Packages.props`** — Central Package Management, starts as:
```xml
<Project>
  <PropertyGroup>
    <ManagePackageVersionsCentrally>true</ManagePackageVersionsCentrally>
  </PropertyGroup>
  <ItemGroup>
    <!-- PackageVersion entries added in Step 2 when the test project resolves them -->
  </ItemGroup>
</Project>
```

**`.editorconfig`** — .NET defaults (`dotnet new editorconfig` output) plus: `end_of_line = lf`, `insert_final_newline = true`, `trim_trailing_whitespace = true`, `[*] charset = utf-8`, `indent_size = 4` for `*.cs`, 2 for `*.{ts,json,yml,yaml,md}`, and `[*.md] trim_trailing_whitespace = false`.

**`.dockerignore`** — `bin/`, `obj/`, `node_modules/`, `**/dist/`, `.git/`, `docs/`, `.omp/`, `.devcontainer/` (context hygiene for the Facilitator image build).

**`OpenFacilitator.slnx`** — new-format solution at root (SDK 10 native), containing `src/facilitator/Facilitator.csproj` and `tests/Facilitator.Tests/Facilitator.Tests.csproj` (added in Step 2 via `dotnet sln add`).

## Step 2 — Facilitator stub + test project

**`src/facilitator/Facilitator.csproj`** — `Microsoft.NET.Sdk.Web`, no `TargetFramework` (inherited), no package refs (plain ASP.NET Core). `/health` exists now because Plan 11's compose healthcheck needs a liveness endpoint, and it gives CI a real behavioral assertion instead of a compile-only check.

**`src/facilitator/Program.cs`**
```csharp
var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();

app.MapGet("/health", () => Results.Ok(new { status = "ok" }));

app.Run();

public partial class Program;
```

**`src/facilitator/wwwroot/.gitkeep`** — SPA embed target (Plan 12).

**`src/facilitator/Dockerfile`** — required now (not deferred to Plan 11) because the release workflow stub in Step 6 actually builds it, and §22.2's compose fragment references exactly this path:
```dockerfile
# syntax=docker/dockerfile:1
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY global.json Directory.Build.props Directory.Packages.props nuget.config OpenFacilitator.slnx ./
COPY src/facilitator/Facilitator.csproj src/facilitator/
RUN dotnet restore src/facilitator/Facilitator.csproj
COPY src/facilitator/ src/facilitator/
RUN dotnet publish src/facilitator/Facilitator.csproj -c Release -o /app --no-restore /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=build /app .
EXPOSE 8080
ENV ASPNETCORE_URLS=http://0.0.0.0:8080
ENTRYPOINT ["dotnet", "Facilitator.dll"]
```
Publish-only-the-web-csproj (test project never enters the image); `.slnx`/props are copied first so CPM resolution works in the restore layer.

**`tests/Facilitator.Tests/Facilitator.Tests.csproj`** — `Microsoft.NET.Sdk`, `dotnet new xunit` based, `ProjectReference` to Facilitator. Create it with `dotnet new xunit` + `dotnet add package Microsoft.AspNetCore.Mvc.Testing xunit xunit.runner.visualstudio Microsoft.NET.Test.Sdk`, **then** lift the resolved versions into `Directory.Packages.props` as `<PackageVersion>` items and strip `Version=` from the csproj (`PackageReference Include="x"` only). Record the exact resolved versions in the commit; do not guess them.

**`tests/Facilitator.Tests/HealthEndpointTests.cs`** — `IClassFixture<WebApplicationFactory<Program>>`; `GET /health` → 200 and body `{"status":"ok"}`.

Gate for this step: `dotnet build OpenFacilitator.slnx -c Release` and `dotnet test OpenFacilitator.slnx -c Release` green locally.

## Step 3 — TypeScript workspace

**`pnpm-workspace.yaml`**
```yaml
packages:
  - "src/gateway"
  - "src/workspace-link"

catalog:
  # exact versions resolved by `pnpm add -Dw` in this step; list them explicitly
  typescript: "<resolved>"
  tsup: "<resolved>"
  vitest: "<resolved>"
```

**Root `package.json`** — `private: true`, `"packageManager": "pnpm@12.5.1"`, `"engines": { "node": ">=22" }`, scripts:
```json
{
  "build": "pnpm -r build",
  "test": "pnpm -r test",
  "lint": "eslint . && prettier --check \"src/**/*.ts\" \"*.{json,mjs,yaml,yml}\"",
  "format": "prettier --write \"src/**/*.ts\" \"*.{json,mjs,yaml,yml}\""
}
```
Root devDependencies installed with `pnpm add -Dw typescript tsup vitest eslint @eslint/js typescript-eslint prettier` and referenced from packages via `catalog:` (TS tooling) / plain names (linting).

**`tsconfig.base.json`** — `target ES2022`, `module ESNext`, `moduleResolution Bundler`, `strict true`, `declaration true`, `skipLibCheck true`. Each package tsconfig extends it.

**`eslint.config.mjs`** — flat config: `@eslint/js` recommended + `typescript-eslint` `recommended`, ignore `dist`, `node_modules`, `docs`.
**`.prettierrc`** — `{ "printWidth": 100, "singleQuote": false }`. **`.prettierignore`** — `docs/`, `dist/`, `*.md` under specs (`docs/specs/**` is normative text; never reflowed).

**`.npmrc`** (repo root) — workspace packages resolve locally via pnpm workspaces, so this only configures future consumption/publish of `@ofac` from the private registry:
```
@ofac:registry=https://npm.pkg.github.com
//npm.pkg.github.com/:_authToken=${NODE_AUTH_TOKEN}
```
Each package `package.json` gets `"publishConfig": { "registry": "https://npm.pkg.github.com" }` and a `repository` field so `npm publish` targets the owner-scoped GitHub Packages registry (`https://npm.pkg.github.com/denisekart`). Local cross-package imports always hit the workspace link, never the registry — no GH Packages read token needed for `pnpm install` while packages are `private: true`.

**`src/workspace-link/`** — package `@ofac/workspace-link`: `package.json` (type module, `exports` → `dist/index.js`, scripts `build: tsup src/index.ts --format esm,cjs --dts`, `test: vitest run`), `src/index.ts` exporting only `export const LINK_PACKAGE = "@ofac/workspace-link";` (protocol types land in Plan 01's `@ofac/protocol` — do not pre-invent envelope types), `test/package.test.ts` vitest smoke asserting the export.

**`src/gateway/`** — package `@ofac/gateway`: identical shape, `export const GATEWAY_PACKAGE = "@ofac/gateway";`.

Gate: `pnpm install && pnpm -r build && pnpm -r test && pnpm lint` green; commit `pnpm-lock.yaml`.

## Step 4 — Remaining §22.1 tree, versions.env, env example

Directories (each seeded with `.gitkeep` plus a 3–6-line `README.md` stating what lands there and in which plan — no phantom files):

- `src/adapters/mattermost/`, `src/adapters/forgejo/`, `src/adapters/kafka/`, `src/adapters/harnesses/` (Plan 06/13/14/15 populate; no npm packages yet — avoids dead pnpm workspace churn)
- `protocol/cloudevents-schemas/`, `protocol/json-schema/`, `protocol/openapi/`, `protocol/conformance-fixtures/` (Plan 01)
- `features/gateway/`, `features/workspace-link/`, `features/harness-adapter/` (Plan 06)
- `examples/slim/deterministic-research-worker/` (Plan 06), `examples/full/coding-worker/` + `examples/full/research-worker/` + `examples/full/seed-repositories/` (Plans 14/15)
- `reference/postgres-init/`, `reference/keycloak/`, `reference/config/`, `reference/bootstrap/`, `reference/checks/` (Plan 11)

**`reference/versions.env`** — tags verified against upstream releases today (Oct 2026); digests stay empty until test/release publication pins them (spec §22.4). Built-image registry namespace is pinned here too so compose/referencectl never hardcodes it:
```bash
# Pinned reference image versions (spec §22.4). Update via PR with a compose verification run.
POSTGRES_TAG=17-alpine
KEYCLOAK_TAG=26.7.5
KAFKA_TAG=4.2.2
FORGEJO_TAG=16.0.2          # spec pins the 16.x family; v17 exists but is untested for us
MATTERMOST_TAG=11.11.1      # confirm exact docker tag format when Plan 11 first pulls
SEAWEEDFS_TAG=4.48
ASPIRE_TAG=13.4.2           # mcr.microsoft.com/dotnet/aspire-dashboard; re-check latest 13.x in Plan 11
OLLAMA_TAG=0.40.9
# Registry for framework-built images (facilitator, gateway, workspace-link, features, examples):
OCI_REGISTRY=ghcr.io/denisekart/open-facilitator
# Release-time digest pins (filled by the release job, Plan 18):
POSTGRES_DIGEST=
KEYCLOAK_DIGEST=
KAFKA_DIGEST=
FORGEJO_DIGEST=
MATTERMOST_DIGEST=
SEAWEEDFS_DIGEST=
ASPIRE_DIGEST=
OLLAMA_DIGEST=
FACILITATOR_DIGEST=
```

**`reference/.env.example`** — placeholder keys §22.2 will consume, each with a comment naming its consumer: `DEMO_POSTGRES_PASSWORD`, `DEMO_KEYCLOAK_ADMIN_PASSWORD`, `DEMO_S3_ACCESS_KEY`, `DEMO_S3_SECRET_KEY` (values `changeme-<consumer>`). Root `.gitignore` already covers `secrets/`, `.env`, `bin/`, `obj/`, `dist/`, `node_modules/` — no changes needed.

## Step 5 — Makefile (repo root)

```make
.PHONY: restore build test lint format compose-slim compose-full clean

restore:
	dotnet restore OpenFacilitator.slnx
	pnpm install

build: restore
	dotnet build OpenFacilitator.slnx -c Release --no-restore
	pnpm -r build

test: build
	dotnet test OpenFacilitator.slnx -c Release --no-build
	pnpm -r test

lint:
	dotnet format OpenFacilitator.slnx --verify-no-changes
	pnpm lint

format:
	dotnet format OpenFacilitator.slnx
	pnpm format

compose-slim compose-full:
	@echo "Reference environment lands in Plan 11: cd reference && ./referencectl up slim|full"
	@exit 2

clean:
	dotnet clean OpenFacilitator.slnx
	rm -rf src/*/dist node_modules
```

Compose jobs deliberately have **no** GitHub workflow file yet (dead YAML until Plan 11); the Makefile targets are the seam.

## Step 6 — GitHub Actions: CI + release/deploy stub + Dependabot

**Registry targets (all artifact types, single owner `denisekart`):**

| Artifact | Private registry | Coordinate |
|---|---|---|
| OCI images (facilitator, gateway, workspace-link, later features/examples) | GHCR | `ghcr.io/denisekart/open-facilitator/<name>` |
| npm packages `@ofac/*` | GitHub Packages npm | `https://npm.pkg.github.com/denisekart` |
| Dev Container Features (Plan 06, OCI per spec §8.5) | GHCR | `ghcr.io/denisekart/open-facilitator/features/<name>` |
| NuGet | none yet — public deps from nuget.org; first internal package pushes to `https://nuget.pkg.github.com/denisekart/index.json` (block already scaffolded in `nuget.config`) | — |

**`.github/workflows/ci.yml`** — triggers: `push` to `main`, all `pull_request`. Four jobs, all on `ubuntu-latest` (free standard runners for a public repo; if it later goes private the jobs fit free minutes; arm64 matrix arrives with Plan 11's compose jobs):

- `dotnet`: `actions/checkout@v5` → `actions/setup-dotnet@v6` with `cache: true` (global.json is honored automatically) → `dotnet build -c Release` → `dotnet test -c Release --no-build`.
- `typescript`: `actions/checkout@v5` → `pnpm/action-setup@v4` (version read from `packageManager`) → `actions/setup-node@v4` with `node-version: 22`, `cache: pnpm` (default registry stays npmjs — the repo `.npmrc` routes only the `@ofac` scope to GitHub Packages; GH Packages does not mirror public packages) → `pnpm install --frozen-lockfile` → `pnpm lint` → `pnpm -r build` → `pnpm -r test`.
- `meta`: actionlint via `docker run --rm -v "$PWD:/work" -w /work rhysd/actionlint:latest`; `pip install yamllint && yamllint -d "{extends: relaxed, rules: {line-length: disable}}" .github reference/.env.example` (run after Step 4 so any yaml under `reference/` is covered); shell check that `reference/versions.env` defines all nine `*_TAG` keys plus `OCI_REGISTRY` (grep each, fail listing missing keys).
- `docker-build` (no push): `docker build -t facilitator:ci src/facilitator/` — proves the Dockerfile compiles the app on every PR without registry credentials.

**`.github/workflows/release.yml`** — deploy spine, stubbed now, completed in Plan 18. Trigger: `push: tags: ["v*"]`. Jobs:
- `containers`: `permissions: {contents: read, packages: write}` → `docker/setup-buildx-action@v3` → `docker/login-action@v3` (`registry: ghcr.io`, `username: ${{ github.actor }}`, `password: ${{ secrets.GITHUB_TOKEN }}`) → `docker/build-push-action@v6` for `src/facilitator/Dockerfile` with `tags: ghcr.io/denisekart/open-facilitator/facilitator:${{ github.ref_name }}, ghcr.io/denisekart/open-facilitator/facilitator:latest`, `platforms: linux/amd64,linux/arm64`, `push: true`. A step comment marks where gateway/workspace-link image entries get added (Plans 04/05).
- `publish-npm` placeholder job: `if: false` with comment — packages flip `private: false` and publish via `npm pkg set version` + `npm publish` to GitHub Packages only once Plan 01's `@ofac/protocol` is consumable; keeps the deploy seam visible but inert.
- Commented-out `sbom`/`digest-pin` steps noting Plan 18 responsibilities (spec §22.6: release requires pinned digests + green conformance matrix).

GHCR package default visibility for first push: **private**; make the `facilitator` image public in repo Packages settings when external consumers need it (owner action, not a code change).

**`.github/workflows/README.md`** — one table: workflow/job → what it proves → registry target → which plan completes it (compose jobs: Plan 11; npm publish flip + SBOM + digest pinning: Plan 18).

**`.github/dependabot.yml`** — weekly updates for three ecosystems: `nuget` (`/`), `npm` (`/` — root config covers the workspace), `github-actions` (`.github/workflows`).

## Step 7 — Docs: development environment, test guide, status flip

- **README.md**: replace the "Planned repository layout" section with the as-built root layout (mark plan-of-population per directory, same annotations as the Step 4 README stubs), and replace the "Working in this workspace" tail with a **"Development environment"** section:
  1. **Recommended path — devcontainer**: open the repo in the existing `.devcontainer` (Docker-in-Docker, .NET 10, Node 24, pnpm 12 preinstalled); nothing to install.
  2. **Manual path**: install .NET SDK per `global.json` (`10.0.40x`), Node ≥ 22, then `corepack enable && corepack install` (honors `packageManager: pnpm@12.5.1`); optional Docker for image builds.
  3. **Daily loop**: `make restore` → `make build` → `make test` → `make lint`; `make format` before commit.
  4. **Running tests**: .NET `dotnet test OpenFacilitator.slnx [-c Release] [--filter FullyQualifiedName~HealthEndpointTests]`; TS per-package `pnpm --filter @ofac/gateway test` or workspace-wide `pnpm -r test`; single file `pnpm --filter @ofac/workspace-link exec vitest run test/package.test.ts`. Behavior/integration tests (Testcontainers, dockerized Facilitator) appear from Plan 02 and will be listed here as they land.
  5. **CI is the merge gate** — link to `.github/workflows/README.md`; release = push a `v*` tag → GHCR.
- **`docs/getting-started.md`** (new, minimal — Plan 11 rewrites it for the reference env): the devcontainer/manual/build/test loop verbatim from README so a contributor has one canonical doc, plus registry coordinates table from Step 6.
- **`docs/contributing.md`** (new): repo layout, make targets table, PR gate (ci.yml green), versions.env pin policy (§22.4: tags now, digests at release; every bump needs a Plan 11 compose verification), adding-a-new-package checklist (pnpm workspace entry + `publishConfig` + catalog toolchain refs / csproj + CPM + slnx), status-flow legend cross-ref to `.omp/plans/IMPLEMENTATION_ROADMAP_PLAN.md`.
- **`.omp/plans/00-repository-skeleton-build-ci.md`**: replace with this plan's content (copy of the approved file) and set its status line.
- **`.omp/plans/IMPLEMENTATION_ROADMAP_PLAN.md`**: Plan 00 entry ⬜→🟡 at start, 🟢 on green main.
- AGENTS.md "Planned repository layout" code block: update to root-relative paths (drop `framework/` prefix, note repo root = framework tree) so agents stop creating a subdir. No `.omp/skills/` edits — Plan 00 lands no spec behavior (skill maintenance rule triggers from Plan 01).

## Critical files & anchors

| File | Why |
|---|---|
| `Directory.Build.props` + `Directory.Packages.props` | every later .NET project inherits TFM/strictness and must use CPM — get the pair right first |
| `src/facilitator/Program.cs` + `public partial class Program` | WebApplicationFactory entry point; Plan 02 replaces the stub body but keeps the pattern |
| `global.json` (`10.0.401`, `latestFeature`) | pins CI to the SDK verified locally; `setup-dotnet@v6` reads it |
| `reference/versions.env` | single pinning authority Plan 11's compose files `env_file:` consume verbatim |
| `pnpm-workspace.yaml` (+ `catalog:`) | Plan 01's `@ofac/protocol` gets added to `packages:`; toolchain versions must come from one place |

## Verification

Run at root, in order; each is a gate on the step it covers:

1. `dotnet build OpenFacilitator.slnx -c Release` → 0 warnings (TreatWarningsAsErrors proves props apply).
2. `dotnet test OpenFacilitator.slnx -c Release` → `HealthEndpointTests` passing — new-behavior proof: in-process host serves `GET /health` → 200 `{"status":"ok"}`.
3. `pnpm install --frozen-lockfile && pnpm -r build && pnpm -r test && pnpm lint` → both packages emit `dist/` + `.d.ts`, 2 vitest smoke tests pass.
4. `make restore build test lint` → all green (Makefile wiring).
5. `docker run --rm -v "$PWD:/work" -w /work rhysd/actionlint:latest` → exit 0 (run locally before pushing).
6. `docker build -t facilitator:ci src/facilitator/ && docker run --rm -p 18080:8080 facilitator:ci &` then `curl -fsS http://127.0.0.1:18080/health` → `{"status":"ok"}` — new-behavior proof of the Dockerfile + GHCR-bound artifact. Kill the container.
7. `make compose-slim` → prints Plan-11 message, exit 2 (intentional).
8. Push branch → `ci.yml` all four jobs green on the PR; §22.1 path check: `find . -name .gitkeep -path './protocol/*' | wc -l` ≥ 4. Merge to `main`, flip statuses. (Optional live proof of the release spine: push tag `v0.0.1` → `release.yml` `containers` job pushes `ghcr.io/denisekart/open-facilitator/facilitator:v0.0.1`, pullable with `docker pull`.)

## Assumptions & contingencies

- Repo stays public → standard `ubuntu-latest` runners are free. If it goes private before Plan 11, `typescript`/`dotnet` jobs still run on private-repo free minutes; no plan change.
- Test-package versions come from `dotnet add package` at execution time (latest 10.0.x / current xunit), not from this file — lift resolved numbers into CPM.
- `MATTERMOST_TAG=11.11.1` tag format is the only version value not directly confirmed; the `meta` job does not pull images, so a wrong format only surfaces at Plan 11 — fallback there: switch to the registry's documented tag pattern (`release-11.11`-style if needed), nothing else changes.
- Devcontainer already ships Node 24/pnpm 12/Docker-in-Docker; no `.devcontainer/` edits (CI's Node 22 is the floor, container's 24 satisfies `engines`).

## Deviations recorded at implementation time

- `ci.yml` `docker-build` uses `-f src/facilitator/Dockerfile .` (repo-root context) — the Dockerfile copies root-level `global.json`/props, so a `src/facilitator/` context cannot see them; matches `release.yml` (`context: .`).
- yamllint scope is `.github` only: `reference/.env.example` is dotenv, not YAML.
- Root `typescript` pinned to `^6.0.3`: tsup 9 (via rollup-plugin-dts) and typescript-eslint 8 do not support the TypeScript 7 native compiler; catalog mirrors this.
- `publish-npm` inert gate is `if: ${{ vars.ENABLE_NPM_PUBLISH == 'true' }}` instead of `if: false` (actionlint rejects constant conditions); flipping the repo variable activates it.
- `pnpm-workspace.yaml` uses pnpm 12's `allowBuilds: {esbuild: true}` (replaces `onlyBuiltDependencies`).
- `.github/workflows/README.md` documents the seam table; `meta` job checks nine `*_TAG`/`OCI_REGISTRY` plus `FACILITATOR_DIGEST` keys (plan's "nine tags + OCI_REGISTRY" — `FACILITATOR_TAG` does not exist; the Facilitator image is built, not pulled).
