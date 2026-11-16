// The context plane's adapter leg (plan 3.2): a routed thread:// fetch is
// answered asynchronously with event.context.fetched.v1 — the platform
// window rides the URI's limit (capped), an unresolvable URI or a platform
// error is an honest error item, and the command ALWAYS settles accepted.
import { describe, expect, it } from "vitest";

import type { EnvelopeCommand, EnvelopeEvent } from "@ofac/protocol";
import type { CommandContext } from "@ofac/workspace-link";

import { CONTEXT_FETCH_COMMAND, answerContextFetch, parseThreadUri } from "../src/context.js";
import type { TelegramApi, TgMessage } from "../src/tg-client.js";

function message(id: number, text: string, seconds: number): TgMessage {
  return { message_id: id, chat: { id: -1001, type: "supergroup" }, date: seconds, text };
}

/** The published item view (one named assertion at the test boundary). */
type ContextItemView = {
  uri: string;
  content: { messages?: { text: string }[]; error?: string } & Record<string, unknown>;
  truncated: boolean;
};

function apiWith(history: TgMessage[] | Error): TelegramApi {
  const noop = async (): Promise<never> => {
    throw new Error("unused");
  };
  return {
    getUpdates: async () => [],
    deleteWebhook: async () => undefined,
    setWebhook: async () => undefined,
    getMe: async () => ({ id: 1, is_bot: true }),
    getFile: noop,
    downloadFile: noop,
    sendMessage: noop,
    sendPhoto: noop,
    sendDocument: noop,
    answerCallbackQuery: noop,
    editMessageText: noop,
    setMessageReaction: noop,
    getChatHistory: async () => {
      if (history instanceof Error) throw history;
      return history;
    },
  } as unknown as TelegramApi;
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
      targetId: "telegram-1",
      actorEvidenceRef: "kernel:context-fetch:fetch-1",
      deadline: deadline ?? new Date(Date.now() + 60_000).toISOString(),
      contextRefs: refs,
    },
  } as unknown as EnvelopeCommand;
}

function capturingCtx(): { ctx: CommandContext; published: EnvelopeEvent[] } {
  const published: EnvelopeEvent[] = [];
  return {
    published,
    ctx: {
      deliveryId: "delivery-ctx-1",
      signal: new AbortController().signal,
      envelope: fetchCommand([]),
      publishEvent: async (env) => {
        published.push(env);
        return { status: "accepted" as const };
      },
    },
  };
}

describe("parseThreadUri", () => {
  it("reads the chat, the root and a bounded limit", () => {
    expect(parseThreadUri("thread://telegram/123/456")).toEqual({
      chatId: "123",
      rootId: "456",
      limit: 20,
    });
    expect(parseThreadUri("thread://telegram/123/456?limit=5")!.limit).toBe(5);
    expect(parseThreadUri("thread://telegram/123/456?limit=999")!.limit).toBe(50);
    expect(parseThreadUri("artifact://w/a/b")).toBeUndefined();
    expect(parseThreadUri("thread://telegram/123")).toBeUndefined();
  });
});

describe("answerContextFetch", () => {
  it("answers a resolvable thread from the platform, oldest-first", async () => {
    const api = apiWith([message(3, "newest", 30), message(2, "middle", 20)]);
    const { ctx, published } = capturingCtx();
    const outcome = await answerContextFetch(
      api,
      "inst-1",
      fetchCommand(["thread://telegram/123/456?limit=2"]),
      ctx,
    );
    expect(outcome).toEqual({ kind: "accepted" });
    expect(published).toHaveLength(1);
    const env = published[0]!;
    expect(env.type).toBe("org.facilitator.event.context.fetched.v1");
    expect(env.dataschema).toBe(
      "https://schemas.openfacilitator.org/v1/event.context.fetched.v1.json",
    );
    // The envelope's data is a Record<string, unknown>; the item shape is
    // asserted once here, as a named view, and read checked below.
    const data = env.data as Record<string, unknown>;
    expect(data["fetchId"]).toBe("context-fetch:fetch-1");
    expect(data["workId"]).toBe("work-1");
    expect(data["responder"]).toBe("telegram-inst-1");
    const items = data["items"] as ContextItemView[];
    expect(items).toHaveLength(1);
    expect(items[0]!.content.messages!.map((m) => m.text)).toEqual(["middle", "newest"]);
  });

  it("answers a platform error and a foreign URI as honest error items", async () => {
    const api = apiWith(new Error("telegram down"));
    const { ctx, published } = capturingCtx();
    const outcome = await answerContextFetch(
      api,
      "inst-1",
      fetchCommand(["thread://telegram/123/456", "artifact://w/a/b"]),
      ctx,
    );
    expect(outcome).toEqual({ kind: "accepted" });
    const data = published[0]!.data as Record<string, unknown>;
    const items = data["items"] as { uri: string; content: Record<string, unknown> }[];
    expect(items[0]!.content["error"]).toBeDefined();
    expect(items[1]!.content["error"]).toBe("thread-unresolvable");
  });

  it("rejects only malformed data", async () => {
    const api = apiWith([]);
    const { ctx } = capturingCtx();
    const cmd = fetchCommand([]);
    const data: Record<string, unknown> = cmd.data as Record<string, unknown>;
    delete data["commandId"];
    expect(await answerContextFetch(api, "inst-1", cmd, ctx)).toEqual({
      kind: "rejected",
      reason: "malformed-context-fetch-data",
    });
  });
});
