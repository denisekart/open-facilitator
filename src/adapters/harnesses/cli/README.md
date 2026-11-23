# @ofac/harness-cli — the playbook-driven harness adapter

Run any CLI tool as a Facilitator harness worker: no code, no container —
one YAML **playbook** declares the capability face and the steps, and the
worker spawns the tool, watches markers, captures outputs, and rides the
full Adapter Protocol (progress, cancel, approvals, the kernel artifact
path, `assignment.completed.v1` with a canonical Result Manifest + digest).

```bash
ofac-cli-worker run --config worker.yaml
# worker ready childId=gw.demo/cli socket=.../child.sock
```

```yaml
# worker.yaml — the worker config (same validation law as every OFAC config:
# unknown keys rejected by name, env overlays OFAC_CHILD_SECRET /
# OFAC_KERNEL_URL / OFAC_ADAPTER_LOG).
apiVersion: facilitator.framework/v1
kind: HarnessAdapter
childWorkspaceId: gw.demo/cli
token: child-secret-demo
kernelUrl: http://localhost:8088
transport: { socket: { path: .ofac-gateway/child.sock } }
playbookFile: research.playbook.yaml
workDir: ./.ofac-worker
```

## The playbook

`sample/research.playbook.yaml` is the worked example (the walkthrough's
fake research tool lives beside it). Each capability declares its manifest
face (`execution`, `outputKinds`, `supportsCancellation`, …) and its steps;
each step runs `command`/`args` (interpolated: `{{objective}}`,
`{{workId}}`, `{{vars.*}}`, `{{steps.<name>.<field>}}`, `{{playbookDir}}`),
optionally under a PTY (`tty: true`), and must satisfy its `expect`
(exit code, `marker` against the de-ANSI-ified stream, `fileExists`).
`capture:` attaches a file or a marker-extracted value as an output
artifact; `ask:` runs the human leg over the ask_user MCP shim
(`mcp/`); `when:` gates a step on config/vars. The advertised capability
manifest is **derived from the playbook** — no separate capabilities file.

The kernel's `retryable` law: a failed `expect` publishes
`assignment.failed.v1` naming the step (`step '<name>'`), retryable when a
transient posture applies; `retry.max` re-runs a step before that.

## Migrating from `@ofac/harness-deterministic`

The old reference worker is gone; its two capabilities ride the sample
playbook (`research.analyze` + `research.publish`, same wire face).

| old `worker.yaml` key  | playbook equivalent                                          |
| ---------------------- | ------------------------------------------------------------ |
| `manifestFile: f.yaml` | gone — the manifest is derived from the playbook             |
| `stepMs: 800`          | the tool's own pacing (the fake tool mirrors 2 × 800 ms)     |
| `askQuestion: "..."`   | `vars.askQuestion` + the `when: askQuestion` step            |
| the scripted two steps | the `steps:` list; captures + progress notes are declarative |

`supportsSteering` is false on fire-per-step capabilities (a spawned CLI
takes a directive at the next step boundary at best): steer-sensitive
lanterns should keep a `steer` recorder step or build hooks themselves.

Want TypeScript instead of YAML? `HarnessHooks` is the same surface the
playbook engine itself uses — see
[`examples/custom-hooks-worker`](../../../examples/custom-hooks-worker/README.md):
you can also build a harness yourself with `HarnessHooks`.
