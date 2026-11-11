# tests/Facilitator.ScenarioWalks

Composed scenario walks: cross-feature sequences that replay a manifest
scenario id as an actual chain of segments, each segment already proven by
a feature test bound to that id in `docs/scenarios/scenario-bindings.json`.

## The three laws

1. **One collection fixture.** Every class carries
   `[Collection("postgres:walks")]`: strictly sequential, one shared
   Postgres+Keycloak pair for the family, never a container of one's own.
2. **The clock is DRIVEN.** Time-dependent legs use
   `factory.AdvanceClock(...)` (factory built `fakeClock: true`), then the
   sweep fires through `POST /api/admin/sweeps/{name}`
   (`nudge|approvals|schedules|session-loss|audit-retention`). Never a
   wall-clock wait; never a `Task.Delay` as a sync substitute.
3. **Waits ride probes.** Asynchronous effects are awaited through
   `TestDbProbe` polling on timeline/outbox/inbox rows (`KernelOps.
   PollUntilAsync`), never sleeps.

## The composition law

A walk chains segments already proven by the tests bound to its scenarios
in the manifest — if a segment isn't proven, write that feature test first,
don't fake a walk green.

## Binding a walk

Append `"ScenarioWalks:<Class>.<Method>"` to the evidence array of every
scenario id the walk names in the B-table of
`tools/scenario-plane/gen-bindings.py`, regenerate the manifest
(`python3 tools/scenario-plane/gen-bindings.py`), then
`make scenario-walks && make scenario-gate`. The
`ScenarioWalkBindingsTests` theory resolves those prefixed ids against this
assembly; bare ids stay in `Facilitator.Tests`.
