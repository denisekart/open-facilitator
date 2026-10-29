// Mattermost REST v4 with the bot token: the ONLY place that talks to the
// provider. Plain global fetch; non-2xx answers a typed MmError naming the
// call. Everything the link handler and the WS listener need about the
// provider is behind the MattermostApi interface so unit tests inject a fake
// (the idempotency-ledger tests must run with zero HTTP).
import { trimSlash } from "@ofac/workspace-link";
import type { AdapterState } from "./post-ledger.js";
import type { MmPost } from "./normalize.js";

/** One Mattermost WebSocket event frame (also the missed-events recovery shape). */
export interface WsEvent {
  event?: string;
  data?: Record<string, unknown>;
  broadcast?: Record<string, unknown>;
  seq?: number;
}

export class MmError extends Error {
  constructor(
    readonly call: string,
    readonly status: number,
    detail?: string,
  ) {
    super(`mattermost ${call} failed: HTTP ${status}${detail === undefined ? "" : ` (${detail})`}`);
    this.name = "MmError";
  }
}

export interface MmUser {
  id: string;
  username: string;
}

export interface MmChannel {
  id: string;
  type: "O" | "P" | "D";
}

export interface MattermostApi {
  me(): Promise<MmUser>;
  channel(channelId: string): Promise<MmChannel>;
  botChannels(): Promise<string[]>;
  channelPosts(channelId: string, sinceCreateAt: number): Promise<MmPost[]>;
  /** WS-gap recovery: events the socket missed after sequence `seq`. */
  missedEvents(seq: number): Promise<WsEvent[]>;
  /** Returns the created post id. */
  createPost(channelId: string, message: string, rootId?: string): Promise<string>;
  /** Returns the ledger identity of the created reaction: a synthesized
   * composite (live 11.11 Reaction objects carry no id field). */
  createReaction(postId: string, emojiName: string): Promise<string>;
  /** Edits a post the adapter created (caller enforces the ledger law). */
  editPost(postId: string, message: string): Promise<void>;
  /** The bot-DM channel with a user, created on first use and cached. */
  dmChannel(userId: string): Promise<string>;
}

interface PostDto {
  id: string;
}
interface UserDto {
  id: string;
  username: string;
}
interface ChannelDto {
  id: string;
  type: string;
}
interface ReactionDto {
  user_id: string;
  post_id: string;
  emoji_name: string;
}

function isChannelType(value: string): value is "O" | "P" | "D" {
  return value === "O" || value === "P" || value === "D";
}

export class MattermostClient implements MattermostApi {
  private readonly base: string;
  private readonly dmCache = new Map<string, string>();

  constructor(
    mmUrl: string,
    private readonly botToken: string,
    /** Owns the channel-type cache; DM channels get cached the moment they resolve. */
    private readonly state?: AdapterState,
    private readonly fetchImpl: typeof fetch = fetch,
  ) {
    this.base = trimSlash(mmUrl);
  }

  private async request<T>(call: string, method: string, path: string, body?: unknown): Promise<T> {
    const response = await this.fetchImpl(`${this.base}/api/v4${path}`, {
      method,
      headers: {
        authorization: `Bearer ${this.botToken}`,
        "content-type": "application/json",
      },
      ...(body === undefined ? {} : { body: JSON.stringify(body) }),
    });
    if (response.status === 204) return undefined as T;
    let payload: unknown;
    try {
      payload = await response.json();
    } catch {
      payload = undefined;
    }
    if (!response.ok) {
      const detail =
        typeof payload === "object" && payload !== null
          ? String((payload as Record<string, unknown>)["message"] ?? "")
          : "";
      throw new MmError(call, response.status, detail === "" ? undefined : detail);
    }
    return payload as T;
  }

  async me(): Promise<MmUser> {
    const dto = await this.request<UserDto>("me", "GET", "/users/me");
    return { id: dto.id, username: dto.username };
  }

  async channel(channelId: string): Promise<MmChannel> {
    const dto = await this.request<ChannelDto>("channel", "GET", `/channels/${channelId}`);
    const channel: MmChannel = {
      id: dto.id,
      type: isChannelType(dto.type) ? dto.type : "O",
    };
    this.state?.cacheChannelType(channel.id, channel.type);
    return channel;
  }

  async botChannels(): Promise<string[]> {
    const me = await this.me();
    const dtos = await this.request<ChannelDto[]>("botChannels", "GET", `/users/${me.id}/channels`);
    for (const dto of dtos) {
      if (isChannelType(dto.type)) this.state?.cacheChannelType(dto.id, dto.type);
    }
    return dtos.map((dto) => dto.id);
  }

  async channelPosts(channelId: string, sinceCreateAt: number): Promise<MmPost[]> {
    // The posts API answers {order, posts}; only the ordered post objects
    // newer than the cursor matter for replay.
    const dto = await this.request<{ order?: string[]; posts?: Record<string, MmPost> }>(
      "channelPosts",
      "GET",
      `/channels/${channelId}/posts?since=${sinceCreateAt}`,
    );
    const posts = dto.posts ?? {};
    const ordered = dto.order ?? Object.keys(posts);
    return ordered
      .map((id) => posts[id])
      .filter((post): post is MmPost => post !== undefined && post !== null);
  }

  async missedEvents(seq: number): Promise<WsEvent[]> {
    // MM WS docs: GET /users/me/missed_events?seq=N returns the buffered
    // events after that sequence (answer is an event array).
    return await this.request<WsEvent[]>(
      "missedEvents",
      "GET",
      `/users/me/missed_events?seq=${seq}`,
    );
  }

  async createPost(channelId: string, message: string, rootId?: string): Promise<string> {
    const dto = await this.request<PostDto>("createPost", "POST", "/posts", {
      channel_id: channelId,
      message,
      ...(rootId === undefined ? {} : { root_id: rootId }),
    });
    return dto.id;
  }

  async createReaction(postId: string, emojiName: string): Promise<string> {
    const me = await this.me();
    // Live 11.11 law: POST /reactions answers with the single stored
    // Reaction OBJECT (docs claim an array), and a Reaction has no `id` —
    // its identity is the composite (user, post, emoji, create_at). The
    // ledger identity is therefore synthesized from the echoed row.
    const row = await this.request<ReactionDto>("createReaction", "POST", "/reactions", {
      user_id: me.id,
      post_id: postId,
      emoji_name: emojiName,
    });
    if (row?.post_id !== postId || row?.emoji_name !== emojiName) {
      throw new MmError("createReaction", 200, "reaction echo does not match the request");
    }
    return `${row.user_id}:${row.post_id}:${row.emoji_name}`;
  }

  async editPost(postId: string, message: string): Promise<void> {
    await this.request<PostDto>("editPost", "PUT", `/posts/${postId}`, {
      id: postId,
      message,
    });
  }

  async dmChannel(userId: string): Promise<string> {
    const cached = this.dmCache.get(userId);
    if (cached !== undefined) return cached;
    const me = await this.me();
    const dto = await this.request<ChannelDto>("dmChannel", "POST", "/channels/direct", [
      me.id,
      userId,
    ]);
    this.dmCache.set(userId, dto.id);
    this.state?.cacheChannelType(dto.id, "D");
    return dto.id;
  }
}
