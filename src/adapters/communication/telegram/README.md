# Telegram communication adapter (`@ofac/telegram-adapter`)

A thin bridge between one Telegram bot and the Facilitator kernel: **just
another integration on the link plane** — it enrolls as a link endpoint
(`role: child`, secret mode), advertises the `command.communication.*`
capability manifest, and is opt-in by existence: nothing happens until
somebody runs this process. One bot serves many groups and many projects:
per-chat admission comes from the `TELEGRAM_CHATS` allowlist, per-conversation
Work binding from the hybrid root law, and media is first-class in both
directions.

```
Telegram ──(long-poll getUpdates | webhook fan-out: message / edited_message / message_reaction)──▶ media ──▶ normalize ──▶ link event plane
         org.facilitator.event.provider.{message,mention,reaction}.v1 (data.provider = "telegram")
kernel ──(link delivery: command.communication.{reply,react,update,acknowledge}.v1)──▶ Bot API (bot token)
```

## Inbound: allowlist + classification law

The adapter runs **long polling by default** (`TELEGRAM_MODE=poll`; on startup
it calls `deleteWebhook{drop_pending_updates:false}` first — Telegram's
poll/webhook exclusivity otherwise makes `getUpdates` error) or **webhook
mode** (`TELEGRAM_MODE=webhook`: `setWebhook` is registered BEFORE the port
binds, so a crash cannot leave Telegram POSTing to a closed listener; the
listener answers 202 immediately after the local secret-token check).

The whole flood control is the chat allowlist plus three publishable cases
(`src/normalize.ts`, pure and unit-tested — the event type names the PURPOSE,
the provider rides `data.provider`):

- the bot's own messages → `message.v1` **echo** (the kernel records the
  delivery for the binding ledger, arm effects suppressed by bot id),
- text containing `@<bot username>` at a word boundary → `mention.v1`,
- private chats (`dm:<userId>` admitted, or `chat.type=private`) →
  `message.v1`, `conversationKind: "dm"`,
- a message in a chat whose root the adapter participates in (its own reply,
  or the forum-topic root it saw) → `message.v1`, `conversationKind: "thread"`,
- an update in an **unconfigured** chat publishes nothing — a mention there
  only earns a one-time onboarding `sendMessage` (guarded by the `notice:<chatId>`
  kv key) that never becomes an event,
- everything else (service messages, reactions un-reacting or custom-emoji-only,
  `message_reaction_count`, `chat_member`) → ignored.

**Hybrid root law** (`rootIdFor`): a forum `message_thread_id` wins; else a
reply to a bound message (bot- or human-authored — the ledger's bound set is
the authority) roots at that message; else the message opens its own
conversation (`root = message_id`, set explicitly for auditability).

**Field law**: `providerDeliveryId = String(update_id)` — Telegram's update id
is unique per delivered update, so the kernel's
`(provider, provider_delivery_id)` unique IS the at-least-once dedupe (the
poll offset and the webhook `seen:<update_id>` guard keep a redelivery from
re-running the pipeline). `text` = caption-else-text clamped to 8192, and a
media-only post publishes `"(attachment)"` so the frozen schema's
`minLength: 1` holds. `labels` are the chat's configured labels (omitted when
empty, never `[]`); `sourceRefs` ride the uploaded media (omitted when none).

**Inbound media** (`src/media.ts`, runs BEFORE publish so the event carries
refs): the largest photo variant / document / voice / video / audio /
animation / sticker → `getFile` → download → dedupe on `file_unique_id` via
the `files` ledger (an image forwarded twice costs one upload and one ref) →
upload through the **reused** `createArtifactClient` from `@ofac/adapter`
(no `workId` — no Work exists yet; the row lands on the adapter's enrollment
team, which is what keeps the team-scoped link-plane PUT authorized) → an
`ArtifactRef` with `kind: "chat.attachment"` and the GUID-free logical URI
`artifact://provider/<fileUniqueId>/<artifactId>`. Oversized or failed media
is a logged **skip — never a failed event**. A rule-routed Work on a
*different* team therefore has a cross-team artifact row until the
link-plane timeline read path is used; the `artifactId` still rides the
assign, and the team-scoped ledger remains the non-disclosure boundary.

## Outbound + idempotency

`src/tg-client.ts` speaks the Bot API over plain fetch (JSON bodies;
`multipart/form-data` for uploads). **Every** executed command records its
provider object id keyed by the command's `idempotencyKey` in the SQLite
chat-ledger BEFORE the receipt goes out — a delivery replay answers
`accepted` without touching Telegram, so replays never double-post.

- **reply**: target chat = `channelOrResourceId`, else the `dm:<accountId>`
  chat recorded when that human first messaged the bot. The message to act on
  = `parentMessageId ?? subjectRef ?? rootThreadId` (a bare id, defensively
  stripped of a `provider/telegram/` prefix); a resolved id recorded as a
  forum **topic** sends with `message_thread_id`, anything else with
  `reply_parameters` (`allow_sending_without_reply`), else unthreaded.
  Text > 4096 splits on newline boundaries and sends sequentially — **only
  the LAST chunk's** id is recorded (a JSON array when >1).
  `data.artifacts` download first (a failure ⇒ `rejected:
  artifact-unavailable`, never a partial post); each ref sends as `sendPhoto`
  when `image/*` and ≤ 10 MB, else `sendDocument` (≤ 50 MB — beyond that
  `rejected: artifact-too-large` with zero posts, and a provider 413 answers
  the same). The FIRST send carries the text as caption (clamped to 1024),
  the rest carry none.
- **react**: the `ACK_EMOJI` table maps the kernel's Slack-style names to
  Telegram's closed allowlist (`white_check_mark → ✅`, `eye → 👀`,
  `+1/thumbsup → 👍`, `-1 → 👎`, `tada → 🎉`, `fire → 🔥`; unmapped → 👀).
- **update**: only a message the ledger proves the adapter created may be
  edited (`editMessageText`); a foreign id ⇒ `rejected:
  not-adapter-message`.
- **acknowledge**: no-op accepted (the ledger check is the whole law).
- After a successful reply/update the bot's own `message.v1` **echo** rides
  back through `CommandContext.publishEvent` (subject
  `provider/telegram/<messageId>`) so the kernel records the outbound
  provider message id into `communication_bindings` — which is what makes a
  later human reply resolvable by rule 2 of the root law.

**Two recorded deviations from the mattermost reference:**

- **T1** — an unknown DM target is `rejected: no-dm-channel`. Mattermost
  creates the bot-DM channel on demand; Telegram cannot open a bot DM before
  the human has `/start`ed, so an honest rejection beats a
  guaranteed-400 retry loop.
- **T2** — reactions ride the `ACK_EMOJI` table: Telegram's
  `setMessageReaction` allowlist is closed and names no `white_check_mark` /
  `eye`, so the Slack-style names MUST map to literal emoji (unmapped ⇒ 👀).

## Configuration (env-only)

| Variable                       | Required     | Default                     | Consumer / meaning                                                                                   |
| ------------------------------ | ------------ | --------------------------- | ---------------------------------------------------------------------------------------------------- |
| `OFAC_KERNEL_URL`              | yes          | —                           | link plane: enroll, capabilities, events, SignalR hub, artifact PUT/GET                              |
| `OFAC_ENROLL_SECRET`           | yes          | —                           | enrollment secret (dev: `enroll-dev`); re-enroll rotates the link token on 401                       |
| `OFAC_ENDPOINT_ID`             | yes          | —                           | logical link endpoint (e.g. `tg-adapter`) the kernel addresses as `link.<id>`                        |
| `OFAC_TEAM`                    | yes          | —                           | kernel team roster slug the enrollment binds to                                                      |
| `TELEGRAM_BOT_TOKEN`           | yes (\*)     | —                           | bot token (inline wins); (\*) or `TELEGRAM_BOT_TOKEN_FILE` (unreadable/empty fails closed)           |
| `TELEGRAM_CHATS`               | yes          | —                           | allowlist: `chatId[:label[:label]]`, comma-separated. `chatId` = signed integer (supergroups negative), `@channelusername`, or `dm:<userId>`; labels lowercase `[a-z0-9-]+`; duplicate chat w/ differing labels ⇒ config error; no labels = admitted, global routing |
| `TELEGRAM_MODE`                | no           | `poll`                      | `poll` \| `webhook` (mutually exclusive — one instance per bot token)                                |
| `TELEGRAM_WEBHOOK_URL`         | mode=webhook | —                           | public HTTPS URL registered via `setWebhook`                                                          |
| `TELEGRAM_WEBHOOK_SECRET`      | mode=webhook | —                           | `secret_token`, compared constant-time against header `X-Telegram-Bot-Api-Secret-Token`              |
| `TELEGRAM_WEBHOOK_LISTEN_PORT` | no           | `8077`                      | inbound port for `POST /webhook` (webhook mode only)                                                 |
| `OFAC_PROVIDER_INSTANCE_ID`    | no           | `local-telegram`            | `reference.providerInstanceId` on every event                                                        |
| `OFAC_TENANT`                  | no           | `api.telegram.org`          | `reference.tenantOrOrganizationId` — `Providers:Telegram:Tenant` must equal it                       |
| `OFAC_BOT_USER_ID`             | no           | from `getMe`                | adapter-side echo law; a mismatch against `getMe` is logged, not fatal                               |
| `TELEGRAM_STATE_PATH`          | no           | `./ofac-tg-adapter.sqlite3` | link ledger + chat ledger (commands/messages/topics/files/kv) + poll offset                          |
| `TELEGRAM_MEDIA_MAX_BYTES`     | no           | `20971520`                  | inbound media ceiling; larger ⇒ recorded skip, never a crash                                         |
| `TELEGRAM_MEDIA_ENABLED`       | no           | `true`                      | `false` ⇒ media noted and skipped                                                                    |
| `TELEGRAM_API_BASE`            | no           | `https://api.telegram.org`  | fake-server seam (the e2e harness points it at a local Bot API stub)                                 |
| `PORT` / `OFAC_HEALTH_PORT`    | no           | off                         | `GET /healthz` → `{ok:true, botId, mode, chats}`                                                     |
| `OFAC_ADAPTER_LOG`             | no           | `info`                      | console log level                                                                                    |

Exit codes (the linkd idiom): **0** clean · **1** runtime · **2** config
error (a named `AdapterConfigError` on stderr). Kernel side: the
`Providers:Telegram` section (`Enabled`, `EndpointId=tg-adapter`,
`Tenant=api.telegram.org`, `BotUserId` set **explicitly** — the probe cannot
read `getMe`'s nested `result.id` without vendor knowledge — and optionally
`ChatLabelRouting` + `AssignRules` for per-chat capability/team routing).

## Running standalone

```sh
TELEGRAM_BOT_TOKEN=123:abc TELEGRAM_CHATS=-1001234567890:ofac:coding \
OFAC_KERNEL_URL=http://localhost:8080 OFAC_ENROLL_SECRET=enroll-dev \
OFAC_ENDPOINT_ID=tg-adapter OFAC_TEAM=team-alpha \
node dist/bin/adapter.js run
```

Create the bot with @BotFather, add it to the group and **disable privacy
mode** (or add it as admin) for full visibility. Webhook mode additionally
needs `TELEGRAM_MODE=webhook TELEGRAM_WEBHOOK_URL=https://… TELEGRAM_WEBHOOK_SECRET=…`
and the kernel's fan-out leg (`POST /api/ingress/webhooks/telegram`).

## Layout & tests

`config.ts` (env + allowlist law) · `normalize.ts` (pure classification +
root law) · `tg-client.ts` (`TelegramApi` test seam + fetch client) ·
`updates-listener.ts` (poll loop, offset checkpoint, backoff
`[250,500,1000,2000,5000,10000]`, `timeout=30`, `limit=100`) · `media.ts`
(download→artifact pipeline + `fetchArtifactContent`) · `chat-ledger.ts`
(commands/messages/topics/files/kv) · `handler.ts` (manifest + dispatcher) ·
`webhook.ts` (secret-token + update_id dedupe normalizer) · `index.ts`
(`run()` wiring) · `bin/adapter.ts`.

Unit tier (`pnpm test`, no network): the config matrix, the classification
law against `test/fixtures/updates.ts`, the media pipeline against a fake
`TelegramApi` + the stub kernel from `@ofac/adapter/testing`, the handler
laws against a call-recording fake, the webhook/ledger laws.
E2E tier (`pnpm build && pnpm test:e2e`): a fake Bot API server + a fake
kernel (enroll/capabilities/events/artifact GET + a minimal SignalR hub)
prove one inbound photo → exactly one `request_upload` → the stored blob's
sha256 → the mention event's `sourceRefs[0]` GETs the exact PNG bytes back,
with `providerDeliveryId == update_id`.
