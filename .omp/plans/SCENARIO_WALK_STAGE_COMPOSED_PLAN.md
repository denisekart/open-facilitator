# Scenario-walk stage: composed scenario tests as an isolated CI stage

## Context

The scenario gate today is bookkeeping over per-feature proofs: 612 feature tests stay inside one feature's law, the 116 ScenarioGate tests only prove the bindings manifest doesn't rot, and nothing replays a scenario id as a *sequence*. The audit's 33 `partial` verdicts are precisely the missing compositions. This plan adds an isolated test assembly `tests/Facilitator.ScenarioWalks/` with 8 cross-feature walk scripts, makes the gate + audit runner resolve evidence across both assemblies, adds a `scenarios` CI job that owns its required-check name and fails when an audit verdict regresses, and lands the cross-language golden thread as a manually-run vitest acceptance file (no CI wiring yet — owner ruling: manual for now).

Settled decisions (owner): roster = the full eight walks; golden thread = manual-only this slice; audit diff = fail on regressions, warn on improvements.

## Step 1 — Clock seam on `KernelTestFactory` (public helper, no InternalsVisibleTo)

In `tests/Facilitator.Tests/Infrastructure/KernelTestFactory.cs` (the `internal FakeTimeProvider? Clock` property, ~line 64), add two public members and keep `Clock` internal:

```csharp
/// <summary>Advance the fake clock (factory built with fakeClock:true); throws when no fake clock is installed.</summary>
public void AdvanceClock(TimeSpan by) =>
    (Clock ?? throw new InvalidOperationException("factory created without fakeClock:true")).Advance(by);

/// <summary>Current fake-clock instant (wall clock when no fake clock is installed).</summary>
public DateTimeOffset ClockUtcNow => Clock?.GetUtcNow() ?? DateTimeOffset.UtcNow;
```

`FakeTimeProvider` is internal in `Infrastructure/LinkTestHarness.cs` (~line 249, has `Advance(TimeSpan)` + `GetUtcNow()` override). The 31 existing `.Clock` reads in 12 test files stay as they are — no churn; the public seam exists so the walk assembly (which gets internals neither way) can drive the clock. Do NOT add `InternalsVisibleTo`.

## Step 2 — Cross-assembly evidence resolution: prefix + shared theory base

Evidence id notation gains one prefix: ids starting `ScenarioWalks:` resolve against the walk assembly; bare `Class.Method` keeps resolving against `Facilitator.Tests` (all 75 existing covered/partial rows unchanged — no manifest rewrite for existing ids).

1. New file `tests/Facilitator.Tests/ScenarioGate/ScenarioEvidenceResolutionTestsBase.cs`: `public abstract class` holding the moved `EveryEvidenceIdResolves` logic PLUS the moved helpers it needs (`FindRepoRoot`, `Load`, the private `Binding` record) and the evidence-matching filter, parameterized by two protected abstracts:
   - `protected abstract System.Reflection.Assembly Subject { get; }` — the assembly whose `GetTypes()` resolve type names (keep the existing artifact-family twin rule `t.Name == typeName || t.FullName == typeName || t.Name == typeName+"Base" || "Fs"+typeName || "S3"+typeName`).
   - `protected abstract string EvidencePrefix { get; }` — `""` means "all dotnet evidence that does NOT contain `:`"; a nonempty prefix (e.g. `"ScenarioWalks:"`) means "only ids starting with that prefix". `js:` ids are skipped here exactly as today.
   - Theory data: scenario ids having ≥1 evidence matching the prefix rule (so the walk-side subclass only checks ids that actually carry walk evidence).
2. `ScenarioBindingsTests` becomes `: ScenarioEvidenceResolutionTestsBase` with `Subject = typeof(ScenarioBindingsTests).Assembly`, `EvidencePrefix = ""`. `ManifestCoversTheCatalogExactly` and `ManualLaneMatchesTiers` stay where they are, unchanged.
3. Walk assembly gets `ScenarioWalkBindingsTests.cs`: `public sealed class ScenarioWalkBindingsTests : ScenarioEvidenceResolutionTestsBase` with `Subject = typeof(ScenarioWalkBindingsTests).Assembly`, `EvidencePrefix = "ScenarioWalks:"`. It needs no collection attribute (DB-free, manifest + reflection only) and must not reference fixture containers.

Verdict vocabulary check stays identical to the `scenario-catalog-audit` skill's list.

## Step 3 — The `tests/Facilitator.ScenarioWalks/` assembly

New project files:

- `Facilitator.ScenarioWalks.csproj` — no `TargetFramework` (inherited from `Directory.Build.props`, net10.0). `ProjectReference` to `../Facilitator.Tests/Facilitator.Tests.csproj` (gives `KernelTestFactory`, `KeycloakFixture`, `KernelOps`, `TestDbProbe`, `PostgresFixture` — all public). `PackageReference` (no versions — CPM pins in `Directory.Packages.props`): `Microsoft.NET.Test.Sdk`, `xunit`, `xunit.runner.visualstudio`. Nothing else; `Microsoft.AspNetCore.Mvc.Testing` and `Testcontainers.PostgreSql` arrive transitively.
- `packages.lock.json` — generated, committed; CI restores the solution with `--locked-mode` so it must exist before CI passes.
- `GlobalUsings.cs` — copy the usings from `tests/Facilitator.Tests/GlobalUsings.cs` that the walks need plus `global using Facilitator.Tests.Infrastructure;` (verify the Facilitator.Tests file's exact set first and mirror, don't invent).
- `WalkCollection.cs`:
  ```csharp
  [CollectionDefinition("postgres:walks")]
  public sealed class WalkCollection : ICollectionFixture<PostgresFixture>, ICollectionFixture<KeycloakFixture>;
  ```
  Follow `tests/Facilitator.Tests/Infrastructure/PostgresCollections.cs`. xUnit constructs one fresh `PostgresFixture`/`KeycloakFixture` per collection in the walk process — that IS the isolation the `GcDbFixture`/`DispatchDbFixture` lesson demands (the fake-clock family never shares a DB with the 612-test families, which live in the other process anyway). All 8 walk classes carry `[Collection("postgres:walks")]` → strictly sequential, one shared container, no fixed sleeps.
- Visibility law: only PUBLIC types cross the assembly line — `KernelTestFactory`, `PostgresFixture`, `KeycloakFixture`, `TestDbProbe`, `KernelOps` are public; `FakeTimeProvider` and `JsonWire` (the `ReadJsonAsync` helper in KernelTestFactory.cs) are internal and invisible here. Walks read response bodies with plain `System.Text.Json` and drive the clock solely through the Step-1 public helpers.
- `AGENTS.md` — ≤40 lines: the three laws (one collection fixture; clock is DRIVEN via `factory.AdvanceClock(...)` then swept through `POST /api/admin/sweeps/{name}` — never wall-clock waits; waits ride `TestDbProbe` polling on timeline/inbox rows), "a walk chains segments already proven by the tests bound to its scenarios in the manifest — if a segment isn't proven, write that feature test first, don't fake a walk green", and the binding-update procedure (edit `gen-bindings.py` B-table → regenerate → gate).
- `.gitignore` needs no change (`TestResults/` already ignored, Makefile writes trxs under `TestResults/`).

`KernelTestFactory` ctor already supports `fakeClock: true` and its ctor takes a `PostgresFixture`; walks construct `new KernelTestFactory(fixture, fakeClock: true, ...)` per walk. Second-actor identities: the shared realm (`reference/keycloak/realm.json`) seeds `alice` (group `/teams/team-alpha`), `bob` (`/teams/team-beta`), `carol` (no group), passwords = usernames, via `KeycloakFixture.TokenForUserAsync(user, user)` / `ActorOfAsync`. Sufficient for every walk below; no realm edits.

## Step 4 — The eight walks

One class per walk in its own file, each `[Collection("postgres:walks")]`. General construction law for each: copy setup/assertion patterns from the tests ALREADY bound to its scenario ids in `docs/scenarios/scenario-bindings.json` (the segment sources named below), chain them across the feature boundary, assert the seam with `TestDbProbe` (`TimelineEventCount`, `OutboxIdByCommand`, `InboxState`, …) and sweep triggers (`POST /api/admin/sweeps/{nudge|approvals|schedules|session-loss}` — handler `src/facilitator/Kernel/Admin/AdminEndpoints.cs:108`; name `audit-retention` also exists). Webhook injection rides the public sink route `POST /api/ingress/webhooks/{provider}` (`src/facilitator/Kernel/Integrations/WebhookIngressEndpoints.cs`) in-proc via `factory.CreateClient()` — verbatim-bytes law preserved; no Kestrel needed (the factory's `useKestrel`/`ServerAddress` exists if a later walk needs sockets).

| File | Scenario ids | Chained segments (source tests to mine) | Seam assertion (the new proof) |
|---|---|---|---|
| `HeldActionChainWalkTests.cs` | C5→N6→C7 | `ApprovalLifecycleTests` (fake-clock expiry), slow-consume denial per N6 row, `ConsentAuthorityTests.NudgeBeforeExpiryWritesTimelineAndStampsOnce` + `TwoRespondersOneDecidedAndTheLoserSeesWhoDecided` | held action created → `AdvanceClock` past expiry + nudge sweep → second human (bob token) consumes → timeline shows ONE decision + loser-visibility event; expired leg denied in the same walk |
| `AutomationDialogueWalkTests.cs` | N2 | the two evidence tests bound to N2 (shared loop budget) | two automation actors (link-enrolled, per `EnrollmentTokenTests` mint/redeem flow) exchange steers → budget trips as ONE stop event, not two |
| `SelfWriteTwoAdaptersWalkTests.cs` | N29 | `WebhookSinkTests.RawDeliveryQueuesWebhookCommandOnceAndReplayDedupes` + N29's bound evidence (self-write detection + exchange cap) | same event injected as webhook for provider A and echoed back via provider B's sink → self-write caught, cap counted across both legs |
| `MergeDuringFollowupWalkTests.cs` | A11 | `WorkRecordTests.Merge_KeepsLoserHistory_RedirectsToSurvivor_ExposesLink` | merge executed while a follow-up steer is in flight → steer lands on survivor, loser trail intact |
| `ReviewForkWalkTests.cs` | E4 (xUnit-side chain) | `WebhookSinkTests` + `ProviderIngressTests.InputOnCompletedWork_ForksSuccessor_AndNamesIt` | review payload to sink → outbox `command.provider.webhook.v1` observed → normalized `event.provider.review.v1` fed back the way the adapter does → `review.decision` timeline row + forked successor with `successor` link (`TestDbProbe.WorkLinkCount`) |
| `RestartNoDoubleAskWalkTests.cs` | N12 | `RestartSurvivalTests.RestartAfterAcceptedEvents_NoLossNoDuplicate` (restart = dispose factory, new factory on same fixture) + `ConsentAuthorityTests.NudgeBeforeExpiryWritesTimelineAndStampsOnce` | pending ask before restart → after restart, one nudge sweep + approvals sweep → exactly one re-ask delivery, settled approval stays settled → this closes N12's missing leg: flip N12 to `covered` |
| `CapacityNudgeSagaWalkTests.cs` | D2 + nudges | N2/N16 nudge-sweep legs + `OpsSurfaceTests` bound to D2 | capacity-deferred assignment → advance clock → nudge sweep fires once → human approves via second actor → dispatch proceeds (outbox row delivered) |
| `StaleMomentWalkTests.cs` | N30 | N30's bound evidence (generation fences + consume-time authorization) | executor reconnect bumps generation, stale-token consume refused + audited, fresh-token consume on the same held action accepted once |

Every walk: empty/missing-state handling = assert the documented 4xx/problem body when probing a refusal leg (the segment tests already pin the shapes); no new error strings.

Manifest update for all 8: append a `"ScenarioWalks:<Class>.<Method>"` entry to the evidence array of EVERY scenario id the walk names, in the B-table of `tools/scenario-plane/gen-bindings.py` (e.g. `"ScenarioWalks:HeldActionChainWalkTests.HeldActionNudgedThenDecidedOnce"`); update the note text of ids whose leg is now walked. Verdict changes in this slice: **N12 partial→covered** only; every other touched id keeps its verdict (composition evidence, not a new Check-line closure — the 33-partials conversion is a later slice per-id triage).

## Step 5 — Plane: 0-second 429 mode + manual golden thread

`tools/scenario-plane/src/fake-provider.ts`: `Mode = "ok" | "403" | "429" | "vanished"` (line ~23) and the 429 arm hardcodes `res.setHeader("retry-after", "2")` (lines ~170-172). Add mode `"429-now"`: same 429 body, `retry-after: 0`, update the mode-validation predicate at line ~205 (the `return v === "ok" || v === "403" || v === "429" || v === "vanished"` expression — exact enclosing function name unverified, confirm on read) and the header comment block.

New `tools/scenario-plane/test/chain.golden.test.ts` — the golden thread, **manual-only**: starts the github adapter the same way `test/provider-legs.e2e.test.ts` starts the adapter legs, but pointed at a developer-running kernel on :8088 (`make compose-slim` brings kernel :8088 + Keycloak :8080 with the realm imported) plus `startFakeProvider()`; walks the Step-8 acceptance chain: ask in chat → `vcs.issue.create` leg → PR webhook in → review decision → kernel `review.decision` timeline → forked successor work (assert via kernel API). Use the 429-now mode on one outbound leg to prove the retry path deterministically. package.json: add script `"test:golden": "vitest run test/chain.golden.test.ts"` AND extend the existing `"test"` script's exclude to also skip it: `vitest run --exclude 'test/**/*.e2e.test.ts' --exclude 'test/**/*.golden.test.ts'` (otherwise `pnpm test` in CI would start pulling a kernel). Makefile: add `scenario-golden:` target running that script + add to the `.PHONY` list on line 1.

No CI wiring, no manifest re-verdict for the golden thread (its legs are already evidenced; the file is documented in `tools/scenario-plane/README.md` as the manual T2 chain proof).

## Step 6 — Makefile + CI `scenarios` job

Makefile, scenario-gate section (~lines 108-132), keeping the `TRX ?=` default pattern:

```make
WALK_TRX ?= tests/Facilitator.ScenarioWalks/TestResults/scenario-walks.trx
scenario-walks: build
	dotnet test tests/Facilitator.ScenarioWalks/Facilitator.ScenarioWalks.csproj -c Release --no-build \
	  --blame-hang-timeout 2m --logger "trx;LogFileName=scenario-walks.trx"
```

Extend the existing `scenario-gate` recipe's runner invocation with `$(if $(wildcard $(WALK_TRX)),--dotnet-trx $(WALK_TRX),)` so a local `make scenario-gate` after `make scenario-walks` cross-checks both trxs. Add `scenario-walks scenario-golden` to `.PHONY`.

`tools/scenario-plane/src/runner.mjs`: two changes.
1. `--dotnet-trx` becomes repeatable: collect ALL occurrences (`args.reduce` over `indexOf` loop), read each trx, union into the same `ran`/`passed` sets. The `flag()` helper stays for the other flags.
2. FQN mapping in `row()` (line ~60): `"ScenarioWalks:Cls.M"` → `Facilitator.ScenarioWalks.Cls.M`, else today's `Facilitator.Tests.` prefix.

New `tools/scenario-plane/src/audit-diff.mjs` (dependency-free node, ~40 lines): args = committed record path + regenerated table on stdin (the `runner.mjs --md` output); parse both `| id | verdict | ...` tables; identical verdict sets → exit 0; a row whose new rank < committed rank → print the moved rows and exit 1; new rank > committed rank → warn to stderr and exit 0. Rank: `covered=3, partial=2, divergent=1, silent-absent=1, absent=1, unexpressible=0, environment-blocked=0`; rows appearing/disappearing at all → exit 1. When `GITHUB_STEP_SUMMARY` is set, append a verdict histogram + the moved-row list.

`.github/workflows/ci.yml`: new top-level job `scenarios` (own required-check name), `needs: [dotnet, typescript]`, `runs-on: ubuntu-latest`, `timeout-minutes: 12` (walk family budget: <6 min wall — one shared container, driven clock, no fixed sleeps). Steps mirroring the `dotnet` job: checkout@v7, setup-dotnet@v6 with `cache-dependency-path: "**/packages.lock.json"`, `dotnet restore OpenFacilitator.slnx --locked-mode`, `dotnet build OpenFacilitator.slnx -c Release --no-restore`, then:
```yaml
      - run: make scenario-walks
      - run: make scenario-gate
      - name: audit record has no regressed verdict
        run: |
          # make scenario-gate already rewrote the working-tree record, so the
          # committed baseline must come from HEAD, not the file on disk.
          git show HEAD:docs/scenarios/audit-results.md > committed-audit.md
          node tools/scenario-plane/src/runner.mjs \
            --dotnet-trx tests/Facilitator.Tests/TestResults/scenario-gate.trx \
            --dotnet-trx tests/Facilitator.ScenarioWalks/TestResults/scenario-walks.trx \
            --md | node tools/scenario-plane/src/audit-diff.mjs committed-audit.md
```
(yamllint-clean, 2-space style as the rest of the file). No services block — Testcontainers use the runner's Docker, as `admin-e2e` does. If the two trxs should instead be the only cross-check inputs, note `make scenario-gate` passes just the gate trx to the record it writes; the diff step re-runs the runner over BOTH so a red walk cannot hide behind the make target's single-trx record.

`OpenFacilitator.slnx`: add `<Project Path="tests/Facilitator.ScenarioWalks/Facilitator.ScenarioWalks.csproj" />` under `/tests/` (required — CI restores/builds the slnx).

## Step 7 — Docs, signposts, lifecycle, skill

- Root `AGENTS.md` is exactly 80 lines and the drift gate (`protocol/tools/agents-docs-check.mjs`, `MAX_LINES = 80`, fails at > 81) allows at most ONE net new line. Do exactly one: a "Follow the truth" bullet — `- Scenario law: changing behavior a manifest row covers (docs/scenarios/scenario-bindings.json) obliges updating its binding + rerunning make scenario-gate; walks live in tests/Facilitator.ScenarioWalks/.` Edit the existing signpost row text to `|Scenario gate (plane + runner + manifest generator + walk assembly)|`tools/scenario-plane/README.md`|` in place (line-neutral). Do NOT add a second table row.
- `tests/Facilitator.Tests/AGENTS.md`: amend the existing `ScenarioGate/` bullet to say evidence ids resolve per-assembly via the shared base (`ScenarioWalks:` prefix = walk assembly).
- New `docs/scenarios/README.md`: the scenario lifecycle (the 5-step adoption path: catalog idea → approved block → `B["X99"]` entry, verdict honest from birth → promotion ladder `absent → partial → covered` by writing the feature test and/or a walk + regenerating + `TRX=full.trx make scenario-gate` → walk bindings may use the `ScenarioWalks:` prefix), plus one pointer to the manual golden-thread run.
- `tools/scenario-plane/README.md`: signpost the golden-thread file + `make scenario-golden` + the 429-now mode.
- Mint a managed skill `scenario-walk-authoring` (via `manage_skill` create): csproj/fixture shape, driven-clock + sweep law, plane injection via the sink route, `ScenarioWalks:` evidence prefix, manifest + trx refresh commands. Update the existing managed skill `scenario-gate-refresh`: drop the literal "must be 116 passing" count (now assembly-split) and add the second `--dotnet-trx`.
- Regenerate `docs/scenarios/audit-results.md` via `make scenario-gate` with both trxs and commit it (N12 row flips to covered, touched rows gain walk evidence).

Out of scope on purpose: compose/reference-env legs stay manual (`docs/runbooks/manual-infra-plan.md`); the golden thread does not fold the verify.sh chain here.

## Critical files & anchors

- `tests/Facilitator.Tests/ScenarioGate/ScenarioBindingsTests.cs` — the theory being split into base + subclass; its artifact-twin resolution rule must be copied verbatim into the base.
- `tests/Facilitator.Tests/Infrastructure/KernelTestFactory.cs` — clock seam (Step 1); ctor already has `fakeClock`/`useKestrel`.
- `tools/scenario-plane/src/runner.mjs` — lines ~30-64: trx parsing + the `Facilitator.Tests.` FQN prefix; both changes land here.
- `tools/scenario-plane/gen-bindings.py` — B-table at line 17: where walk evidence is written; `missing binding for` is the fail-closed hook.
- `.github/workflows/ci.yml` — `dotnet` job steps as the template for the `scenarios` job; no `needs:` exists today, this job is the first DAG edge.

## Verification

Work from repo root, all Release.

1. `dotnet restore OpenFacilitator.slnx --locked-mode && dotnet build OpenFacilitator.slnx -c Release --no-restore` — lock file present, warnings-as-errors clean.
2. `make scenario-walks` → 8 walks + 1 walk-side gate theory pass; concrete new-behavior proof inside `ReviewForkWalkTests`: injecting the review payload to `/api/ingress/webhooks/github` then feeding the normalized event yields `TestDbProbe.TimelineEventCount(workId, "review.decision") == 1` and a `successor` link to the forked work — before this change no single test asserted that chain.
3. Cross-assembly gate: temporarily rename one walk class method (or point a B-table entry at a nonexistent `ScenarioWalks:Nope.Method` after a throwaway regen), rerun `dotnet test tests/Facilitator.Tests -c Release --no-build --filter "FullyQualifiedName~ScenarioGate"` (must stay green — bare ids unaffected) and `dotnet test tests/Facilitator.ScenarioWalks -c Release --no-build --filter "FullyQualifiedName~ScenarioWalkBindingsTests"` (must go red). Restore.
4. `make scenario-walks && make scenario-gate` (the gate trx is the recipe's own default `TRX`, and the new conditional folds the walk trx in) — record regenerates; N12 shows `covered`; histogram prints 76 covered / 32 partial / 2 absent / 4 unexpressible. Full-suite cross-check per the `scenario-gate-refresh` skill: `dotnet test tests/Facilitator.Tests/Facilitator.Tests.csproj -c Release --no-build --logger "trx;LogFileName=full.trx"` then `TRX=tests/Facilitator.Tests/TestResults/full.trx make scenario-gate` — no row other than N12 moves.
5. Audit-diff behavior: with the fresh record committed, `node tools/scenario-plane/src/runner.mjs --md | node tools/scenario-plane/src/audit-diff.mjs docs/scenarios/audit-results.md` exits 0; hand-edit the record to change one row `covered`→`partial` (as the committed baseline) → exits 1 naming the row; `partial`→`covered` → warns, exits 0.
6. `make scenario-gate-e2e` still green (existing T2 lane) and `pnpm -C tools/scenario-plane test` no longer tries to run the golden file; `pnpm -C tools/scenario-plane test:golden` fails with a clear "kernel :8088 not reachable" message when no stack is up (manual precondition is loud, not silent).
7. `make docs-check` — root AGENTS.md ≤ 81 lines, all signposted paths exist.
8. CI end-to-end proof: on the PR, the `scenarios` check appears as its own required context and goes green; then push a commit that flips one committed record row down one rank → the `scenarios` job fails at the audit-diff step with that row named.

## Assumptions & contingencies

- `PostgresFixture`/`KeycloakFixture` instantiate cleanly as fresh per-collection instances in a second process (the admin-e2e assembly already runs them standalone — if a fixture turns out to cache absolute state across the process, fall back to a `ScenarioWalkDbFixture` cloning the ~40 lines of `PostgresFixture`, as `GcDbFixture` does).
- `dotnet test` on a project that `ProjectReference`s another test project discovers only the referencing project's tests (the walk trx contains only walks). If the runner double-discovers Facilitator.Tests' 612 tests, add `<EnableDefaultCompileItems>` scoping or `<ProjectReference ... >` with `ReferenceOutputAssembly="true" OutputItemType="content"`-style trimming — first fix: mark the Facilitator.Tests reference `OutputItemType="Content"`-free plain reference and confirm with `dotnet test tests/Facilitator.ScenarioWalks --list-tests`.
- Segment sources: a few bound evidence tests may prove their leg through fixtures unavailable to a second consumer (private helpers). Law: replicate the setup inside the walk, never weaken the original test; if a segment genuinely can't be reused, that leg becomes a small separate feature test committed before the walk.
- If the walk family busts the <6 min wall budget, first cut is merging same-collection walk classes (they are already sequential) and removing any accidental `Task.Delay`; do not add wall-clock waits.
