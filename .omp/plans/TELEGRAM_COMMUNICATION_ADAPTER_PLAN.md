# Telegram communication adapter (inbound + outbound media, multi-group / multi-project)

## Context

Add a Telegram bot as a communication provider, built the way every other provider here is built: a thin
link-plane child adapter under `src/adapters/communication/telegram/` on the `@ofac/provider-core` adapter
face, plus one `Providers:Telegram` config section — zero vendor vocabulary in the kernel. The bot serves
many groups with many people about many projects from one endpoint, so per-chat routing and per-conversation
Work binding must work; and media must be first-class: images/files pasted into a group become verified
kernel artifacts that reach the harness, and the bot can post an artifact back into the conversation.
End state: `@ofac/telegram-adapter` runs long-polling (default) or webhook, classifies Telegram updates into
`event.provider.{message,mention,reaction}.v1` with `data.provider="telegram"`, uploads inbound media to the
kernel artifact store and names the refs on the event, the kernel routes a labeled chat to the right
capability/team and rides the media into `work.assign` inputs, and outbound `command.communication.reply.v1`
can carry artifacts back as `sendPhoto`/`sendDocument`.

Decisions already taken (treat as requirements, not options):
- Transport: **both** — long polling is the default mode, webhook mode optional and env-selected.
- Conversation identity: **hybrid root** — forum-topic id, else the bot-anchored reply-chain root, else the
  mentioning message id.
- Routing: **labels** — adapter derives label names per chat, kernel `AssignRules` select
  capability/endpoint/team. Needs one additive protocol widening + one kernel arm change.
- Inbound media: **artifact** — upload to the kernel store, refs ride the event as
  `sourceRefs` + a `provider.input` timeline note.
- Outbound media: **reply extension** — additive optional `artifacts` on
  `command.communication.reply.v1`; adapter downloads and posts it.
- Admission: **chat-id allowlist** — an unconfigured chat is inert except for a one-time onboarding reply.
- Tests: deterministic fake Telegram server + an env-gated live smoke script.

## Verified ground truth (re-read the named regions before editing)

- Adapter face to reuse: `src/adapters/provider-core/src/adapter-face.ts` —
  `runProviderAdapter(opts: ProviderAdapterOptions, envelopeSource: string): Promise<ProviderAdapterHandle>`;
  options `{kernelUrl, enrollSecret, endpointId, team, statePath, manifest, normalizeWebhook, handler?,
  logger, fetchImpl?, decorate?}`; handle `{client, ready, publish(event), shutdown()}`. Pass
  `normalizeWebhook: undefined` in long-poll mode (that is the mattermost precedent) and a real
  `WebhookNormalizer` in webhook mode. `exactOptionalPropertyTypes` is on: optional fields are
  conditionally spread, never set to `undefined`.
- Reference adapter to mirror file-for-file: `src/adapters/communication/mattermost/`
  (`config.ts`, `normalize.ts`, `<provider>-client.ts`, `post-ledger.ts`, `ws-listener.ts`, `handler.ts`,
  `index.ts`, `bin/adapter.ts`, `test/fixtures/*`). Manifest builder `communicationManifest(team)` in
  `mattermost/src/handler.ts:60` — four capabilities, each `execution: "synchronous"`,
  `inputSchema: dataschemaFor(id)`. Command type literals `CMD_REPLY/CMD_REACT/CMD_UPDATE/CMD_ACK`.
  Outbound target resolution idiom: `targetPostId(target) = target.parentMessageId ?? target.subjectRef ??
  target.rootThreadId`.
- Kernel admission (`src/facilitator/Kernel/Link/AdmissionGate.cs:113-146`): provider-class event ⇒
  `data.provider` must name an enabled `Providers:<token>` section, type must be in the six central types,
  `data.provider == data.reference.provider`, and `workload.EndpointId == Providers:<token>:EndpointId`.
  `ProviderRegistry.Load` (`Kernel/Integrations/ProviderOptions.cs:144`) fails composition when
  `enabled: true` without `EndpointId`/`Tenant`.
- Kernel ingest (`Kernel/Integrations/ProviderEventStore.cs`): dedupe spine `(provider,
  provider_delivery_id)`; echo law compares `data.actorProviderAccountId` to `BotUserId` or the one-shot
  `BotIdentityProbeUrl` GET (`ResolveBotIdAsync:124`, reads `doc.RootElement.GetProperty("id")`);
  arm dispatch at `:304-311`; `ReferenceOf:1163` reads `channelOrResourceId`, `conversationKind`,
  `rootThreadId` (falls back to `messageId`), `messageId`, `tenantOrOrganizationId`;
  `TargetOf:1164` and `ReplyEnvelope:1169`/`ReactEnvelope:1173` are the outbound envelope builders;
  `AppendDecodeRow:1072` carries `labels` nowhere (see Step 5).
- Label routing today: `MatchAssignRule:968` reads the adapter-normalized top-level `labels: string[]`
  (**only** from the issue arm, `:667`); a matched rule supplies `CapabilityId`, `EndpointId` (preference),
  `Team` (fail-open if unrostered), and adds inputs `{repo: reference.Channel, issue: reference.Root}` with
  `CommandIdBase: "work-assign-coding"` (`AppendRoutingIntentForWork:935`). Non-issue Work creation goes
  through `CommandApplier.CreateWorkBlockAsync` (`Kernel/Ai/CommandApplier.cs:373`) which always uses the
  **global** `options.AssignCapabilityId`/`AssignEndpointId` and hardcodes `SourceRefsJson: "[]"` (`:442`).
- Artifact upload from a link child: already implemented and test-proven —
  `src/adapters/harnesses/adapter-sdk/src/artifacts.ts` `createArtifactClient({baseUrl, token, fetchImpl?})`
  → `.upload(bytes, {mediaType, workId?, assignmentId?, retentionPolicy?, actorEvidenceRef?})`:
  `command.artifact.request_upload.v1` to `POST /api/link/v1/events` (ticket rides the ack `data`),
  `PUT {uploadLocation}` = `/api/link/v1/artifacts/{id}/content` (or presigned `uploadUrl` +
  `command.artifact.confirm_upload.v1`). Both link routes exist: `Kernel/Link/LinkEndpoints.Enroll.cs:214,231`
  and are team-scoped (`ArtifactLedger.GetForWorkloadAsync:562`). `@ofac/adapter` (package
  `src/adapters/harnesses/adapter-sdk`) already exports `createArtifactClient` and depends only on
  `@ofac/gateway`, `@ofac/protocol`, `@ofac/workspace-link` — **reuse it; do not write a second uploader.**
- Harness read of artifacts today: producers/harnesses only *write* (`omp/src/harness.ts:345`,
  `examples/full/coding-worker/src/harness.ts:264`); the human-plane read is
  `GET /api/artifacts/{id}/content` (302→presigned on s3, streams on fs). There is **no** link-plane GET and
  no gateway artifact plumbing (`grep -rn "artifact" src/gateway/src` → one comment in `policy-file.ts`).
  `command.context.fetch.v1` is declared in the protocol but has no kernel or gateway implementation. Plan
  Step 8 closes this with a link-plane content GET.
- Outbound reply law: the kernel builds `command.communication.reply.v1` from
  `communication_bindings(provider, channel, root_message_id, parent_message_id)` —
  `EfCoordinationStore.AppendProviderCompletionReplyAsync:505` (completion reply,
  `communication-reply:<transitionEventId>`) and `InteractionIngress.cs:440-500` (fan-out). The adapter must
  therefore be able to post from `(channelOrResourceId, rootThreadId|parentMessageId|subjectRef)` alone.
- Provider env law for adapters (`mattermost/README.md` table): `OFAC_KERNEL_URL`, `OFAC_ENROLL_SECRET`,
  `OFAC_ENDPOINT_ID`, `OFAC_TEAM`, `OFAC_PROVIDER_INSTANCE_ID`, `OFAC_STATE_PATH`, `PORT`/`OFAC_HEALTH_PORT`,
  `OFAC_ADAPTER_LOG`, plus provider token as `*_TOKEN` **and** `*_TOKEN_FILE` (`resolveBotToken`,
  inline wins).
- Kernel provider section env form: `reference/config/facilitator-providers.env`
  (`Providers__Mattermost__*`, `Providers__Forgejo__AssignRules__0__*`). One env file feeds
  `compose.full.yaml` **and** the replica twin `compose.replicas.full.yaml`.
- Package registration: `pnpm-workspace.yaml` packages list + `Dockerfile.node:49-56`
  `pnpm deploy --filter=@ofac/<pkg> --prod /deploy/<dir>`.
- Telegram API facts (verified against core.telegram.org today): long polling and webhooks are mutually
  exclusive (`getUpdates` fails if a webhook is set); `getUpdates{offset,limit(1-100),timeout,
  allowed_updates}`; `setWebhook{url,secret_token,max_connections,allowed_updates,drop_pending_updates}` and
  Telegram sends header `X-Telegram-Bot-Api-Secret-Token`; `deleteWebhook{drop_pending_updates}` exists;
  `getFile` → bots download ≤ 20 MB, link `https://api.telegram.org/file/bot<token>/<file_path>` valid ≥ 1 h;
  `sendMessage` text ≤ 4096; `sendPhoto` new upload ≤ 10 MB, caption ≤ 1024; `sendDocument` ≤ 50 MB,
  caption ≤ 1024; `setMessageReaction{chat_id,message_id,reaction:[{type:"emoji",emoji}]}` with a closed
  emoji allowlist that does **not** contain `white_check_mark` or `eye`; every send method accepts
  `message_thread_id` and `reply_parameters`; `MessageReactionUpdated` carries `{chat, message_id, user?,
  date, old_reaction, new_reaction}`; `Message` carries `message_thread_id`, `is_topic_message`, `photo`,
  `document`, `caption`; base URL `https://api.telegram.org/bot<token>/<method>`, files require
  `multipart/form-data`.

## Approach

Ordered; each step leaves a buildable tree whose check is named at its end. Steps 2-6 depend on 1;
steps 5, 6, 7 are independent of each other once 4 lands. Nothing here touches the gateway.

### 1 — Protocol widening (three additive optional fields + fixtures + codegen)

`message`/`mention` data schemas are `additionalProperties: false`, so media and labels are impossible
without a schema change. Add, each as a genuinely optional property (v1 additive, no new types):

- `protocol/json-schema/event.provider.message.v1.json` and `event.provider.mention.v1.json`: add to
  `properties`
  - `"labels": { "type": "array", "maxItems": 32, "items": { "$ref": "common.v1.json#/$defs/idToken" } }`
    with the description wording of the issue schema ("Adapter-normalized label names… the kernel reads the
    flag, it never interprets labels") so the vocabulary is identical to the existing issue `labels`.
  - `"sourceRefs": { "type": "array", "maxItems": 16, "items": { "$ref": "common.v1.json#/$defs/artifactRef" } }`
    — the inbound media, already carried verbatim into `provider_events.body`.
- `protocol/json-schema/command.communication.reply.v1.json`: add
  `"artifacts": { "type": "array", "maxItems": 8, "items": { "$ref": "common.v1.json#/$defs/artifactRef" } }`
  — the outbound media (outbound-only shape deliberately separate from `sourceRefs`: the kernel emits these
  for a harness-produced artifact, the adapter consumes them).
- `protocol/conformance-fixtures/valid/event.provider.mention.with-media.json` — a mention envelope whose
  `data` carries `labels: ["ofac:coding"]` and one `sourceRefs` item (copy
  `valid/event.provider.mention.json` and extend; `artifactId` a GUID string, `mediaType: "image/png"`,
  `size`, `digest` lowercase sha256, `storageUrl: "artifact://…"`-shaped uri, `createdBy`, `createdAt`,
  `kind: "chat.attachment"`).
- `protocol/conformance-fixtures/valid/command.communication.reply.with-artifact.json` — reply envelope with
  one `artifacts` item.
- `protocol/conformance-fixtures/invalid/event.provider.mention.bad-label-type.json` — `labels: "coding"`
  (string, not array) expecting `errorClass: "schema-violation"`.
- Register all three in `protocol/conformance-fixtures/index.json` (copy the shape of the existing
  `event.provider.*` entries; the two valid ones take `class: "event"` / `"command"` and no `errorClass`).
- Run `pnpm protocol:codegen` (or `make protocol`) and commit the regenerated
  `src/protocol/src/generated/`, `protocol/dotnet/Ofac.Protocol/Generated/`, `src/protocol/schemas/`.

Check: `pnpm --filter @ofac/protocol test` and
`dotnet run --project protocol/tools/Validate.Fixtures -c Release` both pass; `pnpm -r --if-present
typecheck` passes with the new `ProviderMessageEventV1Data.labels?/sourceRefs?` and
`CommunicationReplyCommandV1Data.artifacts?` generated types present.

### 2 — Kernel: provider-domain vocabulary in the two Work-creation seams (no vendor names)

Goal: the two things Telegram needs that the kernel cannot express today — (a) per-chat capability/team
selection for a **mention**, (b) inbound media refs reaching the Work and the assign. Both are expressed in
domain vocabulary so the provider stays data.

File `src/facilitator/Kernel/Integrations/ProviderOptions.cs`:
- New option on `ProviderOptions`: `public bool ChatLabelRouting { get; init; }`, bound from
  `Providers:<name>:ChatLabelRouting` (default `false`). Semantics: when true, the mention/message
  Work-creation path matches `AssignRules` against the event's normalized `labels` exactly as the issue arm
  does, and an unmatched event keeps today's global pair. Per-provider opt-in keeps the
  forgejo/github/gitlab arms byte-identical (`false` ⇒ the code path they take is unchanged).
  No kernel option is needed for the reply `artifacts` field: the kernel never originates one today.

File `src/facilitator/Kernel/Integrations/ProviderEventStore.cs`:
- `PayloadLabels(JsonElement data)` (`:985`) is already generic over the top-level `labels` array — reuse it
  unchanged for chat events.
- `MatchAssignRule(options, triggerEvent)` currently returns null unless a label matches. Add an internal
  sibling `MatchChatLabelRule(ProviderOptions options, JsonElement? triggerEvent)` that is
  `options.ChatLabelRouting ? MatchAssignRule(options, triggerEvent) : null` and call **only** it from the
  chat seams below (do not widen `MatchAssignRule` itself — the issue arm keeps its current behavior).
- Add the media reader next to `PayloadHeadRef:749`:
  `internal static JsonArray? PayloadSourceRefs(JsonElement data)` returning the top-level `sourceRefs` array
  when it is a non-empty `JsonArray`, else `null`. The kernel never inspects item shapes.
- `MentionArmAsync` (`:456`): the two Work-creation exits are
  `CommandApplier.VerbatimWorkAsync(...)` (`:540`) and `applier.ApplyAsync(...)` (`:516`). Before calling
  either, compute `var rule = MatchChatLabelRule(options, data);` and
  `var sourceRefs = PayloadSourceRefs(data);` and pass both down (new optional parameters on the applier,
  below). Unlinked-mention and `/link` paths are untouched.
- `MessageArmAsync` (`:595`): its slash-plane exit calls `applier.ApplyAsync(...)`; pass `rule: null` and
  `sourceRefs: PayloadSourceRefs(data)` there too, so a `/work` typed in a labeled group routes and carries
  media. Free-text unbound messages stay `message:unbound`.
- `AppendDecodeRow` (`:1072`): add `["labels"]` (the event's normalized labels array, or `new JsonArray()`)
  and `["sourceRefs"]` (`sourceRefs ?? new JsonArray()`) to its `data` object, and document in the XML comment
  that this row is internal-channel, not wire-validated. That is what lets the AI decode sink route and
  attach media too.
- `AppendWorkInputAsync` (`:902`): give it an optional `JsonArray? sourceRefs = null` parameter. When
  non-null, append a second timeline row
  `Kind = "provider.attachment"`, `Actor = $"provider:{provider}"`,
  `Detail = {"note": <the existing note>, "sourceRefs": <array>}`, and keep the progress-row behavior. The
  follow-up-message-with-media path then leaves an auditable timeline row.
- `InputNote(...)`: keep the existing note text; where `sourceRefs` is present the note gains the appended
  line `media: <mediaType>@<artifactId>` per ref, built from the ref items without parsing vendor shapes
  (`artifactRef` is a frozen common def).
- Add `"provider.attachment"` to the `work_events_kind_check` list in
  `src/facilitator/Kernel/Shared/Persistence/Configurations/WorkEventEntityConfiguration.cs:29-33` (the
  check constraint is `CheckSql.InList`, so the edit is a one-token addition) **and** generate the
  corresponding migration (`make db-update` per repo law; new file under `src/facilitator/Migrations/`).
  If the implementer finds the constraint is not enforced by an EF check constraint in the migration
  snapshot, mirror the migration by hand rather than skipping it.

File `src/facilitator/Kernel/Ai/CommandApplier.cs`:
- `VerbatimWorkAsync` (`:116`), `ApplyAsync` (`:66`) → `CreateWorkAsync` (`:349`) and
  `CreateWorkBlockAsync` (`:373`): thread two new optional parameters through —
  `AssignRule? rule = null` and `JsonArray? sourceRefs = null`.
- In `CreateWorkBlockAsync`:
  - team selection: copy the exact pattern of the issue-arm helper
    (`ProviderEventStore.CreateWorkFromProviderAsync:784-798`) — if `rule?.Team` names a rostered team, the
    Work lives on that team; unrostered keeps the workload team (fail-open). Today this method always uses
    `teamGuid`.
  - routing intent: replace the hardcoded block at `:428-448` with the same shape as
    `AppendRoutingIntentForWork`: capability `rule?.CapabilityId ?? options.AssignCapabilityId`;
    `PreferenceEndpointId: rule?.EndpointId ?? options.AssignEndpointId`;
    `TargetId: rule?.TargetId ?? options.AssignTargetId ?? options.Provider + "-interim"`;
    `CommandIdBase: rule is null ? "work-assign" : "work-assign-coding"`;
    `ExtraInputsJson`: when `rule` is non-null the chat rule contributes
    `{"chat": <reference.Channel>, "topic": <reference.Root>}` (chat-domain names, **not** the forge
    `repo`/`issue` pair — do not reuse those two keys for chat, the coding worker's re-hydration reads them);
    `SourceRefsJson: sourceRefs?.ToJsonString() ?? "[]"`. The existing
    `RoutingSink.EmitEffect:328-331` then puts that array on `work.assign` `inputs.sourceRefs`, and
    `ProviderEnvelopes.Assign` (`ProviderEnvelopes.cs:33`) already merges extra inputs over its
    `["sourceRefs"] = new JsonArray()` default — no envelope change needed.
  - after the binding row: when `sourceRefs` is non-null append one `WorkEventEntity` with
    `Kind = "provider.attachment"`, `Detail = {"sourceRefs": <array>}`.
- `DecodeSink` (`src/facilitator/Kernel/Ai/DecodeSink.cs`): read the two new fields in `RunAsync`
  (`labels`, `sourceRefs` from the row's `data`, mirroring how `replyVsReaction` is read at `:104`) and pass
  them to `ApplyAsync`/`FallbackAsync`→`VerbatimWorkAsync` as `rule` (via
  `ProviderEventStore.MatchChatLabelRule` against the reconstructed row — the row needs `labels` precisely
  for this) and `sourceRefs`. `FallbackAsync` (`:251`) signature gains the same two optional parameters.
  Any missing field behaves exactly as today (`"[]"`, no rule).

Check (kernel-side, before any adapter exists):
- New test file `tests/Facilitator.Tests/Integrations/TelegramIngressTests.cs` — copy the harness setup of
  `tests/Facilitator.Tests/Integrations/AssignRuleRoutingTests.cs` (its `PlaneWithRulesAsync`,
  `FleetAsync`, `RoutingHarness.DrainUntilAsync` helpers; per-test unique `gw-` endpoint names). Concretely
  prove, with a `Providers:telegram` section (`ChatLabelRouting=true`, one `AssignRules` row
  `Label=ofac:coding, CapabilityId=coding.implement, EndpointId=<coding gw>, Team=team-coding`):
  1. a `provider.mention.v1` event with `labels:["ofac:coding"]` and one `sourceRefs` item creates exactly one
     Work bound at `(telegram, <chat id>, <message id>)`, drains a `coding.implement` assign addressed to
     `link.<coding gw>` with command id base `work-assign-coding:`, whose
     `inputs.sourceRefs[0].artifactId` equals the uploaded artifact id and whose `inputs.chat` equals the
     chat id;
  2. the same event **without** the label drains the global `AssignCapabilityId` intent (regression guard);
  3. a `provider.mention.v1` **without** `labels`/`sourceRefs` behaves byte-comparably to today's mattermost
     mention path (one binding, `white_check_mark` ack, global intent).
- Existing suites unchanged and passing: `dotnet test --filter "FullyQualifiedName~Integrations"`,
  `dotnet test --filter "FullyQualifiedName~Ai"`, `dotnet test --filter "FullyQualifiedName~Artifacts"`.

### 3 — Adapter package skeleton

New package `@ofac/telegram-adapter` in `src/adapters/communication/telegram/`, mirroring mattermost's
layout, header-comment law style, and `package.json` (`license AGPL-3.0-or-later`, `private: false`,
`type: module`, `bin: {"telegram-adapter": "./dist/bin/adapter.js"}`,
`build: tsup src/index.ts src/bin/adapter.ts --format esm --dts --target node22 --silent`, `test: vitest
run`, `typecheck: tsc --noEmit -p tsconfig.json`; deps `@ofac/protocol`, `@ofac/provider-core`,
`@ofac/workspace-link`, `@ofac/adapter` (the artifact client), `better-sqlite3: catalog:`,
`@types/better-sqlite3: catalog:` — no new third-party Telegram SDK: the Bot API is plain HTTP).
Register in `pnpm-workspace.yaml` (`- "src/adapters/communication/telegram"`, next to the other
communication adapters) and in `Dockerfile.node` deploy list
(`pnpm deploy --filter=@ofac/telegram-adapter --prod /deploy/telegram-adapter`).
Copy `mattermost/tsconfig.json` and `mattermost/vitest.config.ts` verbatim.

Files (one responsibility each, law-naming header comment):
- `src/config.ts` — env law + chat allowlist. `AdapterConfigError(key,msg)`, `parseEnvConfig(env)`,
  `deriveTenant(...)` analogue, `resolveBotToken` reused verbatim from mattermost.
- `src/normalize.ts` — pure Telegram update → `NormalizedEvent | null`, no I/O.
- `src/tg-client.ts` — `TelegramApi` interface (the test seam) + `TelegramClient` over `fetch`;
  `getUpdates`, `deleteWebhook`, `setWebhook`, `getMe`, `getFile`, `downloadFile`, `sendMessage`,
  `sendPhoto`, `sendDocument`, `setMessageReaction`; `multipart/form-data` bodies for uploads.
- `src/updates-listener.ts` — the long-poll loop (the mattermost `ws-listener.ts` analogue): offset
  checkpointing, backoff ladder `[250,500,1000,2000,5000,10000]`, `timeout=30`, `limit=100`,
  `allowed_updates: ["message","edited_message","message_reaction"]`.
- `src/media.ts` — inbound media pipeline: `getFile` → download → sha256 → `createArtifactClient.upload` →
  `ArtifactRef` items; provider-side dedupe by `file_unique_id`.
- `src/chat-ledger.ts` — SQLite state (mattermost `post-ledger.ts` analogue): tables `commands`
  (idempotency law), `messages` (adapter-created provider message ids), `topics` (bound roots),
  `files` (`file_unique_id` → `artifactId`, the media dedupe ledger), `kv` (update offset, bot id, cached
  chat titles).
- `src/handler.ts` — the link command dispatcher: `communicationManifest(team)` + `createAdapterHandler`.
- `src/webhook.ts` — the `WebhookNormalizer` for webhook mode (secret-token law + normalization + media).
- `src/index.ts` — `run(config, logger, deps) → RunHandle`; wires `runProviderAdapter`, listener, media,
  health server, shutdown.
- `src/bin/adapter.ts` — `main(argv, env) → exit code` (0 clean, 1 runtime, 2 config error), SIGINT/SIGTERM
  park.
- `README.md` — mirror mattermost's section order (inbound law, outbound+idempotency, config table,
  standalone run, layout, tests) and state the two deviations below as `T1`/`T2`.

Check: `pnpm -C src/adapters/communication/telegram build && pnpm -C src/adapters/communication/telegram
test` (fixtures not yet written ⇒ `vitest run --passWithNoTests` is acceptable only in this step, never the
final state); `pnpm -r --if-present typecheck` clean.

### 4 — Inbound: config, allowlist, classification, root law

`config.ts` env law (exact literals; provider token both forms, inline wins):

| Variable | Required | Default | Meaning |
| --- | --- | --- | --- |
| `OFAC_KERNEL_URL` | yes | — | link plane base |
| `OFAC_ENROLL_SECRET` | yes | — | enrollment secret |
| `OFAC_ENDPOINT_ID` | yes | — | link endpoint, kernel addresses `link.<id>` (suggest `tg-adapter`) |
| `OFAC_TEAM` | yes | — | enrollment team slug |
| `TELEGRAM_BOT_TOKEN` / `TELEGRAM_BOT_TOKEN_FILE` | yes | — | bot token; also the bot's numeric `id` (see below) |
| `TELEGRAM_CHATS` | yes | — | allowlist: `chatId[:label[,label]]` entries, comma-separated. `chatId` is Telegram's signed integer (supergroups are negative) or `@channelusername`; `dm:<userId>` admits a private chat. Empty label list = admitted, no labels ⇒ global routing. |
| `TELEGRAM_MODE` | no | `poll` | `poll` \| `webhook` |
| `TELEGRAM_WEBHOOK_URL` | mode=webhook | — | public HTTPS URL registered via `setWebhook` |
| `TELEGRAM_WEBHOOK_SECRET` | mode=webhook | — | `secret_token`; compared constant-time against header `X-Telegram-Bot-Api-Secret-Token` (reuse `verifyTokenToken` from `@ofac/provider-core`) |
| `TELEGRAM_WEBHOOK_LISTEN_PORT` | no | `8077` | inbound port for `POST /webhook` (webhook mode only) |
| `OFAC_PROVIDER_INSTANCE_ID` | no | `local-telegram` | `reference.providerInstanceId` |
| `OFAC_TENANT` | no | `api.telegram.org` | `reference.tenantOrOrganizationId` + the value `Providers:Telegram:Tenant` must equal |
| `OFAC_BOT_USER_ID` | no | from `getMe` | adapter-side bot numeric id used for echo classification and echo authorship; independent of `Providers:Telegram:BotUserId`, which the kernel resolves separately (Step 7) — a mismatch is logged, not fatal |
| `TELEGRAM_STATE_PATH` | no | `./ofac-tg-adapter.sqlite3` | link ledger + chat ledger + offset |
| `TELEGRAM_MEDIA_MAX_BYTES` | no | `20971520` | inbound media ceiling; larger ⇒ recorded skip, never a crash |
| `TELEGRAM_MEDIA_ENABLED` | no | `true` | `false` ⇒ media is noted and skipped |
| `TELEGRAM_API_BASE` | no | `https://api.telegram.org` | fake-server seam for the e2e smoke |
| `PORT` / `OFAC_HEALTH_PORT` | no | off | `GET /healthz` → `{ok:true, botId, mode:"poll"\|"webhook", chats:N}` |
| `OFAC_ADAPTER_LOG` | no | `info` | log level |

Config-time laws: every `TELEGRAM_CHATS` entry must parse (non-empty signed integer or `@name` or
`dm:<int>`); a malformed entry is `AdapterConfigError("TELEGRAM_CHATS", …)` ⇒ exit 2. Labels are lowercased
`[a-z0-9-]+` (the kernel `VerbPattern` charset and the `idToken` limit both hold). Duplicate chat id with
differing labels ⇒ config error. `TELEGRAM_MODE=webhook` without url/secret ⇒ config error.

`normalize.ts` classification law — pure function
`normalizeUpdate(update, ctx: NormalizeContext): NormalizedEvent[] | null` with
`NormalizeContext = { botUserId, providerInstanceId, tenantOrOrganizationId, chatTypeFor(chatId): "group"|"supergroup"|"channel"|"private",
admissionFor(chatId): { admitted: true; labels: string[] } | null, boundRootIds(): Set<string>,
mediaArtifactsFor(messageId): ArtifactRefItem[] }`:
- `message`/`edited_message` where `from.id === botUserId` → **echo** `message.v1` (same law as mattermost:
  the kernel records the delivery and updates the binding, arms suppressed by bot id).
- chat not admitted (`admissionFor` null) → `null`, except an update whose text mentions the bot → a special
  `unconfigured-notice` signal consumed by `index.ts` (Step 6 reply), which never publishes an event.
- mention test: `text` contains `@<bot username>` at a word boundary (Telegram delivers no `entities`-based
  mention flag for user mentions; reuse the mattermost `mentions()` word-boundary helper) → `mention.v1`.
- private chat (`dm:<userId>` admitted, or `chat.type === "private"`) → `message.v1` with
  `conversationKind: "dm"`.
- message in a chat whose root is in `boundRootIds()` (the adapter's own reply, or the topic root the adapter
  created) → `message.v1`, `conversationKind: "thread"`.
- everything else → `null`. Never publish every group post: the allowlist plus these three cases is the
  whole flood control.
- Root law (hybrid, computed once in `normalize.ts` as `rootIdFor(message, ctx)`):
  1. `message.message_thread_id` present (forum topic) → root = `String(message_thread_id)`,
     and the event carries `rootThreadId` + `parentMessageId` = that value.
  2. else walk `message.reply_to_message`: if that message is bot-authored **and** its message id is in
     `boundRootIds()`, root = that id; if it is a human message whose id is in `boundRootIds()`, root = that
     id; otherwise continue to 3.
  3. else root = `String(message_id)` — this mention/message opens a new conversation.
  The kernel's `ReferenceOf:1163` already falls back `rootThreadId ?? messageId`, so rule 3 needs no
  `rootThreadId`; set it explicitly anyway for auditability.
- Event field law (all four provider events): `provider: "telegram"`;
  `providerInstanceId: config.providerInstanceId`; `tenantOrOrganizationId: config.tenant`;
  `channelOrResourceId: String(message.chat.id)`; `conversationKind`: `"dm"` for private, `"thread"` when a
  root is set, `"channel"` for a channel post, `"comment"` never; `authorProviderAccountId: String(from.id)`;
  `messageId: String(message_id)`; `providerDeliveryId: String(update.update_id)` (Telegram's update id is the
  natural delivery id: unique per delivered update, so the kernel's
  `(provider, provider_delivery_id)` unique is the at-least-once dedupe);
  `actorProviderAccountId: String(from.id)`; `text`: caption-else-text, clamped to 8192 exactly like
  mattermost's `clamp`, and **`text` must never be empty** — a media-only message with no caption publishes
  `text: "(attachment)"` so the schema's `minLength: 1` holds; `payload`: the verbatim parsed `Update`
  object; `labels`: the chat's configured labels (mention/message events only, empty array ⇒ omit the
  property, never send `[]`); `sourceRefs`: the uploaded refs for this message (omit when none).
- `message_reaction` → `reaction.v1`: `emojiName` = first `new_reaction[]` item with `type === "emoji"`,
  omitted (⇒ `null`, ignored) when the new reaction set is empty or custom-emoji-only;
  `providerDeliveryId: String(update_id)`; `conversationKind`/root by the same `rootIdFor` law applied to
  `MessageReactionUpdated.chat` + `message_id` (look the message up in the chat ledger when it is one the
  adapter saw, else fall back to `channel` + root = message id).
- `message_reaction_count`, `chat_member`, service messages (`new_chat_members`, `group_chat_created`,
  topic edits) → `null`.

Check: `pnpm -C src/adapters/communication/telegram test` — `test/normalize.test.ts` driven by
`test/fixtures/updates.ts` (hand-written Telegram `Update` JSON subset, the `mattermost/test/fixtures/`
pattern) proving each row of the law above, including: forum-topic root wins over reply; reply to a bot post
binds to the bot post; unconfigured group publishes nothing; echo classification; media-only message gets
`text: "(attachment)"`; reaction with empty `new_reaction` ignored; `providerDeliveryId` = `update_id`.
Envelope validity is asserted against the frozen schemas with `validateEnvelope` from `@ofac/protocol`, the
way mattermost's normalize tests do.

### 5 — Inbound media: download → verified artifact → event refs

`media.ts` pipeline, invoked from the listener/webhook **before** `publish` so the event carries refs:
1. Collect attachments from `message.photo` (take the **largest** size variant — Telegram gives 3; use its
   `file_id`), `message.document`, `message.voice`, `message.video`, `message.audio`, `message.animation`,
   `message.sticker`. mediaType/metadata: photo ⇒ `image/jpeg` with the caption's `file_size`; otherwise
   `document.mime_type` / `voice.mime_type` etc.
2. Dedupe on `file_unique_id` via the `files` table: a known id reuses the stored `artifactId` (the same
   image forwarded twice in the same group costs one upload and one ref).
3. Skip, with a structured log and a `payload` note, when `file_size > TELEGRAM_MEDIA_MAX_BYTES`, when
   `TELEGRAM_MEDIA_ENABLED=false`, or when the chat's `file_size` is absent and the downloaded body exceeds
   the cap. Never fail the event because of media.
4. `getFile(file_id)` → `file_path`; download
   `${TELEGRAM_API_BASE}/file/bot<token>/<file_path>` with `fetchImpl`.
5. Upload with the **reused** client: `createArtifactClient({ baseUrl: kernelUrl, token: linkTokenProvider,
   fetchImpl })` from `@ofac/adapter` (already exported from its index; no new uploader anywhere),
   `.upload(bytes, { mediaType, retentionPolicy: "90d", actorEvidenceRef: "workload:<endpointId>" })`.
   Deliberately **no** `workId`/`assignmentId`: at this point no Work exists for the message. The team the
   row lands on is the adapter's enrollment team, which is what keeps
   `PUT /api/link/v1/artifacts/{id}/content` (team-scoped, `ArtifactLedger.GetForWorkloadAsync:562`)
   authorized. Note in the README that a rule-routed Work on a *different* team therefore has a
   cross-team artifact row until Step 8's timeline read path is used; that is the same non-disclosure
   boundary the artifact ledger already enforces, and the `artifactId` still rides the assign.
6. Build the ref exactly per `common.v1.json#/$defs/artifactRef`: `artifactId` = the kernel GUID string,
   `mediaType`, `size` = actual bytes, `digest` = the client's `sha256:<64hex>` (or the value the store
   returns), `storageUrl` = `artifact://provider/<fileUniqueId>/<artifactId>` (the `artifact://` logical-URI
   law from `docs/protocol.md`; a GUID-free logical uri keeps it non-authoritative), `createdBy` =
   `link:<endpointId>`, `createdAt` = ISO instant, `kind` = `"chat.attachment"`.
   The ref array is the event's `sourceRefs`.
7. Record `(file_unique_id, artifactId, mediaType, size, digest)` in `files` BEFORE publishing, so a redelivered
   update converges without a second upload.
Outbound download helper (used by Step 6): `fetchArtifactContent(artifactId)` → `GET
{kernelUrl}/api/link/v1/artifacts/{id}/content` with the link bearer token, following a 302; see Step 8 for
why this route must exist.

Check: `test/media.test.ts` with a fake `TelegramApi` + the stub kernel from
`@ofac/adapter/testing` (it already answers `request_upload` with 202 + a ticket at
`/api/link/v1/events` and accepts `PUT /api/link/v1/artifacts/<hex>/content`, see
`adapter-sdk/src/testing.ts:600,668,771`) — prove: two updates referencing the same `file_unique_id` ⇒
exactly one artifact; oversized media ⇒ zero uploads and the event still publishes; the ref carries the
kernel-returned GUID and matching digest; no `workId` on the ticket request.

### 6 — Outbound: reply / react / update / acknowledge, with media

`handler.ts` — `communicationManifest(team)` identical in shape to mattermost's (four capabilities,
`execution: "synchronous"`, `inputSchema: dataschemaFor(id)`), so no manifest-schema change is needed even
after the reply schema gains `artifacts` (the manifest points at the schema file, not a field subset).
`createAdapterHandler(deps: { api: TelegramApi, state: ChatLedger, identity, media, logger }): LinkHandler`
dispatches on the four `CMD_*` literals and obeys the idempotency law: consult the `commands` table keyed by
`idempotencyKey` **before** any Telegram mutation; a replay answers `accepted` with the stored provider
message id and never double-posts.

- Resolve chat: `target.channelOrResourceId` → `chat_id`. Absent (the `dm` target shape the kernel emits as
  `DmTarget(provider, accountId)`, `ProviderEventStore.cs:1166`) → `chat_id = "@" + ...` is unknown, so use
  the ledger: a `dm:<accountId>` mapping recorded when a private chat was first seen; if unknown, `rejected`
  with reason `no-dm-channel` (the mattermost `resolveChannel` analogue creates the channel on demand;
  Telegram cannot open a bot DM before the human has `/start`ed, so an honest rejection is the right answer —
  document as deviation **T1**).
- Resolve the message to act on: `targetPostId(target) = parentMessageId ?? subjectRef ?? rootThreadId`,
  copied from mattermost. `subjectRef` is `provider/<provider>/<messageId>` shaped in kernel reply envelopes
  (`ReplyEnvelope` subject is `provider/telegram/<messageId>` but the *target* carries ids, not the subject) —
  the adapter must therefore parse a bare id and, defensively, strip a `provider/telegram/` prefix.
- Reply threading: if the resolved id is a numeric topic id recorded in `topics`, send with
  `message_thread_id: <id>`; otherwise send with `reply_parameters: { message_id: <resolved> }` (and
  `allow_sending_without_reply: true`). If neither is resolvable, post to the chat unthreaded.
- `CMD_REPLY` with `data.artifacts` present and non-empty: for each ref, download bytes (Step 8 path) and
  send `sendPhoto` when `mediaType` starts `image/` and size ≤ 10 MB, else `sendDocument` (≤ 50 MB). The
  first send carries the `text` as `caption` (clamped to 1024); the rest carry no caption. Text-only reply
  (`no artifacts`) → `sendMessage`, and when `text.length > 4096` split on newline boundaries into ≤ 4096
  chunks and send sequentially — **only the last chunk's** provider message id is recorded under the
  `idempotencyKey`, and the recorded value is a JSON array when more than one was sent.
  A download failure ⇒ `rejected` with reason `artifact-unavailable` (never a partial silent post).
  An oversize artifact ⇒ send it as `sendDocument` anyway and, on Telegram's 413, `rejected` with reason
  `artifact-too-large`.
- `CMD_REACT`: map `emojiName` (the kernel sends Slack-style names `white_check_mark`, `eye` —
  `ProviderEventStore.cs:92,95`) through a table `ACK_EMOJI: Record<string, string>` to Telegram's closed
  allowlist — `white_check_mark → "✅"`, `eye → "👀"`, `+1/thumbsup → "👍"`, `-1 → "👎"`, `tada → "🎉"`,
  `fire → "🔥"`; unmapped → `"👀"`, then `setMessageReaction(chat_id, message_id, reaction:[{"type":"emoji",
  "emoji":"<char>"}])`. Telegram rejects non-allowlisted emoji, so the table is mandatory (deviation **T2**).
- `CMD_UPDATE`: only edits a message the ledger proves the adapter created (`messages` table), via
  `editMessageText`; otherwise `rejected` reason `not-adapter-message`.
- `CMD_ACK`: no-op `accepted` (mattermost law).
- Echo law: after a successful reply/update, publish the bot's own `message.v1` through
  `ctx.publishEvent` with `actorProviderAccountId = botUserId`, `providerDeliveryId = "<sent message id>"`,
  the target's chat/root, and `text` = the sent text — the kernel's echo law then records the outbound
  provider message id into `communication_bindings.parent_message_id`, which is what makes a later
  human reply resolvable by rule 2 of the root law. Use `decorate` on `runProviderAdapter` to set
  `subject: provider/telegram/<messageId>` so the echo matches the mattermost envelope shape.
- Ledger write ordering: record the provider object id under `idempotencyKey` **before** the receipt goes
  out, exactly like mattermost's post-ledger law.
- Unconfigured-chat onboarding: when `normalizeUpdate` reports an unconfigured mention, `index.ts` sends
  (once per chat, guarded by a `kv` key `notice:<chatId>`) `sendMessage` with
  `Chat id <chatId> is not configured for Facilitator. Add it to TELEGRAM_CHATS as <chatId>:<label> to
  route this conversation.` — a reply, never an event, so nothing reaches the kernel.
- Health server: same shape as mattermost's (`{ok:true, botId, mode, chats}`), gated on
  `PORT`/`OFAC_HEALTH_PORT`.

Check: `test/handler.test.ts` with a fake `TelegramApi` recording calls — prove: delivery replay answers
`accepted` with zero Telegram calls; a reply with one `image/png` ref calls `sendPhoto` with the downloaded
bytes and a caption ≤ 1024; a 5 MB `application/pdf` ref calls `sendDocument`; a 60 MB ref ⇒ `rejected`
`artifact-too-large` and zero posts; a 9000-char text reply sends ≥ 3 `sendMessage` calls;
`CMD_REACT "white_check_mark"` ⇒ `setMessageReaction` with `✅`; an update to a foreign message id ⇒
`rejected` `not-adapter-message`; after a reply, exactly one echo `message.v1` was published and validates
against `event.provider.message.v1.json`. `test/config.test.ts` — the config matrix from the table above.

### 7 — Webhook mode + `Providers:Telegram` wiring

- Long-poll mode (default): on startup, `deleteWebhook{drop_pending_updates: false}` (else `getUpdates`
  errors), then the poll loop. No inbound port except `/healthz`.
- Webhook mode: on startup `setWebhook{url: TELEGRAM_WEBHOOK_URL, secret_token, max_connections: 8,
  allowed_updates: ["message","edited_message","message_reaction"]}` (call it **before** binding the port so
  a crash cannot leave Telegram POSTing to a closed listener — the ordering law), serve
  `POST /webhook` on `TELEGRAM_WEBHOOK_LISTEN_PORT`, answering 202 immediately after the local signature
  check, and `GET /healthz`.
- Kernel-side fan-out: `POST /api/ingress/webhooks/telegram`
  (`Kernel/Integrations/WebhookIngressEndpoints.cs`) delivers
  `command.provider.webhook.v1` to `link.telegram-adapter`; pass it to
  `runProviderAdapter({ normalizeWebhook })`. `webhook.ts` normalizer law: decode base64 `body` → JSON
  `Update`; `headers["x-telegram-bot-api-secret-token"]` must equal `TELEGRAM_WEBHOOK_SECRET`
  (`verifyTokenToken`, constant-time) else `{kind:"bad-signature"}` ⇒ receipt `rejected`;
  otherwise return the same normalized events as poll mode (media uploaded first, so the normalizer is
  async — the `WebhookNormalizer` type already allows a `Promise`). Dedupe by `update_id` against the
  `kv` checkpoint + `commands` table, so a kernel-side redelivery of the same webhook body publishes zero
  new events (`accepted`, zero published).
- `reference/config/facilitator-providers.env` gains the section (single env file, shared by
  `compose.full.yaml` and `compose.replicas.full.yaml` — do not add an inline env block in the overlay):
  ```
  Providers__Telegram__Enabled=true
  Providers__Telegram__EndpointId=tg-adapter
  Providers__Telegram__Tenant=api.telegram.org
  Providers__Telegram__BotTokenFile=/provision/telegram-bot-token
  Providers__Telegram__BotUserId=<numeric bot id>
  Providers__Telegram__ChatLabelRouting=true
  Providers__Telegram__AssignEndpointId=gw.team-alpha
  Providers__Telegram__AssignCapabilityId=research.analyze
  Providers__Telegram__AssignRules__0__Label=ofac:coding
  Providers__Telegram__AssignRules__0__CapabilityId=coding.implement
  Providers__Telegram__AssignRules__0__EndpointId=gw.coding
  Providers__Telegram__AssignRules__0__Team=team-coding
  Providers__Telegram__LinkHintText=Your Telegram account is not linked to a Facilitator identity — DM me /link to link it.
  ```
  `BotUserId` is set explicitly rather than probed: `ResolveBotIdAsync` reads a **root-level** `id` from the
  probe response, but `getMe` answers `{"ok":true,"result":{"id":…}}`, so the existing
  `BotIdentityProbeUrl` mechanism cannot read it without kernel vendor knowledge — which the neutrality law
  forbids. `Providers__Forgejo__BotUserId` is the precedent for the explicit form. The adapter still calls
  `getMe` at startup (health payload + the `OFAC_BOT_USER_ID` override) and logs a mismatch against the
  configured value.
  The env file is committed with a placeholder bot id; document the two manual steps (create the bot with
  @BotFather, add it to a group and disable privacy mode or add it as admin for full visibility) in
  `reference/README.md`'s provider section.
- `reference/compose.full.yaml`: no new service unless a fake Telegram server is wanted. Add an
  `tg-adapter` service **only** when `TELEGRAM_BOT_TOKEN` is present in `reference/.env` — compose cannot
  conditionally include a service, so instead ship it with `profiles: ["telegram"]` (matching how other
  optional legs are handled) running
  `node /app/telegram-adapter/dist/bin/adapter.js run` with the env block from the table above,
  `restart: on-failure:5`, and `depends_on: facilitator: condition: service_healthy`.
- `docs/advanced.md` provider section (~`:234`): name
  `@ofac/telegram-adapter` alongside mattermost/forgejo with one line each for the two deviations.

Check: `make lint && make build && make test && make docs-check`. Then
`TELEGRAM_BOT_TOKEN=<real token> TELEGRAM_CHAT_ID=<-100… group id>
node src/adapters/communication/telegram/dist/bin/adapter.js run` against a kernel with the section above:
post `@<bot> hello` in the group ⇒ expect a `provider_events` row for `provider='telegram'` with
`provider_delivery_id = <update_id>`, a `communication_bindings` row keyed
`('telegram', '<chatId>', '<messageId>')`, and (AiDeck inert) an "On it." `sendMessage` back.

### 8 — Link-plane artifact content GET, so a harness can read an inbound image

The kernel has no link-plane content read today (only `/api/artifacts/{id}/content` on the human plane), so
`inputs.sourceRefs` is currently a pointer a harness cannot dereference. Close it with the minimal route:

- `src/facilitator/Kernel/Link/LinkEndpoints.Enroll.cs`: new `MapArtifactRead(group)` beside
  `MapArtifactContent` (`:214`) —
  `group.MapGet("/artifacts/{id:guid}/content", ...)` using the same
  `LinkAuthMiddleware.RequirePrincipal(http)` + `ledger.GetForWorkloadAsync(workload, id, ct)` guard as the
  PUT (404 non-disclosure for foreign/missing), then the same body as the human-plane read:
  `IArtifactStore` presign-302 when the adapter is an `IStoreSignedUrlSource`, else
  `Results.Stream(stream, view.MediaType, entityTag: digest)`, 404 when the row says stored but the blob is
  gone. Requires `IArtifactStore` as an extra injected parameter on the handler. Register it in
  `MapLinkApi` next to `MapArtifactContent(group)`. Team law: a cross-team inbound image (rule-routed Work on
  another team, Step 5 point 5) answers 404 to that team's workload — acceptable and honest; note it in the
  README of the adapter.
- `src/adapters/harnesses/adapter-sdk/src/artifacts.ts`: add `download(artifactId: string):
  Promise<Uint8Array>` to the `ArtifactClient` interface and implementation (GET
  `${base}/api/link/v1/artifacts/${id}/content`, bearer token, follow redirects, throw on non-2xx).
  Exported already through the package index (`createArtifactClient` + `type ArtifactClient` are re-exported;
  the new member needs no index change).
- Wire media into a harness run minimally, so the "images pasted in chat are available in the harness"
  requirement is provable and not just stored:
  - `src/adapters/harnesses/deterministic/src/harness.ts` (the worked example, cheapest place to prove the
    contract): after reading `inputs.objective` (`:76`), read `inputs.sourceRefs`; when present, download
    each ref with `createArtifactClient(...).download(id)` and include, in the produced report text, one
    line per attachment:
    `attachment <n>: <mediaType> <size> sha256:<hex> <first bytes hex, 16 chars>` — the byte-level proof the
    harness actually received the bytes. The deterministic worker already depends on `@ofac/adapter`
    (`deterministic/src/bin/worker.ts:9` imports `createAdapter`), so no new dependency.
  - `src/adapters/harnesses/omp/src/harness.ts`: same read at `:146`; append to the prompt
    (`:148-152`, after `{objective}` substitution) one line per attachment:
    `Image attachment: <kernelUrl>/api/artifacts/<artifactId>/content (mediaType <t>, <size> bytes)` so the
    agent can fetch it, and store the downloaded bytes under
    `join(execDir, "attachments", String(i))` when `execDir` exists at that point (`:162`) — the omp
    workspace is the harness's own filesystem, which is where a model-facing image belongs. If
    `omp`'s CLI has no way to accept an image path today, keeping the file + the prompt line is the
    accepted v1 behavior; say so in `omp/README.md`.
- `protocol/openapi/link-v1.yaml`: document the new GET in the artifact section (it is the frozen HTTP
  surface; a missing entry is a contract drift `make docs-check` may or may not catch — add it regardless).

Check: new test in `tests/Facilitator.Tests/Artifacts/ArtifactUploadVerticalTests.cs` (or a sibling
`LinkArtifactReadTests.cs` if that file's fixture doesn't fit): a link child does
`request_upload` → `PUT /api/link/v1/artifacts/{id}/content` → `GET` the same route ⇒ 200 with the exact
bytes and the declared `content-type`, a foreign-team endpoint ⇒ 404, a `pending` row ⇒ 404. Then the
e2e-tier proof in `src/adapters/communication/telegram/test/e2e-local/media-e2e.mjs` (pattern:
`github-connector/test/e2e-local/worker-e2e.mjs`): fake Telegram server serving one
`message` update containing a `photo` + a deterministic fake kernel; assert the published
`provider.mention.v1` envelope's `sourceRefs[0].artifactId` resolves through
`GET /api/link/v1/artifacts/<id>/content` to the exact PNG bytes.

## Critical files & anchors

- `src/adapters/communication/mattermost/src/{handler.ts,normalize.ts,post-ledger.ts,index.ts}` — the
  manifest builder, the pure classification law, the idempotency-ledger-before-receipt law, the
  `run()`/health/replay composition. Mirror, don't paraphrase.
- `src/adapters/provider-core/src/adapter-face.ts` + `webhook-receiver.ts` — the only two ways a
  communication adapter gets inbound work (own listener ⇒ `normalizeWebhook: undefined`; kernel fan-out ⇒
  a real normalizer).
- `src/facilitator/Kernel/Integrations/ProviderEventStore.cs:304-311,456-545,902-963,1163-1180` — arm
  dispatch, mention arm, work-input/label/routing-intent helpers, `ReferenceOf`/`TargetOf`/`*Envelope`.
  Every kernel edit lands in or beside these regions.
- `src/facilitator/Kernel/Ai/CommandApplier.cs:116-144,349-449` — the chat Work-creation block that must
  learn rule-based team/capability selection and `sourceRefs`; the decode sink reaches Work creation only
  through it.
- `src/adapters/harnesses/adapter-sdk/src/artifacts.ts` + `src/facilitator/Kernel/Link/LinkEndpoints.Enroll.cs:214-245`
  — the upload client to reuse and the link-plane route pair that the new GET joins.

## Verification

1. Contract: `pnpm --filter @ofac/protocol test`;
   `dotnet run --project protocol/tools/Validate.Fixtures -c Release`.
   New-behavior proof: the three fixtures from Step 1 must be consumed by the index — deleting
   `valid/event.provider.mention.with-media.json` from `index.json` and re-running must be the only way to
   make that case stop running (i.e. it really is registered).
2. Kernel routing + media, from repo root:
   `dotnet test --filter "FullyQualifiedName~TelegramIngressTests"` ⇒
   concrete input: a mention event, `provider: "telegram"`, `channelOrResourceId: "-1001234567890"`,
   `messageId == rootThreadId == "4242"`, `labels: ["ofac:coding"]`, one `sourceRefs` item ⇒
   observable: exactly one `communication_bindings` row at
   `('telegram','-1001234567890','4242')`, one drained `coding.implement`
   `work.assign` to `link.<coding gw>` with `commandId` starting `work-assign-coding:`, and inside it
   `inputs.chat == "-1001234567890"`, `inputs.sourceRefs[0].kind == "chat.attachment"`. Same event with
   `labels: []` ⇒ the assign is the global `research.analyze` capability instead.
3. Adapter unit tier (no network): `pnpm -C src/adapters/communication/telegram test` — the classification,
   config, media, and handler laws listed in Steps 4-6, each with a fake `TelegramApi`.
4. End-to-end media, from `src/adapters/communication/telegram`:
   `node --test test/e2e-local/media-e2e.mjs` (or `pnpm test:e2e`, mirroring `github-connector`'s
   `vitest.e2e.config.ts` + `test:e2e` script) ⇒ concrete input: a fake Telegram server delivering one
   update whose `message.photo` is a 1×1 PNG, `message_id 4243`, `update_id 9001`;
   expected observable: a `request_upload` seen by the fake kernel, a stored blob whose sha256 equals the
   PNG's, and a published event whose `sourceRefs[0].artifactId` GETs back those exact bytes from
   `/api/link/v1/artifacts/{id}/content`, and whose `providerDeliveryId == "9001"`.
5. Live smoke (manual, env-gated, not a CI gate): with a real @BotFather token and a real supergroup id —
   `TELEGRAM_MODE=poll TELEGRAM_BOT_TOKEN=... TELEGRAM_CHATS=<chatId>:ofac:coding
   OFAC_ENDPOINT_ID=tg-adapter OFAC_TEAM=team-alpha OFAC_KERNEL_URL=http://localhost:8080
   OFAC_ENROLL_SECRET=enroll-dev node dist/bin/adapter.js run`, paste an image plus
   `@<bot> look at this` in the group ⇒ expected: the bot reacts or replies "On it.", a Work exists whose
   timeline has `artifact.stored` then `provider.attachment`, and
   `curl -H "Authorization: Bearer <link token>" $KERNEL/api/link/v1/artifacts/<id>/content` returns the
   pasted image bytes. Then in the same topic, send a follow-up reply with no mention ⇒ expected: no new
   Work, and a `provider.input` timeline row on the same Work.
6. Full gate: `make restore && make build && make test && make lint && make docs-check`, and the
   acceptance law `make example-slim-up` + `referencectl verify` from clean (the slim stack stays
   provider-less; this plan must not change that — Telegram lives behind the `telegram` compose profile and
   an opt-in `Providers__Telegram__Enabled=true`, so a clean slim run is unaffected).

## Assumptions & contingencies

- Tenant literal: the adapter sends `tenantOrOrganizationId = "api.telegram.org"` and
  `Providers:Telegram:Tenant` must equal it (`ProviderRegistry.Load` requires a non-empty `Tenant`, and the
  `provider_identities` lookup keys on it). If the implementer prefers a per-deployment instance name
  (e.g. `primary-bot`), it must be changed in **both** `OFAC_TENANT` and `Providers__Telegram__Tenant`
  together, or every mention is refused as unregistered.
- Bot id for the echo law: this plan sets `Providers__Telegram__BotUserId` explicitly rather than probing,
  because `ResolveBotIdAsync` reads a root-level `id` and `getMe` nests it under `result`. If a probe is
  wanted instead, the fallback is an adapter-side `getMe()` on startup whose `result.id` is published as
  `OFAC_BOT_USER_ID`-equivalent state — never kernel vendor code.
- `ChatLabelRouting` defaults to `false`, so no existing provider's routing changes. If a reviewer rejects a
  new option, the fallback is to apply chat label matching whenever `data.labels` is a non-empty array
  (self-selecting, no config), at the cost of the explicit opt-in.
- Cross-team inbound images: an inbound artifact row is owned by the adapter's enrollment team, so a
  rule-routed Work on another team reads it as 404 (Step 5 point 5). If that proves wrong in practice, the
  pre-decided fix is to add a work-lineage read to `GetForWorkloadAsync` (a workload may read a row whose
  `work_id` belongs to a Work in its team), not to relax team scoping.
- Inbound `text` for media-only messages is `"(attachment)"` so `event.provider.message.v1`'s
  `minLength: 1` holds. The alternative — a schema change to allow empty text — is explicitly out of scope.
- Outbound media caps (sendPhoto 10 MB / sendDocument 50 MB) and the `sendDocument`-fallback rule are
  provider-law, not config. If a deployment needs a higher ceiling, that is a connector-side
  local-file-server concern (`sendDocument` accepts a URL for PDF/ZIP only) — record it, don't build it.
- Long polling and webhooks are mutually exclusive on Telegram's side; `TELEGRAM_MODE` picks exactly one, and
  the adapter calls `deleteWebhook` in poll mode / `setWebhook` in webhook mode at startup. Two adapter
  instances in poll mode on one token will fight over `getUpdates` — one instance per bot token is the
  documented operational law.
