# @ofac/scenario-plane

The scenario gate's deterministic plane (`make scenario-gate`, plan
`SCENARIO_GAP_CLOSURE_PLAN.md` step 11):

- `src/fake-provider.ts` — one HTTP host serving the GitHub/GitLab/
  Mattermost/Telegram API+webhook shapes the comms adapters talk to, plus a
  control surface: register a sink, deliver/re-deliver an event, inject a
  403/429/vanished mode per family, read back everything received and sent.
  Adapters point at it via their existing base-URL config; nothing on the
  adapter side knows it is faked.
- `src/runner.mjs` — `scenario-runner`: reshapes the reviewed manifest
  `docs/scenarios/scenario-bindings.json` into the audit record
  (`--json`/`--md`/`--scenario <id>`), and with `--dotnet-trx <file>`
  downgrades any covered/partial row whose codified dotnet evidence did not
  pass in that run to `divergent`/`partial` — verdict honesty over belief.

The verdicts themselves live in the manifest and are a review artifact;
`tests/Facilitator.Tests/ScenarioGate` fails the build when an evidence id
rots or the manifest drifts from `docs/scenarios/approved-scenarios.md`.
