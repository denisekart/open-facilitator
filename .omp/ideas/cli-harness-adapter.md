# CLI harness adapter: run any command-line program as a team's harness

**Status:** idea, pre-plan. Anchors are file paths read from the tree
(2026-11-22), not memory: `docs/advanced.md` §HarnessAdapter contract,
`src/adapters/harnesses/{adapter-sdk,deterministic,omp,codex}`, `docs/protocol.md`.

Ship `@ofac/harness-cli` (npm, public, `private: false`, like `@ofac/adapter` and
`@ofac/harness-omp`) plus the `ofac-cli-worker` bin: a `HarnessHooks`
implementation that **rides the SDK** and owns nothing of the wire.
`createAdapter` keeps the child-connection lifecycle, the reply law, the restart
loop and the reconcile-honest registry; the descriptor stays
`invocation: "sdk"`, `resume: "restart-with-context"`. What it adds is a
**declarative playbook**: an assignment names the steps to run, and the adapter
turns each step into spawn/assert/upload/publish. Today a team with a
non-trivial CLI forks `harnesses/deterministic` and writes TypeScript; this makes
the same thing a YAML file.

## Why

Three shapes exist: `deterministic` (a hardcoded two-step script), `omp`
(spawn + NDJSON rpc loop, live steer), `codex` (spawn + `--json` JSONL, no steer
channel). The last two are near-clones — config loader, per-execution workdir +
optional clone, spawn, transcript upload, `buildResultManifest` +
`buildCompletedEvent`, kill-grace cancel — and that scaffolding is identical for
the *next* twenty CLIs, each of which currently costs a published package. A
playbook engine collapses that tail; `omp`/`codex` stay (they own rpc framing and
MCP injection) and everything simpler stops needing a build step.

## Two postures, one engine

**Deterministic** (default): steps run in order, argv is fixed, no model, no
TTY, exit code + `expect` are the whole truth; byte-reproducible against a fixed
fake binary. That is the posture the repo's AI-simulation rule leans on
(`.omp/ideas/internal-harness-usecases.md`: no deterministic test may rely on an
LLM output), and what lets a playbook ride the existing lifecycle gate unchanged.

**Interactive/agentic**: the program talks to a human, or emits a prompt the
playbook answers — via `promptMarker` + `feed` and, where the CLI can host it,
the same `ask_user` MCP shim omp/codex inject, whose bridge calls
`ctx.requestInput` on the live run.

Hard parts, as decisions rather than surprises:

- **TTY.** Default is pipes; a PTY (node-pty, or a `node:pty`-shaped seam) is
  opt-in per step, since behavior depends on it — `ci-tool run --plain` in pipes,
  `build-tui watch` under a PTY.
- **ANSI.** Always de-ansi before matching and before publishing a progress note;
  the raw stream ships as an artifact, never an event payload (the frame budget is
  262 144 bytes, and timeline text is read by humans).
- **TUI.** Redraws make `expect` flaky. Law: match a de-ansified *tail window*,
  and let a TUI step assert on a captured artifact, not scrollback.
- **Process groups.** Cancel law copied from `codex/src/harness.ts`: SIGTERM the
  group, 5 s grace, SIGKILL, an aborted run publishes nothing (the accepted
  `stop` reply already settled the row).

## Playbook grammar (v1 draft)

A playbook lists named capabilities, each mapping to a step list; `capabilityId`
from the assign envelope selects one, `inputs` fills its variables.

|Key|Meaning|Side effect on the wire|
|---|---|---|
|`run`|`{command, args[], cwd, env, shell?}` — argv array, no implicit shell|—|
|`expect`|`exit`, `stdout`/`stderr` regex, `file` exists/absent, `durationMs`|miss ⇒ `assignment.failed.v1` (`retryable: true`), failed step in `reason`|
|`capture`|`{as, from: stdout\|file\|stderr, mediaType?}` → staged bytes|uploaded via `artifact.request_upload.v1` + `confirm_upload.v1`; declared in the Result Manifest, referenced `artifact://` by `assignment.completed.v1` (`resultManifestRef` + `resultDigest`)|
|`feed`|bytes written to the child's stdin|—|
|`promptMarker`|regex on de-ansified output that, once matched, writes `answer` to stdin|— (the ask below is what needs a human)|
|`ask`|`{question, choices?, contextRef?}` — the human, mid-run|`assignment.input.required.v1`; settled by the `respond` frame carrying `assignment.input.v1`|
|`timeoutMs`|per-step kill|timeout is a failed step, same as an `expect` miss|
|`retry`|`{attempts, backoffMs, retryOn: [exitCode…]}`|one `work.progress.v1` note per retry|
|`onFail`|`abort` (default) \| `continue`|`continue` still fails the run at the end, with the first failure's reason|
|`progress`|note template published at this step|`work.progress.v1` (`ctx.publishProgress`, generation stamped)|

Progress notes are the **only** mid-run channel a playbook has — `HARNESS_EVENTS`
is closed, so a new class is a protocol change, not an adapter feature. The honest
gaps, each needing a wire proposal rather than a workaround:

- no `step`/`checkpoint` event class ⇒ step boundaries are progress notes, not
  machine-readable state;
- `work.status.request.v1` (no kernel producer) and `work.nudge.v1` (no `nudge`
  frame on the child wire — the Gateway answers on the child's behalf) stay
  declared omissions with those reasons, via `fullHarnessCapabilities([...])`;
- steer: no in-run channel for a CLI mid-step, so v1 omits `work.steer.v1`
  ("fire-per-step: no mid-run channel"), codex-style, late steers never
  resurrecting a settled run;
- an interactive step may **not** raise `approval.required.v1` — kernel-issued
  (the approval record owns routing and expiry), so a playbook `gate` is either an
  `ask` or out of scope.

## Configuration law (three layers, secrets team-side)

`assignment payload > playbook > operator config`.

- **Operator config** (`worker.yaml`, validated the way
  `harnesses/deterministic/src/config.ts` does — unknown keys rejected by name,
  every error names its key): transport, `childWorkspaceId`, `kernelUrl`,
  `workDir`, kill grace, default timeouts, the playbook file, the **allow-listed
  binaries** per capability; env overlays mirror the existing law.
- **Playbook**: step lists, default args, allowed `env` key names.
- **Assignment payload**: `inputs.vars` substitute into declared args;
  `inputs.sourceRefs` are kernel artifacts downloaded over the link plane before a
  step's `cwd` (the inbound-media law already proven in
  `deterministic/src/harness.ts`). The payload may **never** name a binary or add a
  flag — else a Work author remote-drives a team's container; anything a caller
  sets must be a declared `vars` slot.
- Secrets: config names them, the container's env or mount supplies the value, an
  assignment never injects one — an injected secret becomes a timeline string.
  AGENTS.md restated: an adapter holds connections, not execution authority.

## Public-package story

- Docs outline mirroring `adapter-sdk/README.md`: a 15-minute quickstart on a fake
  binary (no AI, no network), the grammar table, capability-manifest authoring,
  the honest-omission law, cancel/timeout/retry semantics, the PTY/ANSI decision,
  migration. Stability: `apiVersion: facilitator.framework/v1` + `kind: Playbook`,
  unknown keys rejected so a new key is a visible change; v1 freezes the table above.
- Conformance reuses what exists, no parallel harness: `runAdapterLifecycleContract`
  from `@ofac/adapter/testing` in `test/lifecycle.test.ts` (twice — steering and
  no-steer variants, per the deterministic package's precedent), the
  `protocol/conformance-fixtures/` class `adapter` fixtures for the terminal event,
  a no-Docker walkthrough under `examples/slim/`, plus a workspace and
  `Dockerfile.node` deploy entry and an `src/adapters/AGENTS.md` mention.

## Replacement path for `@ofac/harness-deterministic`

1. Land the engine; the deterministic two-step script becomes a ~20-line playbook:
   two `run`s of a shipped fake, `progress` on each, one `capture` + manifest, an
   optional `ask`.
2. Prove equivalence where that package is load-bearing — it is both the worked
   example and the no-Docker walkthrough: the e2e seeding `research.analyze` /
   `research.publish`, its approval-loop and inbound-media legs, and
   `examples/slim/deterministic-research-worker/`.
3. Only then demote it: it is `private: true` on the npm.github registry today, a
   weaker coordinate than the public ones, so it becomes the engine's own
   conformance fixture and the walkthrough points at the playbook. Keep the
   TypeScript hooks example too — it is the reference shape of `HarnessHooks`,
   which a playbook cannot teach. If equivalence can't be proven, the package
   stays as-is and this adapter is purely additive.

## Open questions

1. Separate package or a `@ofac/adapter` export (`@ofac/adapter/playbook`)? The
   public-surface cost argues for separate.
2. Does `promptMarker` need multi-marker sequencing? The motivating npm-install example asks three questions in a row.
3. node-pty buys TUI support and costs a native build in every worker image —
   worth it, or a pipe-only v1?
4. Should the Result Manifest `structuredResult` carry a step table while no step
   event exists? Per-step parallelism: deferred — fan-out is routing's job.
5. How far the agentic posture goes before it is just omp: likely "prompt marker +
   ask_user"; a program needing a real agent loop uses `@ofac/harness-omp`.
