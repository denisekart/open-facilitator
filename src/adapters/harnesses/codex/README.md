# @ofac/harness-codex

The Open Facilitator harness adapter for the OpenAI **Codex CLI**: a worker
process that attaches to a team Gateway over the child wire, registers
`coding.codex`, and for every assignment spawns one fully autonomous
`codex exec` against a cloned workspace — a full TUI stand-in. Any genuine
interaction (an `ask_user` tool call from the agent) is relayed through the
Facilitator to the integration that owns the work, and the human's answer is
routed back into the **live** run.

## Run

```sh
ofac-codex-worker run --config .facilitator/codex-harness.yaml
```

`WORKER_CONFIG` may supply the config path instead of `--config`. The worker
prints `worker ready childId=… socket=…` once registered and parks.
Exit codes: 0 clean, 1 runtime failure, 2 config error,
3 register-denied-then-exhausted (`restartBackoffMs: []`).

## Agentic mode is FORCED

The harness runs headless, so approvals are forced off:

```
codex exec --json -s workspace-write -c approval_policy="never" -C <repo> <extraArgs…> <prompt>
```

- `--ask-for-approval` **does not exist** in `codex exec` (verified against
  0.166.0): exec is non-interactive; `-c approval_policy="never"` is the
  supported belt-and-braces spelling. Do not add the interactive flag.
- Override anything via `extraArgs` in the worker config (appended verbatim
  before the prompt), e.g. a different sandbox:
  `extraArgs: ["-c", 'sandbox_mode="read-only"']`.
- **No mid-run steering.** Codex exec is fire-per-turn: `supportsSteering`
  is `false` and the adapter answers a steer honestly with `unsupported` —
  a steer is never silently rewritten, and one landing after the run
  settled never resurrects it. Put course corrections in the next
  assignment instead.
- Cancellation SIGTERMs the whole process group (SIGKILL after a grace); a
  cancelled run publishes no terminal event (the accepted stop reply
  already settled the row).

## ask_user: the interactive leg

With `interactivity: mcp` (default) the worker seeds
`$CODEX_HOME/config.toml` (a worker-managed dir under `workDir`) with an
`[mcp_servers.ofac]` entry launching this package's stdio MCP shim. When the
agent calls the `ask_user` tool, the call blocks in the shim; the adapter
publishes `event.assignment.input.required.v1`, the Facilitator routes the
question to the work's owner (chat thread, default endpoint, decision
provider, or the timeline), the human answers, and the tool resolves with
`{answer}` on the **same live run**. No timeout — until you answer, the run
waits. `interactivity: off` omits the whole leg (the tool is simply absent;
the agent has been told via the prompt not to stop and ask in plain text).

`askQuestion: "<question>"` asks a fixed question through the same path on
every run (deterministic proof of the loop; no LLM needed).

## Resume law

Each run records the codex thread id (`thread.started` → `sessions.json`
under `workDir`). A re-dispatched assignment (retry generation) re-spawns
`codex exec resume <thread_id> …` — the native session continues, no cold
restart.

## Worker config (`kind: HarnessAdapter`)

| Key | Meaning |
|---|---|
| `childWorkspaceId`, `transport`, `manifestFile`, `kernelUrl`, `token`, `kernelToken`, `workDir`, `heartbeatSeconds`, `restartBackoffMs` | as every harness adapter (see the deterministic worker) |
| `model` | optional `--model <value>` for every exec |
| `extraArgs` | verbatim argv tail before the prompt — your override seam |
| `interactivity` | `mcp` (default) or `off` |
| `promptTemplate` | wraps the objective; must contain `{objective}` |
| `askQuestion` | fixed interactive-leg question (testing/demonstration) |

Env overlay: `OFAC_CHILD_SECRET` beats the file's `token`;
`OFAC_ADAPTER_LOG` sets the log level. Harness env (not config keys):
`OFAC_GIT_CLONE_URL`, `OFAC_GIT_BRANCH`, `OFAC_GIT_TOKEN` (PAT read at every
run; scrubbed from all error text), `OFAC_CODEX_BIN` (CLI binary — the
fake-CLI test seam).

## Completion truth

The final answer (last `agent_message` item, else the `-o` file) is uploaded
as `final.md` and the raw stdout JSONL as `transcript.jsonl` to the kernel
artifact store; an immutable Result Manifest rides the completed event as
`resultManifestRef` + `resultDigest`. A nonzero exit before completion is an
honest `assignment.failed` (`codex exit <code>: <stderr tail>`) — the error
leg to humans.

## Tests

- `pnpm test` — config law, the fake-CLI harness suite (probe-shaped
  stream, resume, cancel, MCP round-trip through a real spawned shim), and
  the lifecycle conformance contract run twice honestly: interaction leg ON
  and steerReply `unsupported`, plus the interaction-leg opt-out.
- `E2E=1 pnpm test:e2e` — the real binary against a standalone Gateway +
  Facilitator driving the fake CLI through `OFAC_CODEX_BIN` (register →
  completed+digest → SIGKILL crash law). Needs `pnpm build` first.
- Live smoke (non-gated): `pnpm build && node scripts/live-smoke.mjs` —
- real `codex` on PATH + a resolvable `OPENAI_API_KEY`/`CODEX_API_KEY` run
  one assignment end-to-end (stub kernel + in-process Gateway listener);
  otherwise it prints the honest-skip line and exits 0. The fake-CLI suite
  is the gate — the live leg was not exercised here (no codex on PATH).
