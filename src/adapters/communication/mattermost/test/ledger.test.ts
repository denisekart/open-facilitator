// The §13.3 idempotency-ledger law + the channel-type cache, against a fake
// MattermostApi with ZERO HTTP: a replayed reply command must answer accepted
// without ever touching the provider again.
import { mkdtempSync, rmSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { describe, expect, it, afterEach } from "vitest";
import { createMemoryLogger } from "@ofac/workspace-link";
import type { CommandContext } from "@ofac/workspace-link";
import type { EnvelopeCommand, EnvelopeEvent } from "@ofac/protocol";
import { AdapterState } from "../src/post-ledger.js";
import { createAdapterHandler, type AdapterIdentity } from "../src/handler.js";
import type { MattermostApi, WsEvent } from "../src/mm-client.js";

const IDENTITY: AdapterIdentity = {
  botUserId: "bot-id",
  providerInstanceId: "local-chat",
  tenantOrOrganizationId: "chat.localhost",
};

interface FakeApi extends MattermostApi {
  calls: { createPost: number; createReaction: number; editPost: number; dm: string[] };
}

function fakeApi(): FakeApi {
  const calls = { createPost: 0, createReaction: 0, editPost: 0, dm: [] as string[] };
  let n = 0;
  return {
    calls,
    me: async () => ({ id: "bot-id", username: "facilitator" }),
    channel: async (id) => ({ id, type: "O" as const }),
    createPost: async () => {
      calls.createPost += 1;
      return `made-post-${n++}`;
    },
    createReaction: async () => {
      calls.createReaction += 1;
      return `made-reaction-${n++}`;
    },
    botChannels: async () => [],
    channelPosts: async () => [],
    missedEvents: async () => [] as WsEvent[],
    editPost: async () => {
      calls.editPost += 1;
    },
    dmChannel: async (userId) => {
      calls.dm.push(userId);
      return `dm-${userId}`;
    },
  };
}

function replyCommand(overrides: Record<string, unknown> = {}): EnvelopeCommand {
  return {
    specversion: "1.0",
    id: "env-1",
    source: "urn:ofac:kernel",
    type: "org.facilitator.command.communication.reply.v1",
    subject: "cmd-1",
    time: new Date().toISOString(),
    datacontenttype: "application/json",
    dataschema: "https://schemas.openfacilitator.org/v1/command.communication.reply.v1.json",
    data: {
      commandId: "cmd-1",
      idempotencyKey: "idem-1",
      text: "Work accepted.",
      target: {
        provider: "mattermost",
        channelOrResourceId: "chan-1",
        rootThreadId: "root-1",
        conversationKind: "thread",
      },
      ...overrides,
    },
  };
}

const dirs: string[] = [];
function freshState(): AdapterState {
  const dir = mkdtempSync(join(tmpdir(), "mm-ledger-"));
  dirs.push(dir);
  return new AdapterState(join(dir, "state.sqlite3"));
}
afterEach(() => {
  for (const dir of dirs.splice(0)) rmSync(dir, { recursive: true, force: true });
});

function fakeCtx(published: EnvelopeEvent[]): CommandContext {
  return {
    deliveryId: "delivery-1",
    envelope: replyCommand(),
    signal: new AbortController().signal,
    publishEvent: async (env) => {
      published.push(env);
      return { status: "accepted" };
    },
  };
}

describe("reply idempotency law", () => {
  it("same idempotencyKey twice ⇒ second call never hits the provider", async () => {
    const api = fakeApi();
    const handler = createAdapterHandler({
      api,
      state: freshState(),
      identity: IDENTITY,
      logger: createMemoryLogger(),
    });
    const published: EnvelopeEvent[] = [];
    const ctx = fakeCtx(published);
    expect(await handler.onCommand(replyCommand(), ctx)).toEqual({ kind: "accepted" });
    expect(api.calls.createPost).toBe(1);
    expect(published).toHaveLength(1);

    expect(await handler.onCommand(replyCommand(), ctx)).toEqual({ kind: "accepted" });
    expect(api.calls.createPost).toBe(1);
    expect(published).toHaveLength(1);
  });

  it("the echo event rides back with the created post id as providerDeliveryId", async () => {
    const api = fakeApi();
    const handler = createAdapterHandler({
      api,
      state: freshState(),
      identity: IDENTITY,
      logger: createMemoryLogger(),
    });
    const published: EnvelopeEvent[] = [];
    await handler.onCommand(replyCommand(), fakeCtx(published));
    const echo = published[0];
    expect(echo?.type).toBe("org.facilitator.event.provider.message.v1");
    const data = echo?.data as Record<string, unknown>;
    expect(data["providerDeliveryId"]).toBe("made-post-0");
    const reference = data["reference"] as Record<string, unknown>;
    expect(reference["authorProviderAccountId"]).toBe("bot-id");
    expect(reference["parentMessageId"]).toBe("root-1");
  });

  it("a dm target naming only its counterparty resolves the bot-DM channel", async () => {
    const api = fakeApi();
    const handler = createAdapterHandler({
      api,
      state: freshState(),
      identity: IDENTITY,
      logger: createMemoryLogger(),
    });
    const outcome = await handler.onCommand(
      replyCommand({
        target: {
          provider: "mattermost",
          conversationKind: "dm",
          actorProviderAccountId: "human-1",
        },
      }),
      fakeCtx([]),
    );
    expect(outcome).toEqual({ kind: "accepted" });
    expect(api.calls.dm).toEqual(["human-1"]);
    expect(api.calls.createPost).toBe(1);
  });

  it("malformed data rejects by name; unknown types unsupported", async () => {
    const handler = createAdapterHandler({
      api: fakeApi(),
      state: freshState(),
      identity: IDENTITY,
      logger: createMemoryLogger(),
    });
    const ctx = fakeCtx([]);
    const broken = replyCommand();
    delete (broken.data as Record<string, unknown>)["idempotencyKey"];
    expect(await handler.onCommand(broken, ctx)).toEqual({
      kind: "rejected",
      reason: "malformed-reply-data",
    });
    const other = { ...replyCommand(), type: "org.facilitator.command.work.cancel.v1" };
    expect(await handler.onCommand(other, ctx)).toEqual({ kind: "unsupported" });
  });
});

describe("react + update + acknowledge", () => {
  it("react dedupes on the ledger and records the reaction id", async () => {
    const api = fakeApi();
    const handler = createAdapterHandler({
      api,
      state: freshState(),
      identity: IDENTITY,
      logger: createMemoryLogger(),
    });
    const ctx = fakeCtx([]);
    const cmd: EnvelopeCommand = {
      ...replyCommand(),
      type: "org.facilitator.command.communication.react.v1",
      data: {
        commandId: "cmd-2",
        idempotencyKey: "idem-2",
        emojiName: "thumbsup",
        target: {
          provider: "mattermost",
          parentMessageId: "post-target",
          conversationKind: "thread",
        },
      },
    };
    expect(await handler.onCommand(cmd, ctx)).toEqual({ kind: "accepted" });
    expect(await handler.onCommand(cmd, ctx)).toEqual({ kind: "accepted" });
    expect(api.calls.createReaction).toBe(1);
  });

  it("update refuses to edit a post the adapter never created", async () => {
    const api = fakeApi();
    const handler = createAdapterHandler({
      api,
      state: freshState(),
      identity: IDENTITY,
      logger: createMemoryLogger(),
    });
    const cmd: EnvelopeCommand = {
      ...replyCommand(),
      type: "org.facilitator.command.communication.update.v1",
      data: {
        commandId: "cmd-3",
        idempotencyKey: "idem-3",
        text: "edited",
        target: {
          provider: "mattermost",
          channelOrResourceId: "chan-1",
          parentMessageId: "post-stranger",
          conversationKind: "thread",
        },
      },
    };
    expect(await handler.onCommand(cmd, fakeCtx([]))).toEqual({
      kind: "rejected",
      reason: "unknown-post",
    });
    expect(api.calls.editPost).toBe(0);
  });

  it("acknowledge is a no-op accepted, replay included", async () => {
    const api = fakeApi();
    const handler = createAdapterHandler({
      api,
      state: freshState(),
      identity: IDENTITY,
      logger: createMemoryLogger(),
    });
    const cmd: EnvelopeCommand = {
      ...replyCommand(),
      type: "org.facilitator.command.communication.acknowledge.v1",
      data: {
        commandId: "cmd-4",
        idempotencyKey: "idem-4",
        target: { provider: "mattermost", conversationKind: "channel" },
      },
    };
    const ctx = fakeCtx([]);
    expect(await handler.onCommand(cmd, ctx)).toEqual({ kind: "accepted" });
    expect(await handler.onCommand(cmd, ctx)).toEqual({ kind: "accepted" });
    expect(api.calls.createPost).toBe(0);
  });
});

describe("AdapterState", () => {
  it("a recorded reply marks its thread root bound", async () => {
    const state = freshState();
    const handler = createAdapterHandler({
      api: fakeApi(),
      state,
      identity: IDENTITY,
      logger: createMemoryLogger(),
    });
    await handler.onCommand(replyCommand(), fakeCtx([]));
    expect(state.boundRootIds().has("root-1")).toBe(true);
    expect(state.findPost("idem-1")?.providerObjectId).toBe("made-post-0");
    expect(state.findPostByObjectId("made-post-0")?.idempotencyKey).toBe("idem-1");
  });

  it("channel-type cache round-trips and upserts", () => {
    const state = freshState();
    expect(state.channelType("c1")).toBeUndefined();
    state.cacheChannelType("c1", "D");
    expect(state.channelType("c1")).toBe("D");
    state.cacheChannelType("c1", "O");
    expect(state.channelType("c1")).toBe("O");
  });

  it("kv cursor round-trips", () => {
    const state = freshState();
    state.setKv("last_seen_create_at", "1730000000000");
    expect(state.getKv("last_seen_create_at")).toBe("1730000000000");
    state.setKv("last_seen_create_at", "1730000001000");
    expect(state.getKv("last_seen_create_at")).toBe("1730000001000");
  });
});
