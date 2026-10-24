# Plan 05 — Gateway runtime core: single embedded child (`@ofac/gateway`)

**Status:** 🟡 in progress — first implementation commit landed (plan copy + package wiring); flips 🟢 when CI green on `main`.
**Phase:** B — team-owned execution side. **Depends on:** 04 (as-built: SDK + `linkd` merged, `link-e2e` CI job green).
**Branching:** work and commit **directly on `main`** (standing directive from Plans 03/04). Do not push.
**Size judgment:** kept as ONE plan. Config loader, executor seam, ledger, runtime, diagnostics, and e2e form one vertical slice — a Gateway that cannot run end-to-end proves nothing. Steps are independently verifiable checkpoints. Splitting candidate if execution drags: Steps 5–6 (diagnostics + e2e/CI tier) could become a 05b, but only the implementer may decide that, and only with the unit tier already green.

> Questions may be asked during planning, but not during implementation. Every design decision is locked below; the implementer executes top-to-bottom. Missing detail → infer from `docs/specs/open_facilitator_framework_architecture.md` (the "spec", cited S:line), `docs/specs/open-facilitator-appendix.md` (the "appendix", A:line), `.omp/skills/facilitator-protocol/SKILL.md` + `.omp/skills/facilitator-workspace-packaging/SKILL.md`, the as-built SDK (`src/workspace-link/`), and the frozen contract in `protocol/` — do not stall. If a library API is unclear (`better-sqlite3`, `node:util parseArgs`, vitest, YAML), consult its official docs or search for standard patterns; never guess a flag or API and never ask mid-implementation. When an approach fails after a couple of straightforward fixes, step back and prefer the simpler design that still satisfies the spec — do not layer patches on complexity.

## Context

Plan 04 shipped `@ofac/workspace-link`: the team-owned Link client SDK (state machine, delivery pipeline law, SQLite ledger, enroll, manifest POST) plus `linkd`, the one-worker runtime. This plan ships `@ofac/gateway`, the team-side composition over that SDK: one Link endpoint (`gw.<slug>`), ONE embedded child behind a serializable `ChildExecutor` message interface, an execution ledger holding the §6.1 binding tuple, reconcile-on-restart (§6.3), a deny-by-default policy seam, honest status after restart, and a localhost diagnostics endpoint. Spec anchor S:471: "A Gateway is a reusable composition of responsibilities" — the Gateway composes the SDK, it never forks it. After this plan: a Gateway pointed at a dev Facilitator receives a real `work.assign`, executes it via the injected child, publishes `assignment.completed` with a result manifest, and survives SIGKILL with honest reconcile (no phantom completion, no re-execution). `shared`/`standalone` multi-child modes are Plan 06's, deliberately not stubbed here.

## Step 0 — Mandatory inspection before writing any file

Execute all; record findings in the first commit message body. The facts below were verified during planning (2026-10-24) — re-verify, do not re-decide; if code diverges, **the code wins** and the divergence goes in the Implementation Status table.

1. SDK surface you compose (do not fork): `src/workspace-link/src/index.ts` exports `createLinkClient`, `LinkClient`, `LinkClientOptions`, `LinkTransport`, `LinkTokenProvider`, `LinkHandler`, `CommandContext`, `CommandOutcome`, `EnvelopeAck`, `WorkspaceCapabilityManifest`, `Ledger`/`MemoryLedger`/`SqliteLedger` (via `./node`), `enroll`, `EnrollOptions`, `buildEvent`, `createEchoHandler`/`createStubHandler`, `createConsoleLogger`/`createMemoryLogger`, `FaultInjector`, `LinkLogger`, `LinkError` family. `./node` exports `SqliteLedger`, `SignalrTransport`, `HttpPollTransport`, `postCapabilities(manifest, {baseUrl,getToken,fetchImpl?}) → {digest, body}`, `digestOfManifest`. Read `src/core/client.ts` (options incl. `eventSource`, `maxReconnectAttempts`, `backoffMs`), `src/core/token-rotation.ts` (401 refresh = one `token.refresh()` call, caller's ladder retries), `src/bin/linkd.ts` (`run(config, logger): RunHandle` — the startup-order template you mirror, not call).
2. Server semantics you obey: `src/facilitator/Kernel/Shared/Persistence/LinkEnrollmentStore.cs` (re-enroll rotates the token, old dies immediately; team binds to slug `default`), `Kernel/Link/LinkEndpoints.Enroll.cs` (enroll 201 + `{enrollmentId,trustMode,linkToken,expiresAt}`; POST capabilities 204, 403 `team-mismatch` when `manifest.ownerTeam != stored team slug`; endpointId ≤128 chars, no regex), `Kernel/Link/LinkHub.cs` + `Kernel/Shared/Persistence/LinkService.cs` (hello/supersede: second hello bumps `session_epoch`; goodbye deletes the row ⇒ post-goodbye reconnect restarts epoch at 1), `Kernel/Link/LinkOptions.cs` (`Link:HeartbeatSeconds` 30, `Link:MaxInFlight` 16, `Link:InFlightLeaseSeconds` 120, `Link:EnrollmentToken` dev literal `enroll-dev`), `Kernel/Link/LinkIngress.cs` (uplink events: validated, inbox-deduped, events settle `accepted` with no business side effect — heartbeat events are facts, harmless), `Kernel/Link/LinkSink.cs:22-23` (recipient `link.<endpointId>` — dots allowed, so `gw.foo` routes via `link.gw.foo`).
3. Test infra you copy: `src/workspace-link/test/e2e/harness.ts` (`useFacilitator`, `seedDelivery`, `kernelDeliveries`, `linkRow`, `psql`, `makeManifest`, `manifestFile`, `assignEnvelope`, `waitFor`, `tempFile`, literals `KERNEL_TOKEN="dev-token"`, `ENROLL_SECRET="enroll-dev"`), `vitest.config.ts` vs `vitest.e2e.config.ts`, `.github/workflows/ci.yml:79-113` (`link-e2e` job to clone as `gateway-e2e`), `Makefile:32-37` (`link-e2e` target to clone as `gateway-e2e`).
4. Contract you consume, untouched: `protocol/json-schema/capability-manifest.v1.json` (`additionalProperties:false`; cap items: `id`,`version`,`execution` required + optional `inputSchema`,`supportsSteering`,`supportsCancellation`,`supportsNudge`…), `event.workspace.heartbeat.v1.json` (required `workspaceId`,`status∈idle|busy|draining|error`; optional `activeAssignments≥0`; `additionalProperties:false`), `event.assignment.completed.v1.json` (`assignmentId`,`executionGeneration`,`executionId`,`resultManifest`,`workId`), `event.assignment.failed.v1.json` (`executionId` optional, `reason`, `retryable`), `command.work.assign.v1.json` (`WorkAssignCommandV1Data`: `commandId,workId,assignmentId,targetId,capabilityId,deadline,expectedContextVersion,expectedDeliverables,inputs,actorEvidenceRef`), `command.work.cancel.v1.json`, `command.work.status.request.v1.json`. No schema/fixture changes in this plan.
5. Spec anchors: S:318 (binding tuple order `(gatewayId, childWorkspaceId, assignmentId, executionId, connectionGeneration)`), S:324–330 (§6.3 reconcile law + stale-generation rule), S:418/S:473–480 (embedded vs shared modes; embedded is fully supported), A:44 + A:85–113 (`trustMode` MUST be honest, recorded, immutable per session), S:444–462 (§8.4 `.facilitator/gateway.yaml`, keys spec-silent → locked below), A:142–157 (connector ledger: your execution-ledger schema must leave room for a parallel `connector_actions` table later — separate tables, don't overload).
6. Package baseline: `src/gateway/package.json` (stub: esm+cjs build, no deps), `src/gateway/src/index.ts` (only `GATEWAY_PACKAGE`), `pnpm-workspace.yaml` (gateway already a workspace member; catalog pattern), `tsconfig.base.json` (Bundler resolution, explicit `.js` extensions on relative imports), `eslint.config.mjs`, `Makefile` (`build`/`test`/`lint` run `-r`, so a green `pnpm -r build && pnpm -r test` gates every step).
7. Library-docs lookups if needed: `yaml` npm package (`parse`/`stringify`, ESM), `node:util parseArgs`, `node:http` minimal server, better-sqlite3 txn semantics (already proven in the SDK ledger).

## Locked decisions (no implementer choice)

- **Package layout.** `src/gateway/`: single package `@ofac/gateway`, ESM-only like workspace-link: replace the stub `exports`/build — `exports: { ".": { types: "./dist/index.d.ts", import: "./dist/index.js" } }`, `bin: { "ofac-gateway": "./dist/bin/gateway.js" }`, `build: tsup src/index.ts src/bin/gateway.ts --format esm --dts --target node22 --silent`. Deps: `@ofac/workspace-link: "workspace:*"`, `@ofac/protocol: "workspace:*"`, `better-sqlite3: "catalog:"`, `@types/better-sqlite3: "catalog:"` (devDep pair), `yaml: "catalog:"` (runtime) — add `yaml` to the `pnpm-workspace.yaml` catalog with a dated comment (resolve current latest via `npm view yaml version` at implementation time, e.g. `^2.x`). devDeps mirror workspace-link (`@types/node`, tsup, typescript, vitest — all `catalog:`). Add `test:e2e: vitest run --config vitest.e2e.config.ts`. Keep `GATEWAY_PACKAGE` export (rewrite `test/package.test.ts` to assert the new public surface imports). New configs `vitest.config.ts` (include `test/**/*.test.ts`, exclude `**/*.e2e.test.ts`) + `vitest.e2e.config.ts` (`include: ["test/**/*.e2e.test.ts"]`, `fileParallelism: false`, timeouts 120s/180s) — copy workspace-link's.
- **Directory layout** (`src/gateway/src/`): `index.ts` (public exports), `config.ts` (YAML load + validation), `policy.ts` (`PolicyEvaluator`), `child.ts` (`ChildExecutor` message vocabulary + `EmbeddedChild` wrapper over an SDK `LinkHandler`), `ledger.ts` (`ExecutionLedger`, own SQLite file), `runtime.ts` (`GatewayRuntime`), `diagnostics.ts` (`GET /status` http server), `bin/gateway.ts` (CLI). Tests mirror under `src/gateway/test/{config,policy,child,ledger,runtime,diagnostics}/` + `test/e2e/`.
- **Config: YAML at `.facilitator/gateway.yaml`** (S:8.4 layout), path via `--config <path>` (required) or env `GATEWAY_CONFIG` (S:1698 literal; flag wins over env). Shape (exact keys; unknown top-level key ⇒ config error, exit 2; spec is silent on keys — this is the lock, Plan 06 extends it with a `children:` block):
  ```yaml
  apiVersion: facilitator.framework/v1
  kind: Gateway
  gatewayId: gw.team-alpha        # = endpointId; MUST match ^gw\.[a-z0-9]([a-z0-9-]*[a-z0-9])?$ (≤128 chars server-side)
  team: default                   # ownerTeam; kernel enforces via 403 team-mismatch
  facilitatorUrl: http://localhost:8088
  enrollmentSecret: enroll-dev    # optional; env OFAC_ENROLL_SECRET overrides
  linkToken: <tok>                # optional; mounts a pre-minted token, skip enroll (OFAC_LINK_TOKEN overrides)
  trustMode: shared-runtime       # embedded reality (A:44 honest declaration; server whitelist)
  stateDir: ./.ofac-gateway       # ledger + diagnostics live here; spec prior art /var/lib/gateway
  child:
    name: echo                    # built-in | manifest source (see ChildExecutor lock)
    mode: echo                    # echo | stub — SDK createEchoHandler/createStubHandler
    manifestFile: ./capabilities.yaml  # optional file overriding the child capability manifest
  diagnostics:
    port: 0                       # 0 ⇒ disabled (default ON with a port set); binds 127.0.0.1
  heartbeatSeconds: 30            # business workspace.heartbeat cadence (transport heartbeat is the SDK's, from welcome)
  ```
  Loader: `parseConfig(yamlText)` in `config.ts` → typed `GatewayConfig` + explicit field-by-field validation with `GatewayConfigError` (message names the bad key); NO schema-library for this private config (one hand-rolled validator; a YAML-schema file is Plan 06's when `children:` lands). `linkToken` and `enrollmentSecret` both absent ⇒ error unless a token exists in the state-dir token file (below).
- **State dir layout** (`stateDir`, created `0o700` if missing): `gateway.sqlite3` (execution ledger, `0o600` — mirrors SqliteLedger's chmod), `token` (enrolled link token persisted `0o600` after enrollment, so a restart without secret/token reuses it; rotated by re-enroll). No pidfile (spec-silent, nothing reads it).
- **Identity + enrollment.** The Gateway enrolls ITSELF via SDK `enroll({baseUrl, token: secret, endpointId: gatewayId, instanceId: "gw-" + randomBytes(8).toString("hex"), trustMode})`. `endpointId` = configured `gatewayId` (the `gw.<slug>` namespace is a client-side convention; server validates ≤128 chars only). Token lifecycle: if `linkToken`/`OFAC_LINK_TOKEN` given → static provider (refresh re-enrolls only if a secret is ALSO configured — same rule as linkd); else enroll at startup, write token to `<stateDir>/token`; on 401 `refresh()` re-enrolls with the secret and rewrites the file (server rotation: old token dies immediately, so the file write MUST happen before the retry — mirror linkd's `activeToken` closure pattern). With neither secret nor token available at first boot ⇒ exit 4.
- **Manifest handling.** The Gateway POSTs its OWN aggregated manifest: for the single embedded child, the child's manifest verbatim except `ownerTeam` = the GATEWAY's configured `team` (the kernel stores one manifest per endpoint; `ownerTeam` MUST equal the stored team slug or 403 team-mismatch). Child manifest source: builtin per mode (echo/stub → the child advertises `research.analyze` v1 `execution: asynchronous` `supportsSteering: true supportsCancellation: true` — same capability the e2e `makeManifest` uses, so a seeded `work.assign` for it executes end-to-end), or `child.manifestFile` (YAML `{capabilities: [...]}` capability items only — same items as `capability-manifest.v1.json`, validated via `validateAgainstSchemaId` after the Gateway wraps it with `apiVersion/kind/ownerTeam`). POST via SDK `postCapabilities` (reuse its exact-bytes digest for the hello frame — pass the returned `digest` as `capabilityManifestDigest`; NEVER recompute). Zero server changes.
- **ChildExecutor interface: serializable messages, in-process today.** Plan 07 freezes this vocabulary as the cross-process HarnessAdapter contract and Plan 06 carries it over a socket, so every message is a plain JSON-serializable object — no live handles. Exact vocabulary (`child.ts`):
  ```ts
  export type ChildMessage =
    | { kind: "register"; childWorkspaceId: string; manifest: CapabilityManifestV1 }
    | { kind: "start"; executionId: string; connectionGeneration: number; command: EnvelopeCommand }  // work.assign envelope
    | { kind: "steer"; executionId: string; command: EnvelopeCommand }
    | { kind: "status"; executionId: string }
    | { kind: "stop"; executionId: string; reason: string };   // work.cancel
  export type ChildReply =
    | { kind: "accepted"; executionId: string }
    | { kind: "rejected"; reason: string }
    | { kind: "unsupported"; reason?: string }
    | { kind: "status"; executionId: string; state: "running" | "completed" | "failed" | "lost" }
    | { kind: "unknown"; executionId: string };                // no such execution (reconcile answer)
  export interface ChildExecutor {
    readonly childWorkspaceId: string;
    register(): Promise<ChildMessage & { kind: "register" }>;
    send(msg: ChildMessage): Promise<ChildReply>;
    drainEvents(): EnvelopeEvent[];   // child-published events awaiting upstream forwarding
    close(): Promise<void>;
  }
  ```
  `EmbeddedChild implements ChildExecutor` wraps an SDK `LinkHandler` (`createEchoHandler({source})`/`createStubHandler()` — reuse, exported from the SDK; no third echo). Event forwarding law: child events reach the Gateway through a captured `ctx.publishEvent` on the handler side. Concretely: `EmbeddedChild` builds its OWN handler — `onCommand(cmd, ctx)` = `{ kind: "start", … }` dispatch — constructed as `createEchoHandler({ source: "urn:workspace:" + childWorkspaceId })`, so the child publishes via the injected `CommandContext.publishEvent`; `EmbeddedChild` supplies a `CommandContext` whose `publishEvent` queues into `drainEvents()` when the runtime isn't connected, else calls `LinkClient.publishEvent` and forwards (transport-buffer semantics for offline are the SDK's job, already proven). The Gateway NEVER fabricates an `assignment.completed` for a child run: it forwards the child's event bytes, stamping nothing (the child's `executionId`/`executionGeneration` are its own truth).
- **Binding + ledger.** Own SQLite file `<stateDir>/gateway.sqlite3`, better-sqlite3, `PRAGMA journal_mode=WAL`, `busy_timeout=5000`, `user_version=1`, single DDL (room preserved for a future sibling `connector_actions` table — A:146; do NOT add it now):
  ```sql
  CREATE TABLE bindings (
    delivery_id   TEXT PRIMARY KEY,             -- Link deliveryId the command arrived on
    dedupe_key    TEXT NOT NULL UNIQUE,         -- commandId of the business command
    gateway_id    TEXT NOT NULL,
    child_id      TEXT NOT NULL,
    assignment_id TEXT NOT NULL,
    execution_id  TEXT NOT NULL,                -- gateway-issued: "exec-" + randomBytes(8).hex, assigned at accept time
    work_id       TEXT NOT NULL,                -- from WorkAssignCommandV1Data.workId; reconcile's assignment.failed needs it
    connection_generation INTEGER NOT NULL,     -- see lock below
    state         TEXT NOT NULL CHECK (state IN ('running','completed','failed','lost')),
    error         TEXT,
    started_at    TEXT NOT NULL,
    updated_at    TEXT NOT NULL
  );
  ```
  Delivery-level dedupe (validate→insertIfNew→receipt-before-handler, answers `duplicate` to redelivered commands) stays in the SDK `SqliteLedger` — the Gateway mounts a `SqliteLedger` on a SECOND file `<stateDir>/link.sqlite3` (two concerns, two files; the bindings table is the execution truth). No `link.*` collision: this ledger is Gateway-side, unrelated to kernel `link_deliveries`.
- **`connectionGeneration` mapping (spec-silent → locked).** `connectionGeneration = leaseEpoch` from the SDK welcome, captured at the moment the command's binding row is written (`client.session!.leaseEpoch`). Gateway-side execution counter: `executionGeneration` published in progress/completed events is the CHILD's (`createEchoHandler` emits 1; the embedded child runs exactly one generation per execution — a restart of the Gateway does NOT bump it, it fences via connectionGeneration). Enforcement (S:329): any child reply or forward attempt whose recorded `connection_generation` ≠ current `client.session.leaseEpoch` ⇒ the Gateway logs `stale-generation` and does NOT update the bindings row or ack; the SDK's own supersede/fencing makes this the belt to the SDK's braces.
- **Command routing (`runtime.ts`, the Gateway's `LinkHandler`).** Mounted as `createLinkClient({ handler: gatewayHandler })`. Per command envelope:
  1. Type `org.facilitator.command.work.assign.v1` ⇒ parse `data` as `WorkAssignCommandV1Data`; policy check (below); fresh-dedupe already done by the SDK pipeline ⇒ write bindings row (`state='running'`, fresh `executionId`, current epoch) ⇒ `send({kind:"start", …})` ⇒ child `accepted` ⇒ return `{kind:"accepted"}` to the SDK (receipt `accepted`); child `rejected`/`unknown` ⇒ SDK receipt `rejected` + reason and flip bindings row `failed`; non-assign known commands (`steer`/`cancel`/`nudge`/`status.request`/`context.fetch`) ⇒ route `steer`→`{kind:"steer"}` and `cancel`→`{kind:"stop"}` to the child IF a bindings row exists for `data.assignmentId` (state `running`) AND capability supports it, else forward the command to the child anyway (single-child pass-through — the child answers `unsupported`, receipt follows the reply; the SDK's handler-outcome mapping does the rest).
  2. Unmatched command type ⇒ handled by the SDK (`unsupported`, S:309 fail-closed). No re-implementation.
  3. Event-class deliveries ⇒ SDK auto-ack. No Gateway code.
- **Policy port.** `policy.ts`: `interface PolicyEvaluator { evaluate(action: PolicyAction, ctx: PolicyContext): "allow" | "deny" }`, `PolicyAction = "command.forward" | "event.forward"`, `PolicyContext = { commandType: string; capabilityId?: string; childWorkspaceId: string }`. Default `DefaultPolicy(childCapabilities: ReadonlyMap<string /*id*/, CapabilityDescriptor>)`: `command.forward` allow iff `capabilityId` ∈ map AND (for steer/cancel) the descriptor's `supportsSteering`/`supportsCancellation` is true; `event.forward` allow all (child already owns the facts; plan 10-era OIDC engine replaces the guts, the call-site stays). Deny ⇒ `CommandOutcome {kind:"rejected", reason:"policy-denied: <capabilityId>"}` + NO ledger row (policy rejection is not an execution). Single seam, no call-site churn later.
- **Reconcile-on-restart law (§6.3).** The reconcile pass runs inside `GatewayRuntime.start()` immediately after the FIRST welcome (before printing the ready line). Concurrent fresh dispatch is safe by construction: a redelivered command re-answers `duplicate` from the SDK delivery ledger without touching bindings. For every bindings row `state='running'`: `send({kind:"status", executionId})` ⇒ child `running` ⇒ leave the row; child `completed`/`failed` ⇒ update the row to match the child's answer; child `unknown`/transport-dead ⇒ mark the row `lost`. Rows marked `lost` are reported, never re-executed and never silently deleted: they appear in `GET /status` and in a one-time `assignment.failed` event the GATEWAY publishes — data `{ assignmentId, workId, executionId }` from the row (columns above), `executionGeneration: <row.connectionGeneration>` (schema minimum is 1; the leaseEpoch the execution ran under is the honest generation), `retryable: true`, `reason: "gateway-restart-lost"` (the Gateway is the reporting authority for a child that no longer exists — the child published nothing). Then, and only then, flip runtime state to `ready` (diagnostics reports `reconciled: true`) and print the ready line.
- **Business heartbeat + diagnostics.** Every `heartbeatSeconds` (config, default 30) publish `org.facilitator.event.workspace.heartbeat.v1` via `client.publish(...)` with `source: "urn:workspace:<gatewayId>"`, `subject: gatewayId`, data `{ workspaceId: gatewayId, status, activeAssignments }`: `status` = `busy` if any bindings row `running` else `idle`; `draining` between SIGTERM and exit; `error` if the child closed abnormally. `dataschema` auto via SDK `dataschemaFor`. Diagnostics: `node:http` server on `127.0.0.1:<diagnostics.port>` when port > 0, single route `GET /status` 200 JSON (no auth, dev-tier honesty, mirrors `/api/dev`):
  ```json
  { "gatewayId": "gw-x", "state": "ready|starting|draining|closed", "reconciled": true,
    "session": { "leaseEpoch": 2, "sessionId": "…", "state": "ready" },
    "executions": { "running": 1, "completed": 3, "failed": 0, "lost": 0 },
    "bindings": [ { "assignmentId": "…", "executionId": "…", "state": "running", "connectionGeneration": 2 } ],
    "child": { "childWorkspaceId": "…", "mode": "echo" }, "linkStats": { …client.stats } }
  ```
  Bind failure (port taken) ⇒ fail-fast at startup (exit 1) with a clear message — a Gateway that can't report truth is broken.
- **CLI.** `bin/gateway.ts`: `ofac-gateway run --config <path>` (`node:util parseArgs`, mirror linkd's hand-rolled pattern; `--help` prints usage). First positional MUST be `run`. Exit codes copy linkd's law: 0 clean (SIGINT/SIGTERM ⇒ draining heartbeat + `client.stop("gateway-shutdown")` + ledger close + http close), 1 failed-to-reach-ready/shutdown failure, 2 config error, 3 protocol mismatch (terminal closed state), 4 enrollment rejected. Startup line greppable by e2e: `gateway ready endpointId=<id> epoch=<n> child=<childWorkspaceId>` on stdout (printed AFTER reconcile). Log: SDK `createConsoleLogger`, level env `OFAC_GATEWAY_LOG` (default `info`).
- **Public library surface (`src/index.ts`).** `GATEWAY_PACKAGE`, `parseConfig`/`GatewayConfig`/`GatewayConfigError`, `DefaultPolicy`/`PolicyEvaluator`/`PolicyAction`/`PolicyContext`, `ChildExecutor`/`ChildMessage`/`ChildReply`/`EmbeddedChild`, `ExecutionLedger` (bindings ops: `insert`, `markState`, `byState`, `all`, `close`), `GatewayRuntime`/`createGatewayRuntime(config, deps)` + `GatewayStatus`/`gatewayStatus()`, `startDiagnostics`. `createGatewayRuntime` takes injectable deps `{ ledgerFactory?, transportFactory?, handlerFactory?, logger?, fetchImpl? }` (mirrors the harness's `sdkClient` options style) so unit tests run it with `MemoryLedger`-class fakes + a mock `LinkTransport` (copy the SDK's mock-transport test pattern — no network in the unit tier).
- **Export `useFacilitator`-style harness reuse.** Gateway e2e imports the workspace-link harness directly (`import { … } from "../../../../../src/workspace-link/test/e2e/harness.js"` relative path — a test-only cross-package import; the SDK harness is deliberately generic — no extraction into a shared test-utils package this plan, Plan 06 can justify it when a third consumer exists). It supplies its own `startGateway(args)` helper (spawn `node src/gateway/dist/bin/gateway.js run --config <file>`, readiness `/^gateway ready endpointId=\S+ epoch=(\d+)/`) written locally in `test/e2e/gateway-harness.ts`.
- **No new wire types, no fixture changes, no server changes.** Consumes the frozen contract only.

## Step 1 — Package wiring + deps

`pnpm-workspace.yaml`: add `yaml` catalog entry (dated comment, style of existing entries). `src/gateway/package.json`: esm-only exports/bin/build/deps/scripts per lock; `vitest.config.ts` + `vitest.e2e.config.ts`; `tsconfig.test.json` (include src+test, `noEmit`); rewrite `test/package.test.ts` to import the new surface (before runtime exists, export a placeholder module set compiling — or land Step 1 with an empty `index.ts` re-exporting only `GATEWAY_PACKAGE` and let later steps grow it).
Verify: `pnpm install && pnpm -r build && pnpm -r test && pnpm -C src/gateway typecheck` green; `node -e "import('yaml').then(m=>console.log(typeof m.parse))"` from `src/gateway` prints `function`.

## Step 2 — Config, policy, ledger (pure units, no runtime)

`config.ts`, `policy.ts`, `ledger.ts` per locks. Unit tests (`test/config/`, `test/policy/`, `test/ledger/`):
- config: happy path from a temp YAML file; each validation failure named (bad kind, missing key, `gatewayId` not matching `^gw\.` regex, both-token-and-secret absent, unknown top-level key); env overrides (`OFAC_ENROLL_SECRET`, `OFAC_LINK_TOKEN`).
- policy matrix: assign for advertised async capability ⇒ allow; assign for unknown capability ⇒ deny; steer without `supportsSteering` ⇒ deny; event.forward ⇒ allow.
- ledger on a temp file: insert + read-back of the full binding tuple; state transitions running→completed/failed/lost; reopen-persistence (close, re-open, rows intact); dedupe_key UNIQUE conflict surfaces as a typed conflict (caller treats as already-bound).
Verify: `pnpm -C src/gateway test`.

## Step 3 — ChildExecutor + EmbeddedChild

`child.ts` per lock: `EmbeddedChild` over `createEchoHandler`/`createStubHandler`, message vocabulary serializable (test asserts `JSON.parse(JSON.stringify(msg))` round-trips). Tests: register message carries the child manifest with the child's OWN ownerTeam (aggregation rewrites it, not here); start for an assign envelope ⇒ child events land in `drainEvents()` (`work.progress.v1` then `assignment.completed.v1` for echo mode, `executionGeneration: 1`); second `start` for an executionId already running ⇒ `accepted` idempotent (echo re-publishes — acceptable for echo; document); `status` for unknown executionId ⇒ `{kind:"unknown"}`; `stop` ⇒ running→completed path.
Verify: `pnpm -C src/gateway test`.

## Step 4 — GatewayRuntime + CLI

`runtime.ts` + `bin/gateway.ts` per locks. Composition: `enroll-or-mount token → postCapabilities(childManifest∪team) → new SqliteLedger(link.sqlite3) + new ExecutionLedger(gateway.sqlite3) → transport (SignalrTransport default; `--transport` not configurable yet, http-poll is a Plan 06 knob) → EmbeddedChild → gatewayHandler → createLinkClient({transport, ledger, endpointId: gatewayId, capabilityManifestDigest: digest, token, handler, logger, eventSource: "urn:workspace:" + gatewayId})` → `start()` → first welcome ⇒ reconcile pass + ready line + heartbeat interval + diagnostics. SIGTERM: state=draining, publish one last heartbeat `status: draining`, `client.stop()`, close everything, exit 0.
Unit tests (mock `LinkTransport` + real temp-file ledgers + injected fetch for enroll/capabilities — assert POST order: enroll, capabilities(204), hello with digest == postCapabilities digest):
- assign delivery → policy allow → bindings row `running` with tuple + epoch → child started → receipt accepted (assert captured `ReceiptV1` validates via `validateReceipt`).
- policy deny (assign for `not.advertised`) → receipt `rejected` reason prefix `policy-denied`, NO bindings row.
- reconcile: seed bindings row `running` for executionId X; child (mock) answers `unknown` → after first welcome: row `lost`, one `assignment.failed` event captured on the transport with `retryable: true`, ready line printed after.
- stale generation: bump the mock welcome epoch (reconnect) mid-run, deliver a child-side status update against the old generation → `stale-generation` log, row unchanged.
- SIGTERM path: status flips draining, goodbye frame captured, exit code 0 semantics of `runtime.stop()`.
Verify: unit tier + CLI smoke `node dist/bin/gateway.js --help` → 0; missing `--config` → exit 2; bad YAML → exit 2 naming the key.

## Step 5 — Diagnostics endpoint

`diagnostics.ts` per lock; wire into runtime start/stop. Tests (ephemeral port via `server.listen(0)` + real `fetch`): `/status` JSON matches the locked shape with a seeded ledger + fake client state; 404 on any other path; server closes cleanly on `stop()` (no open-handle leak — vitest clean exit proves it).
Verify: `pnpm -C src/gateway test` green incl. the new tier.

## Step 6 — E2E tier + CI + Makefile

`test/e2e/gateway-harness.ts` imports the workspace-link harness and adds: `gatewayConfig(overrides)` — writes a YAML config to a temp dir, `stateDir` a temp dir, `diagnostics.port` an explicit free port found via a `net.createServer().listen(0)` probe (config `0` means disabled, so never pass 0 here); `startGateway(args)` — spawns `node src/gateway/dist/bin/gateway.js run --config <file>` with `OFAC_GATEWAY_LOG=debug`, readiness matched by `/^gateway ready endpointId=\S+ epoch=(\d+)/` on stdout; `gatewayToken(stateDir)` — reads `<stateDir>/token` (the link token for `GET /api/link/v1/capabilities` assertions). Tests (each file `describe.skipIf(!E2E)`, own `useFacilitator()`, fresh `gatewayId` `gw.e2e-<rand36>` — the rand suffix is lowercase alnum so the id satisfies the locked `^gw\.[a-z0-9]([a-z0-9-]*[a-z0-9])?$`):
1. `assign.e2e.test.ts`: start gateway (echo child) → ready line epoch 1 → `GET /api/link/v1/capabilities` (Bearer from `gatewayToken`) returns the aggregated manifest, `ownerTeam: "default"`, capability `research.analyze` → `seedDelivery` a `work.assign` for it → `kernelDeliveries` shows the outbox row delivered → poll `GET /status` until `executions.completed >= 1` → child's `assignment.completed` reached the kernel: a psql helper added locally (`inboxRowByType(dbUrl, type)`) finds a `facilitator.inbox` row with `type='org.facilitator.event.assignment.completed.v1'` and `state='processed'`.
2. `restart-reconcile.e2e.test.ts` (§26.1 item 7 basis): deterministic recipe — run the gateway with child mode `stub` (accepts, never completes) so an execution sits `running` in the bindings ledger: seed 1 assign, poll `/status` until `executions.running === 1`; SIGKILL the gateway (no goodbye ⇒ the server session row survives); restart with the SAME `stateDir` + SAME `gatewayId`; ready line shows epoch bumped (+1 — no goodbye deleted the row); the reconcile pass queries the fresh stub child, gets `unknown`, marks the row `lost` and publishes ONE `assignment.failed` (`retryable: true`, `executionGeneration` == the lost row's `connectionGeneration`): assert via `/status` (`executions.lost === 1`) + psql `facilitator.inbox` holds exactly one `assignment.failed.v1` row for that assignment; then seed a SECOND assign ⇒ executes normally (receipt accepted at the bumped epoch — proves reconcile never fenced the live session).
3. `dedupe-rotation.e2e.test.ts`: (a) restart the running gateway GRACEFULLY (SIGTERM → goodbye → epoch restarts at 1, as-built law from harness.ts header) and re-seed the SAME envelope (same commandId) → client answers `duplicate` (SDK ledger in `link.sqlite3` survived), no new bindings row; (b) token rotation: re-enroll out-of-band (SDK `enroll()` with the secret, same endpointId) → old token dead mid-session → gateway's next send 401 → refresh re-enrolls → delivery still flows (assert a later seeded assign completes + the state-dir token file content changed).
CI: clone `link-e2e` job → `gateway-e2e` (same services/steps, final step `pnpm -C src/gateway test:e2e`); Makefile: `gateway-e2e` target (same env defaults, `-C src/gateway`); add `gateway-e2e` to `.PHONY` and the `docs`-check of `.github/workflows/README.md` job table (it exists per Plan 04).
Verify: `make gateway-e2e` against a local postgres:5433 (`facilitator-dev` container, `PG_ADMIN_CMD` pattern) — all 3 files green; CI job passes.

## Step 7 — Docs, skills, roadmap bookkeeping

- `src/gateway/README.md`: quickstart (config sample → `ofac-gateway run`), lifecycle diagram (enroll→capabilities→hello→reconcile→serve→drain), diagnostics shape, exit codes, ledger schemas, `linkd` vs Gateway split (one-worker minimal runtime stays Plan 04's; Gateway = team multiplexer, single child today).
- `docs/advanced.md` (NEW file — first `advanced.md` resident): Gateway modes chapter: embedded as-built (this plan), shared/standalone marked Plan 06 scope, trustMode table (A:85–113 condensed), config key reference (the locked YAML).
- `.omp/skills/facilitator-workspace-packaging/SKILL.md`: Gateway implemented reality (config keys, state dir, policy seam, child seam socket-compatibility note for Plan 06). AGENTS.md maintenance rule: same commit.
- This file's Implementation Status table (below) per step, same commit as each step; roadmap `IMPLEMENTATION_ROADMAP_PLAN.md` Plan 05 entry ⬜→🟡 at first commit, →🟢 on CI green.
- First implementation commit: copy this finalized content over `.omp/plans/05-gateway-runtime-core.md` verbatim (replacing the thin original) so the approved plan lives with the repo.

## Implementation Status

|Step|State|
|---|---|
|0 Inspection|Fully implemented: Step 0 re-verified against as-built (findings in first commit body)|
|1 Package wiring + deps|Fully implemented: esm-only exports/bin/build, yaml catalog ^2.9.1, vitest unit+e2e configs, tsconfig.test.json; `pnpm -r build && pnpm -r test` green|
|2 Config, policy, ledger|Fully implemented: config.ts validator + policy.ts seam + ledger.ts bindings table; 33 unit tests green (`pnpm -C src/gateway test`)|
|3 ChildExecutor + EmbeddedChild|Fully implemented: child.ts (register/start/steer/stop/status law, builtin echo/stub handlers, manifest load); unit tests green|
|4 GatewayRuntime + CLI|Fully implemented: runtime.ts (start order, token acquire/refresh, digest law, reconcile, forwarding, fencing) + bin/gateway.ts (exit-code law, ready line); runtime unit tests green|
|5 Diagnostics|Fully implemented: diagnostics.ts `/status` (fatal bind, exact GatewayStatus shape); unit tests green|
|6 E2E + CI + Makefile|Fully implemented: test/e2e (assign, restart-reconcile, dedupe-rotation) + harness.ts + CI `gateway-e2e` job + `make gateway-e2e`; all 3 files green locally (4 tests) — divergence: plan premise "next send 401s" is SignalR-invisible (hub claims frozen at upgrade, upgrade-401 surfaces without statusCode) so the rotation e2e verifies frozen-claims + revoke-reconnect-cannot-self-heal + restart-re-enroll instead; SDK mid-session 401 detection recorded as a Plan 06 gap|
|7 Docs, skills, roadmap|Fully implemented: README.md + AGENTS.md signpost + docs/advanced.md Gateway chapter + skill status flip + roadmap/this-table sync; `make docs-check` + actionlint + yamllint green|

States: Not started / In progress / Partially implemented: <what>/ Fully implemented: <evidence>/ Blocked: <reason>. Update this table in the same commit as each step.

## Critical files & anchors

|File / anchor|Why|
|---|---|
|`src/workspace-link/src/core/client.ts` + `src/bin/linkd.ts`|The composition surface + startup-order template (`run()` order, token closure, exit codes) the runtime mirrors|
|`src/workspace-link/test/e2e/harness.ts`|Every e2e primitive (facilitator spawn, seeding, psql, epoch law header) — import, don't fork|
|`src/facilitator/Kernel/Shared/Persistence/LinkEnrollmentStore.cs`|Rotation law (old token dies immediately) driving the token-file + refresh design|
|`src/facilitator/Kernel/Link/LinkEndpoints.Enroll.cs` + `LinkIngress.cs`|capabilities 403 team-mismatch rule, event admission = facts settle processed (why heartbeat events are safe to spam)|
|`protocol/json-schema/event.assignment.failed.v1.json` + `event.workspace.heartbeat.v1.json`|The two events the Gateway itself publishes — exact required fields, `additionalProperties:false`|

## Verification

Repo root, in order; each gates its step:

1. Steps 1–5: `pnpm install && pnpm -r build && pnpm -r test && make lint` — unit tier proves concrete behaviors listed per step (policy denial leaves no row; reconcile marks `lost` + publishes one retryable `assignment.failed` whose `executionGeneration` equals the row's `connectionGeneration` and whose envelope validates via `validateEnvelope`; digest in hello == digest from `postCapabilities`).
2. Step 6 concrete input → observable output: `make gateway-e2e` — seeded `work.assign` for the test's `gw.e2e-<rand>` endpoint ⇒ `/status` shows `executions.completed: 1` and the kernel `link_deliveries` row reads `delivered` (psql via `linkRow`); stub-mode SIGKILL mid-assign ⇒ after restart `/status` shows the row `lost`, psql `facilitator.inbox` holds exactly one `assignment.failed.v1` row for that assignment, and the stub log proves zero re-execution; graceful-SIGTERM re-seed of the same `commandId` ⇒ receipt `duplicate`, zero new binding rows.
3. Full gate: `make build && make test && make lint && make docs-check` + `actionlint` on the amended `ci.yml`; commit on `main`; CI (dotnet, typescript, protocol, meta, docker-build, link-e2e, gateway-e2e) green = roadmap 05 🟢.

## Assumptions & contingencies

- Plan 04 as verified above (SDK surface, harness exports, CI job shape). If reality diverges at Step 0, **the code wins**: conform, record in the status table, do NOT amend the SDK — EXCEPT one sanctioned SDK touch MAY happen: if a harness export isn't importable cross-package (vitest resolution of TS sources across package dirs), fall back to a thin local copy of the needed helpers in `src/gateway/test/e2e/gateway-harness.ts` and note the duplication for Plan 06's test-utils extraction.
- If `createEchoHandler`'s fixed event set (no `work.accepted.v1`) bothers the reconcile story: it doesn't — `work.accepted` is a kernel-side fact of assign admission, not a client obligation (S:284's "after durably accepts" is satisfied by the receipt); do NOT invent a Gateway-published `work.accepted`.
- The 403 team-mismatch path: dev kernel binds every enrollment to team `default` (verified). If a future seed changes that, `team: default` in e2e configs follows the seeded slug; behavior otherwise unchanged.
- better-sqlite3 reuses Plan 04's proven prebuilt binary setup; no new native deps beyond `yaml` (pure JS).
- If reconcile-vs-live-dispatch races bite (a redelivered command during the reconcile pass): the SDK dedupe layer already answers `duplicate` without touching bindings, so ordering is safe by construction; if a real race appears in tests, gate dispatch until reconcile completes via an internal flag in `EmbeddedChild.send` (document, don't redesign).
- No `docs/advanced.md` exists yet (verified): this plan creates it with ONLY the Gateway chapter; roadmap later plans append their chapters — don't pre-create placeholders.
