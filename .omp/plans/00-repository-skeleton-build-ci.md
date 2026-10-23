# Plan 00 — Repository skeleton, build system, and CI spine

**Status:** ⬜ not-started
**Depends on:** —

Status legend: ⬜ `not-started` → 🔵 `replanning` (mandatory gate) → 🟡 `in-progress` → 🟢 `done`. This plan starts with a replanning pass (flip to 🔵): re-verify assumptions against the specs and prior plans' state, run the research below, finalize this file's detail design, then flip to 🟡.

## Objective

Turn the spec-only repo into a buildable monorepo matching spec §22.1 (`framework/` tree: `src/facilitator`, `src/gateway`, `src/workspace-link`, `src/adapters`, `protocol/`, `features/`, `examples/`, `reference/`, `docs/`, `Makefile`) with green CI on an empty-but-compiling codebase.

## Replanning gate

- Run `/scaffold` (stack `dotnet-aspire-svelte`) and reconcile its output against §22.1.
- Decide sln/csproj granularity: one `Facilitator.sln`, projects `Facilitator`, `Facilitator.Tests`; pnpm workspace for TS packages `@ofac/gateway`, `@ofac/workspace-link`.
- Fix TS toolchain: Node 22 LTS, vitest, tsup.
- Pin .NET 10 SDK in `global.json`.

## Scope

- Directory tree with stub projects/packages that compile.
- `versions.env` pinning Postgres 17, Keycloak 26.x, SeaweedFS digest, Aspire dashboard release, Kafka 4.2.x, Forgejo 16.x, Mattermost TE, Ollama (§22.4).
- `Makefile` targets `build|test|lint|compose-slim|compose-full` placeholders.
- GitHub Actions per §22.6: build .NET + TS, validate configs, unit tests; compose jobs gated until Plan 11.
- `.editorconfig`, NuGet/npm config, `.gitignore` additions (`reference/secrets/`, `bin/`, `obj/`, `dist/`, `node_modules/`).

## State after

`make build` and `make test` pass locally and in CI on empty projects; every §22.1 path exists; CI is the merge gate from here on.

## Testing

CI runs dotnet build/test + `pnpm -r build/test` on stubs; no behavior tests yet.

## Documentation

`README.md` build instructions section; `docs/contributing.md` stub (repo layout, make targets, PR gate).

## Architecture fit

§22.1 (paths), §22.4 (pinning), §22.6 (CI/release contract). The static skeleton all later plans write into.

## Additional resources

Spec §22.1/§22.4/§22.6; Dev Container repo `.devcontainer/`; `dotnet-aspire-svelte` scaffold; GitHub Actions `actions/setup-dotnet@v4`, `pnpm/action-setup`.
