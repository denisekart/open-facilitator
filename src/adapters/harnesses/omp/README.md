# @ofac/harness-omp

The Open Facilitator harness adapter for the **omp** (oh-my-pi) CLI: a
worker process that attaches to a team Gateway over the child wire,
registers `coding.omp`, and for every assignment spawns one fully
autonomous `omp --mode rpc` against a cloned workspace — a full TUI
stand-in. Any genuine interaction (an `ask_user` tool call from the agent)
is relayed through the Facilitator to the integration that owns the work,
and the human's answer is routed back into the **live** run.

## Run

```sh
ofac-omp-worker run --config .facilitator/omp-harness.yaml
```

`WORKER_CONFIG` may supply the config path instead of `--config`. The
worker prints `worker ready childId=… socket=…` once registered and parks.
Exit codes: 0 clean, 1 runtime failure, 2 config error,
3 register-denied-then-exhausted (`restartBackoffMs: []`).

## Agentic mode is FORCED

The harness runs headless, so approvals are forced on every spawn:

```
omp --mode rpc --no-ui --auto-approve --cwd <repo> --session-dir <sessions> [--model …] <extraArgs…> [--resume <id>]
```

- `--auto-approve` (agentic mode) is not configurable — a TUI that stops
  to ask for a tool approval in a headless run simply hangs. Override via
  `extraArgs` in the worker config (appended verbatim) if your omp build
  needs different autonomy flags.
- `--no-ui` keeps extensions headless (probe law, omp v18.12.6).
- Steering IS supported (`supportsSteering: true`): a `steer` frame rides
  the live rpc prompt; a directive accepted before the spawn is buffered
  and flushed the moment the session answers.
- Cancellation sends the rpc `abort` frame first and SIGTERMs the process
  on a grace; a cancelled run publishes no terminal event (the accepted
  stop reply already settled the row).

## ask_user: the interactive leg

With `interactivity: mcp` (default) the worker writes a
`.mcp.json` (Claude-compatible — omp auto-loads it from the session cwd)
launching this package's stdio MCP shim, and forces
`--append-system-prompt "You are running headless under the Open
Facilitator. When blocked on a decision only a human can make, call the
ask_user tool and wait for the answer. Do not stop to ask in plain text."`
When the agent calls `ask_user`, the call blocks in the shim; the adapter
publishes `event.assignment.input.required.v1`, the Facilitator routes the
question to the work's owner (chat thread, default endpoint, decision
provider, or the timeline), the human answers, and the tool resolves with
`{answer}` on the **same live run**. No timeout — until you answer, the
run waits. `interactivity: off` omits the whole leg (no config, no prompt
injection; the tool is simply absent).

`askQuestion: "<question>"` asks a fixed question through the same path on
every run (deterministic proof of the loop; no LLM needed).

## Inbound attachments (assign `inputs.sourceRefs`)

When the assign carries `inputs.sourceRefs` (inbound media the kernel stored
as artifacts — e.g. an image pasted into a chat the communication adapter
uploaded), the harness downloads each ref through the link-plane content GET,
stages the bytes at `<execDir>/attachments/<n>` inside the omp workspace, and
appends one prompt line per attachment naming the human-plane content URL,
mediaType and size. The omp CLI has no image-path argument today, so the
staged file + the prompt line is the accepted v1 behavior: a model that wants
the pixels fetches the URL or reads the staged file. Unreadable refs (foreign
team, missing blob) are logged and skipped — media never fails a run.

## Resume law

Each run records the native session id (`get_state` → `data.sessionId`) in
`sessions.json` under `workDir`, keyed `<executionId>:<executionGeneration>`.
A re-dispatched assignment (retry generation) re-spawns with
`--resume <sessionId>` and a follow-up prompt — the native session
continues, no cold restart.

## Worker config (`kind: HarnessAdapter`)

| Key | Meaning |
|---|---|
| `childWorkspaceId`, `transport`, `manifestFile`, `kernelUrl`, `token`, `kernelToken`, `workDir`, `heartbeatSeconds`, `restartBackoffMs` | as every harness adapter (see the deterministic worker) |
| `model` | optional `--model <value>` for every spawn |
| `extraArgs` | verbatim argv tail — your override seam |
| `interactivity` | `mcp` (default) or `off` |
| `promptTemplate` | wraps the objective; must contain `{objective}` |
| `askQuestion` | fixed interactive-leg question (testing/demonstration) |

Env overlay: `OFAC_CHILD_SECRET` beats the file's `token`;
`OFAC_ADAPTER_LOG` sets the log level. Harness env (not config keys):
`OFAC_GIT_CLONE_URL`, `OFAC_GIT_BRANCH`, `OFAC_GIT_TOKEN` (PAT read at
every run; scrubbed from all error text), `OFAC_OMP_BIN` +
`OFAC_OMP_ARGS` (JSON argv prefix — the fake-CLI test seam).

## Completion truth

The final assistant text is uploaded as `final.md` and the raw rpc frame
stream as `transcript.ndjson` to the kernel artifact store; an immutable
Result Manifest rides the completed event as `resultManifestRef` +
`resultDigest`. A nonzero exit before completion is an honest
`assignment.failed` (`omp exit <code>: <stderr tail>`) — the error leg to
humans.

## Functionality list

This harness exports `OMP_CAPABILITIES` (see `@ofac/adapter` README, "The
functionality list"): the full harness command/event list minus the
omissions this runtime genuinely lives with, each with its reason. It
steers and answers `unsupported` (never silence) for anything outside
the list.

## Tests

- `pnpm test` — config law, the fake-omp RPC suite (probe-shaped stream,
  resume, cancel, steer, crash, and the REAL ask_user round-trip through a
  spawned shim + bridge + `.mcp.json`), and the lifecycle conformance
  contract run twice honestly: interaction leg ON with steer accepted, and
  the no-steer/interaction-leg opt-out variant.
- `E2E=1 pnpm test:e2e` — the real binary against a standalone Gateway +
  Facilitator driving the fake CLI through `OFAC_OMP_BIN` (register →
  completed+digest → the full ask_user loop over the wire → steer →
  SIGKILL crash law). Needs `pnpm build` first.
- Live smoke (non-gated): `pnpm build && node scripts/live-smoke.mjs` —
  stub kernel + in-process Gateway + the REAL `omp` CLI, one tiny
  assignment end-to-end; exits nonzero with a readable reason when no CLI
  or no provider is configured. EXERCISED on this env (omp 18.12.6 +
  litellm provider): register → progress → completed in ~7 s with
  `resultManifestRef` + `resultDigest` (`OFAC_SMOKE_MCP=1` also exercises
  the ask_user leg, auto-answered). The fake-CLI suite remains the gate.
