# Scenario-walk stage: composed scenario tests as an isolated CI stage

**Status:** idea → needs a plan.
**Grows from:** the scenario gap-closure work (`.omp/plans/SCENARIO_GAP_CLOSURE_PLAN.md`,
steps 11-12) and the audit record it produced.

## The gap this idea closes

The gate today is **bookkeeping over per-feature proofs, not scenario execution**.
Three layers exist and none of them replays a scenario id:

1. Feature tests (612) — real behavior, but each stays inside one feature's law;
   none crosses a feature boundary the way an approved scenario does.
2. `tests/Facilitator.Tests/ScenarioGate` (116 tests) — proves the bindings
   manifest (`docs/scenarios/scenario-bindings.json`) doesn't rot: evidence ids
   resolve to real tests, catalog coverage is exact, lanes match tiers.
3. `tools/scenario-plane` — a fake provider with a personality (4 plane tests) +
   a T2 lane driving adapter outbound legs over real HTTP (6 tests).

The audit's **33 `partial` verdicts are precisely the missing compositions** —
each partial's note names the leg that no test walks. Nothing today proves
C5→C7 as a sequence, N2's loop with a second automation as a dialogue, or
provider→adapter→kernel→fork as one chain. And when a new scenario is added to
`approved-scenarios.md`, the gate only forces a *binding*; it never forces an
*execution*.

## The design (settled during the gap-closure review — challenge, don't re-litigate blind)

**A walk proves *composition*; per-feature law stays where it is.** A walk
script chains 2-4 already-proven segments across a feature boundary with
multiple actors and a driven clock. If a walk fails, the bug is in the seam
between laws, not inside one.

**Separate assembly: `tests/Facilitator.ScenarioWalks/`.**
- Its own csproj in `OpenFacilitator.slnx`, its own `packages.lock.json`
  (restore runs `--locked-mode`), its own Postgres collection + fixture
  (the `GcDbFixture`/`DispatchDbFixture` lesson: a fake-clock family must not
  share a DB with families whose leases/sweeps it advances).
- ProjectReferences `Facilitator.Tests` for `KernelTestFactory`,
  `KeycloakFixture`, `KernelOps`, `TestDbProbe`. Those are public; the
  `internal FakeTimeProvider? Clock` seam needs a public accessor or
  `InternalsVisibleTo` — decide in the plan, prefer a narrow public
  `AdvanceClock(...)` helper on the factory over InternalsVisibleTo sprawl.
- The 116 ScenarioGate manifest tests STAY in `Facilitator.Tests` (fast,
  DB-free, pure reflection). Only walks move out. The manifest's evidence
  ids must resolve across BOTH test assemblies — one small change to the
  theory: scan a known list of test assemblies (or any loaded
  `Facilitator.Tests*`/`Facilitator.ScenarioWalks` type) instead of
  `typeof(ScenarioBindingsTests).Assembly`.
- Determinism law (imported from `reference/checks/verify.sh`): waits ride
  timeline/inbox event kinds (`TestDbProbe` polling), never wall-clock sleeps;
  the clock is DRIVEN via the admin sweep triggers
  (`POST /api/admin/sweeps/{nudge|approvals|schedules|session-loss}`), never
  waited on.

**One golden thread, not N.** The cross-language full loop (fake provider →
real adapter in Node → kernel webhook sink → forked work) is the only piece
that needs a Node subprocess, and it's the pulse-bleed risk. Exactly ONE
scenario earns it (suggested: the Step-8 acceptance chain — ask in chat →
ticket → PR → review webhook → review.decision timeline → forked successor
work), living as a vitest tier in `tools/scenario-plane/test/` (the seam and
`startFakeProvider` already exist there; the plane needs a 0-second
Retry-After mode for deterministic 429 legs). Everything else reaches the
kernel through `/api/ingress/webhooks/{provider}` in xUnit — the sink stores
verbatim bytes, so a C# walk injecting there gets genuine composition without
shipping Node inside xUnit. The provider→kernel link is already pinned three
ways (adapter vitest tiers, 148 conformance fixtures, the plane), so the
golden thread proves the CHAIN, not the segments.

**Separate CI stage.** A `scenarios` job in `.github/workflows/ci.yml`,
`needs: [dotnet, typescript]`:
- Runs `make scenario-walks` (new target: `dotnet test
  tests/Facilitator.ScenarioWalks/... --blame-hang-timeout 2m --logger trx`)
  + `make scenario-gate-e2e`, and regenerates the audit record from the
  combined trxs (`TRX=... make scenario-gate` exists; note make's
  `TRX ?=` default pattern).
- Own required-check name ⇒ "scenario walk failed", not a lost line in the
  612-test blob.
- Own cadence/tolerance: golden thread may go nightly-only (`schedule:`
  already exists in ci.yml) or get a retry while the xUnit walk family gates
  PRs. Decide per failure-experience in the first weeks.
- Audit-record provenance: the stage renders `audit-results.md`; either
  upload it as an artifact and have the refresh commit come from CI, or diff
  it against the committed one and fail when a verdict moved without a
  manifest edit. Prefer the diff-and-fail (keeps the record a reviewed
  artifact, matches "a verdict, never a belief").
- Wall-budget note: root `AGENTS.md` budgets the .NET suite at <10 min;
  a separate job needs its own stated budget (suggest <6 min for the walk
  family: one shared container, driven clock, no fixed sleeps).

## Scenario coverage: adopting the uncovered

Three buckets, three different moves. Use the manifest notes as the worklist.

**A. `partial` (33) — legs a walk can close.** Each note names the missing
leg. Triage each into: (a) closeable by a walk (composition missing),
(b) needs new product behavior (e.g. N43 cycle detection, N24
minimum-decidability gate, N44 held-action preview surface, N17 global
budget), (c) genuinely judgment → promote to T4 rubric, (d) re-scope the
scenario's Check: line at the next catalog audit. Indicative walk-closable
set from the notes: A2 (reaction-vs-reply scaling), A6 (ladder picks where
the entitled human lives), A12 (dependency leg), A13 (re-route after
misroute), C10 (consent-rule backdating), D1 (proactive cadence), E3
(handover transition), F2 (resume with inherited context — may need a
link-plane test double, see open questions), K3 (label-opened contributor
work), N12, N16, N27, N34, N37 (account-vs-trail sweep — could be a walk or
a permanent invariant test), N42. Roughly a dozen walks convert most of
these; each new walk BINDS its scenarios in the manifest, flipping the
verdict to covered when the whole Check: line is now proven.
Caveat: a walk can only close a partial if the underlying behaviors exist;
when the walk script discovers a missing behavior, that's a small feature
task, split it out — do not fake a walk green.

**B. `absent` (2) — behavior must exist first.** N25 (consent-volume
throttle/grouping — `consent-policy` already carries
`maxInFlightPerActor/groupWindowSeconds` config fields that no code reads
yet: making that config REAL is the feature, then a walk binds it) and N31
(concurrent-contributor collision detection). These are product features
with their own mini-plans; the walk tier just inherits their bindings when
they land.

**C. `unexpressible` (4 T4) — stay manual forever.** Don't fight the
verdict; the rubric lane records them.

**Adding new scenarios later (the adoption path).** Today a new
`### X99` in `approved-scenarios.md` turns the gate red until the manifest
gains a binding — that stays the law. Formalize it as the scenario lifecycle
(one place, referenced from everywhere):
1. Idea space: add to `docs/scenarios/scenario-catalog.md` (frozen
   idea-space rules apply) or straight to the approved set via an audit
   session (`scenario-catalog-audit` skill).
2. `approved-scenarios.md` gains the block (Expected / Check / Tier).
3. `tools/scenario-plane/gen-bindings.py` gains a `B["X99"]` entry —
   verdict honest from birth (usually `partial` or `absent`; an unbound
   scenario fails the generator, a bound-but-untested one says so).
4. Optional promotion ladder: `absent → partial → covered` by writing the
   feature test and/or a walk and re-running the generator +
   `TRX=full.trx make scenario-gate`.
5. A walk's binding may name the walk's FQN — the theory must resolve it
   (see the two-assembly scan change above).

## Agentic-workflow awareness

Agents must *know the scenario plane exists* and *use it as the yardstick*
when they change behavior. Layers, cheapest first:

- **Root `AGENTS.md`** already points at `tools/scenario-plane/README.md`;
  extend the signpost row to name the walk assembly and add one line to
  "Follow the truth": behavior changes to a scenario-governed area must
  check its `Audit:` line — if your change invalidates a covered verdict,
  you own updating the binding + rerunning the gate. Keep within the
  docs-check line caps.
- **`tests/Facilitator.ScenarioWalks/AGENTS.md`** — the collection fixture
  law, the driven-clock law, the no-wall-clock-waits law, "a walk chains
  proven segments; if a segment isn't proven, write that test first", and
  the binding-update procedure. ≤40-line index per repo convention.
- **Skills:** the managed skills `scenario-gate-refresh` and
  `ofac-protocol-schema-change` already exist; the plan should mint/extend
  one skill for *writing a walk* (fixture shape, plane injection, sweep
  triggers, manifest binding, trx regeneration) so subagents landing feature
  work can add walks without relearning the harness. The
  `scenario-catalog-audit` skill (already updated) is the audit-side
  counterpart — its verdict vocabulary must stay identical to the gate's.
- **CI-visible provenance:** the `scenarios` job's summary printout (one
  verdict histogram + any moved verdicts) is the surface where both humans
  and agents see drift; the diff-and-fail rule means an agent that breaks a
  scenario discovers it at the stage that names scenarios.
- **Subagent task templates**: feature plans in `.omp/plans/` gain a
  standard verification bullet — "bind or update the scenario rows your
  change touches (`gen-bindings.py` + `make scenario-gate`)" — so planning
  agents treat the manifest like the wire-contract law it is.

## Components checklist (for the planner)

1. `tests/Facilitator.ScenarioWalks/` — csproj, GlobalUsings, AGENTS.md,
   collection fixture (own container), 8-12 walk scripts, `packages.lock.json`,
   `.slnx` entry, `make scenario-walks` (+ add to `.PHONY`).
2. `Facilitator.Tests` — ScenarioGate theory: cross-assembly evidence
   resolution; a public clock-driving seam on `KernelTestFactory` (replace
   the `internal Clock` reads in tests where sensible).
3. `tools/scenario-plane` — 0-second Retry-After mode; golden-thread vitest
   file; README + plan-linked docs update.
4. `.github/workflows/ci.yml` — `scenarios` job (needs dotnet+typescript;
   walks gate PRs, golden thread policy decided in plan); audit-record
   diff-and-fail step.
5. Manifest + docs — new bindings for the walks, refreshed
   `audit-results.md`, scenario-lifecycle section in
   `docs/scenarios/approved-scenarios.md` header or a short
   `docs/scenarios/README.md`, skill for writing walks.
6. Deferred on purpose: compose/reference-env legs stay manual
   (`docs/runbooks/manual-infra-plan.md`); the golden thread may later fold
   a slim version of the verify.sh chain, but do NOT plan the compose matrix
   as part of this — it is a different failure domain with docker-compose
   startup costs that don't fit a per-PR job.

## Open questions the planning session must settle

- Walk count and ids for the first slice (propose 6-8: C5→N6→C7, N2 with a
  second automation, N29 self-write across two adapters via the plane,
  A11 merge-during-followup, E4 review-fork end-to-end xUnit-side, N12
  restart-no-double-ask, D2+nudges capacity saga, N30 stale-moment refusals).
- Clock seam: public `AdvanceClock` helper vs `InternalsVisibleTo`.
- Golden thread: which single scenario, gate-on-PR vs nightly-first,
  in-workspace Node spawn shape (reuse the T2 lane's harness?).
- Audit diff-and-fail strictness: fail on ANY moved verdict vs allow
  improvements-only (recommend: fail on regressions, warn on improvements).
- Whether walks may use `LaunchHttpServer`/Kestrel (for the webhook sink —
  the sink route needs a real HTTP surface; CliSmokeTests' real-server
  pattern is the precedent) or stay in-proc with a direct
  `WebhookIngressEndpoints` invocation (faster but a weaker chain).
- Where the second-actor IdP identities for walks come from —
  `KeycloakFixture` per-team group memberships already exist
  (gap-closure Step 11 bullet), confirm sufficiency for the walk roster.
