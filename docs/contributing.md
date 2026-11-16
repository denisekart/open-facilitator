# Contributing

This page is the working path into the repository: the commands you run
daily, the gate every PR must pass, and the laws that keep the wire contract
and the version pins honest. The area `AGENTS.md` signposts carry the
per-directory invariants; this page carries the repo-wide law.

| You came to… | Read |
|---|---|
| Get oriented in the tree | [Repository layout](#repository-layout) below, then the layout map in the [root README](../README.md#repository-layout) |
| Run the daily loop | [Make targets](#make-targets) |
| Change the wire | [Protocol changes](#protocol-changes) |
| Know what CI will check | [PR gate](#pr-gate) |
| Add or bump a dependency | [Version pinning policy](#version-pinning-policy), [Adding a new package](#adding-a-new-package) |

## Repository layout

The repository root is the whole framework: kernel, runtimes, adapters, the
wire contract, the reference environment, and this documentation tree.
`docs/specs/` holds the target design; the [root README](../README.md#repository-layout)
maps every top-level directory and states what lives where.

## Make targets

```bash
make restore   # after cloning, or when dependencies change
make build     # before running anything
make test      # the kernel + TypeScript + conformance loop
make lint      # what CI will check about formatting
```

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

Individual tests, per tier: the filter/one-file commands are in
[getting-started.md](getting-started.md#2-build--test-loop).

## Protocol changes

The wire contract is schema-first: edit the JSON Schema under `protocol/`,
add valid+invalid fixtures registered in
`protocol/conformance-fixtures/index.json`, then run `pnpm protocol:codegen`
and commit the regenerated trees (`src/protocol/src/generated/`,
`protocol/dotnet/Ofac.Protocol/Generated/`, `src/protocol/schemas/`).
`make protocol` runs codegen, the OpenAPI stub validation, and the
`git diff --exit-code` drift gate; CI's `protocol` job enforces the same, and
both fixture harnesses (`dotnet run --project
protocol/tools/Validate.Fixtures`, `pnpm -r test`) must pass. The full
versioning rules and the v2 recipe: [protocol.md](protocol.md).

A protocol change lands together with its documentation: the type-registry
table in [protocol.md](protocol.md#type-registry) and the signposts/skills
that cite the changed paths ride the same commit.

## PR gate

`ci.yml` must be green on the PR:

|Job|Covers|
|---|---|
|`dotnet`|build + test on the `global.json` SDK|
|`scenarios`|the scenario gate + composed walks|
|`typescript`|lint + build + test on Node 22|
|`protocol`|codegen drift gate + OpenAPI validation|
|`meta`|the AGENTS.md drift gate, actionlint, yamllint, `reference/versions.env` key completeness|
|`docker-build`|Facilitator + node-runtime images compile|
|`link-e2e`, `gateway-e2e`, `admin-e2e`|live-kernel tiers|
|`features`|the install-contract matrix|

Release jobs run only on `v*` tags — see
[`.github/workflows/README.md`](../.github/workflows/README.md) for
job-by-job scope.

**Acceptance law (agents included):** the slim reference environment is part
of every task's acceptance criteria. The hosted
`ref-env-bringup`/`ref-env-verify` jobs are compute-gated off (free-tier), so
before declaring any task done, run the whole gauntlet **locally**:
`make build test lint docs-check`, `pnpm -r --if-present typecheck`, and
against a booted-but-unseeded stack:

```bash
./referencectl down -v && ./referencectl up slim --no-seed
./referencectl verify --json   # 12 steps, exit 0
```

Behavior covered by a scenario manifest row
(`docs/scenarios/scenario-bindings.json`) obliges updating the binding and
rerunning `make scenario-gate`.

## Version pinning policy

- All reference image tags live in `reference/versions.env`; the compose
  files interpolate it verbatim via `--env-file` — never hardcode a tag
  elsewhere.
- Tags now, digests at release: the release process fills the `*_DIGEST`
  keys.
- Every version bump needs a compose verification run: `make compose-slim`
  to a healthy stack (the CI `ref-env-bringup` job enforces the same on every
  PR).
- .NET: Central Package Management — add `<PackageVersion>` to
  `Directory.Packages.props`, reference versionless in csproj.
- TypeScript: shared toolchain versions live in the `catalog:` block of
  `pnpm-workspace.yaml`.
- Admin SPA (`src/facilitator/ui`): Svelte 5 + vite. `pnpm -r build` emits
  hashed assets into `src/facilitator/wwwroot/` — pure build output,
  git-ignored; the kernel boots without it and the Docker build owns its own
  `pnpm --filter @ofac/ui build` stage. Browser tier: `make admin-e2e`
  (Playwright chromium; CI job `admin-e2e`).

## Adding a new package

**TypeScript (pnpm workspace):**

1. Create `src/<name>/` with `package.json`: name `@ofac/<name>`,
   `"private": true` until consumable, `"publishConfig": { "registry":
   "https://npm.pkg.github.com" }`, `repository` field with `directory`.
2. Add the path to `packages:` in `pnpm-workspace.yaml`.
3. Dev-dependencies on shared tooling use `"catalog:"`.
4. Add build/test to the recursive scripts (tsup + vitest conventions per
   existing packages).

**.NET:**

1. Create the csproj without `TargetFramework` (inherited from
   `Directory.Build.props`).
2. Packages: `<PackageReference Include="X" />` + matching `<PackageVersion>`
   in `Directory.Packages.props`.
3. `dotnet sln add <path>` — the solution file is `OpenFacilitator.slnx`.

A new source area under `src/` also lands its own `AGENTS.md` signpost in
the same commit (`make docs-check` enforces this).

## Next

- Publishing a release: [release-setup.md](release-setup.md)
- The behavioral conformance system behind the `scenarios` job:
  [scenarios/README.md](scenarios/README.md)
- Per-area invariants: the nearest `AGENTS.md`
