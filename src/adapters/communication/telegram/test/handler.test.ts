// The outbound law against a fake TelegramApi recording every call: a
// delivery replay answers accepted with ZERO Telegram calls; a reply with
// one image/png ref calls sendPhoto with the downloaded bytes and a caption
// ≤ 1024; a 5 MB application/pdf ref calls sendDocument; a 60 MB ref ⇒
// rejected artifact-too-large with zero posts; a 9000-char text reply sends
// ≥ 3 sendMessage calls; CMD_REACT "white_check_mark" ⇒ setMessageReaction
// with ✅; an update to a foreign message id ⇒ rejected not-adapter-message;
// after a reply, exactly one echo message.v1 was published and validates
// against the frozen event.provider.message.v1 schema; a DM target with no
// recorded chat ⇒ rejected no-dm-channel (T1); a failed artifact download ⇒
// rejected artifact-unavailable with zero posts.
import { mkdtempSync, rmSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { afterEach, describe, expect, it } from "vitest";
import { createMemoryLogger } from "@ofac/workspace-link";
import type { CommandContext } from "@ofac/workspace-link";
import { validateEnvelope } from "@ofac/protocol";
import type { ArtifactRef, EnvelopeCommand, EnvelopeEvent } from "@ofac/protocol";
import { ChatLedger, parseObjectIds } from "../src/chat-ledger.js";
import {
  CMD_REACT,
  CMD_REPLY,
  chunkText,
  communicationManifest,
  createAdapterHandler,
  type AdapterIdentity,
} from "../src/handler.js";
import {
  TgError,
  type SendMessageParams,
  type SendMediaParams,
  type SetMessageReactionParams,
  type TelegramApi,
  type TgUser,
} from "../src/tg-client.js";
import type { TgMessage, TgUpdate } from "../src/normalize.js";

const IDENTITY: AdapterIdentity = {
  botUserId: "777001",
  providerInstanceId: "local-telegram",
  tenantOrOrganizationId: "api.telegram.org",
};

interface Sent {
  messages: SendMessageParams[];
  photos: SendMediaParams[];
  documents: SendMediaParams[];
  reactions: SetMessageReactionParams[];
  edits: { chat_id: string | number; message_id: number; text: string }[];
}

function fakeApi(options: { reject413OnDocument?: boolean } = {}): TelegramApi & Sent {
  const sent: Sent = { messages: [], photos: [], documents: [], reactions: [], edits: [] };
  let nextId = 5000;
  const message = (): TgMessage => ({
    message_id: nextId++,
    chat: { id: -1001, type: "supergroup" },
  });
  const noop = async (): Promise<never> => {
    throw new Error("unused in the handler test");
  };
  return {
    ...sent,
    getUpdates: async (): Promise<TgUpdate[]> => [],
    deleteWebhook: async () => undefined,
    setWebhook: async () => undefined,
    getMe: async (): Promise<TgUser> => ({ id: 777001, is_bot: true, username: "facilitator" }),
    getFile: noop,
    downloadFile: noop,
    sendMessage: async (params) => {
      sent.messages.push(params);
      return message();
    },
    sendPhoto: async (params) => {
      sent.photos.push(params);
      return message();
    },
    sendDocument: async (params) => {
      sent.documents.push(params);
      if (options.reject413OnDocument === true) {
        throw new TgError("sendDocument", 413, "too large");
      }
      return message();
    },
    editMessageText: async (params) => {
      sent.edits.push(params);
      return message();
    },
    setMessageReaction: async (params) => {
      sent.reactions.push(params);
    },
  };
}

const dirs: string[] = [];
function freshState(): ChatLedger {
  const dir = mkdtempSync(join(tmpdir(), "tg-handler-"));
  dirs.push(dir);
  return new ChatLedger(join(dir, "state.sqlite3"));
}
afterEach(() => {
  for (const dir of dirs.splice(0)) rmSync(dir, { recursive: true, force: true });
});

function artifact(overrides: Partial<ArtifactRef> = {}): ArtifactRef {
  return {
    artifactId: "a1b2c3d4-0000-4000-8000-000000000001",
    mediaType: "image/png",
    size: 90,
    digest: `sha256:${"f".repeat(64)}`,
    storageUrl: "artifact://kernel/a1b2c3d4-0000-4000-8000-000000000001",
    createdBy: "link:gw",
    createdAt: new Date().toISOString(),
    ...overrides,
  };
}

function command(type: string, data: Record<string, unknown>): EnvelopeCommand {
  return {
    specversion: "1.0",
    id: "env-1",
    source: "urn:ofac:kernel",
    type,
    subject: "cmd-1",
    time: new Date().toISOString(),
    datacontenttype: "application/json",
    dataschema: "https://schemas.openfacilitator.org/v1/command.communication.reply.v1.json",
    data: {
      commandId: "cmd-1",
      idempotencyKey: "idem-1",
      target: {
        provider: "telegram",
        channelOrResourceId: "-1001",
        parentMessageId: "4242",
        conversationKind: "thread",
      },
      ...data,
    },
  };
}

function fakeCtx(published: EnvelopeEvent[]): CommandContext {
  return {
    deliveryId: "delivery-1",
    envelope: command(CMD_REPLY, {}),
    signal: new AbortController().signal,
    publishEvent: async (env) => {
      published.push(env);
      return { status: "accepted" };
    },
  };
}

const BYTES = Uint8Array.from([1, 2, 3, 4]);

function source(
  bytes: Uint8Array = BYTES,
  fail = false,
): { download: (id: string) => Promise<Uint8Array> } {
  return {
    download: async () => {
      if (fail) throw new Error("artifact gone");
      return bytes;
    },
  };
}

describe("reply idempotency law", () => {
  it("same idempotencyKey twice ⇒ the second call makes ZERO Telegram calls", async () => {
    const api = fakeApi();
    const handler = createAdapterHandler({
      api,
      state: freshState(),
      identity: IDENTITY,
      media: source(),
      logger: createMemoryLogger(),
    });
    const published: EnvelopeEvent[] = [];
    const cmd = command(CMD_REPLY, { text: "Work accepted." });
    expect(await handler.onCommand(cmd, fakeCtx(published))).toEqual({ kind: "accepted" });
    expect(api.messages).toHaveLength(1);
    expect(published).toHaveLength(1);

    expect(await handler.onCommand(cmd, fakeCtx(published))).toEqual({ kind: "accepted" });
    expect(api.messages).toHaveLength(1);
    expect(published).toHaveLength(1);
  });

  it("the recorded provider id replays out of the ledger (parseObjectIds round-trip)", async () => {
    const state = freshState();
    const api = fakeApi();
    const handler = createAdapterHandler({
      api,
      state,
      identity: IDENTITY,
      media: source(),
      logger: createMemoryLogger(),
    });
    await handler.onCommand(command(CMD_REPLY, { text: "hi" }), fakeCtx([]));
    const stored = state.findCommand("idem-1");
    expect(stored).toBeDefined();
    expect(parseObjectIds(stored!.providerObjectId)).toEqual([String(api.messages.length + 4999)]);
  });
});

describe("text chunking", () => {
  it("a 9000-char reply sends ≥ 3 sendMessage calls; only the LAST id is recorded", async () => {
    const state = freshState();
    const api = fakeApi();
    const handler = createAdapterHandler({
      api,
      state,
      identity: IDENTITY,
      media: source(),
      logger: createMemoryLogger(),
    });
    const text = "x".repeat(4000) + "\n" + "y".repeat(4000) + "\n" + "z".repeat(1000);
    expect(text).toHaveLength(9002);
    const outcome = await handler.onCommand(command(CMD_REPLY, { text }), fakeCtx([]));
    expect(outcome).toEqual({ kind: "accepted" });
    expect(api.messages.length).toBeGreaterThanOrEqual(3);
    for (const sent of api.messages) expect(sent.text.length).toBeLessThanOrEqual(4096);
    const stored = state.findCommand("idem-1");
    const ids = parseObjectIds(stored!.providerObjectId);
    expect(ids).toHaveLength(api.messages.length);
    // The last-chunk law: the array's LAST entry is the final send's id.
    expect(ids.at(-1)).toBe(String(api.messages.length + 4999));
    // threading law: reply_parameters with allow_sending_without_reply
    expect(api.messages[0]?.reply_parameters).toEqual({
      message_id: 4242,
      allow_sending_without_reply: true,
    });
  });

  it("chunkText splits on newlines and hard-splits a single oversized line", () => {
    expect(chunkText("a\nb")).toEqual(["a\nb"]);
    const hard = chunkText("x".repeat(9000));
    expect(hard.map((c) => c.length)).toEqual([4096, 4096, 808]);
  });
});

describe("outbound media", () => {
  it("one image/png ref ⇒ sendPhoto with the downloaded bytes, caption ≤ 1024", async () => {
    const api = fakeApi();
    const handler = createAdapterHandler({
      api,
      state: freshState(),
      identity: IDENTITY,
      media: source(),
      logger: createMemoryLogger(),
    });
    const caption = "done. ".repeat(500); // 3000 chars
    const outcome = await handler.onCommand(
      command(CMD_REPLY, { text: caption, artifacts: [artifact()] }),
      fakeCtx([]),
    );
    expect(outcome).toEqual({ kind: "accepted" });
    expect(api.photos).toHaveLength(1);
    const photo = api.photos[0]!;
    expect(photo.caption).toHaveLength(1024);
    expect(
      "bytes" in photo.media && Buffer.from(photo.media.bytes).equals(Buffer.from(BYTES)),
    ).toBe(true);
    expect(api.messages).toHaveLength(0);
  });

  it("a 5 MB application/pdf ref ⇒ sendDocument", async () => {
    const api = fakeApi();
    const handler = createAdapterHandler({
      api,
      state: freshState(),
      identity: IDENTITY,
      media: source(),
      logger: createMemoryLogger(),
    });
    const outcome = await handler.onCommand(
      command(CMD_REPLY, {
        text: "report attached",
        artifacts: [artifact({ mediaType: "application/pdf", size: 5 * 1024 * 1024 })],
      }),
      fakeCtx([]),
    );
    expect(outcome).toEqual({ kind: "accepted" });
    expect(api.documents).toHaveLength(1);
    expect(api.photos).toHaveLength(0);
  });

  it("a 60 MB ref ⇒ rejected artifact-too-large, ZERO posts", async () => {
    const api = fakeApi();
    const handler = createAdapterHandler({
      api,
      state: freshState(),
      identity: IDENTITY,
      media: source(),
      logger: createMemoryLogger(),
    });
    const outcome = await handler.onCommand(
      command(CMD_REPLY, {
        text: "big",
        artifacts: [artifact({ mediaType: "video/mp4", size: 60 * 1024 * 1024 })],
      }),
      fakeCtx([]),
    );
    expect(outcome).toEqual({ kind: "rejected", reason: "artifact-too-large" });
    expect(api.documents).toHaveLength(0);
    expect(api.photos).toHaveLength(0);
    expect(api.messages).toHaveLength(0);
  });

  it("a provider 413 on the send ⇒ rejected artifact-too-large", async () => {
    const api = fakeApi({ reject413OnDocument: true });
    const handler = createAdapterHandler({
      api,
      state: freshState(),
      identity: IDENTITY,
      media: source(),
      logger: createMemoryLogger(),
    });
    const outcome = await handler.onCommand(
      command(CMD_REPLY, {
        text: "big",
        artifacts: [artifact({ mediaType: "application/pdf", size: 20 * 1024 * 1024 })],
      }),
      fakeCtx([]),
    );
    expect(outcome).toEqual({ kind: "rejected", reason: "artifact-too-large" });
  });

  it("a failed download ⇒ rejected artifact-unavailable, zero posts", async () => {
    const api = fakeApi();
    const handler = createAdapterHandler({
      api,
      state: freshState(),
      identity: IDENTITY,
      media: source(BYTES, true),
      logger: createMemoryLogger(),
    });
    const outcome = await handler.onCommand(
      command(CMD_REPLY, { text: "here", artifacts: [artifact()] }),
      fakeCtx([]),
    );
    expect(outcome).toEqual({ kind: "rejected", reason: "artifact-unavailable" });
    expect(api.photos).toHaveLength(0);
    expect(api.documents).toHaveLength(0);
  });
});

describe("react law (T2)", () => {
  it("white_check_mark ⇒ setMessageReaction with ✅", async () => {
    const api = fakeApi();
    const handler = createAdapterHandler({
      api,
      state: freshState(),
      identity: IDENTITY,
      media: source(),
      logger: createMemoryLogger(),
    });
    const outcome = await handler.onCommand(
      command(CMD_REACT, { emojiName: "white_check_mark" }),
      fakeCtx([]),
    );
    expect(outcome).toEqual({ kind: "accepted" });
    expect(api.reactions).toEqual([
      { chat_id: "-1001", message_id: 4242, reaction: [{ type: "emoji", emoji: "✅" }] },
    ]);
  });

  it("an unmapped name falls back to 👀; a provider/telegram/ prefixed id strips", async () => {
    const api = fakeApi();
    const handler = createAdapterHandler({
      api,
      state: freshState(),
      identity: IDENTITY,
      media: source(),
      logger: createMemoryLogger(),
    });
    await handler.onCommand(
      command(CMD_REACT, {
        emojiName: "rocket",
        target: {
          provider: "telegram",
          channelOrResourceId: "-1001",
          subjectRef: "provider/telegram/99",
          conversationKind: "thread",
        },
      }),
      fakeCtx([]),
    );
    expect(api.reactions[0]?.reaction).toEqual([{ type: "emoji", emoji: "👀" }]);
    expect(api.reactions[0]?.message_id).toBe(99);
  });
});

describe("update law", () => {
  it("an edit to a FOREIGN message id ⇒ rejected not-adapter-message", async () => {
    const api = fakeApi();
    const handler = createAdapterHandler({
      api,
      state: freshState(),
      identity: IDENTITY,
      media: source(),
      logger: createMemoryLogger(),
    });
    const outcome = await handler.onCommand(
      command("org.facilitator.command.communication.update.v1", { text: "edited" }),
      fakeCtx([]),
    );
    expect(outcome).toEqual({ kind: "rejected", reason: "not-adapter-message" });
    expect(api.edits).toHaveLength(0);
  });

  it("an edit to an adapter-created message goes through and echoes", async () => {
    const api = fakeApi();
    const state = freshState();
    state.markAdapterMessage("4242", "-1001", "4242", Date.now());
    const handler = createAdapterHandler({
      api,
      state,
      identity: IDENTITY,
      media: source(),
      logger: createMemoryLogger(),
    });
    const published: EnvelopeEvent[] = [];
    const outcome = await handler.onCommand(
      command("org.facilitator.command.communication.update.v1", { text: "edited" }),
      fakeCtx(published),
    );
    expect(outcome).toEqual({ kind: "accepted" });
    expect(api.edits).toHaveLength(1);
    expect(published).toHaveLength(1);
  });
});

describe("dm target law (T1)", () => {
  it("an unknown dm target ⇒ rejected no-dm-channel", async () => {
    const api = fakeApi();
    const handler = createAdapterHandler({
      api,
      state: freshState(),
      identity: IDENTITY,
      media: source(),
      logger: createMemoryLogger(),
    });
    const outcome = await handler.onCommand(
      command(CMD_REPLY, {
        text: "hello",
        target: { provider: "telegram", conversationKind: "dm", actorProviderAccountId: "555" },
      }),
      fakeCtx([]),
    );
    expect(outcome).toEqual({ kind: "rejected", reason: "no-dm-channel" });
    expect(api.messages).toHaveLength(0);
  });

  it("a dm chat recorded when the human first wrote the bot resolves", async () => {
    const api = fakeApi();
    const state = freshState();
    state.setKv("dm:555", "555");
    const handler = createAdapterHandler({
      api,
      state,
      identity: IDENTITY,
      media: source(),
      logger: createMemoryLogger(),
    });
    const outcome = await handler.onCommand(
      command(CMD_REPLY, {
        text: "hello",
        target: { provider: "telegram", conversationKind: "dm", actorProviderAccountId: "555" },
      }),
      fakeCtx([]),
    );
    expect(outcome).toEqual({ kind: "accepted" });
    expect(api.messages[0]?.chat_id).toBe("555");
  });
});

describe("echo envelope", () => {
  it("after a reply, exactly one message.v1 echo publishes and validates", async () => {
    const api = fakeApi();
    const handler = createAdapterHandler({
      api,
      state: freshState(),
      identity: IDENTITY,
      media: source(),
      logger: createMemoryLogger(),
    });
    const published: EnvelopeEvent[] = [];
    await handler.onCommand(command(CMD_REPLY, { text: "On it." }), fakeCtx(published));
    expect(published).toHaveLength(1);
    const echo = published[0]!;
    expect(echo.type).toBe("org.facilitator.event.provider.message.v1");
    expect(echo.subject).toBe("provider/telegram/5000");
    const data = echo.data as Record<string, unknown>;
    expect(data["providerDeliveryId"]).toBe("5000");
    expect(data["actorProviderAccountId"]).toBe("777001");
    const verdict = validateEnvelope(echo);
    expect(verdict.ok, verdict.ok ? "" : verdict.message).toBe(true);
  });
});

describe("manifest", () => {
  it("four synchronous capabilities, dataschema-per-id, owned by the team", () => {
    const manifest = communicationManifest("team-alpha");
    expect(manifest.ownerTeam).toBe("team-alpha");
    expect(manifest.capabilities).toHaveLength(4);
    for (const capability of manifest.capabilities) {
      expect(capability.execution).toBe("synchronous");
      expect(capability.inputSchema).toBe(
        `https://schemas.openfacilitator.org/v1/${capability.id.replace(/^org\.facilitator\./, "")}.json`,
      );
    }
  });
});
