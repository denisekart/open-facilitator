# @ofac/gitlab-adapter

GitLab communication adapter: one Node process on the
`@ofac/provider-core` adapter face. Inbound: the kernel webhook sink
(`POST /api/ingress/webhooks/gitlab`, the facilitator being the only
web-exposed point) fans verbatim GitLab system-hook deliveries here as
`command.provider.webhook.v1`; this adapter verifies the `X-Gitlab-Token`
shared secret (constant-time — GitLab sends no body HMAC, the token IS the
proof, which is exactly why verification lives in the provider's normalizer)
and normalizes into
`org.facilitator.event.provider.{issue,change_request,pipeline,mention}.v1`.
Outbound: a **link** to the Facilitator kernel (enroll → capabilities →
`communication.reply.v1` command execution → event publish). All state
(comment ledger, idempotency) is local SQLite; the kernel sees only the link.
There is NO webhook HTTP server on this process — only `GET /healthz`.

The on-demand instance is bootstrapped by `tools/gitlab/init-gitlab.sh`
(`make gitlab-up`); the leg set is `reference/checks/verify-gitlab.sh`.

## Commands

```
gitlab-adapter run    # link plane + healthz, park until SIGINT/SIGTERM
```

Exit codes: 0 clean · 1 runtime failure · 2 config error.

## Environment (run)

| Var                                   | Meaning                                                             |
| ------------------------------------- | ------------------------------------------------------------------- |
| `OFAC_KERNEL_URL`                     | Kernel base URL (link plane + ingress)                              |
| `OFAC_ENROLL_SECRET`                  | Enrollment secret for this endpoint                                 |
| `OFAC_ENDPOINT_ID` `OFAC_TEAM`        | Link identity (reference shape: `gitlab-adapter`)                   |
| `GITLAB_URL`                          | GitLab base URL (REST v4; also the derived tenant id: host)         |
| `GITLAB_TOKEN` or `GITLAB_TOKEN_FILE` | Bot PAT, direct or 0400 file (absent ⇒ reply answers `unsupported`) |
| `GITLAB_WEBHOOK_TOKEN`                | Shared secret for inbound deliveries (`X-Gitlab-Token`)             |
| `GITLAB_BOT_USER`                     | Exact-match echo suppression identity                               |
| `OFAC_PROVIDER_INSTANCE_ID`           | Event reference axis (default `local-gitlab`)                       |
| `GITLAB_WORK_LABELS`                  | Comma list; `labeled` ∩ set ⇒ `workTrigger` (default `ofac:work`)   |
| `OFAC_STATE_PATH`                     | SQLite path (default `./ofac-gitlab-adapter.sqlite3`)               |
| `PORT` / `OFAC_HEALTH_PORT`           | Serves `/healthz` only (default 8068)                               |

## Kernel config (`Providers__Gitlab__*`)

Pure config — the kernel boots provider-less; enabling the arm is opt-in per
deployment. Example lines (env-form; `:` = `__` for .NET configuration):

```
Providers__Gitlab__Enabled=true
Providers__Gitlab__EndpointId=gitlab-adapter
Providers__Gitlab__Tenant=<gitlab host, e.g. 127.0.0.1:8929>
Providers__Gitlab__BotUserId=facilitator-bot
Providers__Gitlab__AssignEndpointId=gw.team-alpha
Providers__Gitlab__AssignCapabilityId=research.analyze
Providers__Gitlab__AssignRules__0__Label=ofac:work
Providers__Gitlab__AssignRules__0__CapabilityId=vcs.issue.read
Providers__Gitlab__PrRequestCapability=vcs.change_request.create
```

`AssignRules[n]` routes a labeled issue's interim assign to a specific lane
(label ⇒ capability [+ endpoint/team]); `PrRequestCapability` marks the
capability whose completed manifest may carry `prRequest`/`commentRequests`
(connector dispatch).

## Inbound law (src/normalize.ts)

`X-Gitlab-Token` constant-time verified before parsing; `X-Request-Id`
passes through untouched as `providerDeliveryId` (the kernel dedupe key).
`X-Gitlab-Event` routes the hook kind:

- `Issue Hook` — `open`/`reopen`→`opened`; `update` with a `changes.labels`
  record→`labeled` (`workTrigger` = labels ∩ `GITLAB_WORK_LABELS`);
  `close`→`closed`; other updates drop.
- `Note Hook` — `object_attributes.noteable_type` routes issue vs MR:
  note on Issue → issue `commented` (note id rides `reference.messageId`),
  note on MergeRequest → change_request `commented` (+`commentId`,
  `headRef` from `merge_request.source_branch`); system notes drop; a note
  body containing `@GITLAB_BOT_USER` rides a companion
  `event.provider.mention.v1`.
- `Merge Request Hook` — open/reopen→`opened`, update→`synchronized`,
  approve→`reviewed`, merge→`merged`, close→`closed`; `headRef` from
  `object_attributes.source_branch`. A bot-opened MR passes the echo filter
  (the head-branch resolver binds the connector's pending marker to the
  minted MR number — load-bearing, loop-free).
- `Pipeline Hook` / `Build Hook` — status→action map
  (created/running→pending, success→success, failed→failure,
  canceled→error); the sha→MR resolution is I/O in the dispatch layer and an
  unresolved number drops the delivery (same law as forgejo's commit-status
  arm).

Echo (`user.username === GITLAB_BOT_USER`) drops. Labels normalize from
either string arrays or `[{title|name}]` shapes.

## Outbound law (src/index.ts)

Reply-only capability (`communication.reply.v1`, comment/thread targets).
GitLab notes live on TWO resources (issues, MRs) and the reply envelope
cannot tell them apart — the note resolves provider-side: try the issue, a
404 re-tries the MR. The comment ledger is consulted **before** any provider
mutation, so a replayed command posts nothing; react/update/acknowledge
return `unsupported` receipts, never throw.
