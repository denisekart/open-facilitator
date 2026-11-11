// The link command dispatcher: kernel → adapter side effects over the
// Telegram Bot API, under the idempotency law — the chat-ledger `commands`
// table is consulted BEFORE any Telegram mutation, so a delivery replay
// answers `accepted` with the stored provider message id and never
// double-posts. After a successful reply/update the bot's own message.v1
// echo rides back through ctx.publishEvent: the kernel stores it
// (provider_events dedupes by providerDeliveryId, so a later poll of the
// same message is a no-op) and records the outbound provider message id
// into communication_bindings — which is what makes a later human reply
// resolvable by rule 2 of the root law.
//
// Deviations from the mattermost reference, recorded as T1/T2:
//  T1 — an unknown DM target is `rejected: no-dm-channel`. Mattermost
//       creates the bot-DM channel on demand; Telegram cannot open a bot DM
//       before the human has /started, so an honest rejection beats a
//       guaranteed-400 retry loop.
//  T2 — reactions ride the ACK_EMOJI table: Telegram's setMessageReaction
//       allowlist is closed and does NOT contain `white_check_mark`/`eye`
//       as names, so the Slack-style names the kernel sends must map to
//       literal emoji (unmapped ⇒ 👀).
import type {
  ArtifactRef,
  CapabilityDescriptor,
  CapabilityManifestV1,
  CommunicationAcknowledgeCommandV1Data,
  CommunicationReactCommandV1Data,
  CommunicationPostCommandV1Data,
  CommunicationReplyCommandV1Data,
  CommunicationReplyCommandV1DataButton,
  CommunicationUpdateCommandV1Data,
  ConversationKind,
  ConversationReference,
  EnvelopeCommand,
  ProviderMessageEventV1Data,
} from "@ofac/protocol";
import {
  buildEvent,
  dataschemaFor,
  type CommandContext,
  type CommandOutcome,
  type LinkHandler,
  type LinkLogger,
} from "@ofac/workspace-link";
import { MESSAGE_EVENT } from "./normalize.js";
import { type ChatLedger } from "./chat-ledger.js";
import {
  TgError,
  type InlineKeyboardButton,
  type ReplyMarkup,
  type SendMessageParams,
  type TelegramApi,
} from "./tg-client.js";

export const CMD_REPLY = "org.facilitator.command.communication.reply.v1";
export const CMD_REACT = "org.facilitator.command.communication.react.v1";
export const CMD_UPDATE = "org.facilitator.command.communication.update.v1";
export const CMD_ACK = "org.facilitator.command.communication.acknowledge.v1";
export const CMD_POST = "org.facilitator.command.communication.post.v1";

export const COMMUNICATION_COMMANDS: readonly string[] = [
  CMD_REPLY,
  CMD_REACT,
  CMD_UPDATE,
  CMD_ACK,
  CMD_POST,
];

/** The provider-side identity the echo events are authored under. */
export interface AdapterIdentity {
  botUserId: string;
  providerInstanceId: string;
  tenantOrOrganizationId: string;
}

/** Outbound media download seam (media.ts fetchArtifactContent in
 *  production; a fake in tests). */
export interface ArtifactSource {
  download(artifactId: string): Promise<Uint8Array>;
}

export interface HandlerDeps {
  api: TelegramApi;
  state: ChatLedger;
  identity: AdapterIdentity;
  media: ArtifactSource;
  logger: LinkLogger;
}

/** Telegram's closed reaction allowlist, keyed by the Slack-style names the
 *  kernel sends (ProviderEventStore ack names). T2. */
export const ACK_EMOJI: Record<string, string> = {
  white_check_mark: "✅",
  eye: "👀",
  "+1": "👍",
  thumbsup: "👍",
  "-1": "👎",
  thumbsdown: "👎",
  tada: "🎉",
  fire: "🔥",
};

/** sendMessage text law. */
const TEXT_MAX = 4096;
/** sendPhoto/sendDocument caption law. */
const CAPTION_MAX = 1024;
/** sendPhoto new-upload law; sendDocument law. */
const PHOTO_MAX_BYTES = 10 * 1024 * 1024;
const DOCUMENT_MAX_BYTES = 50 * 1024 * 1024;

/** The capability manifest this adapter advertises (POST /api/link/v1/capabilities). */
export function communicationManifest(team: string): CapabilityManifestV1 {
  const descriptor = (id: string): CapabilityDescriptor => ({
    id,
    version: 1,
    execution: "synchronous",
    inputSchema: dataschemaFor(id),
  });
  // The tuple type demands a first element; the law is exactly these four.
  return {
    apiVersion: "facilitator.framework/v1",
    kind: "CapabilityManifest",
    ownerTeam: team,
    capabilities: [
      descriptor(CMD_REPLY),
      ...COMMUNICATION_COMMANDS.filter((id) => id !== CMD_REPLY).map(descriptor),
    ],
  };
}

function dataOf<T>(cmd: EnvelopeCommand): T | undefined {
  const data = cmd.data;
  if (typeof data !== "object" || data === null) return undefined;
  for (const key of ["commandId", "idempotencyKey", "target"]) {
    if ((data as Record<string, unknown>)[key] === undefined) return undefined;
  }
  return data as unknown as T;
}

/** The provider object a command acts ON: the message the kernel targets.
 *  Copied from mattermost's targetPostId. */
function targetPostId(target: {
  parentMessageId?: string;
  subjectRef?: string;
  rootThreadId?: string;
}): string | undefined {
  return target.parentMessageId ?? target.subjectRef ?? target.rootThreadId;
}

/** Defensive id law: a bare numeric id, else a stripped
 *  `provider/telegram/<id>` subject; anything else is unresolvable. */
function parseProviderId(raw: string | undefined): number | undefined {
  if (raw === undefined) return undefined;
  const stripped = raw.startsWith("provider/telegram/")
    ? raw.slice("provider/telegram/".length)
    : raw;
  const id = Number(stripped);
  return Number.isInteger(id) && id > 0 ? id : undefined;
}

/**
 * Where a command posts: an explicit chat, else — for the `dm` target shape
 * the kernel emits as DmTarget(provider, accountId) — the private chat
 * recorded when that user first messaged the bot. Unknown ⇒ T1 rejection.
 */
function resolveChat(
  state: ChatLedger,
  target: { channelOrResourceId?: string; actorProviderAccountId?: string },
): string | undefined {
  if (target.channelOrResourceId !== undefined && target.channelOrResourceId !== "") {
    return target.channelOrResourceId;
  }
  if (target.actorProviderAccountId !== undefined) {
    return state.getKv(`dm:${target.actorProviderAccountId}`);
  }
  return undefined;
}

/** The 4096 law: split on newline boundaries into ≤ 4096 chunks (a single
 *  oversized line hard-splits — sendMessage would otherwise 400). */
export function chunkText(text: string, max = TEXT_MAX): string[] {
  const chunks: string[] = [];
  let current = "";
  for (const line of text.split("\n")) {
    const pieces: string[] = [];
    let rest = line;
    while (rest.length > max) {
      pieces.push(rest.slice(0, max));
      rest = rest.slice(max);
    }
    pieces.push(rest);
    for (const piece of pieces) {
      const candidate = current === "" ? piece : `${current}\n${piece}`;
      if (candidate.length <= max) {
        current = candidate;
      } else {
        if (current !== "") chunks.push(current);
        current = piece;
      }
    }
  }
  if (current !== "") chunks.push(current);
  return chunks;
}

function clampCaption(text: string): string {
  return text.length > CAPTION_MAX ? text.slice(0, CAPTION_MAX) : text;
}

function clampText(text: string): string {
  return text.length > 8192 ? text.slice(0, 8192) : text;
}

function fileNameFor(ref: ArtifactRef): string {
  const ext =
    ref.mediaType === "image/png"
      ? "png"
      : ref.mediaType === "image/jpeg"
        ? "jpg"
        : ref.mediaType === "application/pdf"
          ? "pdf"
          : "bin";
  return `artifact-${ref.artifactId.slice(0, 8)}.${ext}`;
}

export function createAdapterHandler(deps: HandlerDeps): LinkHandler {
  const { api, state, identity, media, logger } = deps;

  const publishEcho = async (
    ctx: CommandContext,
    chatId: string,
    tgMessageId: number,
    rootId: string | undefined,
    text: string,
    kind: ConversationKind,
  ): Promise<void> => {
    const messageId = String(tgMessageId);
    const reference: ConversationReference = {
      provider: "telegram",
      providerInstanceId: identity.providerInstanceId,
      tenantOrOrganizationId: identity.tenantOrOrganizationId,
      channelOrResourceId: chatId,
      conversationKind: kind,
      authorProviderAccountId: identity.botUserId,
      messageId,
      ...(rootId === undefined ? {} : { rootThreadId: rootId, parentMessageId: rootId }),
    };
    const data: ProviderMessageEventV1Data = {
      reference,
      provider: "telegram",
      providerDeliveryId: messageId,
      actorProviderAccountId: identity.botUserId,
      text: clampText(text === "" ? "(attachment)" : text),
      payload: { echoOf: String(ctx.envelope.id) },
    };
    await ctx.publishEvent(
      buildEvent(MESSAGE_EVENT, {
        source: `urn:ofac:provider:telegram:${identity.tenantOrOrganizationId}`,
        subject: `provider/telegram/${messageId}`,
        data: data as unknown as Record<string, unknown>,
        causationid: ctx.envelope.id,
      }),
    );
  };

  /** The threading law: a resolved id recorded as a forum topic rides
   *  message_thread_id; any other resolvable id rides reply_parameters; an
   *  unresolvable target posts to the chat unthreaded. */
  const threadingFor = (
    chatId: string,
    target: { parentMessageId?: string; subjectRef?: string; rootThreadId?: string },
  ): Pick<SendMessageParams, "message_thread_id" | "reply_parameters"> => {
    const resolved = parseProviderId(targetPostId(target));
    if (resolved === undefined) return {};
    if (state.isTopic(chatId, String(resolved))) return { message_thread_id: resolved };
    return {
      reply_parameters: { message_id: resolved, allow_sending_without_reply: true },
    };
  };

  /**
   * The C7 button law. Telegram cannot author a message AS the presser, so
   * every button routes the decision through a message the human sends:
   * - `prefill` -> a REPLY-keyboard button: the press sends the exact text
   *   as the human's own message (proven authorship, zero extra surface).
   * - `value` -> an inline callback button (64-byte provider law): the
   *   callback_query carries the presser's proven identity; the adapter
   *   re-submits the data as that user's message (normalize.ts).
   * Mixed or oversized => no markup at all: the text always stands alone,
   * a half-rendered decision row is worse than none.
   */
  function markupFor(
    buttons: CommunicationReplyCommandV1DataButton[] | undefined,
  ): ReplyMarkup | undefined {
    if (buttons === undefined || buttons.length === 0 || buttons.length > 3) return undefined;
    const prefills = buttons.flatMap((b) => (b.prefill !== undefined ? [b.prefill] : []));
    const values = buttons.flatMap((b) => (b.value !== undefined ? [b.value] : []));
    if (prefills.length + values.length !== buttons.length) return undefined;
    if (prefills.length > 0 && values.length > 0) return undefined;
    if (prefills.length > 0) {
      return {
        keyboard: prefills.map((text) => [{ text }]),
        resize_keyboard: true,
        one_time_keyboard: true,
      };
    }

    const rows: InlineKeyboardButton[][] = [];
    for (const data of values) {
      if (new TextEncoder().encode(data).length > 64) return undefined;
      rows.push([{ text: String(buttons[rows.length]?.label ?? data), callback_data: data }]);
    }

    return { inline_keyboard: rows };
  }

  /** The shared send core of reply + post: text chunks and media, the
   *  4096/1024/50MB laws, every download BEFORE any post, markup on the
   *  LAST text chunk. Returns the sent message ids or a rejection reason. */
  async function deliver(
    chatId: string,
    text: string,
    artifacts: ArtifactRef[],
    threading: Pick<SendMessageParams, "message_thread_id" | "reply_parameters">,
    markup: SendMessageParams["reply_markup"],
  ): Promise<{ sentIds: number[] } | { reason: string }> {
    const sentIds: number[] = [];
    try {
      if (artifacts.length === 0) {
        const chunks = chunkText(text);
        for (const [index, chunk] of chunks.entries()) {
          const sent = await api.sendMessage({
            chat_id: chatId,
            text: chunk,
            ...threading,
            ...(index === chunks.length - 1 && markup !== undefined ? { reply_markup: markup } : {}),
          } as SendMessageParams);
          sentIds.push(sent.message_id);
        }
      } else {
        const payloads: { ref: ArtifactRef; bytes: Uint8Array }[] = [];
        for (const ref of artifacts) {
          try {
            payloads.push({ ref, bytes: await media.download(ref.artifactId) });
          } catch {
            return { reason: "artifact-unavailable" };
          }
        }

        for (const [index, { ref, bytes }] of payloads.entries()) {
          const asPhoto = ref.mediaType.startsWith("image/") && ref.size <= PHOTO_MAX_BYTES;
          if (!asPhoto && ref.size > DOCUMENT_MAX_BYTES) {
            return { reason: "artifact-too-large" };
          }
          const params = {
            chat_id: chatId,
            media: { bytes, fileName: fileNameFor(ref) },
            ...(index === 0 && text !== "" ? { caption: clampCaption(text) } : {}),
            ...(index === payloads.length - 1 && markup !== undefined ? { reply_markup: markup } : {}),
            ...threading,
          };
          try {
            const sent = asPhoto ? await api.sendPhoto(params) : await api.sendDocument(params);
            sentIds.push(sent.message_id);
          } catch (error) {
            if (error instanceof TgError && error.status === 413) {
              return { reason: "artifact-too-large" };
            }
            throw error;
          }
        }
      }
      return { sentIds };
    } catch (error) {
      if (error instanceof TgError && error.status === 413) {
        return { reason: "artifact-too-large" };
      }
      logger.error("send failed", { error: String(error) });
      return { reason: "telegram-send-failed" };
    }
  }

  return {
    async onCommand(cmd: EnvelopeCommand, ctx: CommandContext): Promise<CommandOutcome> {
      switch (cmd.type) {
        case CMD_REPLY: {
          const data = dataOf<CommunicationReplyCommandV1Data>(cmd);
          if (data === undefined) return { kind: "rejected", reason: "malformed-reply-data" };
          const prior = state.findCommand(data.idempotencyKey);
          if (prior !== undefined) return { kind: "accepted" };
          const chatId = resolveChat(state, data.target);
          if (chatId === undefined) return { kind: "rejected", reason: "no-dm-channel" };
          const threading = threadingFor(chatId, data.target);
          const rootId = data.target.rootThreadId ?? data.target.parentMessageId ?? "";
          const artifacts = data.artifacts ?? [];
          const outcome = await deliver(
            chatId,
            data.text,
            artifacts,
            threading,
            markupFor(data.buttons),
          );
          if ("reason" in outcome) return { kind: "rejected", reason: outcome.reason };
          {
            const sentIds = outcome.sentIds;
            if (sentIds.length === 0) return { kind: "rejected", reason: "empty-reply" };
            // Ledger BEFORE the receipt: the provider object id rides the
            // idempotency key; >1 sent object ⇒ a JSON array of ids.
            const lastMessageId = sentIds[sentIds.length - 1]!;
            state.recordCommand({
              idempotencyKey: data.idempotencyKey,
              providerObjectId:
                sentIds.length > 1 ? JSON.stringify(sentIds.map(String)) : String(lastMessageId),
              chatId,
              rootId,
              kind: artifacts.length === 0 ? "message" : "photo",
              createdAt: Date.now(),
            });
            await publishEcho(
              ctx,
              chatId,
              lastMessageId,
              rootId === "" ? undefined : rootId,
              data.text,
              data.target.conversationKind,
            );
            logger.info("reply sent", { chatId, commandId: data.commandId });
            return { kind: "accepted" };
          }
        }
        case CMD_POST: {
          // The B4 law: a team notice posts top-level in a dm or a channel —
          // no thread costume. The same send core, no threading, the C7
          // buttons riding the last chunk.
          const data = dataOf<CommunicationPostCommandV1Data>(cmd);
          if (data === undefined) return { kind: "rejected", reason: "malformed-post-data" };
          if (data.target.conversationKind !== "dm" && data.target.conversationKind !== "channel") {
            return { kind: "rejected", reason: "unsupported-conversation-kind" };
          }
          if (state.findCommand(data.idempotencyKey) !== undefined) {
            return { kind: "accepted" };
          }
          const chatId = resolveChat(state, data.target);
          if (chatId === undefined) return { kind: "rejected", reason: "no-dm-channel" };
          const outcome = await deliver(
            chatId,
            data.text,
            data.artifacts ?? [],
            {},
            markupFor(data.buttons),
          );
          if ("reason" in outcome) return { kind: "rejected", reason: outcome.reason };
          const sentIds = outcome.sentIds;
          if (sentIds.length === 0) return { kind: "rejected", reason: "empty-post" };
          const lastMessageId = sentIds[sentIds.length - 1]!;
          state.recordCommand({
            idempotencyKey: data.idempotencyKey,
            providerObjectId:
              sentIds.length > 1 ? JSON.stringify(sentIds.map(String)) : String(lastMessageId),
            chatId,
            rootId: "",
            kind: (data.artifacts ?? []).length === 0 ? "message" : "photo",
            createdAt: Date.now(),
          });
          await publishEcho(
            ctx,
            chatId,
            lastMessageId,
            undefined,
            data.text,
            data.target.conversationKind,
          );
          logger.info("post sent", { chatId, commandId: data.commandId });
          return { kind: "accepted" };
        }
        case CMD_REACT: {
          const data = dataOf<CommunicationReactCommandV1Data>(cmd);
          if (data === undefined) return { kind: "rejected", reason: "malformed-react-data" };
          if (state.findCommand(data.idempotencyKey) !== undefined) {
            return { kind: "accepted" };
          }
          const chatId = resolveChat(state, data.target);
          if (chatId === undefined) return { kind: "rejected", reason: "no-dm-channel" };
          const messageId = parseProviderId(targetPostId(data.target));
          if (messageId === undefined) {
            return { kind: "rejected", reason: "target-missing-post" };
          }
          const emoji = ACK_EMOJI[data.emojiName] ?? ACK_EMOJI["eye"]!;
          try {
            await api.setMessageReaction({
              chat_id: chatId,
              message_id: messageId,
              reaction: [{ type: "emoji", emoji }],
            });
          } catch (error) {
            if (error instanceof TgError && error.status === 413) {
              return { kind: "rejected", reason: "artifact-too-large" };
            }
            logger.error("react failed", { error: String(error), commandId: data.commandId });
            return { kind: "rejected", reason: "telegram-send-failed" };
          }
          state.recordCommand({
            idempotencyKey: data.idempotencyKey,
            providerObjectId: `reaction:${chatId}:${messageId}:${emoji}`,
            chatId,
            rootId: String(messageId),
            kind: "reaction",
            createdAt: Date.now(),
          });
          logger.info("reaction set", { chatId, messageId, commandId: data.commandId });
          return { kind: "accepted" };
        }
        case CMD_UPDATE: {
          const data = dataOf<CommunicationUpdateCommandV1Data>(cmd);
          if (data === undefined) return { kind: "rejected", reason: "malformed-update-data" };
          if (state.findCommand(data.idempotencyKey) !== undefined) {
            return { kind: "accepted" };
          }
          const chatId = resolveChat(state, data.target);
          if (chatId === undefined) return { kind: "rejected", reason: "no-dm-channel" };
          const messageId = parseProviderId(data.target.parentMessageId ?? data.target.subjectRef);
          if (messageId === undefined) {
            return { kind: "rejected", reason: "target-missing-post" };
          }
          // The update law: only a message the ledger proves the adapter
          // created may be edited.
          if (!state.isAdapterMessage(String(messageId))) {
            return { kind: "rejected", reason: "not-adapter-message" };
          }
          try {
            await api.editMessageText({ chat_id: chatId, message_id: messageId, text: data.text });
          } catch (error) {
            logger.error("update failed", { error: String(error), commandId: data.commandId });
            return { kind: "rejected", reason: "telegram-send-failed" };
          }
          const rootId = data.target.rootThreadId ?? String(messageId);
          state.recordCommand({
            idempotencyKey: data.idempotencyKey,
            providerObjectId: String(messageId),
            chatId,
            rootId,
            kind: "edit",
            createdAt: Date.now(),
          });
          await publishEcho(
            ctx,
            chatId,
            messageId,
            rootId,
            data.text,
            data.target.conversationKind,
          );
          logger.info("message edited", { chatId, messageId, commandId: data.commandId });
          return { kind: "accepted" };
        }
        case CMD_ACK: {
          const data = dataOf<CommunicationAcknowledgeCommandV1Data>(cmd);
          if (data === undefined) return { kind: "rejected", reason: "malformed-acknowledge-data" };
          // No provider side effect: the ledger check is the whole law (a
          // replayed ack stays a no-op accepted, same as the first pass).
          void state.findCommand(data.idempotencyKey);
          return { kind: "accepted" };
        }
        default:
          return { kind: "unsupported" };
      }
    },
  };
}
