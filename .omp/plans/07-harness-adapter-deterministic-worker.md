# Plan 07 — HarnessAdapter contract + deterministic worker harness

**Status:** 🟡 in progress — Step 1 (contract amendment) landed; the Implementation Status table below is maintained per step in the same commit.
**Phase:** B. **Depends on:** 05 (🟡, near-🟢) + 06 (implemented locally — code wins where this file's quotes diverge).
**Branching:** work and commit **directly on `main`** (standing directive from Plans 03–06). Do not push.
**Size judgment:** kept as ONE plan — the contract schema, the SDK that implements it, and the reference harness that proves it are one vertical slice; the contract is unproven without the harness. Split candidate if execution drags: Steps 5–6 (e2e + example + docs) as a 07b, decidable only once the unit tier (Steps 1–4) is green.

> Questions may be asked during planning, but not during implementation. Every design decision is locked below; the implementer executes top-to-bottom. Missing detail → infer from `docs/specs/open_facilitator_framework_architecture.md` (the "spec", S:line), `docs/specs/open-facilitator-appendix.md` (the "appendix", A:line), `.omp/skills/facilitator-protocol/SKILL.md` + `.omp/skills/facilitator-workspace-packaging/SKILL.md`, the as-built Gateway (`src/gateway/`), the SDK (`src/workspace-link/`), and the frozen contract in `protocol/` — do not stall. If a library/API is unclear (Node `node:net`/`node:crypto`, quicktype options, ajv, vitest), consult its official docs or search for the standard pattern; never guess a flag and never ask mid-implementation. When an approach fails after a couple of straightforward iterations, step back and re-read the relevant locked decision instead of adding a layer — prefer the boring, smaller design; a special case growing past one call-site is re-wired, not patched.

## Context

Plan 06 gave children a real transport to the Gateway: NDJSON frames over unix socket / loopback TCP, the `ChildMessage`/`ChildReply` vocabulary, the event relay (`event` frame carries a **full CloudEvents envelope**, forwarded unmodified), and a child-side client `connectChild` exported from `@ofac/gateway` whose header explicitly says "reconnect/restart policy is the CALLER's call (Plan 07's adapter owns restart policy)". This plan turns that internal vocabulary into the **public HarnessAdapter lifecycle contract** (spec §8.3, S:420–442): a machine-readable frame schema in `protocol/`, a thin `@ofac/adapter` package (lifecycle scaffold over `connectChild`: restart policy, execution registry, event helpers), and a reference implementation — a deterministic research-worker harness (spec §21.1's "deterministic demonstration harness", S:1422/S:1436) that accepts a real `work.assign`, publishes progress, writes a Markdown report file, completes with a schema-valid `resultManifest`, honors steer mid-run, and reconciles honestly after a crash. End state: `Gateway(mode: standalone) + deterministic worker` pointed at a dev Facilitator completes a seeded assignment end-to-end; a third party can implement an adapter from the schema + README alone; §26.1 item 7's "≥2 different adapters" gets its second real adapter alongside the built-in echo.

**Contract-home decision (planning-time, user-approved):** the frame schema joins `protocol/` as a contract amendment pre-authorized by Plan 06's lock ("when a non-Node adapter needs the wire, propose a contract amendment per §5.5 discipline"). Rationale: the adapter contract is now cross-team-public; schemas give third parties + both conformance harnesses + payload-limit fixture basis. This supersedes 06's "no schema in protocol/" only by ADDITION — the TS types in `src/gateway/src/childwire.ts`/`child.ts` stay the as-built source; the schema profiles them verbatim.

**Deviations from the thin plan (locked, minimal):**
- The thin plan's frame names (`register`, `command.deliver`, `command.accept|reject|unsupported`, `event.publish`, `steer`, `cancel`, `status.request|response`) do NOT exist on the wire. Profile the 06 as-built frames verbatim: `hello`/`command`(message kinds `start|steer|status|stop`)/`reply`(ChildReply kinds `accepted|rejected|unsupported|status|unknown`)/`event`/`heartbeat`/`register-ack`/`goaway`. Justification: 06 is shipped; a parallel naming would be a second protocol (§8.3's lifecycle maps onto the shipped frames, not vice versa).
- §8.3's six-method `HarnessAdapter` interface profiles as: `start`→`start` frame, `steer`→`steer`, `status`→`status`, `interrupt`→`stop`, `observe`→ the push-based `event` relay (spec's AsyncIterable becomes push events — same typed-event mapping, one fewer abstraction). `respond` (input_required answer) has NO wire frame in v1 ⇒ adapters v1 advertise no input support; fail-closed: nothing in Phase B sends it. Justification: no Kernel→child input channel exists yet; inventing a frame without a producer is over-engineering (S:436's law — unsupported operations are reported, never silently reinterpreted).
- Artifact interim: the deterministic worker writes a local file + a `file://` `storageUrl` in the manifest. `result-manifest.v1.json`'s `artifactRef.storageUrl` is `format: uri`, and S:1132 explicitly allows "a worker may write into team storage and submit a verified external reference"; digest+owner+createdAt are present ⇒ conformant interim, no fake store, no pulling Plan 11's endpoint forward. Justification: boring + spec-sanctioned.
- Native-session persistence hook: NO SQLite session store in v1. The deterministic harness declares `resume: "restart-with-context"` (S:442's sanctioned alternative) — a crashed harness's executions reconcile to `lost` via the shipped re-register probe (`unknown` reply ⇒ `lost` + ONE retryable `assignment.failed` reason `"child-lost-execution"`). `@ofac/adapter` exposes an optional `sessionStore` hook (interface only) as the seam where a resumable harness adds its store. Justification: a store nobody resumes from is dead weight; the reconcile path is already the tested law.

## Step 0 — Mandatory inspection before writing any file

Execute all; record findings in the first commit message body. Facts below verified during planning (2026-10-24); re-verify, do not re-decide; if code diverges, **the code wins** and the divergence goes in the Implementation Status table.

1. Gateway as-built (`src/gateway/src/`): `child.ts:32-51` (`ChildMessage`/`ChildReply`, discriminator `kind`; `MANIFEST_SCHEMA_ID = "https://schemas.openfacilitator.org/v1/capability-manifest.v1.json"` :27-28; `builtinChildManifest(mode)` advertising `research.analyze` v1 with `outputKinds:["analysis.report"]`, `outputMediaTypes:["text/markdown"]`; `newExecutionId()` = `exec-<8hex>` :214-216); `childwire.ts:39-95` (frame literals, `v:1` on every frame, direction gates, `MAX_CHILD_FRAME_BYTES = 262_144`, `RegisterDenyReason`/`GoawayReason` literals); `connect.ts` (`connectChild(options)` → `{connected, publishEvent(env), close(), waitClosed()}`, options `{childWorkspaceId, token, path?|port?+host?, onCommand(frame): Promise<ChildReply>, heartbeatSeconds?=30, handshakeTimeoutSeconds?=5, onNotice?}` — NO auto-reconnect); `listen.ts` (`startChildListener(config, deps)`, auth via per-child token/`OFAC_CHILD_SECRET`, stale-socket takeover); `config.ts` (v2 keys: `mode`, `children[]`, `childTransport.socket.path`/`.tcp.port`, `handshakeTimeoutSeconds`, `leaseMissedBeats`); `runtime.ts` (onAssign `:575-656`, settleStart reply law `:658-675`, steer/cancel bound-child law, forwardEvent `:775-800` child-generation fence + row settle, re-register probe `:410-427`).
2. Working child-process consumer to imitate: `src/gateway/test/e2e/child-runner.mjs` (real child: `connectChild` from built dist + `buildEvent` from `@ofac/workspace-link`; prints `child ready <id>`) and `src/gateway/test/e2e/gateway-harness.ts` (exports `E2E`, `useFacilitator()`, `assignEnvelope`, `seedDelivery`, `waitFor`, `freePort`, `gatewayConfig({fleet, gatewayMode})` YAML writer, `startGateway` (ready regex `/^gateway ready endpointId=\S+ epoch=(\d+) children=(\d+)/`), `gatewayStatus`, `inboxRowsByType`, `startChild`/`childReady`).
3. SDK reusable surface (`src/workspace-link/src/`): `index.ts` exports `buildEvent(type, {source, subject, data?, traceparent?, correlationid?, causationid?, id?, time?, dataschema?})` (validates via `@ofac/protocol`, `dataschema` defaults to `dataschemaFor(type)`, base `SCHEMA_BASE = "https://schemas.openfacilitator.org/v1/"`), `createConsoleLogger`/`createMemoryLogger`, `MAX_ENVELOPE_BYTES`; `core/handlers.ts` echo/stub = the placeholders this plan supersedes on the demo path (leave `linkd` UNTOUCHED — it talks to a Facilitator, not a Gateway).
4. Contract tree (`protocol/`): `json-schema/result-manifest.v1.json` (required `manifestId`,`outputs`; optional `revisionOf`,`notes`; `$ref` `common.v1.json#/$defs/artifactRef` — required `artifactId, mediaType, size, digest, storageUrl, createdBy, createdAt`, digest pattern `^sha256:[0-9a-f]{64}$`, `storageUrl` `format: uri`); `event.assignment.completed.v1.json` (required `workId, assignmentId, executionId, executionGeneration, resultManifest`); `event.work.progress.v1.json` (required `workId, assignmentId, executionGeneration, progressNote`); `command.work.assign.v1.json` (required incl. `inputs`, `expectedDeliverables`), `.steer` (`directive`), `.cancel` (`reason`); naming: file `X.v1.json` ↔ `$id https://schemas.openfacilitator.org/v1/X.v1.json`. `conformance-fixtures/index.json` case classes (`command|event|control|receipt|enroll|delivery`) + loaders: TS `src/protocol/test/fixtures.test.ts:36` (index-driven, per-class branches — model a new `adapter` class on the existing `schemaFile`-bearing enroll/delivery branch), .NET `protocol/tools/Validate.Fixtures/Program.cs:18` (fail-closed class table). Codegen: `pnpm protocol:codegen` (quicktype over both schema dirs) → `src/protocol/src/generated/*` + `protocol/dotnet/Ofac.Protocol/Generated/WireTypes.cs` + byte-copies into `src/protocol/schemas/`; drift gate `make protocol` (`git diff --exit-code`). **Any new schema file auto-enters both codegen outputs + the .NET command-table derivation — check `Program.cs`'s class derivation first so `adapter.v1.json` is NOT misread as a wire message type; if it derives message types from filenames, add an explicit exclusion for it.**
5. Package template: `src/gateway/package.json` (tsup esm+dts node22, `private: true`, `publishConfig` GH Packages, repo.directory), `pnpm-workspace.yaml` (`packages: [src/gateway, src/protocol, src/workspace-link]` — MUST gain the two new dirs), `tsconfig.base.json` (strict + `exactOptionalPropertyTypes`), per-package `tsconfig.json`/`tsconfig.test.json`/`vitest.config.ts`/`vitest.e2e.config.ts`, `src/gateway/test/package.test.ts` (export-surface assertion precedent).
6. Spec/appendix re-read: §8.1–8.5 (S:378–466: authority law S:422, six-method contract S:424–433, per-method semantics S:435–440, session hook + descriptor S:442, `.facilitator/` layout S:444–462 incl. `harness.yaml` S:452), §15.2–15.4 (S:1043–1141: completion-is-manifest law S:1061, `artifact://` is logical not network S:1126, worker-submits-verified-external-ref S:1132), §21.1 acceptance script (S:1436), §23.1 steering tail (S:1824–1831: steer lands on the running native run), §26.1 items 3/7/12 (S:2010/S:2013/S:2018), appendix §8 (A:226–256), §12.3 (A:358–360 deterministic-runs persona), §10.1 must-haves.
7. Docs gate as-built: `protocol/tools/agents-docs-check.mjs` — AGENTS.md ≤80 lines, `/Plan [0-9]/` FORBIDDEN in AGENTS.md, every backticked worktree path must exist. `src/adapters/AGENTS.md` ("no code has shipped") and `src/adapters/harnesses/README.md` ("Populated in Plan 06") are both stale and MUST be rewritten by this change. `.omp/skills/facilitator-protocol/SKILL.md` "adapters still unbuilt" line and `.omp/skills/facilitator-workspace-packaging/SKILL.md` spec-proposal HarnessAdapter section (§ 39–52) likewise.
8. CI/Make as-built: `Makefile:39-44` (`gateway-e2e` → `pnpm -C src/gateway test:e2e`, needs `FACILITATOR_URL`/`FACILITATOR_PG`, dev Postgres `facilitator-dev` :5433), `.github/workflows/ci.yml:116-151` job `gateway-e2e`. This plan EXTENDS that job + adds a Make target; no new job.

## Locked decisions (no implementer choice)

### `protocol/json-schema/adapter.v1.json` — the public frame contract

- Single schema, `$id "https://schemas.openfacilitator.org/v1/adapter.v1.json"`, `title "AdapterFrameV1"`, describing ONE frame line (either direction) as `oneOf` over `$defs` per frame: `hello`, `registerAck`, `command`, `reply`, `eventFrame`, `heartbeat`, `goaway`. Top-level `required: ["v","type"]`, `"v": {"const": 1}` (note the property is named `v` — the `apiVersion` style is envelope-level, frames use `v`, matching `childwire.ts`), discriminator `type` with `const` per variant. Payloads `$ref` frozen neighbors: `manifest` → `capability-manifest.v1.json`, envelope → `cloudevents-schemas/envelope-event.json`, embedded child payloads mirror `child.ts` EXACTLY: `command.message` = start|steer|status|stop variants (`additionalProperties: false`), `reply.reply` = accepted|rejected|unsupported|status|unknown variants. Frame byte limit stays a code/doc constant (`MAX_CHILD_FRAME_BYTES = 262_144`), not a schema keyword (size validation is transport-level; fixtures cover it via a >limit envelope case at the harness level, per the existing `limits.maxEnvelopeBytes` idiom).
- Run `make protocol`; commit whatever quicktype emits (drift gate is the arbiter). If quicktype output for `AdapterFrameV1` is unusable junk, the fix is `--exclude`-equivalent handling in `protocol/tools/codegen.mjs` (add a documented skip list for `adapter.v1.json` in the TS/C# name derivation while the schema still ships + validates) — decide by inspecting the output, and record the choice in the status table. The schema's consumers this phase are the fixture harnesses + documentation; generated DTOs are a bonus.
- **Rival pattern avoided:** do NOT mint `org.facilitator.command.*` names for frames (they're not CloudEvents messages; the `.NET` fixture tool's fail-closed command table is derived from schema filenames — confirm exclusion at inspection item 4).

### Fixtures (`protocol/conformance-fixtures/`)

New class `"adapter"` in `index.json`, cases carry `"schemaFile": "adapter.v1.json"` (expect valid/invalid, invalid `errorClass: "schema-violation"`). Valid (≥10): `adapter.hello.json`, `adapter.command.start.json`, `adapter.command.steer.json`, `adapter.command.stop.json`, `adapter.command.status.json`, `adapter.reply.accepted.json`, `adapter.reply.status.json`, `adapter.reply.unknown.json`, `adapter.event.completed.json` (full envelope embedding a schema-valid completed payload + manifest), `adapter.heartbeat.json`, `adapter.registerAck.denied.json`, `adapter.goaway.json`. Invalid (≥4): missing `v`; unknown frame `type: "event.publish"` (the retired thin-plan name — proves fail-closed on future naming drift); `register-ack` with reason outside the `RegisterDenyReason` enum; completed-event frame with bad digest prefix (e.g. `sha512:`) inside the envelope. Loader changes: TS `fixtures.test.ts` gains an `adapter` branch validating only against `schemaFile` (copy the enroll branch); .NET `Program.cs` gains the class (same rule). No other harness consumes the class.

### `@ofac/adapter` package — `src/adapters/harnesses/adapter-sdk/`

Layout/`package.json` cloned from `src/gateway` (name `@ofac/adapter`, repo.directory `src/adapters/harnesses/adapter-sdk`, scripts build/test/test:e2e/typecheck identical, deps: `@ofac/protocol`, `@ofac/workspace-link`, `@ofac/gateway` all `workspace:*`; NO other runtime deps — zero-dep besides workspace packages, gate item honored). Exports: `"."` and `"./testing"`.

Public API (`src/index.ts`, single module split `adapter.ts` + `registry.ts` + `types.ts`):

```ts
export interface AdapterDescriptor {            // §8.3 S:442 descriptor, documentation-grade (not on the wire)
  readonly invocation: "sdk";                   // v1 ships the sdk invocation only
  readonly supports: { readonly steer: boolean; readonly interrupt: boolean; readonly resume: "restart-with-context" };
}
export interface RunContext {
  readonly executionId: string;
  readonly executionGeneration: number;         // = start.connectionGeneration; stamps event payloads
  readonly command: EnvelopeCommand;            // the work.assign envelope, verbatim
  readonly signal: AbortSignal;                 // fired by stop frame
  publishEvent(env: EnvelopeEvent): void;       // fire-and-forget relay (connectChild.publishEvent)
  publishProgress(note: string): void;          // buildEvent sugar: work.progress.v1, executionGeneration stamped, subject = assignmentId
  setSteered(cb: (directive: string) => void): void; // register once; steer frames invoke it (default: steer → unsupported reply)
  sessionRef?: string;                          // opaque native-run id; echoed in status replies' executionId slot ONLY as documentation — ChildReply has no field, so the SDK keeps it in the registry (harness persistence hook)
}
export type RunOutcome = { readonly kind: "completed" } | { readonly kind: "failed"; readonly reason: string };
export interface HarnessHooks {
  onExecute(ctx: RunContext): Promise<RunOutcome>;   // resolves ⇒ registry entry dropped; publish the terminal event yourself (completed) or return failed → SDK publishes assignment.failed.v1 (reason)
  onSteer?(directive: string, ctx: RunContext): void;
  onCancel?(reason: string, ctx: RunContext): void;  // cooperative interrupt (§8.3 S:439); hard kill = runtime's job
  statusProvider?(): { readonly activeCount: number }; // reserved seam; v1 registry answers status itself
  sessionStore?: SessionStore;                    // OPTIONAL interface (get/set/delete executionId↔sessionRef) — the persistence seam; nothing in-repo implements it
}
export interface CreateAdapterOptions {
  childWorkspaceId: string;                       // "<gatewayId>/<name>" — Gateway-side law, validated here too (regex ^[a-z0-9]([a-z0-9-]*[a-z0-9])?$ on the name part, ≤128)
  token?: string;                                 // omitted ⇒ read OFAC_CHILD_SECRET
  manifest: CapabilityManifestV1;
  transport: { readonly socketPath: string } | { readonly host: string; readonly port: number };
  descriptor: AdapterDescriptor;
  hooks: HarnessHooks;
  heartbeatSeconds?: number;                      // default 30
  restartBackoffMs?: readonly number[];           // default [100,250,500,1000,2000,5000] capped-loop; [] disables auto-restart
  logger?: LinkLogger;                            // default createConsoleLogger()
}
export interface AdapterHandle {
  ready(): Promise<void>;                         // resolves on first register-ack accepted; rejects "register denied: <reason>"
  status(): { connected: boolean; childGeneration: number; activeExecutions: readonly string[] };
  close(): Promise<void>;
  readonly events: { on(event: "connected"|"disconnected"|"goaway", cb: (info?: unknown) => void): void };
}
export function createAdapter(opts: CreateAdapterOptions): Promise<AdapterHandle>;
```

Locked behaviors:
- **Frame handling:** `createAdapter` = `connectChild` + the lifecycle law. On `command{start}`: dedupe first (in-memory registry keyed `executionId`; a duplicate `start` — matched by `command.data.commandId` — replies `{kind:"accepted", executionId}` without re-executing, 06's dedup law); unknown message kinds ⇒ `{kind:"unsupported"}`; spawn `onExecute` detached, reply `{kind:"accepted", executionId}` immediately (async capability law), registry entry `running`. On `command{steer}`: known execution + hook ⇒ invoke, reply `accepted`; known + no hook ⇒ reply `unsupported` (S:436 report-don't-reinterpret); unknown execution ⇒ `{kind:"unknown", executionId}` (reconcile law). On `command{stop}`: abort signal + `onCancel`, reply `accepted`, entry dropped on run settle; unknown ⇒ `unknown`. On `command{status}`: registry hit ⇒ `{kind:"status", executionId, state}` (running | terminal from a settled snapshot kept 60s, const `TERMINAL_TTL_MS = 60_000`); miss ⇒ `unknown`. Handler throws ⇒ `{kind:"rejected", reason:"adapter handler failed"}` (connectChild already guarantees a reply).
- **Restart policy (the `connectChild` debt this plan pays):** on socket close ≠ local `close()`: backoff loop (locked default list, cap-loop forever), re-`connectChild`; registry survives ⇒ in-flight runs continue and a Gateway re-register probe gets `running`; registry lost with the process ⇒ `unknown` ⇒ Gateway marks `lost` + one retryable `assignment.failed` (Gateway law, 06 as-built — adapter adds nothing). Register denial at restart: retry the backoff loop too (a restarting Gateway may briefly deny `child socket in use`-adjacent races ⇒ keep trying; reasons `denied: bad-token`/`child-id-namespace`/`duplicate-id` ⇒ emit `error` log + retry — honest, never silent exit).
- **Event publishing:** only `publishEvent`/`publishProgress` reach the wire; `publishProgress` builds via `buildEvent` with `source: "urn:workspace:" + childWorkspaceId`, `subject: <assignmentId>`, data `{workId, assignmentId, executionGeneration, progressNote}` (traceparent: copy from the assign envelope if present, else omit). Terminal publish: SDK publishes `assignment.failed.v1` `{workId, assignmentId, executionId, executionGeneration, reason, retryable: true}` ONLY when `onExecute` returns `{kind:"failed"}` or throws; `assignment.completed.v1` is the harness's own publish (05 law: only child-published completed/failed settle rows — the SDK never fabricates completion).
- **No config file, no CLI** in `@ofac/adapter` — it is a library; config/CLI belongs to worker binaries (below + 08).
- **Test kit (`"./testing"` export):** `export function runAdapterLifecycleContract(opts: { startAdapter(): Promise<{handle: AdapterHandle; stop(): Promise<void>}>; socketPath: string; gatewayId: string }): void` — calls `describe`/`it` internally (vitest-global safe), spawning an in-process `startChildListener` from `@ofac/gateway` (no Facilitator needed). Assertions: hello carries the manifest; full lifecycle start→progress→completed-event-relay (payload validates against `result-manifest.v1.json` via `@ofac/protocol`'s `validateAgainstSchemaId`); steer-mid-run reaches `onSteer`; steer without hook ⇒ `unsupported` reply; stop aborts (signal fired, `onCancel` called, accepted reply); kill listener mid-run ⇒ adapter auto-reconnects and probe answers `running` when the run survived, `unknown` after `stop()`; status reply states after each phase; duplicate start dedupe. The deterministic package runs this suite against the REAL adapter (its own hooks) — proving `@ofac/adapter` green is Plan 08's Feature test + Plan 16's provisioning entry point ("run this kit").

### Deterministic harness — `src/adapters/harnesses/deterministic/` (package `@ofac/harness-deterministic`, bin `ofac-deterministic-worker`)

Layout cloned from `src/gateway` (deps: `@ofac/adapter`, `@ofac/protocol`, `@ofac/workspace-link`, `yaml`; devDeps catalog set). The package is the worked example: third-party implementers copy this + the README.

- **Config** (`src/config.ts`): YAML file (the `yaml` package parses JSON too), hand-rolled validator in 06's style (unknown-key rejection, error names the bad key, `WorkerConfigError`). Keys:
  ```yaml
  apiVersion: facilitator.framework/v1
  kind: HarnessAdapter
  childWorkspaceId: gw.team-alpha/deterministic    # name part regex + ≤128 (same law as Gateway admission)
  token: child-secret-1                            # optional; OFAC_CHILD_SECRET env wins
  transport: { socket: { path: ./child.sock } }    # XOR transport: { tcp: { host: 127.0.0.1, port: 8090 } }; exactly one; relative socket path resolved against workDir
  manifestFile: ./capabilities.yaml                # CapabilityManifestV1; ownerTeam validated === childWorkspaceId prefix? NO — validated schema-only (06 discards child ownerTeam; mirror that)
  workDir: ./.ofac-worker                          # artifacts under <workDir>/artifacts/; dirs created 0o700
  stepMs: 1500                                     # 0..60000 — per-step sleep (two steps per run)
  heartbeatSeconds: 30
  restartBackoffMs: [100, 250, 500, 1000, 2000, 5000]
  ```
  Env overlays: `OFAC_CHILD_SECRET`, log level `OFAC_ADAPTER_LOG`. CLI (`src/bin/worker.ts`): `ofac-deterministic-worker run --config <path>` (env `WORKER_CONFIG` fallback), ready line `worker ready childId=<id> socket=<path>`, exit codes 0 clean / 1 runtime / 2 config / 3 register-denied-then-exhausted (with `restartBackoffMs: []`) — mirroring `bin/gateway.ts` idioms. Auto-restart default ON (locked default list) so a restarting Gateway is survivable (spec §8.1 headless law).
- **Scripted execution** (`src/harness.ts`, the two-step deterministic run, zero randomness):
  1. `publishProgress("accepted: analyzing <capabilityId> for <workId>")`.
  2. sleep `stepMs` (cancellable via `ctx.signal` — abort ⇒ resolve as cancelled, NO terminal event; the cancel reply already completed the Gateway row).
  3. `publishProgress("drafting report")`.
  4. sleep `stepMs` (same cancellability).
  5. Write `<workDir>/artifacts/<executionId>-report.md` (create dirs; overwrite same-execution rewrite is a same-bytes rewrite — idempotent):
     ```
     # Research report <workId>
     Objective: <inputs.objective if string, else workId>
     Assignment: <assignmentId> (execution <executionId>)
     ## Steps
     - step 1 complete at +<elapsedMs>ms
     - step 2 complete at +<elapsedMs>ms
     ## Steering directives
     - +<elapsedMs>ms: <directive>          (one line per steer received, in arrival order; section body "none" when empty)
     ## Conclusion
     Deterministic analysis complete.
     ```
     (elapsed values are wall-clock ⇒ digest is timing-dependent; tests assert structure/directive lines, never a fixed digest.)
  6. Publish `assignment.completed.v1` via `ctx.publishEvent(buildEvent(...))`: data `{workId, assignmentId, executionId, executionGeneration: ctx.executionGeneration, resultManifest: {manifestId: "man-<executionId>", outputs: [{artifactId: "art-<executionId>-report", mediaType: "text/markdown", size: <bytes>, digest: "sha256:<hex of file bytes>", storageUrl: "file://" + absolute path, createdBy: "workspace:<childWorkspaceId>", createdAt: <ISO Z now>}]}}` — every field per `result-manifest.v1.json`/`common.v1.json#/$defs/artifactRef` (sha256 via `node:crypto`).
  7. Return `{kind:"completed"}`.
  Steer: `onSteer(directive, ctx)` appends `{directive, elapsedMs}` to the run's in-memory list (visible in the report; a steer after step 5 (report written) still gets the `accepted` reply — recorded in the registry only; document: steer-landed-after-write is observable as no-content-change, honest). Cancel: `onCancel` clears timers.
- **Manifest fixture** (`examples/slim/deterministic-research-worker/capabilities.yaml` + a copy under the package `sample/`): `CapabilityManifestV1` advertising `research.analyze` v1, `execution: "asynchronous"`, `ownerTeam: "team-alpha"`, `outputKinds: ["analysis.report"]`, `outputMediaTypes: ["text/markdown"]` — same descriptor bytes as 06's `builtinChildManifest("echo")` capability entry so an embedded+fleet demo can't route-ambiguously (identical descriptor dedupes per 06's conflict law; but the example runs a standalone Gateway ⇒ single advertiser either way).
- **`linkd` untouched** — the no-Gateway placeholder stays for `link-e2e`.

### Example + local loop — `examples/slim/deterministic-research-worker/`

`gateway.yaml` (mode `standalone`, `childTransport.socket.path: ./child.sock`, `children: []`, diagnostics free port), `worker.yaml` (matching socket + `childWorkspaceId: gw.demo/deterministic`), `capabilities.yaml`, `README.md`: exact ordered commands — start dev Facilitator (`FACILITATOR_URL`/dev Postgres per `link-e2e` README idiom), `ofac-gateway run --config gateway.yaml`, `ofac-deterministic-worker run --config worker.yaml`, seed a `work.assign` (reuse the e2e harness's `assignEnvelope` shape → document the `curl POST /api/link/v1/events`-equivalent or the CLI: use `dotnet run --project src/facilitator -- work create ...` 02 idiom + show expected inbox/timeline output), expected output: worker log lines, `GET http://127.0.0.1:<diag>/status` showing the execution, report file path + `sha256sum` match against the manifest's digest (concrete check: `sha256sum <report> | head -c71` == manifest digest). Plan 08 replaces this wiring with the devcontainer Features, keeping the walkthrough as the no-Docker path (08's file already says so).

## Steps (build order; each ends green)

### Step 1 — Contract amendment: schema + fixtures + loaders + codegen
`adapter.v1.json` per locks; ≥10 valid + ≥4 invalid fixtures + `index.json` cases; both fixture loaders gain the `adapter` class; `make protocol` + commit generated output (exclusion fix in `codegen.mjs` if the inspection item-4 check fires). Verify: `make test` green incl. new fixture cases; `git diff --exit-code` drift gate passes after `make protocol`.

### Step 2 — `@ofac/adapter` package
New dir + workspace registration (`pnpm-workspace.yaml`), package.json/tsconfigs/vitest configs cloned from 06's package; `types.ts`/`registry.ts`/`adapter.ts` per locked API/behaviors; `test/package.test.ts` (export surface), unit tests per locked behavior (drive the adapter with an in-process `startChildListener` + scripted Gateway-side frames written raw to the socket: dedupe, steer-no-hook ⇒ unsupported literal, status states + `TERMINAL_TTL_MS` expiry, failed-publish law, handler-throw reply literal, restart policy with `restartBackoffMs: [10,20]` + listener swap). Verify: `pnpm -C src/adapters/harnesses/adapter-sdk build && … test && … typecheck`; root `pnpm -r build`/`make lint` clean.

### Step 3 — Deterministic harness package
`config.ts` (validator laws per-key), `harness.ts` (script above; digest via `node:crypto`), `src/bin/worker.ts` (CLI + ready line + exit codes), `sample/capabilities.yaml`. Unit tests: config law matrix (each named-key error, XOR transports, env-overlay precedence, stepMs bounds); harness script driven in-process with `createMemoryLogger` + fake `RunContext`: no-steer report structure (sections present, "none" body), steer-mid-run recorded with ordering, cancel-before-step2 ⇒ no terminal publish, completed-event payload validates against `result-manifest.v1.json` + `event.assignment.completed.v1.json` via `validateAgainstSchemaId`, digest == rehash of the written file, `storageUrl` is absolute `file://`. Verify as Step 2 for this package.

### Step 4 — Lifecycle contract kit
`./testing` export per locked signature; deterministic package's `test/lifecycle.test.ts` = `runAdapterLifecycleContract({startAdapter: <spawns createAdapter with the real deterministic hooks>, …})` all green (this is the kit's first two consumers: the kit itself + a second instantiation with a no-steer hooks variant expecting the `unsupported` reply — proves the kit asserts, not just runs). Verify: kit suite green; kit file has zero imports from the deterministic package (usable by 08/16 standalone).

### Step 5 — E2E + CI + Make
New `src/adapters/harnesses/deterministic/test/e2e/worker.e2e.test.ts` + `vitest.e2e.config.ts`, importing `gateway-harness.ts` helpers by relative path (`../../../../gateway/test/e2e/gateway-harness.js`-style; it re-exports the workspace-link harness). `describe.skipIf(!E2E)`, own `useFacilitator()`. Scenario (concrete input → observable output):
1. Gateway standalone boot (`children: []`, ready `children=0`) → spawn the REAL worker binary (`node dist/bin/worker.js run --config …`, ready line `worker ready childId=…`) → poll `/status` until child connected and `GET /api/link/v1/capabilities` shows `research.analyze` `ownerTeam: "default"`.
2. Seed `work.assign` for `research.analyze` (harness's `seedDelivery`) → poll kernel: `inboxRowsByType` shows `org.facilitator.event.work.progress.v1` ×2 then `org.facilitator.event.assignment.completed.v1`; the completed row's `data.resultManifest` validates against `result-manifest.v1.json` (in-test ajv via `@ofac/protocol`) AND the referenced `file://` path exists on disk AND `createHash("sha256")` of its bytes == the manifest digest.
3. Steer mid-run: second seeded assign (larger `stepMs`), once progress #1 seen seed a `work.steer` (`directive: "focus on costs"`) → reply accepted per bound-child law → after completion, the report file contains `focus on costs` under `## Steering directives` (grep the file — steer landed on the RUNNING execution, §23.1).
4. Crash law: third assign, SIGKILL the worker mid-run, Gateway marks child offline (lease), re-spawn worker → probe answers `unknown` ⇒ exactly ONE new `assignment.failed` row with reason `child-lost-execution`, `retryable: true` (06's child-kill law, now proven with a real harness).
Wiring: Makefile gains `worker-e2e: FACILITATOR_URL=… pnpm -C src/adapters/harnesses/deterministic test:e2e` (mirror `gateway-e2e` lines exactly, incl. env doc); `ci.yml` `gateway-e2e` job gains one step running the same command after the gateway step (same postgres service, no new job). Verify: `make worker-e2e` local.

### Step 6 — Example, docs, skills, roadmap bookkeeping
- `examples/slim/deterministic-research-worker/` per above (4 files).
- `src/adapters/harnesses/adapter-sdk/README.md`: write-your-own-adapter walkthrough (descriptor→hooks→lifecycle law: who replies what and when; the `event`-relay-is-full-envelope law; restart ownership; `runAdapterLifecycleContract` as your conformance gate).
- `docs/advanced.md`: new `## HarnessAdapter contract` section (after the Gateway config table): lifecycle diagram (mermaid sequence: kernel→gateway→adapter→harness: start/progress/steer/stop/completed), frame table (the seven frames, directions, payloads, `v:1`), authority law restated (adapter thin, harness authoritative, Gateway mediates — S:422), `file://` verified-external-ref interim + what Plan-era artifact store replaces, resume-`restart-with-context` statement.
- `src/adapters/AGENTS.md`: purpose/invariants updated to as-built (harnesses shipped: adapter-sdk + deterministic; drop "no code has shipped"); `src/adapters/harnesses/README.md` rewritten (no `Plan N` strings, paths must exist — docs-check).
- `.omp/skills/facilitator-workspace-packaging/SKILL.md`: status blockquote + HarnessAdapter section → as-built (frames = 06 wire profiled by `adapter.v1.json`; `@ofac/adapter` API surface; kit location; invocation `sdk` only). `.omp/skills/facilitator-protocol/SKILL.md`: fix "adapters still unbuilt"; note fixture class `adapter`.
- Roadmap: Plan 07 entry ⬜→🟡 at first commit, →🟢 on CI green; this file's Implementation Status table updated in the same commit as each step; FIRST implementation commit copies this content over `.omp/plans/07-harness-adapter-deterministic-worker.md` verbatim (05/06 precedent) + roadmap Plan 07 "Status" line points at it.

## Implementation Status

|Step|State|
|---|---|
|0 Inspection|Fully implemented: all 8 items verified 2026-10-25; plan's line anchors held (frames childwire.ts:41-91, ChildMessage/ChildReply child.ts:31-43 — one line off the quoted 32-51, code wins); the .NET command-table derivation filters `$id` contains `/command.`, so adapter.v1.json is NOT misread as a wire type (no Program.cs exclusion needed); `src/protocol/src/schemas.ts` gains an explicit import per schema (registry is by-name, not by-directory) — added for adapter.v1.json; envelope $refs are bare sibling-by-$id (`envelope-event.json`), not path-prefixed|
|1 adapter.v1.json + fixtures + loaders + codegen|Fully implemented: schema + 12 valid/4 invalid fixtures + both loader branches; eventFrame's completed-envelope `data` carries an `if/then` $ref into event.assignment.completed.v1.json so the bad-digest case is schema-visible; quicktype output for AdapterFrameV1 was junk (flattened oneOf: merged `reason` union can't carry free-form stop reasons; $ref renamed published CapabilityDescriptor types) => documented CODEGEN_SKIP list in protocol/tools/codegen.mjs (schema ships + mirrors + validates, DTO pass skipped) — the plan's sanctioned contingency|
|2 @ofac/adapter package|Fully implemented: createAdapter = connectChild + lifecycle law (dedupe by commandId, steer-no-hook ⇒ `unsupported` literal, registry status truth + TERMINAL_TTL_MS expiry ⇒ `unknown`, failed-publish law, handler-throw ⇒ rejected reply); restart loop with restartBackoffMs (cap-loop, `[]` ⇒ exit). Divergence: backoff sleep timer is REF'd (an unref'd timer let a spawn-before-Gateway worker's event loop drain mid-backoff → silent exit 0; e2e-proven)|
|3 Deterministic harness package|Fully implemented: config law matrix incl. unknown-key naming + XOR transports + env overlay; two-step script, cancellable sleeps (cancel ⇒ NO terminal event), report structure + steering section, completed-event payload schema-validated + digest == rehash of file bytes + absolute file:// storageUrl; bin exit codes 0/1/2/3|
|4 Lifecycle contract kit|Fully implemented: runAdapterLifecycleContract in ./testing (in-process listener, no Facilitator); instantiated twice in deterministic/test/lifecycle.test.ts (steering accepted + no-steer unsupported) — kit asserts, proven by the second variant|
|5 E2E + CI + Make|Fully implemented: worker.e2e.test.ts four-phase scenario green (register+ownerTeam rewrite, progress×2+completed w/ manifest validation + on-disk digest match, live steer lands in report file, SIGKILL ⇒ respawn ⇒ exactly one child-lost-execution failed row); Makefile worker-e2e target; ci.yml gateway-e2e job runs the same command after the gateway step|
|6 Example, docs, skills, roadmap|Fully implemented: examples/slim/deterministic-research-worker (gateway.yaml/worker.yaml/capabilities.yaml/README walkthrough incl. sha256sum check); adapter-sdk README (authority law, frame/reply table, minimal worker, kit as gate); docs/advanced.md HarnessAdapter section (mermaid lifecycle, frame table, authority, file:// interim, restart-with-context); adapters+ harnesses READMEs as-built; both skills updated (fixture class `adapter`, 61 cases, 30 schemas); status table + roadmap flipped in this commit|

States: Not started / In progress / Partially implemented / Fully implemented: <notes+divergences> / Blocked: <reason>.

## Critical files & anchors

|File / anchor|Why|
|---|---|
|`src/gateway/src/childwire.ts:39-95` + `src/gateway/src/child.ts:32-51`|The frame/payload vocabulary `adapter.v1.json` must mirror verbatim — import shapes from code, never redefine|
|`src/gateway/src/connect.ts`|`connectChild` as-built surface + "restart is the caller's call" — `createAdapter` wraps exactly this|
|`src/gateway/test/e2e/gateway-harness.ts` + `child-runner.mjs`|Every e2e step builds on these helpers; child-runner is the worker-binary template|
|`protocol/conformance-fixtures/index.json` + `src/protocol/test/fixtures.test.ts` + `protocol/tools/Validate.Fixtures/Program.cs`|Both class-dispatchers that must learn the `adapter` class identically|
|`protocol/json-schema/common.v1.json#/$defs/artifactRef`|Exact required artifact-ref fields the worker's completion event must emit|

## Verification

Repo root, in order; each gates its step:
1. Step 1: `make test` — new `adapter`-class fixtures validate/fail as declared on BOTH harnesses; concrete: `adapter.event.publish.invalid.json` (unknown type) rejected as `schema-violation` by both .NET and TS runs; `make protocol` leaves the tree clean.
2. Steps 2–4: `pnpm -r build && pnpm -r test && pnpm -C src/adapters/harnesses/adapter-sdk typecheck && pnpm -C src/adapters/harnesses/deterministic typecheck && make lint`. Unit tier proves concretely: duplicate `start` executes once (one report, one completed publish); steer against a no-hook adapter replies exactly `{kind:"unsupported"}`; status after `close()`-equivalent ⇒ `unknown` after TTL; SIGTERM-mid-run listener swap ⇒ adapter reconnects (backoff list observed in logs) and probe answers `running`; worker config rejects unknown key `transport.x` naming the key.
3. Step 5: `make worker-e2e` — the four-part scenario above, observable: two progress rows then a completed row whose manifest validates + file digest matches on-disk bytes; `focus on costs` visible inside the report file on disk; SIGKILL → exactly one `child-lost-execution` `assignment.failed`.
4. Full gate: `make build && make test && make lint && make docs-check`; 06's e2e files unchanged+green in the same CI job; CI green = roadmap 07 🟢.
5. Manual loop smoke (example README followed literally on a dev box): commands work verbatim, `sha256sum` comparison line passes — README is the third-party implementability proof.

## Assumptions & contingencies

- 05/06 flip 🟢 on `main` with the surfaces verified at Step 0. If reality diverges, **the code wins**: conform, record in the status table. If `connectChild`'s surface grew (extra options), the wrapper passes through — no redesign.
- If quicktype cannot emit sane types for `AdapterFrameV1`: use the documented skip in `codegen.mjs` (schema ships, generated types skipped) — decided at Step 1, recorded in the status table; do NOT hand-edit generated files.
- If the .NET fixture tool's fail-closed command-table derivation treats `adapter.v1.json` as a message type: add the explicit exclusion (Step 1 inspection item 4 anticipated it) — this is conforming the tool to 01's own "derived from schema filenames" law, not a contract change.
- If `GET /api/link/v1/capabilities` timing in e2e is flaky (06's debounce 1000 ms): assert final-consistency (`waitFor` with generous timeout), 06's precedent.
- If CI `ubuntu-latest` socket paths exceed `sun_path` (~108 bytes): keep `workDir` paths short (`./child.sock` default + short tmp mkdtemp in tests, 06's fallback idiom `/tmp/ofac-<pid>.sock`).
- No new deps beyond the catalog (yaml already cataloged); no Facilitator/Gateway/SDK source changes EXCEPT the two fixture-loader class branches (+ possible `codegen.mjs` exclusion) — anything larger is scope creep, stop and re-read this line. `linkd`, echo/stub builtins, `protocol/openapi`: untouched.
- **Implementation posture:** prefer the boring branch; when a part resists after a couple of iterations, re-read its locked decision and check whether the design is carrying unnecessary weight (e.g. a hook nobody calls gets deleted, not documented). Simple, well-bounded components over speculative seams.
