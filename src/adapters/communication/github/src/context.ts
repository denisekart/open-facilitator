// The context plane of the github adapter: a routed
// `command.context.fetch.v1` (kernel → adapter, one thread:// URI per
// command) is answered ASYNCHRONOUSLY with `event.context.fetched.v1`
// published on the link the adapter already speaks. The honest v1 law (plan
// 3.2): the thread window comes FROM THE PLATFORM (`listIssueComments` +
// `listReviewComments`, the URI's `limit`, capped at 50) — a thread here IS
// the issue/PR comment thread, so the window is the issue's comment list (a
// PR thread also carries its inline review conversation, which rides too).
// Anything unanswerable is an honest ERROR ITEM, never a failed command: a
// fetch is a read, and a read that could not answer says what it could not.
import { randomUUID } from "node:crypto";

import type { EnvelopeCommand, EnvelopeEvent } from "@ofac/protocol";
import type { CommandContext } from "@ofac/workspace-link";
import { buildEvent } from "@ofac/workspace-link";

import type { GitHubApi } from "./github-client.js";

export const CONTEXT_FETCH_COMMAND = "org.facilitator.command.context.fetch.v1";
export const CONTEXT_FETCHED_EVENT = "org.facilitator.event.context.fetched.v1";

/** The platform window ceiling the plan fixes (the API's own page limit). */
export const HISTORY_MAX = 50;

/** `thread://github/<repo>/<number>?limit=N` — the kernel-side URI law; a
 *  thread is an issue/PR comment thread, so every path segment but the last
 *  is the repo (`owner/name`) and the last is the issue/PR number. */
export function parseThreadUri(
  uri: string,
): { repo: string; number: number; limit: number } | undefined {
  if (!uri.startsWith("thread://github/")) return undefined;
  const rest = uri.slice("thread://github/".length);
  const queryAt = rest.indexOf("?");
  const path = queryAt === -1 ? rest : rest.slice(0, queryAt);
  const query = queryAt === -1 ? "" : rest.slice(queryAt + 1);
  const segments = path.split("/");
  if (segments.length < 2 || segments[segments.length - 2] === "") return undefined;
  const number = Number(segments[segments.length - 1]);
  if (!Number.isInteger(number) || number <= 0) return undefined;
  const repo = segments.slice(0, -1).join("/");
  let limit = 20;
  for (const pair of query.split("&")) {
    const [key, value] = pair.split("=");
    if (key === "limit" && value !== undefined) {
      const parsed = Number(value);
      if (Number.isInteger(parsed) && parsed > 0) limit = Math.min(parsed, HISTORY_MAX);
    }
  }
  return { repo, number, limit };
}

function clamp(text: string): string {
  return text.length <= 2000 ? text : `${text.slice(0, 1999)}…`;
}

function historyContent(
  uri: string,
  comments: { author: string; body: string; created: string }[],
) {
  return {
    uri,
    messages: comments.map((comment) => ({
      sender: comment.author,
      text: clamp(comment.body),
      timestamp: comment.created,
    })),
    truncated: false,
  };
}

/**
 * Answer one fetch command: parse every `thread://github/…` ref, read the
 * window from the platform, publish one `event.context.fetched.v1` carrying
 * all items. The command itself always settles `accepted` — the answer rides
 * the event plane; malformed data is the only rejection.
 */
export async function answerContextFetch(
  api: GitHubApi | undefined,
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
    if (api === undefined) {
      // No bot token: the platform window cannot be read — an honest error
      // item, not a failed read.
      items.push({ uri, content: { error: "no-bot-token" }, truncated: false });
      continue;
    }
    try {
      // The issue/PR comment window is the thread; the issue itself is the
      // thread root (its title + state head the window). A PR thread ALSO
      // carries its inline review conversation, so the window reads both
      // routes (the issue-comments route answers `[]` for a PR-only
      // conversation).
      const issue = await api.getIssue(parsed.repo, parsed.number);
      const comments = await api.listIssueComments(parsed.repo, parsed.number, parsed.limit);
      const review = await api.listReviewComments(parsed.repo, issue.number);
      const merged = [
        { author: "thread-root", body: `${issue.title} (${issue.state})`, created: "" },
        ...comments,
        ...review
          .slice(0, parsed.limit)
          .map((entry) => ({ author: entry.user, body: entry.body, created: "" })),
      ].slice(0, HISTORY_MAX);
      items.push({ uri, content: historyContent(uri, merged), truncated: false });
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
    source: `urn:ofac:provider:github:${providerInstanceId}`,
    subject: `work/${workId}`,
    causationid: cmd.id,
    data: {
      fetchId,
      workId,
      responder: `github-${providerInstanceId}`,
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
