// The context plane of the mattermost adapter: a routed
// `command.context.fetch.v1` (kernel → adapter, one thread:// URI per
// command) is answered ASYNCHRONOUSLY with `event.context.fetched.v1`
// published on the link the adapter already speaks. The honest v1 law (plan
// 3.2, telegram's mirror): the thread window comes FROM THE PLATFORM
// (`getPostThread`, the URI's `limit`, capped at 50) — a thread IS a post
// thread here, so `GET /posts/{root}/thread` is the whole window; the ledger
// stores post ids, not bodies, so a ledger-first window would need a column
// the adapter does not have. Anything unanswerable is an honest ERROR ITEM,
// never a failed command: a fetch is a read, and a read that could not
// answer says what it could not.
import { randomUUID } from "node:crypto";

import type { EnvelopeCommand, EnvelopeEvent } from "@ofac/protocol";
import type { CommandContext } from "@ofac/workspace-link";
import { buildEvent } from "@ofac/workspace-link";

import type { MattermostApi, MmThread } from "./mm-client.js";
import type { MmPost } from "./normalize.js";

export const CONTEXT_FETCH_COMMAND = "org.facilitator.command.context.fetch.v1";
export const CONTEXT_FETCHED_EVENT = "org.facilitator.event.context.fetched.v1";

/** The platform window ceiling the plan fixes (the API's own page limit). */
export const HISTORY_MAX = 50;

const URI_PREFIX = "thread://mattermost/";

/** `thread://mattermost/<channelId>/<rootPostId>?limit=N` — the kernel-side
 *  URI law; the channel is segment 1, the thread root segment 2. */
export function parseThreadUri(
  uri: string,
): { channelId: string; rootPostId: string; limit: number } | undefined {
  if (!uri.startsWith(URI_PREFIX)) return undefined;
  const rest = uri.slice(URI_PREFIX.length);
  const queryAt = rest.indexOf("?");
  const path = queryAt === -1 ? rest : rest.slice(0, queryAt);
  const query = queryAt === -1 ? "" : rest.slice(queryAt + 1);
  const segments = path.split("/");
  if (segments.length < 2 || segments[0] === "" || segments[1] === "") return undefined;
  let limit = 20;
  for (const pair of query.split("&")) {
    const [key, value] = pair.split("=");
    if (key === "limit" && value !== undefined) {
      const parsed = Number(value);
      if (Number.isInteger(parsed) && parsed > 0) limit = Math.min(parsed, HISTORY_MAX);
    }
  }
  return { channelId: segments[0]!, rootPostId: segments[1]!, limit };
}

function clamp(text: string): string {
  return text.length <= 2000 ? text : `${text.slice(0, 1999)}…`;
}

/** The window law: the platform's own `order` list is chronological, so the
 *  window is its LAST `limit` entries (trimming the oldest beyond the cap).
 *  Ids the posts map lacks are skipped (lenient, like channelPosts). */
function windowOf(thread: MmThread, limit: number): { posts: MmPost[]; truncated: boolean } {
  const ordered = thread.order
    .map((id) => thread.posts[id])
    .filter((post): post is MmPost => post !== undefined && post !== null);
  return {
    posts: ordered.slice(Math.max(ordered.length - limit, 0)),
    truncated: ordered.length > limit,
  };
}

function historyContent(
  uri: string,
  posts: MmPost[],
  windowTruncated: boolean,
): Record<string, unknown> {
  const messages = posts
    .map((post) => ({
      messageId: post.id,
      sender: post.user_id ?? "unknown",
      text: clamp(post.message ?? ""),
      timestamp: post.create_at !== undefined ? new Date(post.create_at).toISOString() : undefined,
      sourceRefs: [],
    }))
    .filter((entry) => entry.text !== "" || entry.timestamp !== undefined);
  // The item says what the content hides: a trimmed window, or a body that
  // hit the 2000-char clamp, is an incomplete read.
  const clamped = posts.some((post) => post.message !== undefined && post.message.length > 2000);
  return { uri, messages, truncated: windowTruncated || clamped };
}

/**
 * Answer one fetch command: parse every `thread://mattermost/…` ref, read the
 * window from the platform, publish one `event.context.fetched.v1` carrying
 * all items. The command itself always settles `accepted` — the answer rides
 * the event plane; malformed data is the only rejection.
 */
export async function answerContextFetch(
  api: MattermostApi,
  providerInstanceId: string,
  cmd: EnvelopeCommand,
  ctx: CommandContext,
): Promise<{ kind: "accepted" } | { kind: "rejected"; reason: string }> {
  const data = cmd.data as Record<string, unknown> | undefined;
  if (typeof data !== "object" || data === null || typeof data["commandId"] !== "string") {
    return { kind: "rejected", reason: "malformed-context-fetch-data" };
  }
  const fetchId = String(data["commandId"]);
  const workId = String(data["workId"] ?? "");
  const refs = Array.isArray(data["contextRefs"]) ? (data["contextRefs"] as unknown[]) : [];
  const items: { uri: string; content: Record<string, unknown>; truncated: boolean }[] = [];
  for (const ref of refs) {
    const uri = String(ref);
    const parsed = parseThreadUri(uri);
    if (parsed === undefined) {
      items.push({ uri, content: { error: "thread-unresolvable" }, truncated: false });
      continue;
    }
    try {
      const thread = await api.getPostThread(parsed.rootPostId);
      const { posts, truncated } = windowOf(thread, parsed.limit);
      items.push({ uri, content: historyContent(uri, posts, truncated), truncated });
    } catch (error) {
      items.push({
        uri,
        content: { error: error instanceof Error ? error.name : "platform-error" },
        truncated: false,
      });
    }
  }
  const deadlineExceeded =
    typeof data["deadline"] === "string" && new Date(data["deadline"]).getTime() < Date.now();
  const event: EnvelopeEvent = buildEvent(CONTEXT_FETCHED_EVENT, {
    id: randomUUID(),
    source: `urn:ofac:provider:mattermost:${providerInstanceId}`,
    subject: `work/${workId}`,
    causationid: cmd.id,
    data: {
      fetchId,
      workId,
      responder: `mattermost-${providerInstanceId}`,
      items:
        items.length > 0
          ? items
          : [{ uri: "thread://unknown", content: { error: "no-refs" }, truncated: false }],
      ...(deadlineExceeded ? { note: "deadline-exceeded" } : {}),
    },
  });
  await ctx.publishEvent(event);
  return { kind: "accepted" };
}
