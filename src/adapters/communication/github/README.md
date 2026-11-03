# @ofac/github-adapter

The inbound GitHub provider module: a **link-plane endpoint** on the
[`@ofac/provider-core`](../../provider-core) adapter face. There is
**no webhook HTTP server here** — GitHub (and GitLab, and forgejo) deliveries
terminate at the kernel sink (`POST /api/ingress/webhooks/github`, the only
web-exposed point) and fan out to this endpoint as
`command.provider.webhook.v1` envelopes carrying the verbatim bytes +
headers. The provider-core receiver seam verifies the HMAC
(`X-Hub-Signature-256: sha256=<hex>` over the raw body, provider-local law)
and runs the GitHub→domain normalizer, publishing the central forge events
(`data.provider: "github"`, delivery id from `X-GitHub-Delivery`).

Stateless apart from the SQLite comment ledger: outbound
`command.communication.reply.v1` executes as one bot-identity comment, and
the ledger makes a delivery replay answer `accepted` without a second
comment. GitHub's own echo webhook for the bot comment dies at the
exact-match `GITHUB_BOT_USER` filter.

## Normalization (label policy lives here; the kernel reads `workTrigger`)

| GitHub payload                                                      | Domain event                                                                                      |
| ------------------------------------------------------------------- | ------------------------------------------------------------------------------------------------- |
| `issues` opened / labeled / closed                                  | `event.provider.issue.v1` (`labels[]` from `{name}`, workTrigger = labels ∩ `GITHUB_WORK_LABELS`) |
| `issue_comment` on an issue                                         | `event.provider.issue.v1` action `commented` (`commentId`)                                        |
| `issue_comment` on a PR / `pull_request_review_comment`             | `event.provider.change_request.v1` action `commented`                                             |
| `pull_request` opened/synchronize/reopened/reviewed/closed(+merged) | `event.provider.change_request.v1` (`headRef` = `pull_request.head.ref`)                          |
| `pull_request_review` submitted                                     | `change_request.v1` action `reviewed` + `commentId`                                               |
| `status` / `check_suite` completed                                  | `event.provider.pipeline.v1` (sha→PR resolved via the REST API when the payload lacks a number)   |
| any comment body mentioning `@<GITHUB_BOT_USER>`                    | companion `event.provider.mention.v1` (workTrigger only when unbound)                             |

Bot-authored events drop (echo law); the bot's own `opened` PR/issue events
are exempt (the PR-created loop-closer must pass).

## Environment

| Env                                             | Meaning                                                              |
| ----------------------------------------------- | -------------------------------------------------------------------- |
| `OFAC_KERNEL_URL` / `OFAC_ENROLL_SECRET`        | Link plane (mode-(a) enroll, secret `enroll-dev` in dev)             |
| `OFAC_ENDPOINT_ID`                              | `github-adapter` (must equal `Providers__Github__EndpointId`)        |
| `OFAC_TEAM`                                     | Kernel team roster slug the enrollment binds to                      |
| `GITHUB_URL`                                    | REST base (default `https://api.github.com`; e2e points at the fake) |
| `GITHUB_TOKEN` \| `GITHUB_TOKEN_FILE`           | Bot token; absent file ⇒ inbound-only run                            |
| `GITHUB_WEBHOOK_SECRET`                         | HMAC secret (same value as the webhook registration)                 |
| `GITHUB_BOT_USER`                               | Exact-match echo identity + the @-mention name                       |
| `GITHUB_WORK_LABELS`                            | Comma list (default `ofac:work`) marking issues as work triggers     |
| `OFAC_STATE_PATH` / `PORT` / `OFAC_ADAPTER_LOG` | Ledger path / health port / log level                                |

## Kernel-side config (pure config — no kernel code)

For `reference/config/facilitator-providers.env` (kept out of the shared
example file until a deployment opts in; GitHub ships unit/e2e-only — no
compose service, no verify-full leg):

```bash
# GitHub provider set (adapter: @ofac/github-adapter; connector: @ofac/github-connector)
Providers__Github__Enabled=true
Providers__Github__EndpointId=github-adapter
Providers__Github__Tenant=<host of GITHUB_URL, e.g. api.github.com>
Providers__Github__SiteUrl=https://github.com
Providers__Github__BotUserId=<bot login>
Providers__Github__AssignEndpointId=gw.team-alpha
Providers__Github__AssignCapabilityId=research.analyze
Providers__Github__PrRequestCapability=research.analyze
Providers__Github__AssignRules__0__Label=ofac:work
Providers__Github__AssignRules__0__CapabilityId=research.analyze
Providers__Github__AssignRules__0__EndpointId=gw.team-alpha
```

Webhook registration points at the kernel:
`https://<kernel>/api/ingress/webhooks/github`, content type JSON,
with `X-Hub-Signature-256` enabled.

## Tests

`pnpm test` — normalizer/fixture/config/handler units over docs-shaped
payloads (`test/fixtures/payloads.ts`, verbatim from GitHub's official
webhook docs, signed with the real HMAC scheme). The full-loop e2e (kernel +
Gateway + connector + this adapter + fake GitHub + scripted worker) lives in
[`@ofac/github-connector`](../github-connector/README.md).
