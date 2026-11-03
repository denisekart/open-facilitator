# @ofac/github-connector

The team-side privileged GitHub arm: a **Gateway fleet child** that
executes GitHub _ops_ — open/read/comment/review PRs, reply to and resolve
review threads, comment on and label issues, push branches, fire commit
statuses — under the Gateway's policy/approval umbrella. The GitHub token
never leaves this process; the kernel sees only op results on the link
plane. The inbound/event half lives in
[`@ofac/github-adapter`](../github/README.md); this package is outbound
mutations with a durable `connector_actions` ledger (from
`@ofac/provider-core`: canonicalize inputs → intent → replay/reconcile →
ONE mutation → settle; an ambiguous write leaves the row `pending` and the
retry reconciles by READ before re-firing).

Runs anywhere a Gateway child runs. It speaks **only to its Gateway** over
the child socket/TCP; the kernel URL exists solely to satisfy the SDK's
artifact-seam shape and no kernel credential is ever taken.

## Ops (capability ids)

| Op                                                             | Gated by                 | Effect                                                                                                                                        |
| -------------------------------------------------------------- | ------------------------ | --------------------------------------------------------------------------------------------------------------------------------------------- |
| `vcs.issue.read` / `vcs.issue.comment` / `vcs.issue.label`     | `GITHUB_ENABLE_ISSUES`   | fetch issue, comment as the bot, attach labels (set-union upsert)                                                                             |
| `vcs.branch.push`                                              | `GITHUB_ENABLE_VCS`      | create a ref via `POST /git/refs` (422 "already exists" reconciles a retry)                                                                   |
| `vcs.change_request.create` / `.read` / `.comment` / `.review` | `GITHUB_ENABLE_MRS`      | open/read/comment/review a PR; create reconciles a lost response by listing open PRs on the head branch — never a duplicate                   |
| `vcs.discussion.reply` / `vcs.discussion.resolve`              | `GITHUB_ENABLE_MRS`      | reply to a review comment via `in_reply_to` (stamped marker reconciles a lost answer); resolve via the GraphQL `resolveReviewThread` mutation |
| `vcs.pipeline.trigger`                                         | `GITHUB_ENABLE_PIPELINE` | POST a commit status (safe upsert)                                                                                                            |

A disabled domain is invisible on the wire: the manifest omits its ops, so
no command row can ever route to it.

## Configuration

YAML file (worker law: unknown keys rejected by name, every error names its
key) with env overlay. YAML keys: `childWorkspaceId`, `team`, `token`,
`transport` (`socket.path` or `tcp.host|port`), `kernelUrl`, `statePath`,
`heartbeatSeconds`, `restartBackoffMs`.

| Env (always wins)                                                                             | Meaning                                                             |
| --------------------------------------------------------------------------------------------- | ------------------------------------------------------------------- |
| `OFAC_CHILD_SECRET`                                                                           | Fleet token — the Gateway's child secret, never a kernel credential |
| `GITHUB_URL`                                                                                  | Required. The GitHub API the ops execute against (fake in e2e)      |
| `GITHUB_TOKEN` \| `GITHUB_TOKEN_FILE`                                                         | Required pair (one must resolve): the bot PAT                       |
| `GITHUB_ENABLE_ISSUES` / `GITHUB_ENABLE_VCS` / `GITHUB_ENABLE_MRS` / `GITHUB_ENABLE_PIPELINE` | Domain flags (`true`/`false`); all-off is a config error            |
| `OFAC_ADAPTER_LOG`                                                                            | Log level (default `info`)                                          |

Kernel-side `Providers__Github__*` config and the webhook-registration
contract are documented in
[`@ofac/github-adapter`](../github/README.md).

## Tests

- `pnpm test` — config law + the op law against the in-process
  **fake GitHub** (`test/fake-github.ts`, exported for reuse): one-PR
  idempotency, the ambiguous-timeout reconcile (`loseNextPrResponse`),
  stamped-reply reconcile, ledger conflicts.
- `pnpm test:e2e` — the full loop (plan §2A): a dedicated Facilitator booted
  WITH `Providers__Github__*` + a standalone Gateway + this connector +
  the adapter + a **scripted deterministic worker**
  (`test/e2e-local/worker-e2e.mjs`, zero LLM) + the fake GitHub. Legs:
  signed `issues` labeled fixture → Work row + routing decision → worker
  `prRequest` → EXACTLY ONE PR with the assignment-marker head branch → the
  fake's real `pull_request` webhook binds the marker to the PR number;
  `pull_request_review` (@bot) → steer → re-dispatch → folded
  "steer incorporated" comment via `vcs.change_request.comment`; seeded
  `vcs.discussion.reply` → `in_reply_to` on the fake + settled ledger row.
  Needs the standard e2e gate env (`FACILITATOR_URL`/`FACILITATOR_PG`/
  `KEYCLOAK_URL`, plus `PG_ADMIN_CMD`/`PG_ADMIN_URL` where psql runs in a
  container).
