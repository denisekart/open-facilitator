// The context plane of the gitlab adapter: a routed
// `command.context.fetch.v1` (kernel → adapter, one thread:// URI per
// command) is answered ASYNCHRONOUSLY with `event.context.fetched.v1`
// published on the link the adapter already speaks. The honest v1 law (plan
// 3.2, telegram's shape): the thread window comes FROM THE PLATFORM
// (`listIssueNotes`, the URI's `limit`, capped at 50) — the ledger stores
// note ids, not bodies, so a ledger-first window would need a column the
// adapter does not have. Anything unanswerable is an honest ERROR ITEM,
// never a failed command: a fetch is a read, and a read that could not
// answer says what it could not.
import { randomUUID } from "node:crypto";

import type { EnvelopeCommand, EnvelopeEvent } from "@ofac/protocol";
import type { CommandContext } from "@ofac/workspace-link";
import { buildEvent } from "@ofac/workspace-link";

import type { GitLabApi, GitLabIssueNote } from "./gitlab-client.js";

export const CONTEXT_FETCH_COMMAND = "org.facilitator.command.context.fetch.v1";
export const CONTEXT_FETCHED_EVENT = "org.facilitator.event.context.fetched.v1";

/** The platform window ceiling the plan fixes (the notes API's own page
 *  limit is 100; the plan caps the window at 50). */
export const HISTORY_MAX = 50;

const URI_PREFIX = "thread://gitlab/";

/** `thread://gitlab/<repo>/<iid>?limit=N` — the kernel-side URI law. GitLab
 *  repos are `group/proj` (multi-segment), so the IID is the LAST path
 *  segment and everything before it is the repo path. */
export function parseThreadUri(
  uri: string,
): { repo: string; iid: string; limit: number } | undefined {
  if (!uri.startsWith(URI_PREFIX)) return undefined;
  const rest = uri.slice(URI_PREFIX.length);
  const queryAt = rest.indexOf("?");
  const path = queryAt === -1 ? rest : rest.slice(0, queryAt);
  const query = queryAt === -1 ? "" : rest.slice(queryAt + 1);
  const slashAt = path.lastIndexOf("/");
  if (slashAt <= 0 || slashAt === path.length - 1) return undefined;
  const repo = path.slice(0, slashAt);
  const iid = path.slice(slashAt + 1);
  if (iid === "" || !/^\d+$/.test(iid)) return undefined;
  let limit = 20;
  for (const pair of query.split("&")) {
    const [key, value] = pair.split("=");
    if (key === "limit" && value !== undefined) {
      const parsed = Number(value);
      if (Number.isInteger(parsed) && parsed > 0) limit = Math.min(parsed, HISTORY_MAX);
    }
  }
  return { repo, iid, limit };
}

function clamp(text: string): string {
  return text.length <= 2000 ? text : `${text.slice(0, 1999)}…`;
}

/** The envelope's items carry a `uri`-format string: anything with a scheme
 *  qualifies; a bare word does not and rides the fallback location. */
function isUriShaped(value: string): boolean {
  return /^[A-Za-z][A-Za-z0-9+.-]*:\/\//.test(value);
}

function historyContent(uri: string, notes: GitLabIssueNote[]): Record<string, unknown> {
  return {
    uri,
    messages: notes
      .filter((note) => !note.system) // journal entries are not messages
      .slice()
      .reverse() // the platform answers newest-first; a context reads oldest-first
      .map((note) => ({
        messageId: String(note.id),
        author: note.author,
        text: clamp(note.body),
        timestamp: note.createdAt,
        sourceRefs: [],
      }))
      .filter((entry) => entry.text !== "" || entry.timestamp !== undefined),
  };
}

/**
 * Answer one fetch command: parse every `thread://gitlab/…` ref, read the
 * window from the platform, publish one `event.context.fetched.v1` carrying
 * all items. The command itself always settles `accepted` — the answer rides
 * the event plane; malformed data is the only rejection.
 */
export async function answerContextFetch(
  api: GitLabApi,
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
      // The wire's items require a `uri`-format string: a ref that is not a
      // URI at all rides the honest fallback location, the raw value in the
      // content.
      items.push({
        uri: isUriShaped(uri) ? uri : "thread://unresolvable",
        content: { error: "thread-unresolvable", uri },
        truncated: false,
      });
      continue;
    }
    try {
      const notes = await api.listIssueNotes(parsed.repo, Number(parsed.iid), parsed.limit);
      items.push({ uri, content: historyContent(uri, notes), truncated: false });
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
    source: `urn:ofac:provider:gitlab:${providerInstanceId}`,
    subject: `work/${workId}`,
    causationid: cmd.id,
    data: {
      fetchId,
      workId,
      responder: `gitlab-${providerInstanceId}`,
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
