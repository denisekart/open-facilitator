# Plan: omp + Codex harness adapters with interactive relay, as Dev Container Features

## Context

Two new execution-harness adapters — **omp** (oh-my-pi CLI) and **Codex** (OpenAI `codex` CLI) — built as siblings of `src/adapters/harnesses/deterministic` on top of `@ofac/adapter`, each shipped as its own Dev Container Feature (`features/src/omp-adapter`, `features/src/codex-adapter`), published like the existing four features. The adapters are **full TUI stand-ins**: the harness runs fully autonomous (no per-tool-call approvals), and any genuine interaction — a question to the human, a consequential error — is relayed through the Facilitator to the integration that owns the work, and the human's answer is routed back into the **live** run. This interactive leg does not exist on the wire today (`protocol/json-schema/adapter.v1.json` literally says "`respond` has no v1 frame"); it is built here, minimally: one new event, one new command, one new child-wire message, one new manifest flag.

Decisions already made (not re-debatable at implementation):
- Separate feature per adapter (a Dev Container Feature instantiates once per container; separate features let containers compose multiple harness types).
- Native CLIs are **assumed present** on PATH; install.sh hard-fails with a base-image hint unless `requireCli:false`.
- Resume-aware: persist `executionId ↔ native session id`; a retry resumes the native session.
- Honest steering: omp supports live steer; Codex reports steer unsupported mid-run (never silently rewritten).
- Gate = fake-CLI e2e + feature scenario tests; live-CLI smoke is a non-gated script. Reference env/compose unchanged (deterministic worker stays the demo).
- Interactive v1 contract: the orphan wire event `event.work.input_required.v1` (exists in protocol/, fixture + registry row, but zero runtime producers/consumers) is RENAMED and extended to `org.facilitator.event.assignment.input.required.v1` — dotted naming matches every other wire type (`command.work.status.request.v1` precedent); it was the lone snake_case outlier. New command `org.facilitator.command.assignment.input.v1` + child-wire message `respond`. Block-until-answered (no timeout; deadline advisory). Kernel parks the assignment row in its existing `input_required` status (side-state already legal in the execution state machine). Fan-out law: work's originating integration → team default endpoint → registered `IDecisionProvider` (when enabled) → timeline-only + progress note. The DB CHECK value for the assignment status stays snake_case `input_required` (DB status domain, not wire naming).
- Independent agentic mode: adapters force autonomous flags (`omp --auto-approve`; `codex exec -s workspace-write --ask-for-approval never`), overridable via worker.yaml `extraArgs`; READMEs state the law.
- Harness→human questions are injected deterministically (no LLM needed at runtime): the adapter serves a tiny **stdio MCP server** exposing an `ask_user` tool, injected into each harness (omp: generated MCP config + `--append-system-prompt`; codex: generated `$CODEX_HOME/config.toml` `[mcp_servers.ofac]` + AGENTS.md note). When the agent calls `ask_user`, the tool call blocks in the adapter, the adapter publishes `input.required`, and the tool resolves when the `respond` arrives. Native-event question detection is a documented future extension, not v1.

Implementation is parallelized per **Tracks** below; one subagent per track, disjoint file ownership.

## Frozen identifiers

| Thing | Value |
|---|---|
| Packages | `@ofac/harness-omp`, `@ofac/harness-codex` (dirs `src/adapters/harnesses/omp`, `.../codex`) |
| Bins | `ofac-omp-worker`, `ofac-codex-worker` → `dist/bin/worker.js` |
| Capabilities | `coding.omp` (supportsSteering true, supportsCancellation true, supportsInteraction true), `coding.codex` (supportsSteering **false**, supportsCancellation true, supportsInteraction true) |
| New child message | `respond` (Gateway→child) |
| New wire types | `org.facilitator.event.assignment.input.required.v1` (rename+extend of the existing orphan `event.work.input_required.v1` — one event, no duplicates), `org.facilitator.command.assignment.input.v1` (new) |
| Features | `features/src/omp-adapter` (id `ofac-omp-adapter`), `features/src/codex-adapter` (id `ofac-codex-adapter`), both version `1.0.0` |
| Units | `31-omp-adapter.sh`, `32-codex-adapter.sh` (after 30, before 40) |
| Worker configs | `configPath` option default `.facilitator/omp-harness.yaml` / `.facilitator/codex-harness.yaml`; runtime env `WORKER_CONFIG` wins, as today |
| Feature options | `configPath` (string) + `requireCli` (boolean, default `true` → arrives as env `REQUIRECLI`) |

## Track 0 (serial, first): protocol contract

Everything downstream depends on the schemas + generated code.

1. Rename + extend the orphan event. `protocol/json-schema/event.work.input_required.v1.json` today declares `{workId, assignmentId, question}` and has no runtime arm anywhere. Replace the file with `protocol/json-schema/event.assignment.input.required.v1.json`: `$id` `https://schemas.openfacilitator.org/v1/event.assignment.input.required.v1.json`, `title` `AssignmentInputRequiredEventV1Data` (title derives generated type names). Payload `additionalProperties:false`, refs to `common.v1.json#/$defs`: REQUIRED `workId`, `assignmentId`, `executionId`, `requestId` (idToken), `executionGeneration` (integer ≥1), `question` (string 1..16384), `createdAt` (rfc3339); OPTIONAL `choices` (array, ≤8 items, each string ≤256), `contextRef` (evidenceRef), `deadline` (rfc3339, advisory display only).
2. New command `protocol/json-schema/command.assignment.input.v1.json`: `$id` same pattern, `title` `AssignmentInputCommandV1Data`; REQUIRED `commandId`, `requestId` (the input.required `requestId`), `answer` (string 1..16384), `actorEvidenceRef` (evidenceRef).
3. `protocol/json-schema/adapter.v1.json`: add `$defs/respondMessage` `{kind:"respond", executionId, requestId, text}` to `childMessage` oneOf; fix the description that claims respond has no v1 frame.
4. `protocol/json-schema/capability-manifest.v1.json`: add `"supportsInteraction": {"type":"boolean"}` to the CapabilityDescriptor properties.
5. Registration gauntlet (all mandatory for a new/renamed type): swap the import in `src/protocol/src/schemas.ts` `all[]` (old line 33 imports the old event) and add the command import; append `org.facilitator.command.assignment.input.v1` to the hand-maintained `protocol/dotnet/Ofac.Protocol/Validation/KnownCommands.cs` (link admission fails closed without it); fixtures in `protocol/conformance-fixtures/`: replace `valid/event.work.input_required.json` with `valid/event.assignment.input.required.json` (full extended payload), add `valid/command.assignment.input.json` and an `invalid/command.assignment.input.bad-evidence.json` pair with `errorClass`; register every fixture in `protocol/conformance-fixtures/index.json` (`{file, expect, class, type}`); rewrite the registry row at `docs/protocol.md:48` for the new name/fields and add the command row.
6. `pnpm protocol:codegen` (quicktype pass emits `src/protocol/src/generated/types.ts` + `protocol/dotnet/Ofac.Protocol/Generated/WireTypes.cs` + byte-mirror into `src/protocol/schemas/`); `make protocol` drift gate must end with a clean git diff.

Verify: codegen exits 0, conformance fixture suite passes, `make protocol` no diff.

## Track A (parallel with K, D): wire — Gateway + adapter SDK + conformance kit + deterministic worker

Files owned: `src/gateway/src/{childwire.ts,child.ts,runtime.ts}` + tests; `src/adapters/harnesses/adapter-sdk/src/{types.ts,adapter.ts,testing.ts}` + tests; `src/adapters/harnesses/deterministic/**`. Depends only on Track 0 strings.

1. `childwire.ts` / `child.ts`: add `respond` to the child-message union (`{kind:"respond", executionId, requestId, text}`), frame validation, and `send` on the child member executor. Reply law: a `command` frame carrying the respond message gets a normal `accepted`/`unknown` reply keyed by requestId.
2. `runtime.ts` command switch (~:645, `onInteraction` arms ~:787 are the pattern): new envelope-command arm for `org.facilitator.command.assignment.input.v1` — bound-child law exactly as steer/cancel (`this.paths.execution.byAssignment(data.assignmentId)`; no row ⇒ `rejected: no-binding`; confirm-held ⇒ `rejected: pending-approval`). Send `{kind:"respond", executionId, requestId, text:answer}`. Gateway-local execution row stays `running` (child status replies must answer `running`).
3. SDK `types.ts`: `RunContext` gains `requestInput(req: { question: string; choices?: readonly string[]; contextRef?: string }): Promise<{ text: string }>`.
4. SDK `adapter.ts`: implement — mint `requestId = "inp-" + executionId + "-" + ++seq`, publish the full `input.required` CloudEvent envelope via the existing event relay (built like `buildCompletedEvent`, `executionGeneration` from the assign fence), store a pending resolver keyed by requestId, resolve on the matching `respond` child message. Rejections: run aborted while waiting (reject `AbortError`); adapter closed. Unknown or respond-after-abort: drop silently. Record the answer as a progress note (`"input answered: <first 120 chars>"`). No timeout.
5. Conformance kit `testing.ts`: add a question leg to `runAdapterLifecycleContract`, ON by default (opt-out `{ interactionLeg?: false }` exists only for adapters whose hooks can never answer): the contract adapter's gated hook calls `requestInput`; the kit observes the published `input.required` event frame, delivers a `command.assignment.input.v1` over the in-process Gateway, asserts the hook resolves with the answer and status stays `running` while pending; a respond for an unknown requestId gets reply `unknown`. Existing legs (steer supported + unsupported variants) unchanged.
6. Deterministic worker (`src/adapters/harnesses/deterministic`) — the regression home for every new interaction scenario (it is the only adapter in the regularly-run suite): new optional worker.yaml key `askQuestion` (string; config loader + config.test.ts). When set, `createDeterministicHooks` calls `await ctx.requestInput({ question: cfg.askQuestion })` between the analyzing/drafting steps and embeds the answer in the report Markdown exactly where steering directives are embedded today. `test/lifecycle.test.ts` runs the contract with the askQuestion hook so the question leg executes in `make test`; also strip-it run proving `interactionLeg:false` passes. `sample/capabilities.yaml`: `supportsInteraction: true` on the research capability.

Verify: gateway, adapter-sdk, and deterministic suites green — including the deterministic contract's question leg.

## Track K (parallel with A, D): kernel — interactivity ingress, fan-out, respond

Files owned: `src/facilitator/Kernel/Work/{InteractionIngress.cs, InteractionEndpoints.cs, CloudEventIngress.cs, LinkIngress.cs, ProviderEventStore.cs(arm)}`, `src/facilitator/Kernel/Shared/Persistence/Configurations/WorkEventEntityConfiguration.cs`, a new EF migration, `tests/Facilitator.Tests/**` new files. Depends on Track 0 generated types/names only (string constants).

1. `InteractionIngress.cs` (new, shape-mirror `ApprovalIngress.cs`): handle `event.assignment.input.required.v1` on BOTH ingress surfaces — `CloudEventIngress.cs` (arm position: beside the approval.required event arm ~:89) and `LinkIngress.cs` (~:117):
   - validate payload against the schema, fence-check `executionGeneration` against the assignment row, dedupe by requestId (inbox dedupes envelope ids; requestId is the second guard),
   - transition the assignment row `running → input_required` via `ExecutionLedger` (the edge is already legal: `ExecutionLedger.cs:152-186` admits either live state as source; the DB CHECK already lists `input_required`),
   - record timeline row kind `input.required` (question + requestId). Timeline kinds are CLOSED by a CHECK: extend `CheckSql.InList` in `src/facilitator/Kernel/Shared/Persistence/Configurations/WorkEventEntityConfiguration.cs` (~:27-31) with `input.required` and `input.answered`, and add an EF migration that drops + recreates `work_events_kind_check`. No new tables.
   - fan-out per the frozen law, in order: (a) the work's originating integration — a row in `facilitator.communication_bindings` by `work_id` (the binding `ResultIngress.cs` ~:266 writes: provider/channel/root_message_id); deliver the question as `command.communication.reply.v1` (frozen schema: REQUIRED `commandId`, `target` (common.v1 `conversationTarget` — populate the binding's provider/channel/root-message fields the same way `ResultIngress` composes its connector commands), `text`, `idempotencyKey`), where `text` = the question + choices rendered (`<question>\n\n1) <a>\n2) <b>` when choices exist) + trailer `\n\nReply in this thread to answer (request <requestId>)`, and `idempotencyKey` = `input:<requestId>`; (b) else a team-declared default endpoint — config key `OFAC:InteractionDefaultEndpoint` (string, optional, appsettings + Options pattern), addressed on the link plane like other dispatch rows; (c) else, when an `IDecisionProvider` is registered (same seam `RoutingSink` takes), ask it to pick among live link sessions with a communication-capable manifest, adopt only in-set answers, audit the pick like RoutingSink does; (d) else timeline-only + one `work.progress.v1` note "waiting for input: <question>". Every fan-out leg is an outbox row routed by channel — never direct endpoint addressing.
2. Respond path: `InteractionEndpoints.cs` (new, mirror `ApprovalEndpoints.cs`): `POST /api/works/{workId}/interactions/{requestId}/respond` body `{answer, actorEvidenceRef?}` (identity resolution + `kernel:` evidence minting per the approval respond law). Refusals: unknown requestId ⇒ 404; already answered ⇒ 409 (command-ledger dedupe, commandId `input:<requestId>`); fence mismatch ⇒ 409. On accept: transition `input_required → running`, timeline row kind `input.answered` (answer excerpt + actor), outbox row envelope `command.assignment.input.v1` addressed to the assignment's bound recipient endpoint (the Gateway). Also a link-plane arm in `LinkIngress.cs` for `command.assignment.input.v1` arriving from a communication adapter (chat reply mapped adapter-side), same settle/404/409 semantics.
3. Answer-side mapping: `ProviderEventStore.cs` — when a provider message event lands on a channel/root-message bound (communication_bindings) to a work that has a pending `input.required` without a matching `input.answered`, synthesize the respond (answer = message text, actorEvidenceRef = provider evidence) instead of new steer/work-creation. Only the oldest pending requestId per work auto-answers (adapters surface multiple questions sequentially).
4. Tests (xUnit, Testcontainers Postgres, follow `tests/Facilitator.Tests` conventions): ingress → status transition + timeline + fan-out rows for each law arm (binding present / default-endpoint config / decision-provider pick / timeline-only); respond → `input_required→running` + envelope outbox to bound endpoint; respond twice → 409; unknown requestId → 404; fence mismatch refused; provider-message auto-respond arm.

Verify: `dotnet test --filter "FullyQualifiedName~Interaction"` green; `make build` warning-free.

## Track B (starts after A merges): omp adapter package

Copy the `deterministic` package shape wholesale (`src/adapters/harnesses/deterministic/`: package.json, tsconfigs, vitest configs, config loader, bin, lifecycle + e2e tests). Package `@ofac/harness-omp`, bin `ofac-omp-worker`.

- **Invoke**: per execution spawn `omp --mode rpc --auto-approve --cwd <workDir>/<executionId>/repo --session-dir <workDir>/<executionId>/sessions [--model …] <extraArgs…>` (argv, no shell). worker.yaml keys (clone deterministic's loader: unknown-key rejection, env overlays): all deterministic keys plus `model` (string, optional), `extraArgs` (string array, appended verbatim), `interactivity` (`"mcp"` default; `"off"` skips MCP injection). Config file `kind: HarnessAdapter`.
- **RPC loop**: stdin/stdout JSONL. start: `{"id":"ofac-1","type":"prompt","message":<prompt>}`; accept `success:true` disposition `started|queued|handled`. steer → `{"type":"steer","message":…}` (live, `supportsSteering:true`); stop → `{"type":"abort"}` then kill on SIGTERM grace. Session id: `get_state` → `sessionId` → persist via SDK `SessionStore` implementation (`<workDir>/sessions.json`, survives restart, keyed `executionId:executionGeneration`).
- **Retry/resume**: on a new generation for the same assignment, relaunch with `--resume <sessionId>` instead of a fresh prompt (prompt becomes a follow-up `prompt` command).
- **Workspace**: clone per execution exactly as `examples/full/coding-worker/src/git.ts` does (copy that file into this package — no cross-package import of an example): clone URL/branch/PAT from env `OFAC_GIT_CLONE_URL`, `OFAC_GIT_BRANCH`, `OFAC_GIT_TOKEN` (same names the coding-worker reads — confirm the exact env names in `examples/full/coding-worker/src/bin/worker.ts` when copying).
- **Prompt**: verbatim assign `objective` as the prompt text; optional worker.yaml `promptTemplate` with `{objective}` placeholder. No adapter-side composition.
- **ask_user MCP**: package-internal stdio MCP server (add `@modelcontextprotocol/sdk` — pin via pnpm catalog, add to `pnpm-workspace.yaml` catalog). The server runs **in the worker process** over a socket pair; a generated config file `<workDir>/<executionId>/mcp.json` (or omp `--config` overlay) launches a node shim (`dist/bin/mcp-shim.js`, same bundle) that connects back. Tool schema: `ask_user {question: string, choices?: string[]} → {answer: string}`; implementation = `ctx.requestInput(req)`. System prompt injection: `--append-system-prompt "You are running headless under the Open Facilitator. When blocked on a decision only a human can make, call the ask_user tool and wait for the answer. Do not stop to ask in plain text."` (exact literal).
- **Events**: turn-complete → `publishProgress("turn complete")`; tool-call start (excluding ask_user) → throttled `publishProgress("tool: <name>")`; final assistant message → completion: upload transcript file + `final.md` via `createArtifactClient`, `buildResultManifest` (copy `deterministic/src/harness.ts` publication sequence verbatim in shape), `buildCompletedEvent`, publish, return `{kind:"completed"}`. CLI crash / nonzero exit before completion → return `{kind:"failed", reason:"omp exit <code>: <stderr tail 2KB>"}` (SDK publishes retryable failed.v1 — the error leg to humans).
- **Tests**: config tests; harness tests spawning a **fake omp** fixture script (`test/fake-omp.mjs`) that speaks rpc (reads prompt/steer/abort, emits canned events incl. an MCP-configured ask_user round-trip via the shim against the SDK stub kernel); `test/lifecycle.test.ts` running `runAdapterLifecycleContract` including the new interaction leg; `test/e2e/worker.e2e.test.ts` spawning the real bin against a standalone Gateway (copy deterministic's e2e, `describe.skipIf(!E2E)`).
- `sample/capabilities.yaml`: `coding.omp` descriptor with the frozen flags.
- README.md: copy-me template; MUST state the agentic-mode law (auto-approve forced, why, how to override via extraArgs) and the ask_user flow.

## Track C (parallel with B): codex adapter package

Same skeleton. Package `@ofac/harness-codex`, bin `ofac-codex-worker`, capability `coding.codex`, `supportsSteering:false`.

- **Invoke**: `codex exec --json -s workspace-write --ask-for-approval never -C <workDir>/<executionId>/repo <extraArgs…> <prompt>` with env `CODEX_HOME=<workDir>/codex-home` (worker-managed dir containing the generated `config.toml` with `[mcp_servers.ofac]` pointing at the same MCP-shim pattern as Track B — factor the MCP server + shim into a shared internal folder `src/adapters/harnesses/omp/src/mcp/` and have codex own its own copy `.../codex/src/mcp/` to keep track ownership disjoint; duplication is accepted, extraction to a package is a later cleanup). Confirm current exec flags (`--json`, sandbox/approval spelling, `exec resume`) against `codex exec --help` at implementation start — flags are `unverified — confirm first`; fall back to `--full-auto` equivalents if `--ask-for-approval` is absent in the pinned version.
- **Parse**: stdout JSONL event stream (codex exec emits `session.created` carrying `session_id`, item/turn events, final `agent_message`). Map: session id → `SessionStore`; item events → throttled progress notes; final message → completion (same artifact/manifest sequence). Unknown event types: ignore, never crash.
- **Resume**: retry generation → `codex exec resume <session_id> --json … <answerOrObjective>`. **Steer**: mid-run → SDK answers `unsupported` (do not implement `onSteer` for running state; a steer arriving after a run settled but before completion publication must not resurrect it).
- **Cancel**: SIGTERM the process group; SDK drops cancelled runs without terminal events (existing law).
- **Tests**: mirror Track B: fake codex fixture emitting a recorded-shaped JSONL stream (session.created, item events, agent_message, plus an ask_user MCP round-trip), config tests, `runAdapterLifecycleContract` (with `steerReply:"unsupported"` variant — this adapter runs BOTH legs as its honest surface), e2e with the real bin.
- README + sample capabilities as Track B.

## Track D (parallel with A/K/B/C): features, build, release, CI

Files owned: `features/src/omp-adapter/**`, `features/src/codex-adapter/**`, `features/build.mjs`, `features/test/{omp-adapter,codex-adapter}/**`, `.github/workflows/release.yml`, `.github/workflows/ci.yml`, `pnpm-workspace.yaml`.

1. `pnpm-workspace.yaml`: add `src/adapters/harnesses/omp` and `.../codex` (do this in Track 0 commit if a single owner is needed — otherwise D owns it; B/C packages exist only after their tracks merge).
2. Per feature dir, clone `features/src/harness-adapter` files with these deltas:
   - `devcontainer-feature.json`: ids/versions/descriptions per frozen table; options `configPath` + `requireCli` (boolean default true); same `entrypoint` supervisor + `installsAfter` node.
   - `install.sh`: identical contract (node≥22 gate, checksum-verify+extract to `/usr/local/lib/openfacilitator/<name>`, `.installed-version` idempotency, supervisor staging from `pkg/`). CLI gate: `command -v omp|codex` — fail with `ERROR: '<cli>' not found on PATH — install it in the base image (npm i -g @openai/codex / oh-my-pi) or set requireCli:false` unless `REQUIRECLI` is false. Shim names `ofac-omp-worker` / `ofac-codex-worker` → `$PREFIX/dist/bin/worker.js`.
   - Unit heredoc: `31-omp-adapter.sh` / `32-codex-adapter.sh` — copy unit 30's self-enroll block **byte-for-byte** (incl. the mirror law comment pointing at `reference/container/units`), default `WORKER_CONFIG` per frozen table, exec the new shim, same exit-code/restart law.
3. `features/build.mjs`: two `FEATURES` table rows `{feature:"omp-adapter", pkg:"@ofac/harness-omp", …}`, `{feature:"codex-adapter", pkg:"@ofac/harness-codex", …}` (field names per existing rows).
4. `features/test/<name>/`: `scenarios.json` (ubuntu+debian, `requireCli:false`) + `ubuntu.sh`/`debian.sh` asserting: install stamp exists, shim on PATH, unit file at `/usr/local/share/openfacilitator/units/31-omp-adapter.sh`, unit starts with a fake CLI (scenario script writes a `/usr/local/bin/omp` stub emitting a `worker ready` line path — copy assertions from `features/test/harness-adapter/*`).
5. `release.yml` features job: majors loop list becomes `gateway workspace-link harness-adapter omp-adapter codex-adapter`.
6. `ci.yml` gateway-e2e job: add `pnpm -C src/adapters/harnesses/omp test:e2e` + codex equivalent (copy the deterministic step's shape).
7. Per-feature `README.md`: options table, base-image CLI requirement + agentic-mode note.
8. NOT touched: `Dockerfile.node` (these adapters do not ship in the node-runtime image — ref-env keeps deterministic only), `reference/**` (compose demo unchanged), existing `harness-adapter` feature (its `adapter` option stays deterministic-only; arbitrary-bundle story stays roadmap Plan 32).

## Track E (after B/C): docs

`src/adapters/harnesses/README.md` shipped list (omp + codex lines); `docs/advanced.md` HarnessAdapter section: interactivity law (input.required → routing law → respond → live run), agentic-mode requirement; update `.omp/skills/facilitator-workspace-packaging/SKILL.md` unit/feature table (new unit numbers, new feature dirs) per the repo doc-sync rule (rides the same merge).

## Integration order & parallelism

```
Track 0 ──┬── Track A (wire/SDK)  ──┬── Track B (omp)   ──┐
          ├── Track K (kernel)      └── Track C (codex)   ├── Track E (docs) → full verify
          └── Track D (features/CI/release, self-contained)┘
```
A ∥ K ∥ D run concurrently (disjoint files). B and C start when A lands (they need `RunContext.requestInput` + extended kit). E after B/C. Each track MUST run its own package-level tests only (`pnpm -C <pkg> test`, `dotnet test --filter …`); the full gate runs once at the end — no per-track `make build`/lint loops.

## Critical files & anchors

- `protocol/json-schema/adapter.v1.json` — childMessage oneOf is the exact insertion point for `respond`; its description carries the stale "no respond frame" claim to fix.
- `src/gateway/src/runtime.ts:680+` (`onAssign`) and the steer/cancel bound-child arm (`onInteraction`, ~:787) — the routing pattern the new `assignment.input` command arm copies.
- `src/adapters/harnesses/adapter-sdk/src/{types.ts,adapter.ts,testing.ts}` — RunContext/createAdapter/contract seams for `requestInput`.
- `src/adapters/harnesses/deterministic/` — package shape to clone; `src/harness.ts` completed-publication sequence (upload → `buildResultManifest` → `buildCompletedEvent`) both adapters copy.
- `features/src/harness-adapter/install.sh` — the unit-30 heredoc + self-enroll block to mirror into units 31/32.
- `src/facilitator/Kernel/Work/ApprovalIngress.cs` + `ApprovalEndpoints.cs` + `ResultIngress.cs` (`communication_bindings` insert ~:266) — ingress/endpoint/fan-out precedents for Track K.

## Verification

1. Per-track unit gates as listed above; `make lint` and `pnpm -r --if-present typecheck` at integration.
2. **New-behavior proof (interactive loop, fake CLI, no network):** `pnpm -C src/adapters/harnesses/omp test` drives the conformance interaction leg: fake omp calls ask_user → kit observes an `input.required` event frame with the exact question → kit sends `command.assignment.input.v1` through the in-process Gateway → adapter's blocked `ask_user` tool resolves with the answer → run completes with a result manifest. Same leg for codex with `steerReply:"unsupported"`.
3. **Deterministic regression proof:** `pnpm -C src/adapters/harnesses/deterministic test` — the always-on kit question leg executes against the `askQuestion` hook: publish input.required observed by the kit, deliver the respond, assert the answer lands embedded in the report artifact.
4. **Kernel loop:** xUnit scenario — publish input.required via link-plane fixture → assert timeline + fan-out outbox row per law arm; POST respond → assert envelope command routed to bound endpoint; respond twice → 409.
5. **Features:** `npx --yes @devcontainers/cli@^0.79 features test -p features` (from repo root per existing usage) — new omp-adapter/codex-adapter scenarios green on ubuntu+debian; existing scenarios unchanged.
6. **Build/release smoke:** `node features/build.mjs` produces both pkg tarballs + sha256; `make build && make test` (full suite, <10min budget) warning-free.
7. **Live smoke (non-gated, manual):** each package's `scripts/live-smoke.mjs` (stub kernel + in-process Gateway + real CLI, one clone-and-edit assignment) run once with a real `omp` on PATH (present in this env) and, if `OPENAI_API_KEY` available, `codex`; record output in the PR. If `codex` is absent on the machine, the codex leg reports "live smoke not exercised — fake-CLI coverage only".

## Assumptions & contingencies

- **omp rpc frame shapes** are taken from pi rpc docs (`prompt`/`steer`/`abort`/`get_state`, `response` records, event stream) — this env has omp v18.12.6: Track B's first step probes `omp --mode rpc` live and adapts the fake fixture to the REAL stream shape; the frozen interface (what the adapter publishes upstream) does not change.
- **codex exec flags**: exact sandbox/approval/resume spellings unverified against a pinned CLI version; Track C confirms via `codex exec --help` at start; if a flag is absent, use the nearest documented equivalent and record it in the README.
- **pnpm catalog**: `@modelcontextprotocol/sdk` addition goes into `pnpm-workspace.yaml` catalog if the repo pins versions centrally (match existing catalog usage); a direct version in both package.json files is acceptable fallback.
- Implementation runs as parallel subagents (one per track); integration owner lands Track 0 and runs the final gate.
