# @ofac/adapter — write your own HarnessAdapter

The HarnessAdapter contract as a TypeScript library: you implement
`HarnessHooks` around your harness (CLI subprocess, SDK, in-process engine);
this package speaks the frozen child wire (`protocol/json-schema/adapter.v1.json`,
the profile of the shipped 06 child transport) to a Gateway. Invocation is
`sdk` only — the adapter is a library inside your worker process, never a
spawned CLI.

Worked example to copy: `../deterministic` (config loader, CLI, `sample/`).

## Authority law

The **harness** is authoritative over execution state; the **adapter** is
thin translation; the **Gateway** mediates. Consequences baked into this
package:

- `assignment.completed.v1` is YOUR publish, never the SDK's. `onExecute`
  resolving `{kind:"completed"}` means "I already published it".
- The SDK publishes exactly one terminal event: `assignment.failed.v1`
  (`retryable: true`) when `onExecute` returns `{kind:"failed"}` or throws.
- A steer against a harness with no `onSteer` hook replies `unsupported` —
  report, never reinterpret.
- An unknown `executionId` replies `unknown` — the Gateway's reconcile probe
  then decides `running` vs `lost`; never lie from amnesia.

## Lifecycle: who replies what, when

|Frame in|Adapter answer|
|---|---|
|`command{start}`|dedupe by `command.data.commandId` (in-memory registry, keyed `executionId`): new ⇒ spawn `onExecute` detached + reply `accepted` immediately (async law); duplicate ⇒ `accepted` WITHOUT re-executing|
|`command{steer}`|running + hook ⇒ invoke, `accepted`; running, no hook ⇒ `unsupported`; unknown ⇒ `unknown`|
|`command{stop}`|abort `ctx.signal` + `onCancel`, reply `accepted`; the run settles itself|
|`command{status}`|registry truth: `status{running/terminal}`; gone (never-seen or past `TERMINAL_TTL_MS` = 60 s) ⇒ `unknown`|

Events reach the wire only through `ctx.publishEvent` / `ctx.publishProgress`,
and the relay law is absolute: the `event` frame carries the FULL CloudEvent
envelope, bytes forwarded unmodified by the Gateway. `publishProgress` is
sugar for `work.progress.v1` (generation stamped, `subject` = assignmentId);
your terminal/manifest events you build yourself (`buildEvent` from
`@ofac/workspace-link`, digests per `common.v1.json#/$defs/artifactRef`).

## Minimal worker

```ts
import { createAdapter } from "@ofac/adapter";
import { createConsoleLogger } from "@ofac/workspace-link";

const adapter = createAdapter({
  childWorkspaceId: "gw.demo/my-harness", // Gateway-side law, validated here too
  transport: { socketPath: "./child.sock" }, // or { host: "127.0.0.1", port: 8090 }
  kernel: { baseUrl: "http://localhost:8088" }, // required: the artifact byte path (token defaults to the child token)
  manifest, // CapabilityManifestV1 — what you advertise; ownerTeam is discarded Gateway-side
  descriptor: { invocation: "sdk", supports: { steer: true, interrupt: true, resume: "restart-with-context" } },
  hooks: {
    async onExecute(ctx) {
      ctx.publishProgress("accepted");
      const run = spawnMyHarness(ctx.command, ctx.signal); // cooperative: ctx.signal fires on `stop`
      ctx.setSteered((d) => run.sendDirective(d)); // omit ⇒ steers answer `unsupported`
      const ok = await run.done;
      if (!ok) return { kind: "failed", reason: "harness exited nonzero" }; // SDK publishes failed.v1
      ctx.publishEvent(buildCompletedEnvelope(ctx)); // YOU publish completed.v1
      return { kind: "completed" };
    },
    onCancel: (reason) => myCleanup(reason), // optional; the SDK aborts ctx.signal for you
  },
  logger: createConsoleLogger(),
});
await adapter.ready(); // first accepted register-ack; rejects `register denied: <reason>`
```

**Restart ownership is yours.** `connectChild` is one-shot; `createAdapter`
adds the loop: drop ⇒ reconnect per `restartBackoffMs` (default
`[100,250,500,1000,2000,5000]`, cap-loop forever; `[]` ⇒ first drop rejects
and you exit — a supervisor restarts you). Spawn-before-Gateway works: the
loop parks until the socket appears. A re-registered adapter answers the
Gateway's reconcile probe from its registry — running executions answer
`running`, dead ones answer `unknown` ⇒ the Gateway reports the assignment
lost, exactly once. In-flight runs survive a socket swap (the run holds the
new connection's publisher). `SessionStore` is the seam for a harness that
can resume a native run id; v1's sanctioned path is `restart-with-context`.

## Conformance gate

`@ofac/adapter/testing` exports `runAdapterLifecycleContract({ startAdapter,
socketPath, gatewayId, steerReply?, afterSteer?, afterCancel? })` — a
vitest-global-safe suite that boots an in-process Gateway listener (no
Facilitator) and drives the full law against YOUR adapter: hello carries the
manifest; start→progress→completed relay with the manifest validated against
`result-manifest.v1.json`; steer reaches the hook (or answers your declared
`steerReply`); stop aborts; kill-the-listener mid-run ⇒ auto-reconnect +
probe answers `running`. Call it in your `test/lifecycle.test.ts` — the
deterministic harness instantiates it twice (steering + no-steer variants)
to prove the kit asserts, not just runs.

## The functionality list

`src/capabilities.ts` is the single authoritative list of what a harness may
be asked to do and what it may answer with — the harness-plane twin of the
comms plane's command family:

- `HARNESS_COMMANDS` — what may ARRIVE (assign, steer, cancel,
  assignment.input, nudge, status.request).
- `HARNESS_EVENTS` — what a harness may PUBLISH (progress, input.required,
  completed, failed, artifact.available, memory candidate/used).
- `HARNESS_OUTBOUND_COMMANDS` — what a harness SENDS (artifact
  request/confirm_upload via the artifact client, memory retain/recall +
  context.fetch via the memory client).
- `HARNESS_FRAMES` — the adapter.v1 lifecycle frames (mechanics, not
  capability).

`fullHarnessCapabilities(omissions)` derives a `HarnessCapabilities` from
the full list; an omission MUST carry its reason, and
`unsupportedFor(cmd, caps)` is the answer owed for anything outside the
list — never silence, never a timeout. `omp`, `codex` and `deterministic`
each export their honest declaration (`OMP_CAPABILITIES`,
`CODEX_CAPABILITIES`, `DETERMINISTIC_CAPABILITIES`); the as-built gap
(nudge has no child-wire frame — the Gateway answers on the child's
behalf) is recorded there, not hidden.

## Package layout

|File|Role|
|---|---|
|`src/types.ts`|the public surface above (documentation-grade; wire shapes are imported, never redefined)|
|`src/capabilities.ts`|the harness-facing functionality list + the honest-omission law|
|`src/registry.ts`|execution registry: dedupe, `status` truth, `TERMINAL_TTL_MS`|
|`src/adapter.ts`|`createAdapter` = `connectChild` + the lifecycle law + restart loop|
|`src/testing.ts`|the lifecycle conformance kit|
