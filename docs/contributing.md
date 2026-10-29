# Contributing

## Repository layout

Repo root **is** the spec §22.1 `framework/` tree — no `framework/` subdirectory, `docs/specs/` is the spec node. Each directory's README names the plan that populates it (see repository layout in the root README).

## Make targets

|Target|What it does|
|---|---|
|`make restore`|`dotnet restore OpenFacilitator.slnx` + `pnpm install`|
|`make build`|`pnpm -r build` (stages the SPA's wwwroot) then .NET Release build (depends on restore)|
|`make test`|Kernel .NET tests (--no-build) + `pnpm -r test` + conformance fixtures (depends on build; browser tier is `make admin-e2e`)|
|`make admin-e2e`|Browser tier: builds the SPA + kernel, installs chromium (staged Playwright `cli.js` via node), runs `tests/Facilitator.E2E` (Docker needed)|
|`make lint`|`dotnet format --verify-no-changes` + eslint + prettier --check|
|`make format`|`dotnet format` + `pnpm format`|
|`make compose-slim` / `compose-full`|Boots the reference environment (`reference/compose.slim.yaml`, plus the `compose.full.yaml` overlay); `make compose-down` tears it down|
|`make clean`|`dotnet clean` + remove TS dist outputs and node_modules|

## Protocol changes

The wire contract is schema-first: edit the JSON Schema under `protocol/`, add valid+invalid fixtures registered in `protocol/conformance-fixtures/index.json`, then run `pnpm protocol:codegen` and commit the regenerated trees (`src/protocol/src/generated/`, `protocol/dotnet/Ofac.Protocol/Generated/`, `src/protocol/schemas/`). `make protocol` runs codegen, the OpenAPI stub validation, and the `git diff --exit-code` drift gate; CI's `protocol` job enforces the same, and both fixture harnesses (`dotnet run --project protocol/tools/Validate.Fixtures`, `pnpm -r test`) must pass. Full rules: [docs/protocol.md](protocol.md).

## PR gate

`ci.yml` must be green on the PR: `dotnet` (build + test on the `global.json` SDK), `typescript` (lint + build + test on Node 22), `meta` (actionlint, yamllint, `reference/versions.env` key completeness), `docker-build` (Facilitator image compiles). Release jobs run only on `v*` tags — see `.github/workflows/README.md`.

**Acceptance law (agents included):** the slim reference environment is part of every task's acceptance criteria. The hosted `ref-env-bringup`/`ref-env-verify` jobs are compute-gated off (free-tier), so before declaring any task done, run the whole gauntlet **locally**: `make build test lint docs-check`, `pnpm -r --if-present typecheck`, and against a booted-but-unseeded stack:

```bash
./referencectl down -v && ./referencectl up slim --no-seed
./referencectl verify --json   # 11 steps, exit 0
```

## Version pinning policy (spec §22.4)

- All reference image tags live in `reference/versions.env`; the compose files
  interpolate it verbatim via `--env-file` — never hardcode a tag elsewhere.
- Tags now, digests at release: the release process fills the `*_DIGEST` keys.
- Every version bump needs a compose verification run: `make compose-slim` to a
  healthy stack (the CI `ref-env-bringup` job enforces the same on every PR).
- .NET: Central Package Management — add `<PackageVersion>` to `Directory.Packages.props`, reference versionless in csproj.
- TypeScript: shared toolchain versions live in the `catalog:` block of `pnpm-workspace.yaml`.
- Admin SPA (`src/facilitator/ui`): Svelte 5 + vite. `pnpm -r build` emits hashed assets into
  `src/facilitator/wwwroot/` — pure build output, git-ignored; the kernel boots without it and the
  Docker build owns its own `pnpm --filter @ofac/ui build` stage. Browser tier: `make admin-e2e`
  (Playwright chromium; CI job `admin-e2e`).

## Adding a new package

**TypeScript (pnpm workspace):**
1. Create `src/<name>/` with `package.json`: name `@ofac/<name>`, `"private": true` until consumable, `"publishConfig": { "registry": "https://npm.pkg.github.com" }`, `repository` field with `directory`.
2. Add the path to `packages:` in `pnpm-workspace.yaml`.
3. Dev-dependencies on shared tooling use `"catalog:"`.
4. Add build/test to the recursive scripts (tsup + vitest conventions per existing packages).

**.NET:**
1. Create the csproj without `TargetFramework` (inherited from `Directory.Build.props`).
2. Packages: `<PackageReference Include="X" />` + matching `<PackageVersion>` in `Directory.Packages.props`.
3. `dotnet sln add <path>` — the solution file is `OpenFacilitator.slnx`.

## Status flow for plans

Plans live in `.omp/plans/`; the catalogue and status legend (⬜ not-started → 🔵 replanning gate → 🟡 in-progress → 🟢 done) live in `.omp/plans/IMPLEMENTATION_ROADMAP_PLAN.md`.
