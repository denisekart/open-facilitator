# Plan: `@ofac/harness-cli` — playbook-driven CLI harness adapter, replacing `@ofac/harness-deterministic`

## Context

Today a team whose harness is a plain command-line program must fork `src/adapters/harnesses/deterministic` and write TypeScript. Ship a new **public** package `@ofac/harness-cli` (bin `ofac-cli-worker`) implementing `HarnessHooks` on top of `@ofac/adapter`'s `createAdapter`, driven by a declarative YAML playbook (`apiVersion: facilitator.framework/v1`, `kind: Playbook`): an assignment's `capabilityId` selects a capability's step list; each step is spawn/assert/upload/publish. Then fully replace the deterministic harness in three ordered steps: **(1) engine + parity proof, (2) re-point every consumer, (3) remove the deterministic package**, keeping a small TypeScript `HarnessHooks` example so the hooks shape is still teachable. Source idea: `.omp/ideas/cli-harness-adapter.md`.

Locked decisions (user): separate public package (not an `@ofac/adapter/playbook` export); **node-pty** PTY opt-in per step (native-build cost accepted); **multi-marker** `promptMarkers` (ordered marker→answer list, the npm-install 3-prompts case); deterministic replacement in-scope with removal/demotion last. Agentic posture stops at scripted interaction: `promptMarkers`/`feed` + the `ask_user` MCP shim + playbook `ask`; **no steer channel, no in-adapter model loop** — a program needing a real agent loop uses `@ofac/harness-omp`.

## Ground truth (verified this session)

- SDK: `src/adapters/harnesses/adapter-sdk/src/types.ts` — `AdapterDescriptor{invocation:"sdk", supports:{steer, interrupt, resume:"restart-with-context"}}`; `RunContext` = `{executionId, executionGeneration, command, signal, publishEvent, publishProgress(note), setSteered, requestInput({question, choices?, contextRef?})→Promise<{text}>, sessionRef?, kernel:{baseUrl, token}, context?, fetchContext}`; `RunOutcome = {kind:"completed", usage?} | {kind:"failed", reason}`; `HarnessHooks{onExecute, onSteer?, onCancel?, statusProvider?, sessionStore?}`; `CreateAdapterOptions{childWorkspaceId, token?, identity?, tls?, manifest: CapabilityManifestV1, transport, descriptor, hooks, heartbeatSeconds?, restartBackoffMs?, logger?, telemetry?, kernel}`. SDK owns child-connection lifecycle, restart loop, reply law, registry.
- Omission law: `src/adapters/harnesses/deterministic/src/capabilities.ts` uses `fullHarnessCapabilities([{command: HARNESS_COMMAND_WORK_NUDGE, reason: "no nudge frame on the adapter.v1 child wire (the Gateway answers on the child's behalf)"}, {command: HARNESS_COMMAND_WORK_STATUS_REQUEST, reason: "no kernel producer yet; the run answers input.required instead"}])` — import names from `@ofac/adapter`.
- Conformance: `runAdapterLifecycleContract(options: AdapterLifecycleContractOptions)` from `@ofac/adapter/testing` (`adapter-sdk/src/testing.ts:51`): `{startAdapter(gate), socketPath, gatewayId, steerReply?: "accepted"|"unsupported", contextLeg?, afterSteer?, afterCancel?, interactionLeg?, interactionAnswer?}`. Deterministic's `test/lifecycle.test.ts` is the precedent to copy.
- Byte path: `artifactUri`, `buildCompletedEvent`, `buildResultManifest`, `createArtifactClient` exported from `@ofac/adapter` (see `deterministic/src/harness.ts` imports).
- Config law to mirror: `deterministic/src/config.ts` — YAML + strict unknown-key-rejection-by-name; env overlays `OFAC_CHILD_SECRET` (token), `OFAC_KERNEL_URL`, `OFAC_KERNEL_TOKEN`, `OFAC_ADAPTER_LOG`.
- Kill law to copy: `codex/src/harness.ts` — spawn `detached: true` (own group), cancel = SIGTERM the group (`process.kill(-pid, ...)`), `KILL_GRACE_MS` then SIGKILL; aborted run publishes nothing.
- ask_user shim to copy: `omp/src/mcp/bridge.ts` + `omp/src/bin/mcp-shim.ts` (in-process bridge; shim back-connects; tool call blocks in shim, bridge calls `ctx.requestInput`); codex seeds `$CODEX_HOME/config.toml`, omp writes an auto-loaded `.mcp.json`.
- Package shape to copy: `src/adapters/harnesses/omp/package.json` (`private: false`, `publishConfig.registry: "https://registry.npmjs.org"`, license `AGPL-3.0-or-later`, `type: module`, tsup `--format esm --dts --target node22`, deps `@ofac/adapter|gateway|otel|protocol|workspace-link` + `yaml`, all `workspace:*`/`catalog:`).
- `pnpm-workspace.yaml` lists packages explicitly — add `"src/adapters/harnesses/cli"`. node-pty appears nowhere in the repo.
- Inbound-media law to mirror: `deterministic/src/harness.ts` — `inputs.sourceRefs` dereferenced via `createArtifactClient` over the link plane; unreadable refs degrade to a marker line, never fail the run.
- Deterministic worker behaviors to reach parity with: capabilities `research.analyze` v1 (async, `outputKinds:[analysis.report]`, `outputMediaTypes:[text/markdown]`, steerable) and `research.publish` v1 (not steerable/cancellable); two steps of `stepMs` each with `work.progress.v1` notes; optional `askQuestion` config → `ctx.requestInput` between steps, answer embedded in report; report + Result Manifest uploaded, completed event via `buildCompletedEvent`.

## Step 1 — Package skeleton + operator config

Create `src/adapters/harnesses/cli/` mirroring the omp package layout: `package.json` (name `@ofac/harness-cli`, bin `ofac-cli-worker` → `./dist/bin/worker.js`, `files:["dist","sample"]`, tsup build of `src/index.ts src/bin/worker.ts src/bin/mcp-shim.ts`, deps as omp **plus** `node-pty` direct-version — it is single-package, so a literal semver, not `catalog:`), `tsconfig.json`, `tsconfig.test.json`, `vitest.config.ts`, `vitest.e2e.config.ts` (copy deterministic's), `sample/`, `src/`, `test/`. Add the workspace entry to `pnpm-workspace.yaml` and the package copy lines to `Dockerfile.node`'s dependency layer (mirror lines 18/37 there, paths swapped).

`src/config.ts`: load `worker.yaml` (`kind: HarnessAdapter`) exactly like deterministic's — strict allow-listed keys, every error names its key; same env overlays, **plus** new keys: `playbookFile` (required, resolved relative to config dir), `killGraceMs` (default 5000), `defaultTimeoutMs` (default 600000), `askUser: "mcp"|"off"` (default `"off"`). Unit tests per key (unknown key rejected by name, env overlay wins).

## Step 2 — Playbook schema + loader

`src/playbook/{schema.ts, load.ts}`: parse YAML `apiVersion: facilitator.framework/v1`, `kind: Playbook`, `capabilities: [{id, version, execution: "asynchronous", acceptedContexts, outputKinds, outputMediaTypes, allowedEnvKeys?: [...], steps: [...]}]`. Strict validation like config.ts: unknown keys rejected by name at every level. Step keys (v1 — frozen table, exact names):

| key | shape | behavior |
|---|---|---|
| `name` | string | required, shown in progress/failure reasons |
| `run` | `{command, args[], cwd?, env?, shell?: false}` | argv array; no implicit shell; binary must be on the per-capability allow-list; `vars`/`env` interpolation only into declared slots |
| `tty` | boolean | default false (pipes); true → node-pty |
| `timeoutMs` | int | per-step kill; timeout ⇒ failed step like an `expect` miss |
| `expect` | `{exit?, stdout?, stderr?, fileExists?, fileAbsent?, maxDurationMs?}` | regex on **de-ansified** streams; miss ⇒ `assignment.failed.v1` `retryable: true`, reason names the step |
| `promptMarkers` | `[{match, answer}]` | ordered, each fires once on de-ansified output; writes `answer` bytes to stdin (PTY: `write`); unmatched non-optional marker at exit ⇒ failed step |
| `feed` | string | bytes written to stdin at spawn (before markers) |
| `ask` | `{question, choices?, contextRef?}` | `ctx.requestInput`; answer exposed to later steps as `{{steps.<name>.answer}}` |
| `capture` | `{as, from: "stdout"\|"file"\|"stderr", mediaType}` | upload via artifact client, declared in Result Manifest, `artifactUri` referenced by completed event |
| `expect`+`retry` | `{attempts, backoffMs, retryOn: [exitCode...]}` | one `work.progress.v1` note per retry |
| `onFail` | `"abort"`\|`"continue"` | default abort; `continue` records first failure and fails the run at the end with that reason |
| `progress` | string | `ctx.publishProgress` note at step start (de-ansified, truncated) |

`src/playbook/ansi.ts`: strip ANSI/CR-redraw before matching and before progress notes; raw stream ships only as artifact bytes (frame budget 262 144). `src/playbook/interpolate.ts`: `{{vars.*}}` from `inputs.vars`, `{{workId}}`/`{{assignmentId}}`/`{{objective}}`, `{{steps.<name>.answer}}`, `{{steps.<name>.exitCode}}`; substitution only into declared `args`/`env` values; payload may never name a binary or add a flag (allow-listed binaries + declared `allowedEnvKeys` are the whole payload surface).

## Step 3 — Step executor

`src/playbook/run.ts`: one step = spawn (`child_process.spawn` pipes, or node-pty when `tty: true`), always `detached: true` own group, kill law verbatim from `codex/src/harness.ts` (`process.kill(-pid, "SIGTERM")`, `killGraceMs` grace, SIGKILL; aborted run publishes nothing). Tee stdout/stderr: raw buffer for artifacts, de-ansified rolling tail window for `promptMarkers`/`expect` (TUI law: match a tail window, not scrollback). Marker engine consumes the ordered list, writing each `answer` on its first match. `onCancel` aborts via the same group kill. No new event classes: progress notes only (HARNESS_EVENTS is closed).

## Step 4 — Human leg: `ask` + ask_user MCP shim

Copy `omp/src/mcp/bridge.ts` + `omp/src/bin/mcp-shim.ts` into the cli package (adjust import specifiers). When config `askUser: "mcp"` and a capability declares `askUserInject: {file, format: "json"|"toml"}`, before spawn seed the file in the step `cwd`/home pointing the CLI's MCP-loader at the shim (`{{shimCommand}}` placeholder = `node <dist/bin/mcp-shim.js>` + bridge address, as `omp/src/mcp/bridge.ts:writeMcpConfig` does). Shim tool calls bridge into `ctx.requestInput` on the live run. Playbook `ask` steps work regardless of shim support — a CLI that can't host MCP just can't ask unprompted.

## Step 5 — Hooks, capabilities, worker bin

`src/harness.ts`: `createCliHooks(cfg, playbook)` → `HarnessHooks.onExecute(ctx)`: read `capabilityId`/`inputs` from the `work.assign` envelope (same extraction as `deterministic/src/harness.ts:63-70`), download `inputs.sourceRefs` via `createArtifactClient` into `<workdir>/attachments/<index>` (unreadable ref ⇒ marker line, never fail), run steps, upload each `capture` via `createArtifactClient` (failed upload ⇒ throw ⇒ SDK publishes `assignment.failed`), finish with `buildResultManifest` + `buildCompletedEvent` exactly as deterministic's tail does (`artifactUri(workId, assignmentId, artifactId)`). `RunOutcome.failed{reason}` = `"step '<name>': <why>"`. `onCancel` → group kill.

`src/capabilities.ts`: `fullHarnessCapabilities([...nudge, status-request omissions verbatim from deterministic/src/capabilities.ts])`; descriptor `{invocation:"sdk", supports:{steer:false, interrupt:true, resume:"restart-with-context"}}`; steered frame ⇒ SDK `unsupported` (no `setSteered`). Check whether `@ofac/adapter` exports a steer-command constant alongside `HARNESS_COMMAND_WORK_NUDGE` (grep `HARNESS_COMMAND_` in `adapter-sdk/src`); if yes, also omit it with reason `"fire-per-step: no mid-run channel"`. The advertised `CapabilityManifestV1` is **derived from the playbook** (fields per capability entry; `supportsSteering:false, supportsCancellation:true, supportsNudge:false`).

`src/bin/worker.ts`: `ofac-cli-worker run --config <path>` (same CLI shape as deterministic's bin).

## Step 6 — Conformance + engine tests

`test/config.test.ts`, `test/playbook.test.ts` (schema rejects unknown keys by name), `test/run.test.ts` (markers/feed/expect/retry against a shipped `sample/fake-tool.mjs` — deterministic, no AI), `test/ansi.test.ts`. `test/lifecycle.test.ts`: `runAdapterLifecycleContract` twice, copying deterministic's `test/lifecycle.test.ts` structure — once `steerReply: "unsupported", interactionLeg: true` (its `ask` step answers), and a context-leg variant (`contextLeg: true`). Terminal events checked against `protocol/conformance-fixtures/` class `adapter` fixtures as deterministic does.

## Step 7 — Parity proof (gate for the cutover)

`sample/research.playbook.yaml` + `sample/fake-research-tool.mjs`: reproduces `research.analyze` v1 + `research.publish` v1 — identical capability ids/versions/output kinds/media types (`examples/slim/deterministic-research-worker/capabilities.yaml` is the byte template), two steps with progress notes, one report `capture` + manifest, optional `ask` when config sets a question, attachment marker lines from `sourceRefs` files the fake tool reads. **Parity check:** `test/e2e/worker.e2e.test.ts` copied from deterministic's (spawn `dist/bin/worker.js` against a standalone Gateway; same SIGKILL-mid-run/reconcile legs), asserting the completed event carries a `analysis.report`/`text/markdown` artifact whose report contains the objective, and the publish leg emits `research-publish.result`. `research.analyze` parity delta (accepted): `supportsSteering` flips `true`→`false` — update `examples/slim/deterministic-research-worker/capabilities.yaml` accordingly when re-pointing; confirm no e2e leg steers research.analyze (grep `steer` in `src/adapters/harnesses/deterministic/test/e2e/`). If parity cannot be reached, stop before Step 8 and leave deterministic as-is (purely additive outcome).

## Step 8 — Re-point every consumer (verified list)

- `pnpm-workspace.yaml`: entry already added Step 1; remove `"src/adapters/harnesses/deterministic"` in Step 9.
- `Dockerfile.node` lines ~18/37/55: swap the two COPY lines and `pnpm deploy --filter=@ofac/harness-deterministic` → `@ofac/harness-cli`; header comment line 2.
- `Makefile` ~93–98: e2e target → `pnpm -C src/adapters/harnesses/cli test:e2e`; help text mentions `ofac-deterministic-worker` → `ofac-cli-worker`.
- `.github/workflows/ci.yml` ~390–395: same swap (keep commented devcontainer block coherent: retitle to the cli worker).
- `features/src/harness-adapter/`: `install.sh` (`ADAPTER` gate: implement `cli`, drop the `deterministic` branch; supervisor unit runs `ofac-cli-worker run --config`), `README.md`; `features/test/harness-adapter/{debian,ubuntu}.sh`, `features/test/gateway/combo.sh`.
- `examples/local/templates/`: `kernel/worker-config/{worker.yaml,capabilities.yaml,policy.yaml,gateway.yaml}` and `node/{worker.yaml.tmpl,capabilities.yaml,policy.yaml,gateway.yaml.tmpl,compose.yaml.tmpl,units/30-harness-adapter.sh}` — child name/id `deterministic`→`cli`, worker config gains `playbookFile`; `examples/local/tools/localctl/src/smoke.ts` + `examples/local/README.md` string swaps.
- `examples/slim/deterministic-research-worker/`: rename dir to `examples/slim/cli-research-worker/` (it's now a playbook walkthrough, not "deterministic"); `worker.yaml` → `kind: HarnessAdapter` + `playbookFile: research.playbook.yaml` (point at the shipped sample copy or vendor the playbook beside it), `capabilities.yaml` generated from playbook fields (steering false), README reworked. **Acceptance law:** slim ref env must still pass `up slim --no-seed && referencectl verify`.
- Docs: `docs/advanced.md:151` (worked-example worker now `@ofac/harness-cli`) and `:268` (walkthrough path); `docs/specs/open-facilitator-appendix.md` + architecture doc mentions; `src/adapters/AGENTS.md:16,34` (worked example is now `harnesses/cli`; "New harness adapter: copy `harnesses/cli` and run the conformance kit"). Doc-sync rule: AGENTS.md edits ride in the same commit.

## Step 9 — Remove `@ofac/harness-deterministic`

`git rm -r src/adapters/harnesses/deterministic` (its engine-test value is replaced by the cli package's suite; its e2e precedent ported in Step 7). Keep the teachable TypeScript shape the playbook can't: move a trimmed `harness.ts` + its lifecycle test to `examples/custom-hooks-worker/` as `private: true`, workspace-listed, header comment "you can also build a harness yourself with `HarnessHooks`" — linked from `@ofac/harness-cli` README migration section.

## Critical files & anchors

- `src/adapters/harnesses/deterministic/src/{harness.ts,config.ts,capabilities.ts}` — the behaviors to port and the validation style to copy (until Step 9 deletes them).
- `src/adapters/harnesses/codex/src/harness.ts` (~lines 81–135, 258–286) — kill-group/grace law to copy verbatim.
- `src/adapters/harnesses/omp/src/mcp/bridge.ts` + `src/bin/mcp-shim.ts` — shim/bridge to copy for the ask_user leg.
- `src/adapters/harnesses/adapter-sdk/src/{types.ts,testing.ts}` — SDK surface + `runAdapterLifecycleContract` options.
- `Dockerfile.node`, `Makefile`, `.github/workflows/ci.yml`, `features/src/harness-adapter/install.sh`, `examples/local/templates/node/*`, `examples/slim/deterministic-research-worker/*` — the consumer cutover surface.

## Verification

1. `pnpm install && pnpm -r build && pnpm -r --if-present typecheck` clean (new package + Dockerfile/workspace edits).
2. `pnpm -C src/adapters/harnesses/cli test` — unit + lifecycle-contract variants pass; a playbook whose `expect.exit: 3` runs the fake tool exiting 0 must produce `assignment.failed.v1` with reason containing `step '<name>'` and `retryable: true`.
3. New-behavior marker proof: a test playbook whose fake tool prints `Proceed? [y/N]` (ANSI-wrapped) and exits with the fed answer in a file — assert the de-ansified regex matched, answer written, completed event published.
4. PTY proof: one `tty: true` step against the fake tool under node-pty completes and its expect matches (skip-guard only where node-pty build is absent).
5. Parity e2e: `pnpm -C src/adapters/harnesses/cli test:e2e` (standalone Gateway, no Docker) — assign `research.analyze`, completed event references an `analysis.report`/`text/markdown` artifact; SIGKILL mid-run then re-spawn reconciles to exactly one terminal event.
6. End-to-end gate: `make link-e2e` (Postgres at `FACILITATOR_PG`) and slim acceptance from clean: `up slim --no-seed && referencectl verify` exits 0.
7. `make docs-check` and `make lint` clean after the AGENTS.md/doc swaps.

## Assumptions & contingencies

- `research.analyze` steering drop is acceptable (slim capabilities updated in Step 8); if an e2e leg steers it, keep a `steer` no-op progress-note recorder before flipping the flag.
- node-pty native build available on worker images (user accepted); if a CI image lacks build tools, fall back: make the PTY unit test `describe.skipIf` on load failure — the pipes posture must never require node-pty at runtime import (dynamic `import("node-pty")` only when a `tty: true` step spawns).
- npm publish of `@ofac/harness-cli` uses the existing public-package release flow for `@ofac/harness-omp` (same registry, `private: false`); publish itself is out of scope for this repo change.
- If Step 7 parity fails on a behavior that can't be expressed in the frozen v1 grammar, add the minimal grammar key (with a conformance test) rather than weakening the parity gate — and only before consumer cutover.
