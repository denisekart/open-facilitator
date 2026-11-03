# @ofac/gitlab-connector

The team-side privileged forge arm: a **Gateway fleet child** that
executes GitLab _ops_ — open MRs, comment, label, review, reply to and
resolve discussion threads, push branches, fire commit statuses — under the
Gateway's policy/approval umbrella. The GitLab token never leaves this
process; the kernel sees only op results on the link plane. The inbound/event
half lives in [`@ofac/gitlab-adapter`](../gitlab/README.md); this package is
outbound mutations with a durable `connector_actions` ledger
(read-before-reconcile: every replayed op consults the ledger and, when the
ledger is mid-flight, re-reads the provider to reconcile before
re-executing).

Runs anywhere a Gateway child runs — a supervisor unit beside a workspace, a
sidecar, or a team VM. It speaks **only to its Gateway** over the child
socket/TCP; the kernel URL exists solely to satisfy the SDK's artifact-seam
shape and no kernel credential is ever taken. Project inputs are
`group/proj` paths resolved provider-side via URL-encoded
`path_with_namespace`.

## Ops (capability ids)

| Op                                                             | Gated by                 | Effect                                                                                                               |
| -------------------------------------------------------------- | ------------------------ | -------------------------------------------------------------------------------------------------------------------- |
| `vcs.issue.read` / `vcs.issue.comment` / `vcs.issue.label`     | `GITLAB_ENABLE_ISSUES`   | fetch issue, note as the bot, upsert labels (read-before-reconcile)                                                  |
| `vcs.branch.push`                                              | `GITLAB_ENABLE_VCS`      | create a branch, or move an existing one via `DELETE`+`POST` (GitLab has no update-ref)                              |
| `vcs.change_request.create` / `.read` / `.comment` / `.review` | `GITLAB_ENABLE_MRS`      | open/read/note/review an MR (review = approve or a review with comment); the Gateway policy confirm-holds this class |
| `vcs.discussion.reply` / `vcs.discussion.resolve`              | `GITLAB_ENABLE_MRS`      | reply inside an MR discussion thread / resolve it — GitLab v4 has native discussion threads, so both are advertised  |
| `vcs.pipeline.trigger`                                         | `GITLAB_ENABLE_PIPELINE` | POST a commit status                                                                                                 |

A disabled domain is invisible on the wire: the manifest omits its ops, so
no command row can ever route to it.

## Configuration

YAML file (worker law: unknown keys rejected by name, every error names its
key) with env overlay. YAML keys: `childWorkspaceId`, `team`, `token`,
`transport` (`socket.path` or `tcp.host|port`), `kernelUrl`, `statePath`,
`heartbeatSeconds`, `restartBackoffMs` (`[]` disables auto-restart — the
exit-3 register-denied idiom).

| Env (always wins)                                      | Meaning                                                             |
| ------------------------------------------------------ | ------------------------------------------------------------------- |
| `OFAC_CHILD_SECRET`                                    | Fleet token — the Gateway's child secret, never a kernel credential |
| `GITLAB_URL`                                           | Required. The git host the ops execute against                      |
| `GITLAB_TOKEN` or `GITLAB_TOKEN_FILE`                  | Required. Bot PAT, direct or file's first line (0400)               |
| `GITLAB_ENABLE_ISSUES` / `_VCS` / `_MRS` / `_PIPELINE` | Opt-in flags (`"true"`/`"1"`); unset domains don't advertise        |
| `OFAC_KERNEL_URL`                                      | Artifact-seam shape only (default `http://127.0.0.1:8080`)          |
| `OFAC_ADAPTER_LOG`                                     | Log level (default `info`)                                          |

Exit codes: 0 clean · 1 runtime failure · 2 config error ·
3 register denied (backoff disabled).

## Live legs

`make gitlab-up` boots an on-demand GitLab CE (arm64-capable omnibus image,
minimal `GITLAB_OMNIBUS_CONFIG`) and runs `tools/gitlab/init-gitlab.sh`
(idempotent API bootstrap: bot user + PAT, org/project from
`tools/gitlab/seed/`, work label, sample issue, project webhook pointed at
`{kernel}/api/ingress/webhooks/gitlab`). The leg set is
`./reference/checks/verify-gitlab.sh` (gitlab-boot-check → seed →
issue-work → comment-input → review-steer → discussion → pipeline-note;
every leg SKIPs cleanly when no instance is reachable). `make gitlab-down`
removes the container and its volume.
