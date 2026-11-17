# Code test coverage: measure it, then raise it along the pyramid

**Status:** idea, pre-plan. Anchors are file paths read from the tree
(2026-11-17), not memory.

Goal: get an honest, repeatable coverage number for everything we ship,
then spend deliberate effort raising it — following the testing pyramid
(unit → integration → e2e) — and fix any unintended behavior the push
surfaces. We are **not** targeting 100%; we stop where the marginal test
stops earning its keep.

## Why now

The repo tests a lot but *measures* nothing:

- .NET: `coverlet.collector` is already referenced
  (`tests/Facilitator.Tests/Facilitator.Tests.csproj`) but no `make`
  target collects or merges coverage; `make test` just runs xUnit.
- TypeScript: vitest runs across the workspace packages (`src/gateway`,
  `src/workspace-link`, `src/adapters/*`, `src/facilitator/ui` …) with no
  `coverage` provider configured anywhere.
- The tiers exist but are unaggregated: unit-ish xUnit suites, the
  Playwright browser tier (`tests/Facilitator.E2E`), scenario walks
  (`tests/Facilitator.ScenarioWalks`), the reference-env acceptance run
  (`reference/checks/verify.sh`), and provider E2E. Nobody can say what
  fraction of the kernel, gateway, link SDK, adapters, or console each
  tier actually lights up — so nobody can say where a new test would pay
  off most.

Without a number, "improve the tests" is vibes. With a baseline, it is a
budget allocation problem.

## The two laws of this effort

1. **Valid run = fresh environment + freshly built code.** A coverage
   number (or a green suite) only counts when produced by the
   full-ladder script: clean checkout state → build → bring up a fresh
   reference env (`./referencectl down -v && ./referencectl up slim`,
   fresh volumes) → run tiers → collect. Reusing a warm stack or a stale
   `wwwroot`/`bin` is a measurement, not a proof. This must be one
   command, not a human checklist, or it will rot after the first week.
2. **The 10-minute wall budget (± 5) still applies** to any test run we
   declare "the" run. Consequences the design must respect:
   - Coverage collection adds overhead (instrumentation, I/O, merge) —
     it has to fit inside the budget, not beside it.
   - The pyramid is also the *performance* answer: unit tests are cheap
     to rerun; every point of coverage we win at the unit tier buys back
     minutes we don't spend in container-heavy legs.
   - If the ladder overruns even after shaping, split: a fast gate
     (unit + integration, budgeted) that runs always, and a nightly
     full-ladder run (adds e2e + reference legs) that produces the
     headline number.

## Phase 1 — measure (one repeatable ladder, one report)

- One entry point (tentatively `make coverage`): fresh build →
  `dotnet test /p:CollectCoverage` with coverlet per suite →
  `vitest --coverage` (v8 provider) per TS package → optional browser /
  reference legs behind a flag.
- **Merge into one artifact**, not five: Cobertura for .NET,
  lcov/cobertura for TS, joined into a single per-directory rollup
  (`src/facilitator`, `src/gateway`, `src/workspace-link`,
  `src/adapters/*`, `src/facilitator/ui`, `protocol/*`, `tools/*`) plus a
  per-tier attribution (which tier covered what). A number without tier
  attribution cannot guide pyramid work.
- **Baseline snapshot committed** as data (e.g.
  `.omp/coverage/baseline.json` + a short human summary) so every later
  change is diffable, and a drift check: the script fails if a directory
  regresses, so coverage can only ratchet upward.
- Deliverable of the phase: the analysis — a per-area table: coverage
  %, tier mix, and the notable dead zones (untested branches in code
  that *does* have tests), which is what actually drives Phase 2.

## Phase 2 — raise it, pyramid-first

Allocation rule: buy coverage where it is cheapest per point and where
risk concentrates — event first, then the tier that fits the seam.

1. **Unit tier first** (target: the bulk of the gain). Pure logic with
   awkward seams gets tests at its own level, not via a container:
   policy evaluation, validator/guard-chain branches, workflow step
   machines and trigger matching (`src/facilitator/Kernel/Workflows`),
   outbox/backoff paths, protocol validation edge cases, gateway routing
   tables, console stores (`*.svelte.ts`: route, deck, health, auth
   token decoding).
2. **Integration tier second**: EF/Postgres legs, link-plane dispatch
   through real containers, adapter ↔ kernel wire contracts — the
   existing xUnit + testcontainers harness, extended along the baseline's
   integration dead zones.
3. **E2e tier last and sparingly**: Playwright (`tests/Facilitator.E2E`)
   and scenario walks cover flows, not branches; expect them to move the
   number the least per minute of budget. New e2e only where a
   user-visible journey is unpinned.

**Stop condition:** per-area diminishing returns, explicitly declared
per directory, e.g. stop when (a) new tests mostly exercise generated
code, DTO plumbing, or defensive `throw`s, or (b) a point of coverage
costs more than ~1 min of wall budget. Documented per area in the
report — a deliberate plateau, not abandonment. Numbers are expected to
land somewhere like high-60s/70s overall with the kernel core notably
higher; the exact line is the analysis's call, not a quota.

**Side effects are deliverables.** During the push, anything a test
pokes loose — a branch that cannot be reached, a swallowed error, an
inconsistency between behavior and docs — gets either fixed or filed as
its own known-issue entry in the run log. "Tests found things" is a
success criterion equal to the coverage delta.

## Phase 3 — hold the line

- The ratchet: fresh-env `make coverage` in the release runbook
  (`ref-env`-style gates), baseline updated only in the same commit that
  moves code.
- CI placement decision deferred to the analysis: likely unit+integration
  per PR (inside 10 ± 5), full ladder nightly.

## Explicit non-goals

- 100% coverage, or coverage quotas per file.
- Covering generated code (`protocol/*` codegen output) or pure
  composition-root wiring.
- Rewriting existing tests for style — new tests piggyback on the
  existing harnesses (`Facilitator.Tests` infrastructure, vitest setups,
  `KernelWebHostFixture`) rather than inventing parallel ones.
- Mutation testing (a plausible Phase 4 once the number is high enough to
  make weakening questions interesting).

## First concrete steps

1. Spike `make coverage` locally (no CI): coverlet on
   `Facilitator.Tests`, `--coverage` on 2–3 representative TS packages,
   a merge script (`tools/coverage-report.mjs`), and see the real numbers
   + the real wall-time cost inside the 10 ± 5.
2. Run the ladder once on a truly fresh env; commit the baseline and the
   dead-zone analysis.
3. Turn the analysis into a ranked work list (area × tier × expected
   gain × minutes) and pick the first pyramid-ordered tranche.
