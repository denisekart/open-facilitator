# Contributing

## Repository layout

Repo root **is** the spec §22.1 `framework/` tree — no `framework/` subdirectory, `docs/specs/` is the spec node. Each directory's README names the plan that populates it (see repository layout in the root README).

## Make targets

|Target|What it does|
|---|---|
|`make restore`|`dotnet restore OpenFacilitator.slnx` + `pnpm install`|
|`make build`|.NET Release build + `pnpm -r build` (depends on restore)|
|`make test`|.NET tests (--no-build) + `pnpm -r test` (depends on build)|
|`make lint`|`dotnet format --verify-no-changes` + eslint + prettier --check|
|`make format`|`dotnet format` + `pnpm format`|
|`make compose-slim` / `compose-full`|Placeholder (exit 2) until Plan 11 ships `reference/referencectl`|
|`make clean`|`dotnet clean` + remove TS dist outputs and node_modules|

## PR gate

`ci.yml` must be green on the PR: `dotnet` (build + test on the `global.json` SDK), `typescript` (lint + build + test on Node 22), `meta` (actionlint, yamllint, `reference/versions.env` key completeness), `docker-build` (Facilitator image compiles). Release jobs run only on `v*` tags — see `.github/workflows/README.md`.

## Version pinning policy (spec §22.4)

- All reference image tags live in `reference/versions.env`; compose files (Plan 11) consume it verbatim via `env_file:` — never hardcode a tag elsewhere.
- Tags now, digests at release: the release job (Plan 18) fills the `*_DIGEST` keys.
- Every version bump needs a compose verification run (available from Plan 11; until then, bumps are reviewed by hand).
- .NET: Central Package Management — add `<PackageVersion>` to `Directory.Packages.props`, reference versionless in csproj.
- TypeScript: shared toolchain versions live in the `catalog:` block of `pnpm-workspace.yaml`.

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
