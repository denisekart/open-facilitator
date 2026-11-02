# Mattermost communication adapter (`@ofac/mattermost-adapter`)

The first communication adapter: a thin bridge between a Mattermost
server (Team Edition 11.11) and the Facilitator kernel. It is **just another
integration on the link plane** — it enrolls as a link endpoint
(`role: child`, secret mode), advertises the `command.communication.*`
capability manifest, and is opt-in by existence: nothing happens until
somebody runs this process.

```
Mattermost ──(bot WebSocket v4: posted / reaction_added / direct_added)──▶ normalize ──▶ link event plane
            org.facilitator.event.provider.{mention,reaction,message}.v1 (data.provider = "mattermost")
kernel ──(link delivery: command.communication.{reply,react,update,acknowledge}.v1)──▶ REST v4 (bot token)
```

## Inbound: bot WebSocket listener (deviation D1)

Mattermost 11.11 webhooks carry **no signing secret** (custom webhooks sign,
but outgoing webhooks — the event-carrying form — only ship a plaintext
token; and the payload lacks `root_id`). So the adapter takes the
contingency as the primary form: it connects to
`GET <MATTERMOST_URL>/api/v4/websocket` **as the bot** and consumes the
event stream its memberships entitle it to. Live 11.11 handshake law: the
server sends no unauthenticated greeting (idle-close ~5 s), so the client
sends `authentication_challenge` immediately on open and the connection is
settled by the post-auth `hello` greeting (lowercase on the wire; docs show
HELLO). Trust comes from the bot credential, there is no inbound port, and
sequence-numbered reconnect recovery (`/users/me/missed_events?seq=`) plus a
startup posts-API replay (cursor in the SQLite state file, 24 h fallback
window, mentions + DMs only) cover the gaps.

Our mapping (chat-domain vocabulary — `src/normalize.ts`, pure and unit-tested;
the event type names the PURPOSE, the provider rides `data.provider`):

- the bot's own posts → `message.v1` **echo** (the kernel records the
  delivery for the binding ledger and suppresses arm effects by botUserId),
- text starting with `OFAC_BOT_MENTION` (or `@<bot username>`) → `mention.v1`,
- DM channels with the bot (channel-type cache, filled on miss via
  `GET /channels/{id}` and on `direct_added`) → `message.v1`,
- posts in a thread whose root the adapter previously replied to
  (post-ledger bound roots) → `message.v1`,
- everything else → ignored. Public channels never flood the kernel.

## Outbound + idempotency

`src/mm-client.ts` speaks REST v4 with the bot token (createPost with
`root_id`, reactions, bot-DM channel creation + cache, `/users/me`, post
edits). Live 11.11 law: `POST /reactions` answers the single Reaction
OBJECT (docs claim an array) and a Reaction carries no `id` — the ledger
identity is the synthesized `(user, post, emoji)` composite, identical to
what `normalize.ts` derives from the inbound `reaction_added` frame, so
outbound echo and inbound event dedupe to one row. **Every** executed
command records its provider object id keyed by
the command's `idempotencyKey` in the SQLite post-ledger BEFORE the receipt
goes out — a delivery replay answers `accepted` without touching Mattermost,
so replays never double-post. `update` only edits a post the ledger
proves the adapter created; `acknowledge` is a no-op accepted. After a
successful reply/update, the bot's own `message.v1` echo rides back through
`CommandContext.publishEvent` so the kernel records the outbound post id into
`communication_bindings`.

## Configuration (env-only)

| Variable                    | Required | Default                     | Consumer / meaning                                                             |
| --------------------------- | -------- | --------------------------- | ------------------------------------------------------------------------------ |
| `OFAC_KERNEL_URL`           | yes      | —                           | link plane: enroll, capabilities, events, SignalR hub                          |
| `OFAC_ENROLL_SECRET`        | yes      | —                           | enrollment secret (dev: `enroll-dev`); re-enroll rotates the link token on 401 |
| `OFAC_ENDPOINT_ID`          | yes      | —                           | logical link endpoint (e.g. `mm-adapter`) the kernel addresses as `link.<id>`  |
| `OFAC_TEAM`                 | yes      | —                           | kernel team roster slug the enrollment binds to                                |
| `MATTERMOST_URL`            | yes      | —                           | provider base URL; its **host** is the event tenant identity                   |
| `MATTERMOST_BOT_TOKEN`      | yes      | —                           | bot token: WS auth + every REST write                                          |
| `OFAC_PROVIDER_INSTANCE_ID` | no       | `local-chat`                | `reference.providerInstanceId` on every event                                  |
| `OFAC_BOT_MENTION`          | no       | `@facilitator`              | mention trigger token                                                          |
| `OFAC_STATE_PATH`           | no       | `./ofac-mm-adapter.sqlite3` | delivery ledger + post ledger + caches + replay cursor                         |
| `PORT` / `OFAC_HEALTH_PORT` | no       | off                         | optional `GET /healthz` → `{ok:true, botId, ws:"up"                            | "down"}` |
| `OFAC_ADAPTER_LOG`          | no       | `info`                      | console log level                                                              |

No webhook secret — D1: the inbound leg is the bot WebSocket, no inbound port.

## Running standalone against any kernel

```sh
pnpm -C src/adapters/communication/mattermost build
OFAC_KERNEL_URL=https://facilitator.example \
OFAC_ENROLL_SECRET=enroll-dev OFAC_ENDPOINT_ID=mm-adapter OFAC_TEAM=team-alpha \
MATTERMOST_URL=http://chat.localhost:8065 MATTERMOST_BOT_TOKEN=<token> \
node dist/bin/adapter.js run
```

Exit codes: 0 clean · 1 runtime · 2 config error. The bot needs channel
membership for whatever conversations it should see; DMs with the bot always
arrive.

## Layout (SDK-extraction seams)

`config.ts` env law · `normalize.ts` pure classification (no I/O) ·
`mm-client.ts` provider REST (`MattermostApi` interface = test seam) ·
`ws-listener.ts` the inbound socket · `handler.ts` the link command
dispatcher + capability manifest · `post-ledger.ts` SQLite state ·
`index.ts` `run(config, logger)` wiring · `bin/adapter.ts` env entry.

Tests (`pnpm test`): fixture-driven classification, envelope validity against
the frozen schemas, the idempotency-ledger replay law with a fake provider
client, channel-type cache, config matrix. No live Mattermost.
