// The webhook-normalizer law + the chat-ledger tables. The normalizer:
// a wrong x-telegram-bot-api-secret-token ⇒ bad-signature (⇒ the receiver
// answers the receipt rejected); a redelivered update_id ⇒ normalized with
// ZERO events (accepted, nothing published). The ledger: the commands table
// answers the replay lookup, the messages table gates the edit law, the
// files table dedupes media, setKvOnce is exactly-once.
import { mkdtempSync, rmSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";
import Database from "better-sqlite3";
import { afterEach, describe, expect, it } from "vitest";
import { createMemoryLogger } from "@ofac/workspace-link";
import type { WebhookDelivery } from "@ofac/provider-core/node";
import { ChatLedger } from "../src/chat-ledger.js";
import { SECRET_HEADER, createWebhookNormalizer } from "../src/webhook.js";
import { MediaPipeline } from "../src/media.js";
import type { NormalizeContext } from "../src/normalize.js";
import { mentionUpdate, GROUP_ID } from "./fixtures/updates";

const dirs: string[] = [];
function freshLedger(): ChatLedger {
  const dir = mkdtempSync(join(tmpdir(), "tg-ledger-"));
  dirs.push(dir);
  return new ChatLedger(join(dir, "ledger.sqlite3"));
}
afterEach(() => {
  for (const dir of dirs.splice(0)) rmSync(dir, { recursive: true, force: true });
});

const CONTEXT: NormalizeContext = {
  botUserId: "777001",
  botUsername: "facilitator",
  providerInstanceId: "local-telegram",
  tenantOrOrganizationId: "api.telegram.org",
  chatTypeFor: () => "supergroup",
  admissionFor: (chatId) => (chatId === String(GROUP_ID) ? { admitted: true, labels: [] } : null),
  boundRootIds: () => new Set(),
  mediaArtifactsFor: () => [],
};

function delivery(secret: string, update: unknown): WebhookDelivery {
  return {
    provider: "telegram",
    contentType: "application/json",
    rawBody: Buffer.from(JSON.stringify(update), "utf8"),
    headers: { [SECRET_HEADER]: secret },
  };
}

function normalizer(state: ChatLedger) {
  const api = {
    getUpdates: async () => [],
    deleteWebhook: async () => undefined,
    setWebhook: async () => undefined,
    getMe: async () => ({ id: 1, is_bot: true, username: "f" }),
    getFile: async () => {
      throw new Error("no media in this test");
    },
    downloadFile: async () => new Uint8Array(),
    sendMessage: async () => {
      throw new Error("unused");
    },
    sendPhoto: async () => {
      throw new Error("unused");
    },
    sendDocument: async () => {
      throw new Error("unused");
    },
    editMessageText: async () => {
      throw new Error("unused");
    },
    setMessageReaction: async () => undefined,
  } as const;
  const media = new MediaPipeline({
    api,
    state,
    kernelUrl: "http://kernel.invalid",
    token: async () => "t",
    endpointId: "tg-adapter",
    maxBytes: 1024,
    enabled: true,
    logger: createMemoryLogger(),
  });
  return createWebhookNormalizer({
    secret: "s3cret",
    state,
    context: CONTEXT,
    media,
    onUnconfigured: async () => undefined,
    logger: createMemoryLogger(),
  });
}

describe("webhook normalizer law", () => {
  it("a wrong secret-token ⇒ bad-signature", async () => {
    const verdict = await normalizer(freshLedger())(delivery("wrong", mentionUpdate));
    expect(verdict).toEqual({ kind: "bad-signature" });
  });

  it("an absent token header ⇒ bad-signature", async () => {
    const base = delivery("s3cret", mentionUpdate);
    const verdict = await normalizer(freshLedger())({
      ...base,
      headers: { "content-type": "application/json" },
    });
    expect(verdict).toEqual({ kind: "bad-signature" });
  });

  it("first delivery normalizes one event; the SAME update_id again ⇒ zero published", async () => {
    const state = freshLedger();
    const normalize = normalizer(state);
    const first = await normalize(delivery("s3cret", mentionUpdate));
    expect(first.kind === "normalized" && first.events).toHaveLength(1);
    const second = await normalize(delivery("s3cret", mentionUpdate));
    expect(second).toEqual({ kind: "normalized", events: [] });
  });

  it("a non-JSON body normalizes to zero events (no throw)", async () => {
    const verdict = await normalizer(freshLedger())({
      provider: "telegram",
      contentType: "application/json",
      rawBody: Buffer.from("not json"),
      headers: { [SECRET_HEADER]: "s3cret" },
    });
    expect(verdict).toEqual({ kind: "normalized", events: [] });
  });
});

describe("chat ledger tables", () => {
  it("commands: replay lookup before mutation", () => {
    const state = freshLedger();
    expect(state.findCommand("k1")).toBeUndefined();
    state.recordCommand({
      idempotencyKey: "k1",
      providerObjectId: "5001",
      chatId: "-1001",
      rootId: "4242",
      kind: "message",
      createdAt: Date.now(),
    });
    expect(state.findCommand("k1")?.providerObjectId).toBe("5001");
    // An adapter-created message gates the edit law and binds the root.
    expect(state.isAdapterMessage("5001")).toBe(true);
    expect(state.isAdapterMessage("9999")).toBe(false);
    expect(state.boundRootIds().has("4242")).toBe(true);
    expect(state.boundRootIds().has("5001")).toBe(true);
  });

  it("topics: a recorded forum topic resolves as a thread target", () => {
    const state = freshLedger();
    state.markTopic("-1001", "42", Date.now());
    expect(state.isTopic("-1001", "42")).toBe(true);
    expect(state.isTopic("-1002", "42")).toBe(false);
    expect(state.boundRootIds().has("42")).toBe(true);
  });

  it("files: the media dedupe ledger round-trips", () => {
    const state = freshLedger();
    state.recordFile({
      fileUniqueId: "u1",
      artifactId: "a1",
      mediaType: "image/png",
      size: 10,
      digest: `sha256:${"a".repeat(64)}`,
    });
    expect(state.findFile("u1")?.artifactId).toBe("a1");
    expect(state.findFile("nope")).toBeUndefined();
  });

  it("kv: setKvOnce is exactly-once; chat types cache", () => {
    const state = freshLedger();
    expect(state.setKvOnce("notice:-100", "now")).toBe(true);
    expect(state.setKvOnce("notice:-100", "now")).toBe(false);
    state.cacheChatType("-1001", "supergroup");
    expect(state.chatType("-1001")).toBe("supergroup");
    expect(state.chatType("-1")).toBeUndefined();
  });

  it("an unsupported schema version fails closed", () => {
    const dir = mkdtempSync(join(tmpdir(), "tg-ledger-v-"));
    dirs.push(dir);
    const path = join(dir, "v.sqlite3");
    const first = new ChatLedger(path);
    first.close();
    // Reopening at the written version is fine; a forged future version is not.
    {
      const raw = new Database(path);
      raw.pragma("user_version = 99");
      raw.close();
    }
    expect(() => new ChatLedger(path)).toThrow(/unsupported schema version/);
  });
});
