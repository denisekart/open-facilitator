# @ofac/forgejo-adapter

Forgejo communication adapter: one Node process that owns both
edges — inbound: the kernel webhook sink (`POST /api/ingress/webhooks/forgejo`,
the facilitator being the only web-exposed point) fans verbatim deliveries here
as `command.provider.webhook.v1`, and this adapter verifies the HMAC and
normalizes them into `org.facilitator.event.provider.{issue,change_request,pipeline}.v1`
and an outbound **link** to the Facilitator kernel (enroll → capabilities →
`communication.reply.v1` command execution → event publish). All state
(comment ledger, idempotency) is local SQLite; the kernel sees only the link.

## Commands

```
forgejo-adapter run    # link + webhook server, park until SIGINT/SIGTERM
forgejo-adapter init   # idempotent seed: admin login-first (sign-up first
                       # admin when no account exists), org, repo + README,
                       # labeled issue, webhook registration, bot PAT -> token file
```

Exit codes: 0 clean · 1 runtime failure · 2 config error.

## Environment (run)

| Var                                     | Meaning                                                           |
| --------------------------------------- | ----------------------------------------------------------------- |
| `OFAC_KERNEL_URL`                       | Kernel base URL (link hub + ingress)                              |
| `OFAC_ENROLL_SECRET`                    | Enrollment secret for this endpoint                               |
| `OFAC_ENDPOINT_ID` `OFAC_TEAM`          | Link identity                                                     |
| `FORGEJO_URL`                           | Forgejo base URL (also the derived tenant id: host)               |
| `FORGEJO_TOKEN` or `FORGEJO_TOKEN_FILE` | Bot PAT, direct or 0400 file                                      |
| `FORGEJO_WEBHOOK_SECRET`                | HMAC-SHA256 key for inbound deliveries                            |
| `FORGEJO_BOT_USER`                      | Exact-match echo suppression identity                             |
| `OFAC_PROVIDER_INSTANCE_ID`             | Event reference axis (default `local-git`)                        |
| `FORGEJO_WORK_LABELS`                   | Comma list; `labeled` ∩ set ⇒ `workTrigger` (default `ofac:work`) |
| `OFAC_STATE_PATH`                       | SQLite path (default `./ofac-forgejo-adapter.sqlite3`)            |
| `PORT` / `OFAC_HEALTH_PORT`             | Serves `/healthz` only (default 8067)                             |

`init` adds `DEMO_FORGEJO_ADMIN_USER|PASSWORD|EMAIL`, `FORGEJO_INIT_ORG`,
`FORGEJO_INIT_REPO`, `FORGEJO_INIT_ISSUE_TITLE`, `FORGEJO_HOOK_URL`,
`FORGEJO_TOKEN_FILE`.

## Inbound law (src/normalize.ts)

HMAC verified over the **raw bytes** before parsing — Gitea-family headers
send bare hex in `X-Forgejo-Signature`/`X-Gitea-Signature` (no `sha256=`
prefix); the GitHub-style prefixed copy is also accepted. `X-Forgejo-Delivery`
passes through untouched as `providerDeliveryId` (the kernel dedupe key).
Echo (`sender.login === FORGEJO_BOT_USER`) drops; `issue_comment` routes on
the `issue.pull_request` discriminator; `pull_request closed` splits on
`merged`; `status` needs a caller-resolved PR/issue number (sha→PR lookup)
or drops.

## Outbound law (src/handler.ts)

Reply-only capability (`communication.reply.v1`, comment/thread targets).
The comment ledger is consulted **before** any provider mutation, so a
replayed command posts nothing; react/update/acknowledge return `unsupported`
receipts, never throw.
