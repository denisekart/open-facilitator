# Coverage ladder — Phase 1: measure, report, ratchet

**Status:** plan for `.omp/ideas/test-coverage-pyramid.md` Phase 1 (+ Phase-3
ratchet mechanics, which Phase 1 produces). Phase 2 (raising coverage) is a
later, separate plan driven by the analysis this one produces.

## Context

The repo tests a lot but measures nothing: `coverlet.collector` is referenced
(`tests/Facilitator.Tests/Facilitator.Tests.csproj`) but no target collects it,
and no vitest package configures a coverage provider. This plan builds one
repeatable entry point — fresh build → collect .NET + TS coverage → merge into
one rollup → commit a ratchet baseline — so the pyramid work has a number to
spend against. The first deliverable is the harness plus a committed baseline
and a dead-zone analysis table; no new tests yet.

Law from the idea that shapes the design: a valid number comes from a fresh
full-ladder run inside the 10 ± 5 min wall budget. Phase 1 ships the **fast
ladder** (unit + in-process integration: xUnit suites + vitest) as `make
coverage`; container/e2e/reference legs stay out of the default run and are
opt-in flags (they will blow the budget once enabled, per the idea's split
rule — that decision is explicitly deferred to the baseline analysis).

## Facts established during planning (verified this session)

- .NET test projects: `tests/Facilitator.Tests` (has `coverlet.collector` 10.1.0,
  CPM-pinned in `Directory.Packages.props`; **no other test csproj references it**
  — E2E and ScenarioWalks would each need their own `<PackageReference Include="coverlet.collector" />` if included), all with committed
  `packages.lock.json` + `RestorePackagesWithLockFile` (`Directory.Build.props`) —
  any csproj edit needs a `dotnet restore` to refresh the lock file.
- TS: 24 unit-tier `vitest.config.ts` across the pnpm workspace (list:
  `find src tools -name vitest.config.ts -not -path '*/node_modules/*'`); none has
  a `coverage` block. `@ofac/ui` (`src/facilitator/ui`) configures vitest inside
  `vite.config.ts` (`test:` block, jsdom), no separate vitest.config.
  `vitest` is catalog-pinned `^5.0.5`; `@vitest/coverage-v8@5.x` exists on npm.
- `pnpm -r test` is the existing unit-tier fan-out (`make test`).
- `.gitignore` already ignores `coverage/` and `TestResults/` — coverage output
  must NOT land in those bare directory names at merge points if we want parts
  of it committed; the committed baseline lives under `.omp/coverage/` (not
  ignored).
- `Makefile` targets to imitate: `test:` (build → dotnet test -c Release
  --no-build → pnpm -r test), `scenario-gate:` (trx logger pattern).

## Approach

### Step 1 — wire coverage collection, keep it out of `make test`

- .NET: do **not** put coverlet properties in the csproj (would tax every
  `make test`). Collect via CLI on the test project only:
  `dotnet test tests/Facilitator.Tests -c Release --no-build /p:CollectCoverage=true /p:CoverletOutputFormat=cobertura /p:CoverletOutput=<abs>/artifacts/coverage/dotnet/unit/ /p:ExcludeByFile=\"**/Migrations/**,**/wwwroot/**\"`
  — the kernel project has generated EF `Migrations/`; excluding it +
  `wwwroot` matches the idea's non-goal (generated code). If the flag set
  misbehaves with CPM/lock files, fall back to a `coverlet.runsettings` in the
  test project passed via `--settings`.
  Phase 1 scope: `Facilitator.Tests` + `Facilitator.ScenarioWalks` (ScenarioWalks
  exercises the same `Facilitator.dll`, so it needs `<PackageReference Include="coverlet.collector" />` added to its csproj + lock refresh; its coverage attributes to the `integration` tier). `Facilitator.E2E` (Playwright) stays out of the default ladder — flag-gated, Step 3.
- TS: add `"@vitest/coverage-v8": "catalog:"` to `pnpm-workspace.yaml`
  `catalog:` (pin `^5.0.5` line, matching the vitest entry) and to the
  `devDependencies` of every package that owns a unit `vitest.config.ts`.
  Enable per package via a shared config fragment, not 24 copies: create
  `tools/coverage/vitest-coverage.ts` exporting
  `coverage = { provider: "v8", reporter: ["lcov", "json-summary"], reportsDirectory: <repo>/artifacts/coverage/ts/<pkg>, all: true, include: [<pkg src globs>] }`
  and `mergeConfig` it into each config (`defineConfig(mergeConfig(coverageBase("<pkg>"), {...}))`;
  `mergeConfig` from `vitest/config`). The fragment takes the package slug so
  each reportsDirectory is distinct without 24 near-copies.
  `all: true` matters: untested files must count as 0% or the baseline flatters.
  For `src/facilitator/ui` add the same into `vite.config.ts`'s `test:` block.
  Concretely: `reportsDirectory` uses lcov reporter; per-package name derived
  from the config file's own path so each package stays independent.
- pnpm fan-out: new root script `"test:coverage": "pnpm -r --workspace-concurrency=4 test -- --coverage"`
  (vitest accepts `--coverage` on CLI; provider comes from config). Keep
  `test` untouched so `make test` cost is unchanged.

### Step 2 — `make coverage` target (fast ladder) + merge script

Makefile target `coverage: build` (imitates `test:`), running, in order, with
`artifacts/coverage/` wiped first (`artifacts/` is the new gitignored root for
raw outputs — `.gitignore` gains one line; `coverage/`-named dirs stay ignored):

1. `dotnet test tests/Facilitator.Tests ...` (Step 1 flags, tier tag `unit`),
   then ScenarioWalks likewise (tier tag `integration`).
2. `pnpm run test:coverage`.
3. `node tools/coverage-report.mjs` — the merger/reporter.

`tools/coverage-report.mjs` (plain Node, no new deps; parse Cobertura XML +
lcov by hand or with a tiny hand-rolled parser — no equivalent tool exists in
`tools/` today):
- Inputs: everything under `artifacts/coverage/` (dotnet `*.cobertura.xml`,
  ts `lcov.info`), tagged by tier from directory layout.
- Map source paths to repo-relative paths; bucket into the rollup areas from
  the idea: `src/facilitator` (minus `ui`), `src/facilitator/ui`,
  `src/gateway`, `src/workspace-link`, `src/adapters/*` (per adapter),
  `src/protocol`, `src/otel`, `tools/scenario-plane`. Exclude
  `protocol/dotnet/**Generated**` per non-goals.
- Output two files under `.omp/coverage/` (committed):
  - `baseline.json` — schema `{ "generated": iso, "areas": { "<area>": { "lines": { "pct": n, "covered": n, "total": n }, "branches": {...}, "tiers": { "<tier>": pct-or-null } } }, "tierSeconds": { ... } }`.
  - `README.md` — human rollup table (area × lines/branches %, tier mix, wall
    seconds per leg). Rewrite wholesale per run.
- Ratchet: compare each area's line % against the committed `baseline.json`;
  any area regressing > 0.5 pt (float slop for merge-order noise) fails the
  script with a diff printout, **unless** `COVERAGE_UPDATE=1`, which instead
  rewrites `baseline.json` + `README.md` and exits 0 (how you move the ratchet).
  New areas appear with their fresh number, no failure.
- The merger also prints the **dead-zone analysis** seed: per area, files
  with ≥1 test touch (appear in some suite's coverage) whose branch % < line
  % − 20 — the "has tests, untested branches" list Phase 2 consumes. Phase 1
  commits the first such analysis into `.omp/coverage/README.md`.

### Step 3 — opt-in heavy legs (behind flags, off by default)

`make coverage FULL=1` additionally runs, after the fast ladder and before the
merger (each tag `e2e` / `reference`):
- `dotnet test tests/Facilitator.E2E` with the same coverlet flags (adds
  `coverlet.collector` PackageReference + lock refresh to that csproj).
- `./referencectl down -v && ./referencectl up slim` then
  `reference/checks/verify.sh` — note the kernel runs in a **container**, so
  its .NET coverage is unmeasurable there; the reference leg contributes
  tier attribution (which flows proved) only, recorded as a non-numeric entry
  in `tierSeconds`/README. If FULL=1 pushes the run past ~15 min as measured,
  leave FULL documented-as-nightly and do NOT wire it anywhere.
Timing: `make coverage` wraps legs with `date +%s` deltas (or a tiny
`tools/coverage/with-time.sh`) so `tierSeconds` is real data, feeding the
budget decision.

### Step 4 — execute the ladder once, honestly

Run `make coverage` (fresh: `make clean`-ish state not required for unit legs,
but the run must follow a `pnpm install` after the catalog change and a
`dotnet restore` after the csproj edits). If a package's `all: true` include
glob misattributes dist artifacts, fix the per-package `coverage.include`
before committing the baseline — a wrong baseline is worse than none. Then run
twice to confirm the ratchet passes against its own output (idempotence).
Commit: config changes + `tools/coverage-report.mjs` + `.omp/coverage/`
(baseline + analysis README).

## Critical files & anchors

- `Makefile` — `test:` (lines ~12–16) as the template for `coverage:`; add
  `coverage` to `.PHONY`.
- `tests/Facilitator.ScenarioWalks/Facilitator.ScenarioWalks.csproj` +
  `tests/Facilitator.E2E/Facilitator.E2E.csproj` — add `coverlet.collector`
  only for the legs that get instrumented (ScenarioWalks default, E2E only if
  FULL=1 is kept).
- `pnpm-workspace.yaml` — `catalog:` block (~line 60s) gains the
  `@vitest/coverage-v8` entry.
- `tools/coverage-report.mjs` — new merger + ratchet (the heart of the change).
- `.omp/coverage/baseline.json` + `.omp/coverage/README.md` — committed
  artifact, new.
- `src/facilitator/ui/vite.config.ts` — the one coverage config that lives in
  a vite config, not a vitest.config.

## Verification

1. `make coverage` from a clean tree finishes < 10 min end to end (print
   `tierSeconds`; if > 10, shape per the idea: first try dropping
   ScenarioWalks to the FULL leg, report the measured split).
2. Proof of new behavior, not just green: pick a file currently at 0% that is
   *not* imported by any test (e.g. a leaf in `src/workspace-link/src` —
   confirm from the run) and show it listed in `baseline.json` with
   `lines.pct == 0` — proves `all: true` is really counting untested code.
   Then add one trivial vitest import-and-assert test for it, re-run, see the
   area % rise and the ratchet accept it via `COVERAGE_UPDATE=1`; commit
   nothing from that experiment (`git checkout` the test + baseline).
3. Ratchet works: hand-edit `baseline.json` to bump one area by +5 pts, run
   `make coverage` (collect step may reuse artifacts via
   `node tools/coverage-report.mjs` alone) → exits nonzero naming the area;
   restore.
4. Regression: `make test` wall time unchanged (± noise) — collection must not
   have leaked into the normal gate.
5. `pnpm lint` and `node protocol/tools/agents-docs-check.mjs` (make
   docs-check) pass with the new tool + docs.

## Assumptions & contingencies

- Phase 2 tranche selection is deferred: the analysis table this plan
  produces is its input; nothing here pre-commits a coverage quota.
- If vitest v8 coverage inside `pnpm -r` fan-out proves flaky on one package
  (jsdom + svelte stores), run that package's coverage leg separately in the
  Makefile rather than dropping it — losing `src/facilitator/ui` from the
  baseline is not acceptable (it's a named rollup area).
- If Cobertura↔lcov branch semantics diverge enough to make rollup %s
  misleading, fall back to per-ecosystem rollup columns (dotnet % / ts %) per
  area instead of one merged number, and say so in the README — never
  silently average apples into oranges.
- CI placement untouched (idea defers it to post-baseline analysis).
- `docs/` mentions: add a short "measuring coverage" section to
  `docs/getting-started.md` §(dev loop) so the ladder is discoverable; the
  mechanical doc pass is not planned beyond that one pointer.
