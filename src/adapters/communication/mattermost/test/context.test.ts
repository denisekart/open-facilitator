// The context plane's adapter leg (plan 3.2, mattermost law: a thread IS a
// post thread): a routed thread://mattermost/… fetch is answered
// asynchronously with event.context.fetched.v1 — the window comes FROM THE
// PLATFORM (getPostThread, the URI's limit, capped at 50, oldest trimmed), an
// unknown root post (API 404) or an unresolvable URI is an honest ERROR ITEM,
// and the command ALWAYS settles accepted.
import { mkdtempSync, rmSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { afterEach, describe, expect, it } from "vitest";
import { createMemoryLogger, type CommandContext } from "@ofac/workspace-link";
import type { EnvelopeCommand, EnvelopeEvent } from "@ofac/protocol";

import {
  createAdapterHandler,
  communicationManifest,
  type AdapterIdentity,
} from "../src/handler.js";
import { CONTEXT_FETCH_COMMAND, parseThreadUri } from "../src/context.js";
import { AdapterState } from "../src/post-ledger.js";
import { MmError, type MattermostApi, type MmThread, type WsEvent } from "../src/mm-client.js";
import type { MmPost } from "../src/normalize.js";

const IDENTITY: AdapterIdentity = {
  botUserId: "bot-id",
  providerInstanceId: "local-chat",
  tenantOrOrganizationId: "chat.localhost",
};

/** The published item view (one named assertion at the test boundary). */
type ContextItemView = {
  uri: string;
  content: {
    messages?: { messageId: string; sender: string; text: string; timestamp?: string }[];
    truncated?: boolean;
    error?: string;
  } & Record<string, unknown>;
  truncated: boolean;
};

function post(id: string, user: string, message: string, createAt: number): MmPost {
  return { id, channel_id: "chan-1", user_id: user, message, create_at: createAt };
}

/** The fake answers every fetch with this thread — or throws this error
 *  (an MmError with status 404 is the platform's unknown-root answer). */
function apiWith(thread: MmThread | Error): MattermostApi {
  const noop = async (): Promise<never> => {
    throw new Error("unused");
  };
  return {
    me: noop,
    channel: noop,
    botChannels: noop,
    channelPosts: noop,
    getPostThread: async () => {
      if (thread instanceof Error) throw thread;
      return thread;
    },
    missedEvents: async () => [] as WsEvent[],
    createPost: noop,
    createPostWithFiles: noop,
    uploadFile: noop,
    downloadFile: noop,
    createReaction: noop,
    editPost: noop,
    dmChannel: noop,
  } as unknown as MattermostApi;
}

function fetchCommand(refs: unknown[], deadline?: string): EnvelopeCommand {
  return {
    specversion: "1.0",
    id: "cmd-env-1",
    source: "urn:facilitator:instance:main",
    type: CONTEXT_FETCH_COMMAND,
    subject: "work/work-1",
    time: new Date().toISOString(),
    datacontenttype: "application/json",
    dataschema: "https://schemas.openfacilitator.org/v1/command.context.fetch.v1.json",
    data: {
      commandId: "context-fetch:fetch-1",
      workId: "work-1",
      targetId: "mattermost-1",
      actorEvidenceRef: "kernel:context-fetch:fetch-1",
      deadline: deadline ?? new Date(Date.now() + 60_000).toISOString(),
      contextRefs: refs,
    },
  } as unknown as EnvelopeCommand;
}

function fakeCtx(published: EnvelopeEvent[]): CommandContext {
  return {
    deliveryId: "delivery-1",
    envelope: fetchCommand([]),
    signal: new AbortController().signal,
    publishEvent: async (env) => {
      published.push(env);
      return { status: "accepted" };
    },
  };
}

const dirs: string[] = [];
function freshState(): AdapterState {
  const dir = mkdtempSync(join(tmpdir(), "mm-context-"));
  dirs.push(dir);
  return new AdapterState(join(dir, "state.sqlite3"));
}
afterEach(() => {
  for (const dir of dirs.splice(0)) rmSync(dir, { recursive: true, force: true });
});

function handler(api: MattermostApi) {
  return createAdapterHandler({
    api,
    state: freshState(),
    identity: IDENTITY,
    logger: createMemoryLogger(),
  });
}

describe("parseThreadUri", () => {
  it("reads the channel, the root post and a bounded limit", () => {
    expect(parseThreadUri("thread://mattermost/chan-1/root-1")).toEqual({
      channelId: "chan-1",
      rootPostId: "root-1",
      limit: 20,
    });
    expect(parseThreadUri("thread://mattermost/chan-1/root-1?limit=5")?.limit).toBe(5);
    expect(parseThreadUri("thread://mattermost/chan-1/root-1?limit=999")?.limit).toBe(50);
    expect(parseThreadUri("artifact://w/a/b")).toBeUndefined();
    expect(parseThreadUri("thread://telegram/123/456")).toBeUndefined();
    expect(parseThreadUri("thread://mattermost/chan-1")).toBeUndefined();
    expect(parseThreadUri("thread://mattermost//root-1")).toBeUndefined();
  });
});

describe("context-fetch capability arm", () => {
  it("the manifest declares the asynchronous context-fetch descriptor", () => {
    const manifest = communicationManifest("team-a");
    const descriptor = manifest.capabilities.find((c) => c.id === CONTEXT_FETCH_COMMAND);
    expect(descriptor).toEqual({
      id: CONTEXT_FETCH_COMMAND,
      version: 1,
      execution: "asynchronous",
      inputSchema: "https://schemas.openfacilitator.org/v1/command.context.fetch.v1.json",
    });
  });
});

describe("answerContextFetch through the handler", () => {
  it("answers a resolvable thread from the platform, chronological, trimmed to the limit", async () => {
    // Five posts, the platform's `order` law: chronological ids.
    const posts: Record<string, MmPost> = {};
    const order: string[] = [];
    for (const [index, id] of ["p1", "p2", "p3", "p4", "p5"].entries()) {
      posts[id] = post(id, `user-${index}`, `message ${id}`, 1_700_000_000_000 + index * 1000);
      order.push(id);
    }
    const api = apiWith({ order, posts });
    const published: EnvelopeEvent[] = [];
    const outcome = await handler(api).onCommand(
      fetchCommand(["thread://mattermost/chan-1/p-root?limit=3"]),
      fakeCtx(published),
    );
    expect(outcome).toEqual({ kind: "accepted" });
    expect(published).toHaveLength(1);
    const env = published[0]!;
    expect(env.type).toBe("org.facilitator.event.context.fetched.v1");
    expect(env.dataschema).toBe(
      "https://schemas.openfacilitator.org/v1/event.context.fetched.v1.json",
    );
    expect(env.causationid).toBe("cmd-env-1");
    const data = env.data as Record<string, unknown>;
    expect(data["fetchId"]).toBe("context-fetch:fetch-1");
    expect(data["workId"]).toBe("work-1");
    expect(data["responder"]).toBe("mattermost-local-chat");
    const items = data["items"] as ContextItemView[];
    expect(items).toHaveLength(1);
    expect(items[0]!.uri).toBe("thread://mattermost/chan-1/p-root?limit=3");
    // The oldest two are trimmed; what remains reads oldest-first.
    const messages = items[0]!.content.messages!;
    expect(messages.map((m) => m.text)).toEqual(["message p3", "message p4", "message p5"]);
    expect(messages[0]!.sender).toBe("user-2");
    expect(messages[0]!.timestamp).toBe(new Date(1_700_000_002_000).toISOString());
    // A trimmed window says so — on the item and inside the content.
    expect(items[0]!.truncated).toBe(true);
    expect(items[0]!.content.truncated).toBe(true);
  });

  it("an unknown root post (platform 404) is an honest error item, still accepted", async () => {
    const api = apiWith(new MmError("getPostThread", 404, "store.sql_post.get.find_error"));
    const published: EnvelopeEvent[] = [];
    const outcome = await handler(api).onCommand(
      fetchCommand(["thread://mattermost/chan-1/nope"]),
      fakeCtx(published),
    );
    expect(outcome).toEqual({ kind: "accepted" });
    const data = published[0]!.data as Record<string, unknown>;
    const items = data["items"] as ContextItemView[];
    expect(items).toHaveLength(1);
    expect(items[0]!.uri).toBe("thread://mattermost/chan-1/nope");
    expect(items[0]!.content.error).toBe("MmError");
    expect(items[0]!.truncated).toBe(false);
  });

  it("a foreign-scheme and a malformed URI are honest error items", async () => {
    const api = apiWith({ order: [], posts: {} });
    const published: EnvelopeEvent[] = [];
    const outcome = await handler(api).onCommand(
      fetchCommand(["artifact://w/a/b", "thread://mattermost/chan-only"]),
      fakeCtx(published),
    );
    expect(outcome).toEqual({ kind: "accepted" });
    const data = published[0]!.data as Record<string, unknown>;
    const items = data["items"] as ContextItemView[];
    expect(items).toHaveLength(2);
    expect(items[0]!.content.error).toBe("thread-unresolvable");
    expect(items[1]!.content.error).toBe("thread-unresolvable");
  });

  it("rejects only malformed data", async () => {
    const api = apiWith({ order: [], posts: {} });
    const published: EnvelopeEvent[] = [];
    const cmd = fetchCommand([]);
    const data: Record<string, unknown> = cmd.data as Record<string, unknown>;
    delete data["commandId"];
    expect(await handler(api).onCommand(cmd, fakeCtx(published))).toEqual({
      kind: "rejected",
      reason: "malformed-context-fetch-data",
    });
    expect(published).toHaveLength(0);
  });
});
