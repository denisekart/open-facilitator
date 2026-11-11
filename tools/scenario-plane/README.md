# @ofac/scenario-plane

The scenario gate's deterministic plane (`make scenario-gate`, plan
`SCENARIO_GAP_CLOSURE_PLAN.md` step 11):

- `src/fake-provider.ts` — one HTTP host serving the GitHub/GitLab/
  Mattermost/Telegram API+webhook shapes the comms adapters talk to, plus a
  control surface: register a sink, deliver/re-deliver an event, inject a
  403/429/429-now/vanished mode per family, read back everything received and sent.
  Adapters point at it via their existing base-URL config; nothing on the
  adapter side knows it is faked.
- `src/runner.mjs` — `scenario-runner`: reshapes the reviewed manifest
  `docs/scenarios/scenario-bindings.json` into the audit record
  (`--json`/`--md`/`--scenario <id>`), and with `--dotnet-trx <file>`
  (repeatable — one per assembly: gate trx + walks trx)
  downgrades any covered/partial row whose codified dotnet evidence did not
  pass in that run to `divergent`/`partial` — verdict honesty over belief.
- `src/audit-diff.mjs` — compares a committed `audit-results.md` against a
  freshly regenerated table (stdin): a regressed verdict or a row appearing
  or disappearing exits 1; an improved verdict warns and passes. The CI
  `scenarios` job's last step.

The verdicts themselves live in the manifest and are a review artifact;
`tests/Facilitator.Tests/ScenarioGate` fails the build when an evidence id
rots or the manifest drifts from `docs/scenarios/approved-scenarios.md`
(the `ScenarioWalks:`-prefixed half resolves against
`tests/Facilitator.ScenarioWalks` — the composed walk assembly).

## The manual golden thread (T2)

`test/chain.golden.test.ts` is the manual T2 chain proof: the acceptance
chain (ask → `vcs.issue.create` outbound leg → PR webhook through the
kernel sink → review decision → timeline/fork seam) against a
developer-running slim stack (`make compose-slim`: kernel :8088, Keycloak
:8080) plus the in-process plane. Run it with `make scenario-golden`
(`pnpm test` and CI skip `*.golden.test.ts`). One leg injects the plane's
`429-now` mode (429 with `Retry-After: 0`) so the shared retry law is
proven deterministically, no wall-clock wait. Manual preconditions (no
stack up, or a provider-less kernel) fail loud with a named message.
