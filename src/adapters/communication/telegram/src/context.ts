// The context plane of the telegram adapter: a routed
// `command.context.fetch.v1` (kernel → adapter, one thread:// URI per
// command) is answered ASYNCHRONOUSLY with `event.context.fetched.v1`
// published on the link the adapter already speaks. The honest v1 law (plan
// 3.2): the thread window comes FROM THE PLATFORM (`getChatHistory`, the
// URI's `limit`, capped at 50) — the ledger stores message ids, not bodies,
// so a ledger-first window would need a column the adapter does not have.
// Anything unanswerable is an honest ERROR ITEM, never a failed command: a
// fetch is a read, and a read that could not answer says what it could not.
import { randomUUID } from "node:crypto";

import type { EnvelopeCommand, EnvelopeEvent } from "@ofac/protocol";
import type { CommandContext } from "@ofac/workspace-link";
import { buildEvent } from "@ofac/workspace-link";

import type { TgMessage, TelegramApi } from "./tg-client.js";

export const CONTEXT_FETCH_COMMAND = "org.facilitator.command.context.fetch.v1";
export const CONTEXT_FETCHED_EVENT = "org.facilitator.event.context.fetched.v1";

/** The platform window ceiling the plan fixes (the API's own page limit). */
export const HISTORY_MAX = 50;

/** `thread://telegram/<chatId>/<rootMessageId>?limit=N` — the kernel-side
 *  URI law; the chat is segment 1, the thread root segment 2. */
export function parseThreadUri(
  uri: string,
): { chatId: string; rootId: string; limit: number } | undefined {
  if (!uri.startsWith("thread://telegram/")) return undefined;
  const rest = uri.slice("thread://telegram/".length);
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
  return { chatId: segments[0]!, rootId: segments[1]!, limit };
}

function clamp(text: string): string {
  return text.length <= 2000 ? text : `${text.slice(0, 1999)}…`;
}

function historyContent(uri: string, messages: TgMessage[]): Record<string, unknown> {
  return {
    uri,
    messages: messages
      .slice()
      .reverse() // the platform answers newest-first; a context reads oldest-first
      .map((message) => ({
        messageId: String(message.message_id),
        author: message.from?.id !== undefined ? String(message.from.id) : "unknown",
        text: clamp(message.text ?? message.caption ?? ""),
        timestamp:
          message.date !== undefined ? new Date(message.date * 1000).toISOString() : undefined,
        sourceRefs: [],
      }))
      .filter((entry) => entry.text !== "" || entry.timestamp !== undefined),
  };
}

/**
 * Answer one fetch command: parse every `thread://telegram/…` ref, read the
 * window from the platform, publish one `event.context.fetched.v1` carrying
 * all items. The command itself always settles `accepted` — the answer rides
 * the event plane; malformed data is the only rejection.
 */
export async function answerContextFetch(
  api: TelegramApi,
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
      const messages = await api.getChatHistory({ chat_id: parsed.chatId, limit: parsed.limit });
      items.push({ uri, content: historyContent(uri, messages), truncated: false });
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
    source: `urn:ofac:provider:telegram:${providerInstanceId}`,
    subject: `work/${workId}`,
    causationid: cmd.id,
    data: {
      fetchId,
      workId,
      responder: `telegram-${providerInstanceId}`,
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
