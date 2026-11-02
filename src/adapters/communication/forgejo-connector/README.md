# @ofac/forgejo-connector

The team-side privileged forge arm: a **Gateway fleet child** that
executes forge _ops_ — open PRs, comment, push branches, fire commit
statuses — under the Gateway's policy/approval umbrella. The forge token
never leaves this process; the kernel sees only op results on the link
plane. The inbound/event half lives in
[`@ofac/forgejo-adapter`](../forgejo/README.md); this package is outbound
mutations with a durable `connector_actions` ledger (read-before-reconcile:
every replayed op consults the ledger and, when the ledger is mid-flight,
re-reads the provider to reconcile before re-executing).

Runs anywhere a Gateway child runs — a supervisor unit beside a workspace
(the reference env runs it inside `demo-workspace`), a sidecar, or a team
VM. It speaks **only to its Gateway** over the child socket/TCP; the kernel
URL exists solely to satisfy the SDK's artifact-seam shape and no kernel
credential is ever taken.

## Ops (capability ids)

| Op                                                 | Gated by                                       | Effect                                                                                               |
| -------------------------------------------------- | ---------------------------------------------- | ---------------------------------------------------------------------------------------------------- |
| `vcs.issue.read`                                   | `FORGEJO_ENABLE_ISSUES`                        | fetch issue + labels (read-before-reconcile)                                                         |
| `vcs.issue.comment` / `vcs.change_request.comment` | `FORGEJO_ENABLE_ISSUES` / `FORGEJO_ENABLE_VCS` | comment as the bot, ledger-keyed by `idempotencyKey`                                                 |
| `vcs.branch.push`                                  | `FORGEJO_ENABLE_VCS`                           | create/update a branch via raw-file refs                                                             |
| `vcs.change_request.create`                        | `FORGEJO_ENABLE_VCS`                           | open a PR; the Gateway policy confirm-holds this class, so the approval flow supplies actor evidence |
| `vcs.pipeline.trigger`                             | `FORGEJO_ENABLE_CI`                            | POST a commit status                                                                                 |

A disabled domain is invisible on the wire: the manifest omits its ops, so
no command row can ever route to it.

## Configuration

YAML file (worker law: unknown keys rejected by name, every error names its
key) with env overlay; see `reference/config/workspace/forgejo-connector.yaml`
for a worked file. YAML keys: `childWorkspaceId`, `team`, `token`,
`transport` (`socket.path` or `tcp.host|port`), `kernelUrl`, `statePath`,
`heartbeatSeconds`, `restartBackoffMs` (`[]` disables auto-restart — the
exit-3 register-denied idiom).

| Env (always wins)                                                    | Meaning                                                             |
| -------------------------------------------------------------------- | ------------------------------------------------------------------- |
| `OFAC_CHILD_SECRET`                                                  | Fleet token — the Gateway's child secret, never a kernel credential |
| `FORGEJO_URL`                                                        | Required. The forge the ops execute against                         |
| `FORGEJO_TOKEN` or `FORGEJO_TOKEN_FILE`                              | Required. Bot PAT, direct or file's first line (0400)               |
| `FORGEJO_ENABLE_ISSUES` / `FORGEJO_ENABLE_VCS` / `FORGEJO_ENABLE_CI` | Opt-in flags (`"true"`/`"1"`); unset domains don't advertise        |
| `OFAC_KERNEL_URL`                                                    | Artifact-seam shape only (default `http://127.0.0.1:8080`)          |
| `OFAC_ADAPTER_LOG`                                                   | Log level (default `info`)                                          |

Without `FORGEJO_URL` the unit exits 0 (opt-in by existence — the slim
stack never sets it, so the unit is silently absent).

Exit codes: 0 clean (incl. opt-out) · 1 runtime failure · 2 config error ·
3 register denied (backoff disabled).
