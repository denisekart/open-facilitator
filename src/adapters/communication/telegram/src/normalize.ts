// Pure inbound normalization (no I/O): a Telegram Update in, zero or one
// normalized provider events out, or the `unconfigured-mention` signal (the
// one-time onboarding reply index.ts sends — never an event). The whole
// flood control is the chat allowlist plus these three publishable cases:
//
//   bot-authored message    → message.v1 ECHO (the kernel records the
//                             delivery for the binding ledger, arms
//                             suppressed by bot id)
//   chat not admitted       → nothing (null), except a bot-mention →
//                             unconfigured-mention signal
//   @<bot username> mention → mention.v1
//   private chat (dm)       → message.v1, conversationKind dm
//   root in boundRootIds    → message.v1, conversationKind thread
//   anything else           → null (never publish every group post)
//
// Root law (hybrid): forum `message_thread_id` first, else a reply to a
// bound message (bot- or human-authored — the bound set is the authority),
// else the message opens its own conversation (root = message id, set
// explicitly for auditability; the kernel's ReferenceOf falls back
// rootThreadId ?? messageId anyway).
//
// Field law: provider="telegram"; providerDeliveryId = String(update_id)
// (Telegram's update id is the natural delivery id — unique per delivered
// update, so the kernel's (provider, provider_delivery_id) unique is the
// at-least-once dedupe); text = caption-else-text clamped to 8192 and never
// empty (a media-only post publishes "(attachment)" so the schema's
// minLength: 1 holds); labels omitted when empty, never []; sourceRefs
// omitted when none; service messages ignored.
import type {
  ArtifactRef,
  ConversationKind,
  ConversationReference,
  ProviderMentionEventV1Data,
  ProviderMessageEventV1Data,
  ProviderReactionEventV1Data,
} from "@ofac/protocol";

// Chat-domain vocabulary: the type names the PURPOSE; this adapter IS the
// Telegram→purpose mapping layer, so the provider token below is in-package
// mapping data.
export const MESSAGE_EVENT = "org.facilitator.event.provider.message.v1";
export const MENTION_EVENT = "org.facilitator.event.provider.mention.v1";
export const REACTION_EVENT = "org.facilitator.event.provider.reaction.v1";

// ── the Telegram wire subset we classify on ────────────────────────────────

export interface TgChat {
  id: number;
  type: "private" | "group" | "supergroup" | "channel";
  title?: string;
  username?: string;
}

export interface TgUser {
  id: number;
  is_bot?: boolean;
  username?: string;
  first_name?: string;
}

/** One photo size variant / document-ish attachment: the file triple every
 *  Telegram attachment carries. */
export interface TgFileRef {
  file_id: string;
  file_unique_id: string;
  file_size?: number;
  mime_type?: string;
  file_name?: string;
  width?: number;
  height?: number;
}

export interface TgMessage {
  message_id: number;
  chat: TgChat;
  from?: TgUser;
  date?: number;
  text?: string;
  caption?: string;
  message_thread_id?: number;
  is_topic_message?: boolean;
  reply_to_message?: TgMessage;
  photo?: TgFileRef[];
  document?: TgFileRef;
  voice?: TgFileRef;
  video?: TgFileRef;
  audio?: TgFileRef;
  animation?: TgFileRef;
  sticker?: TgFileRef;
  [key: string]: unknown;
}

export interface TgReactionEntry {
  type: string;
  emoji?: string;
  custom_emoji_id?: string;
}

export interface TgMessageReactionUpdated {
  chat: TgChat;
  message_id: number;
  user?: TgUser;
  date?: number;
  old_reaction: TgReactionEntry[];
  new_reaction: TgReactionEntry[];
  message_thread_id?: number;
}

export interface TgUpdate {
  update_id: number;
  message?: TgMessage;
  edited_message?: TgMessage;
  message_reaction?: TgMessageReactionUpdated;
  callback_query?: TgCallbackQuery;
  [key: string]: unknown;
}

/** An inline-button press (the C7 callback law). */
export interface TgCallbackQuery {
  id: string;
  from?: TgUser;
  /** The message carrying the button (chat identity + timestamp). */
  message?: { chat?: { id: number; type?: ChatType }; date?: number };
  data?: string;
}

export type ChatType = TgChat["type"];

// ── context + result ───────────────────────────────────────────────────────

export interface NormalizeContext {
  botUserId: string;
  botUsername: string;
  providerInstanceId: string;
  tenantOrOrganizationId: string;
  /** Cached chat-type lookup (updates carry the type; the ledger caches it
   *  so reaction updates, which name no type, still classify). */
  chatTypeFor(chatId: string): ChatType | undefined;
  /** The allowlist: null ⇒ the chat is inert. */
  admissionFor(chatId: string): { admitted: true; labels: string[] } | null;
  /** Roots the adapter participated in (own replies + created topic roots):
   *  the bound-thread classifier. */
  boundRootIds(): Set<string>;
  /** Artifact refs the media pipeline already uploaded for this message id. */
  mediaArtifactsFor(messageId: string): ArtifactRef[];
}

export type NormalizedEvent =
  | { type: typeof MENTION_EVENT; data: ProviderMentionEventV1Data }
  | { type: typeof MESSAGE_EVENT; data: ProviderMessageEventV1Data }
  | { type: typeof REACTION_EVENT; data: ProviderReactionEventV1Data };

export type NormalizeResult =
  | { kind: "events"; events: NormalizedEvent[] }
  /** An unconfigured chat mentioning the bot: index.ts answers once with
   *  the onboarding sendMessage; NEVER an event. */
  | { kind: "unconfigured-mention"; chatId: string; text: string }
  | { kind: "ignored" };

const TEXT_MAX = 8192;

function clamp(text: string): string {
  return text.length > TEXT_MAX ? text.slice(0, TEXT_MAX) : text;
}

/** `@token` at a word boundary (Telegram delivers no entities-based mention
 *  flag for user mentions; the mattermost word-boundary law, generalized to
 *  mid-text occurrences since Telegram mentions are rarely leading). */
export function mentionsToken(text: string, token: string): boolean {
  const escaped = token.replace(/[.*+?^${}()|[\]\\]/g, "\\$&");
  return new RegExp(`(^|[^\\w/])${escaped}(?![\\w-])`).test(text);
}

/** Every attachment of a message, normalized to the file triple. Photos:
 *  Telegram sends 3 size variants — the LARGEST (by declared file_size,
 *  else by area) is the one we take. */
export function collectAttachments(message: TgMessage): TgFileRef[] {
  const out: TgFileRef[] = [];
  if (message.photo !== undefined && message.photo.length > 0) {
    const largest = [...message.photo].sort(
      (a, b) => (a.file_size ?? a.width! * a.height!) - (b.file_size ?? b.width! * b.height!),
    )[message.photo.length - 1]!;
    out.push({ ...largest, mime_type: "image/jpeg" });
  }
  for (const key of ["document", "voice", "video", "audio", "animation", "sticker"] as const) {
    const attachment = message[key];
    if (attachment !== undefined && typeof attachment.file_id === "string") {
      out.push(attachment);
    }
  }
  return out;
}

/** Service messages (joins, topic edits, migrations): no text, no caption,
 *  no media, or a service marker field set. */
function isServiceMessage(message: TgMessage): boolean {
  const markers = [
    "new_chat_members",
    "left_chat_member",
    "new_chat_title",
    "new_chat_photo",
    "delete_chat_photo",
    "group_chat_created",
    "supergroup_chat_created",
    "channel_chat_created",
    "migrate_to_chat_id",
    "migrate_from_chat_id",
    "pinned_message",
    "successful_payment",
  ];
  return markers.some((marker) => message[marker] !== undefined);
}

/** The hybrid root law. Returns the root id + whether it is a BOUND (already
 *  open, adapter-known) conversation. Rule 1 (forum topic) wins over a
 *  reply; rule 2 honors a reply to any bound message, bot- or human-authored
 *  (the bound set is the authority for what the adapter participates in);
 *  rule 3 opens a new conversation rooted at the message itself. */
export function rootIdFor(
  message: TgMessage,
  ctx: NormalizeContext,
): { rootId: string; bound: boolean } {
  if (message.message_thread_id !== undefined) {
    return { rootId: String(message.message_thread_id), bound: true };
  }
  const replied = message.reply_to_message;
  if (replied !== undefined) {
    const repliedId = String(replied.message_id);
    if (ctx.boundRootIds().has(repliedId)) return { rootId: repliedId, bound: true };
    // A reply into an unknown conversation continues THAT message's topic
    // when one exists (forum replies carry the thread id on the parent).
    if (replied.message_thread_id !== undefined) {
      return { rootId: String(replied.message_thread_id), bound: true };
    }
  }
  return { rootId: String(message.message_id), bound: false };
}

function kindFor(chatType: ChatType | undefined, bound: boolean): ConversationKind {
  if (chatType === "private") return "dm";
  return bound ? "thread" : "channel";
}

function reference(
  ctx: NormalizeContext,
  authorId: string,
  chatId: string,
  kind: ConversationKind,
  messageId: string,
  rootId: string | undefined,
): ConversationReference {
  return {
    provider: "telegram",
    providerInstanceId: ctx.providerInstanceId,
    tenantOrOrganizationId: ctx.tenantOrOrganizationId,
    channelOrResourceId: chatId,
    conversationKind: kind,
    authorProviderAccountId: authorId,
    messageId,
    ...(rootId === undefined ? {} : { rootThreadId: rootId, parentMessageId: rootId }),
  };
}

/** Caption-else-text, clamped; a media-only message publishes
 *  "(attachment)" so the frozen schema's minLength: 1 holds. */
function textOf(message: TgMessage): string | undefined {
  const text = clamp(message.caption ?? message.text ?? "");
  if (text !== "") return text;
  return collectAttachments(message).length > 0 ? "(attachment)" : undefined;
}

function messageEvent(
  message: TgMessage,
  update: TgUpdate,
  ctx: NormalizeContext,
  data: {
    kind: ConversationKind;
    rootId: string;
    text: string;
    labels: string[];
  },
): NormalizedEvent {
  const chatId = String(message.chat.id);
  const refs = ctx.mediaArtifactsFor(String(message.message_id));
  const labels = data.labels;
  return {
    type: MESSAGE_EVENT,
    data: {
      reference: reference(
        ctx,
        String(message.from?.id ?? message.chat.id),
        chatId,
        data.kind,
        String(message.message_id),
        data.rootId,
      ),
      provider: "telegram",
      providerDeliveryId: String(update.update_id),
      actorProviderAccountId: String(message.from?.id ?? message.chat.id),
      text: data.text,
      payload: update as unknown as Record<string, unknown>,
      ...(labels.length > 0 ? { labels } : {}),
      ...(refs.length > 0 ? { sourceRefs: refs } : {}),
    },
  };
}

/** Classify one Telegram Update. Pure: the media pipeline ran BEFORE this
 *  call (its refs arrive through ctx.mediaArtifactsFor). */
export function normalizeUpdate(update: TgUpdate, ctx: NormalizeContext): NormalizeResult {
  const message = update.message ?? update.edited_message;
  if (message !== undefined) {
    const result = normalizeMessage(message, update, ctx);
    if (result !== null) return { kind: "events", events: [result] };
    const chatId = String(message.chat.id);
    const text = message.caption ?? message.text ?? "";
    if (
      ctx.admissionFor(chatId) === null &&
      text !== "" &&
      mentionsToken(text, `@${ctx.botUsername}`)
    ) {
      return { kind: "unconfigured-mention", chatId, text };
    }
    return { kind: "ignored" };
  }
  const reaction = update.message_reaction;
  if (reaction !== undefined) {
    const event = normalizeReactionUpdate(reaction, update, ctx);
    return event === null ? { kind: "ignored" } : { kind: "events", events: [event] };
  }
  const callback = update.callback_query;
  if (callback !== undefined) {
    // The C7 callback law: an inline-button press becomes the presser's own
    // message carrying the button data verbatim - the slash plane and the
    // identity gate then run their ordinary law, and the press carries the
    // presser's proven identity. The `data` is the whole text: an
    // unrecognized payload is an unbound free-text message, never a crash.
    const event = normalizeCallbackQuery(callback, update, ctx);
    return event === null ? { kind: "ignored" } : { kind: "events", events: [event] };
  }
  // message_reaction_count, chat_member, …: ignored.
  return { kind: "ignored" };
}

/** The C7 inline-button press -> a message.v1 from the presser. */
export function normalizeCallbackQuery(
  callback: TgCallbackQuery,
  update: TgUpdate,
  ctx: NormalizeContext,
): NormalizedEvent | null {
  const chatId = String(callback.message?.chat?.id ?? "");
  if (chatId === "" || callback.from === undefined) return null;
  if (ctx.admissionFor(chatId) === null) return null;
  const kind = kindFor(callback.message?.chat?.type ?? ctx.chatTypeFor(chatId), false);
  const messageId = `callback:${callback.id}`;
  return {
    type: MESSAGE_EVENT,
    data: {
      reference: reference(ctx, String(callback.from.id), chatId, kind, messageId, undefined),
      provider: "telegram",
      providerDeliveryId: messageId,
      actorProviderAccountId: String(callback.from.id),
      text: clamp(callback.data ?? ""),
      payload: update as unknown as Record<string, unknown>,
    },
  };
}

function normalizeMessage(
  message: TgMessage,
  update: TgUpdate,
  ctx: NormalizeContext,
): NormalizedEvent | null {
  const chatId = String(message.chat.id);
  const text = textOf(message);

  // (a) Echo: the bot's own post — always message.v1, kernel-side
  // suppression owns it (the stored event is how the binding ledger learns
  // our provider message id).
  if (message.from?.id !== undefined && String(message.from.id) === ctx.botUserId) {
    if (text === undefined) return null;
    const { rootId } = rootIdFor(message, ctx);
    return messageEvent(message, update, ctx, {
      kind: kindFor(message.chat.type ?? ctx.chatTypeFor(chatId), true),
      rootId,
      text,
      labels: ctx.admissionFor(chatId)?.labels ?? [],
    });
  }

  // Service messages and contentless posts never classify.
  if (isServiceMessage(message) || text === undefined) return null;

  const admission = ctx.admissionFor(chatId);
  if (admission === null) return null;

  const { rootId, bound } = rootIdFor(message, ctx);
  const kind = kindFor(message.chat.type ?? ctx.chatTypeFor(chatId), bound);

  // (b) Explicit @-mention of the bot wins over any chat scoping.
  if (mentionsToken(text, `@${ctx.botUsername}`)) {
    return {
      type: MENTION_EVENT,
      data: {
        reference: reference(
          ctx,
          String(message.from?.id ?? 0),
          chatId,
          kind,
          String(message.message_id),
          rootId,
        ),
        provider: "telegram",
        providerDeliveryId: String(update.update_id),
        actorProviderAccountId: String(message.from?.id ?? 0),
        text,
        payload: update as unknown as Record<string, unknown>,
        ...(admission.labels.length > 0 ? { labels: admission.labels } : {}),
        ...(() => {
          const refs = ctx.mediaArtifactsFor(String(message.message_id));
          return refs.length > 0 ? { sourceRefs: refs } : {};
        })(),
      },
    };
  }

  // (c) DMs with the bot and follow-ups in a conversation the adapter joined
  // — and nothing else. The allowlist + these cases is the flood control.
  if (message.chat.type === "private") {
    return messageEvent(message, update, ctx, {
      kind: "dm",
      rootId,
      text,
      labels: admission.labels,
    });
  }
  if (bound) {
    return messageEvent(message, update, ctx, {
      kind,
      rootId,
      text,
      labels: admission.labels,
    });
  }
  return null;
}

function normalizeReactionUpdate(
  reaction: TgMessageReactionUpdated,
  update: TgUpdate,
  ctx: NormalizeContext,
): NormalizedEvent | null {
  // The emojiName law: the first plain-emoji entry of the NEW reaction set;
  // an empty set (un-react) or a custom-emoji-only set is ignored.
  const emoji = reaction.new_reaction.find(
    (entry) => entry.type === "emoji" && typeof entry.emoji === "string" && entry.emoji !== "",
  );
  if (emoji?.emoji === undefined) return null;
  if (reaction.user === undefined) return null;
  const chatId = String(reaction.chat.id);
  if (ctx.admissionFor(chatId) === null) return null;
  const messageId = String(reaction.message_id);
  const bound = ctx.boundRootIds().has(messageId);
  const chatType = reaction.chat.type ?? ctx.chatTypeFor(chatId);
  return {
    type: REACTION_EVENT,
    data: {
      reference: reference(
        ctx,
        String(reaction.user.id),
        chatId,
        chatType === "private" ? "dm" : bound ? "thread" : "channel",
        messageId,
        messageId,
      ),
      provider: "telegram",
      providerDeliveryId: String(update.update_id),
      actorProviderAccountId: String(reaction.user.id),
      emojiName: emoji.emoji.slice(0, 64),
      payload: update as unknown as Record<string, unknown>,
    },
  };
}
