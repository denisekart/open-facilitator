// Pure inbound normalization (no I/O): a Mattermost WS `posted` /
// `reaction_added` payload in, one normalized provider event out — or null
// when the post must be IGNORED (Plan 22 D1 classification law):
//
//   bot-authored            → message.v1 (echo; the kernel suppresses arm
//                             effects by botUserId but keeps the delivery
//                             so the binding ledger records our post id)
//   system post             → ignored
//   @mention of the bot     → mention.v1
//   DM channel with the bot → message.v1
//   thread rooted at a post the adapter created (ledger) → message.v1
//   anything else           → ignored (never normalize every public post)
import type {
  ConversationReference,
  ProviderMentionEventV1Data,
  ProviderMessageEventV1Data,
  ProviderReactionEventV1Data,
} from "@ofac/protocol";

// Chat-domain vocabulary: the type names the PURPOSE; this adapter IS the
// Mattermost→purpose mapping layer, so the provider token below is in-package
// mapping data.
export const MENTION_EVENT = "org.facilitator.event.provider.mention.v1";
export const REACTION_EVENT = "org.facilitator.event.provider.reaction.v1";
export const MESSAGE_EVENT = "org.facilitator.event.provider.message.v1";

/** The wire subset of a Mattermost Post we classify on. */
export interface MmPost {
  id: string;
  channel_id: string;
  user_id: string;
  message: string;
  root_id?: string;
  create_at?: number;
  type?: string;
  props?: Record<string, unknown>;
}

/** The wire subset of a Mattermost Reaction we classify on. */
export interface MmReaction {
  id: string;
  user_id: string;
  post_id: string;
  emoji_name: string;
  create_at?: number;
}

export type ChannelType = "O" | "P" | "D";

export interface NormalizeContext {
  botUserId: string;
  botUsername: string;
  /** Configured mention token, e.g. "@facilitator". */
  botMention: string;
  providerInstanceId: string;
  tenantOrOrganizationId: string;
  channelTypeFor(channelId: string): Promise<ChannelType | undefined>;
  /** Post ids the adapter itself created (post-ledger): bound thread roots. */
  boundRootIds(): Set<string>;
}

export type NormalizedEvent =
  | { type: typeof MENTION_EVENT; data: ProviderMentionEventV1Data }
  | { type: typeof MESSAGE_EVENT; data: ProviderMessageEventV1Data }
  | { type: typeof REACTION_EVENT; data: ProviderReactionEventV1Data };

export interface NormalizeOptions {
  /** Startup replay suppresses bound-thread classification (mentions + DMs only). */
  replay?: boolean;
}

const TEXT_MAX = 8192;

function clamp(text: string): string {
  return text.length > TEXT_MAX ? text.slice(0, TEXT_MAX) : text;
}

/** `@token` followed by whitespace or end-of-message (not a prefix of a longer handle). */
function mentions(text: string, token: string): boolean {
  return text === token || text.startsWith(`${token} `) || text.startsWith(`${token}\n`);
}

function reference(
  ctx: NormalizeContext,
  authorId: string,
  channelId: string,
  kind: ConversationReference["conversationKind"],
  postId: string,
  rootId: string | undefined,
): ConversationReference {
  const ref: ConversationReference = {
    provider: "mattermost",
    providerInstanceId: ctx.providerInstanceId,
    tenantOrOrganizationId: ctx.tenantOrOrganizationId,
    channelOrResourceId: channelId,
    conversationKind: kind,
    authorProviderAccountId: authorId,
    messageId: postId,
    ...(rootId !== undefined && rootId !== ""
      ? { rootThreadId: rootId, parentMessageId: rootId }
      : {}),
  };
  return ref;
}

function kindFor(
  channelType: ChannelType | undefined,
  rootId: string | undefined,
): ConversationReference["conversationKind"] {
  if (channelType === "D") return "dm";
  return rootId !== undefined && rootId !== "" ? "thread" : "channel";
}

/**
 * Classify a `posted` payload. `raw` is the verbatim parsed post object
 * (rides the event as `payload`); pass the parsed `post` for both in
 * production (the WS `data.post` is a JSON string of the full Post).
 */
export async function normalizePost(
  post: MmPost,
  raw: Record<string, unknown>,
  ctx: NormalizeContext,
  options: NormalizeOptions = {},
): Promise<NormalizedEvent | null> {
  const rootId = post.root_id !== undefined && post.root_id !== "" ? post.root_id : undefined;
  const text = clamp(post.message);

  // (a) Echo: the bot's own post — always message.v1, kernel-side suppression
  // owns it (the stored event is how the binding ledger learns our post id).
  if (post.user_id === ctx.botUserId) {
    if (text === "") return null;
    return {
      type: MESSAGE_EVENT,
      data: {
        reference: reference(
          ctx,
          post.user_id,
          post.channel_id,
          kindFor(await ctx.channelTypeFor(post.channel_id), rootId),
          post.id,
          rootId,
        ),
        provider: "mattermost",
        providerDeliveryId: post.id,
        actorProviderAccountId: post.user_id,
        text,
        payload: raw,
      },
    };
  }

  // System posts (joins, additions, MM types) are never conversations.
  if (post.type !== undefined && post.type !== "") return null;
  if (text === "") return null;

  // (b) Explicit mention wins over any channel scoping.
  if (mentions(text, ctx.botMention) || mentions(text, `@${ctx.botUsername}`)) {
    return {
      type: MENTION_EVENT,
      data: {
        reference: reference(
          ctx,
          post.user_id,
          post.channel_id,
          kindFor(await ctx.channelTypeFor(post.channel_id), rootId),
          post.id,
          rootId,
        ),
        provider: "mattermost",
        providerDeliveryId: post.id,
        actorProviderAccountId: post.user_id,
        text,
        payload: raw,
      },
    };
  }

  // (c) Follow-ups only from a DM with the bot or a thread we joined — and
  // never from the startup replay window.
  const channelType = await ctx.channelTypeFor(post.channel_id);
  if (channelType === "D") {
    return {
      type: MESSAGE_EVENT,
      data: {
        reference: reference(ctx, post.user_id, post.channel_id, "dm", post.id, rootId),
        provider: "mattermost",
        providerDeliveryId: post.id,
        actorProviderAccountId: post.user_id,
        text,
        payload: raw,
      },
    };
  }
  if (options.replay !== true && rootId !== undefined && ctx.boundRootIds().has(rootId)) {
    return {
      type: MESSAGE_EVENT,
      data: {
        reference: reference(ctx, post.user_id, post.channel_id, "thread", post.id, rootId),
        provider: "mattermost",
        providerDeliveryId: post.id,
        actorProviderAccountId: post.user_id,
        text,
        payload: raw,
      },
    };
  }
  return null;
}

/** Classify a `reaction_added` payload; `channelId` is the event's broadcast channel. */
export async function normalizeReaction(
  reaction: MmReaction,
  raw: Record<string, unknown>,
  channelId: string,
  ctx: NormalizeContext,
): Promise<NormalizedEvent | null> {
  if (reaction.id === "" || reaction.emoji_name === "") return null;
  const channelType = await ctx.channelTypeFor(channelId);
  return {
    type: REACTION_EVENT,
    data: {
      reference: reference(
        ctx,
        reaction.user_id,
        channelId,
        channelType === "D" ? "dm" : "channel",
        reaction.post_id,
        undefined,
      ),
      provider: "mattermost",
      providerDeliveryId: reaction.id,
      actorProviderAccountId: reaction.user_id,
      emojiName: reaction.emoji_name.slice(0, 64),
      payload: raw,
    },
  };
}

/** Lenient parse of the WS `data.post` / `data.reaction` JSON-string fields. */
export function parseJsonField(value: string | undefined): Record<string, unknown> | undefined {
  if (value === undefined) return undefined;
  try {
    const parsed: unknown = JSON.parse(value);
    if (typeof parsed === "object" && parsed !== null && !Array.isArray(parsed)) {
      return parsed as Record<string, unknown>;
    }
  } catch {
    /* fall through */
  }
  return undefined;
}

function str(obj: Record<string, unknown>, key: string): string | undefined {
  const value = obj[key];
  return typeof value === "string" ? value : undefined;
}

function num(obj: Record<string, unknown>, key: string): number | undefined {
  const value = obj[key];
  return typeof value === "number" ? value : undefined;
}

export function asPost(raw: Record<string, unknown>): MmPost | undefined {
  const id = str(raw, "id");
  const channelId = str(raw, "channel_id");
  const userId = str(raw, "user_id");
  const message = str(raw, "message");
  if (id === undefined || channelId === undefined || userId === undefined || message === undefined)
    return undefined;
  const rootId = str(raw, "root_id");
  const createAt = num(raw, "create_at");
  const type = str(raw, "type");
  const props = raw["props"];
  return {
    id,
    channel_id: channelId,
    user_id: userId,
    message,
    ...(rootId !== undefined ? { root_id: rootId } : {}),
    ...(createAt !== undefined ? { create_at: createAt } : {}),
    ...(type !== undefined ? { type } : {}),
    ...(typeof props === "object" && props !== null
      ? { props: props as Record<string, unknown> }
      : {}),
  };
}

export function asReaction(raw: Record<string, unknown>): MmReaction | undefined {
  const userId = str(raw, "user_id");
  const postId = str(raw, "post_id");
  const emoji = str(raw, "emoji_name");
  if (userId === undefined || postId === undefined || emoji === undefined) return undefined;
  const createAt = num(raw, "create_at");
  // Live 11.11 law: a Reaction carries no `id` — its identity is the
  // composite (user, post, emoji). Synthesize the ledger identity so
  // inbound dedupe matches the outbound createReaction echo.
  return {
    id: `${userId}:${postId}:${emoji}`,
    user_id: userId,
    post_id: postId,
    emoji_name: emoji,
    ...(createAt !== undefined ? { create_at: createAt } : {}),
  };
}
