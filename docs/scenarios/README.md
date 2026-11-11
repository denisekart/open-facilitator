# Scenario lifecycle

How a scenario goes from an idea to `covered` evidence. The gate pieces:

- `approved-scenarios.md` — the reviewed catalog (the ids and their text).
- `scenario-bindings.json` — the generated + hand-reviewed bindings
  (`tools/scenario-plane/gen-bindings.py`'s B-table is the source).
- `audit-results.md` — the record the runner regenerates (`make scenario-gate`).
- `tests/Facilitator.Tests/ScenarioGate` + `tests/Facilitator.ScenarioWalks` —
  the xUnit wall that fails when bindings rot.

## The adoption path

1. **Catalog idea.** Draft the scenario block (id, title, family, tier) in
   `approved-scenarios.md` as a proposal; it gets reviewed like any spec.
2. **Approved block → B-table entry.** Add `B["X99"] = (verdict, evidence,
   note)` to `tools/scenario-plane/gen-bindings.py` and regenerate. The
   verdict is honest from birth: a row is `covered`/`partial` only with
   evidence ids that resolve right now, otherwise `absent`/`divergent`.
3. **Promotion ladder `absent → partial → covered`.** Write the feature test
   (inside one feature's law) and/or a composed walk
   (`tests/Facilitator.ScenarioWalks/` — a cross-feature sequence), point
   the B-table evidence at it, regenerate, and rerun the cross-check with a
   full trx: `TRX=tests/Facilitator.Tests/TestResults/full.trx make scenario-gate`.
4. **Walk bindings** may use the `ScenarioWalks:` evidence prefix
   (`ScenarioWalks:Class.Method`), which resolves against the walk
   assembly; bare ids keep resolving against `Facilitator.Tests`.
5. **The CI `scenarios` job** runs the walks + the gate and fails when a
   committed record's verdict regresses (`audit-diff.mjs`).

## The manual golden thread

The T2 acceptance chain (chat ask → issue leg → PR webhook → review →
fork) runs manually against a live slim stack:
`make scenario-golden` (see `tools/scenario-plane/README.md`).
