// The media pipeline law against a fake TelegramApi + the stub kernel from
// @ofac/adapter/testing (it answers request_upload with 202 + a ticket and
// accepts PUT /api/link/v1/artifacts/<hex>/content): two updates referencing
// the same file_unique_id ⇒ exactly one artifact; oversized media ⇒ zero
// uploads and the event still publishes (the pipeline returns without a
// ref, the normalizer's "(attachment)" law holds); the ref carries the
// kernel-returned GUID and a matching digest; the ticket request carries NO
// workId (no Work exists yet for the message).
import { mkdtempSync, rmSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { createHash } from "node:crypto";
import { beforeAll, afterAll, afterEach, describe, expect, it } from "vitest";
import { createMemoryLogger } from "@ofac/workspace-link";
import { startStubKernel, type StubKernel } from "@ofac/adapter/testing";
import { ChatLedger } from "../src/chat-ledger.js";
import { MediaPipeline } from "../src/media.js";
import type { TgMessage } from "../src/normalize.js";
import type { TelegramApi, TgFile } from "../src/tg-client.js";
import { PNG_VARIANTS } from "./fixtures/updates";

// The fixed "PNG": deterministic bytes whose sha256 the stub kernel echoes.
const PNG = Uint8Array.from(
  Buffer.from(
    "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==",
    "base64",
  ),
);

let kernel: StubKernel;
const dirs: string[] = [];

function fakeApi(options: { declaredSize?: number } = {}): TelegramApi {
  const file: TgFile = {
    file_id: "largeC",
    file_unique_id: "uniqPNG",
    file_path: "photo/file_0.jpg",
  };
  const noop = async (): Promise<never> => {
    throw new Error("unused in the media test");
  };
  return {
    getUpdates: async () => [],
    deleteWebhook: async () => undefined,
    setWebhook: async () => undefined,
    getMe: async () => ({ id: 1, is_bot: true, username: "facilitator" }),
    getFile: async () => file,
    downloadFile: async () => PNG,
    sendMessage: noop,
    sendPhoto: noop,
    sendDocument: noop,
    editMessageText: noop,
    setMessageReaction: async () => undefined,
    answerCallbackQuery: async () => undefined,
    ...(options.declaredSize === undefined
      ? {}
      : {
          getFile: async (): Promise<TgFile> => ({
            file_id: file.file_id,
            file_unique_id: file.file_unique_id,
            file_path: file.file_path ?? "",
            file_size: options.declaredSize ?? 0,
          }),
        }),
  };
}

function photoMessage(messageId: number): TgMessage {
  return {
    message_id: messageId,
    chat: { id: -1001, type: "supergroup" },
    from: { id: 5, is_bot: false },
    photo: PNG_VARIANTS.map((v) => ({ ...v, file_size: PNG.byteLength })),
  };
}

function freshState(): ChatLedger {
  const dir = mkdtempSync(join(tmpdir(), "tg-media-"));
  dirs.push(dir);
  return new ChatLedger(join(dir, "state.sqlite3"));
}

function pipeline(
  state: ChatLedger,
  api: TelegramApi,
  over: { maxBytes?: number; enabled?: boolean } = {},
): MediaPipeline {
  return new MediaPipeline({
    api,
    state,
    kernelUrl: kernel.url,
    token: async () => "link-token",
    endpointId: "tg-adapter",
    maxBytes: over.maxBytes ?? 20971520,
    enabled: over.enabled ?? true,
    logger: createMemoryLogger(),
  });
}

beforeAll(async () => {
  kernel = await startStubKernel();
});

afterAll(async () => {
  await kernel.close();
});

afterEach(() => {
  for (const dir of dirs.splice(0)) rmSync(dir, { recursive: true, force: true });
});

/** Watch the stub kernel's inbox for the request_upload commands. */
function requestUploads(): Record<string, unknown>[] {
  return kernel.events
    .filter((env) => env["type"] === "org.facilitator.command.artifact.request_upload.v1")
    .map((env) => env["data"] as Record<string, unknown>);
}

describe("media pipeline", () => {
  it("same file_unique_id across two updates ⇒ exactly one artifact, shared ref", async () => {
    const state = freshState();
    const media = pipeline(state, fakeApi());
    const before = requestUploads().length;

    const refsA = await media.processMessage(photoMessage(101));
    const refsB = await media.processMessage(photoMessage(102));

    expect(refsA).toHaveLength(1);
    expect(refsB).toHaveLength(1);
    expect(refsB[0]?.artifactId).toBe(refsA[0]?.artifactId);
    expect(requestUploads().length - before).toBe(1);
    // The stored blob's digest matches the sha256 the ref carries.
    const stored = kernel.artifacts.get(refsA[0]!.artifactId);
    expect(stored).toBeDefined();
    expect(refsA[0]?.digest).toBe("sha256:" + createHash("sha256").update(PNG).digest("hex"));
    expect(stored?.bytes.equals(Buffer.from(PNG))).toBe(true);
    // The ArtifactRef construction law.
    expect(refsA[0]).toMatchObject({
      mediaType: "image/jpeg",
      size: PNG.byteLength,
      storageUrl: `artifact://provider/uniqPNG/${refsA[0]?.artifactId}`,
      createdBy: "link:tg-adapter",
      kind: "chat.attachment",
    });
  });

  it("oversized media (declared) ⇒ zero uploads, no ref, no throw", async () => {
    const state = freshState();
    const before = requestUploads().length;
    const media = pipeline(state, fakeApi(), { maxBytes: PNG.byteLength - 1 });
    const refs = await media.processMessage(photoMessage(201));
    expect(refs).toEqual([]);
    expect(requestUploads().length - before).toBe(0);
  });

  it("oversized media (actual body over the cap) ⇒ zero uploads, event survives", async () => {
    const state = freshState();
    const before = requestUploads().length;
    const media = pipeline(state, fakeApi(), { maxBytes: PNG.byteLength - 1 });
    // No declared size on the variant ⇒ the download-time check must catch it.
    const message = photoMessage(202);
    message.photo = PNG_VARIANTS.map((v) => ({
      file_id: v.file_id,
      file_unique_id: v.file_unique_id,
    }));
    const refs = await media.processMessage(message);
    expect(refs).toEqual([]);
    expect(requestUploads().length - before).toBe(0);
  });

  it("media disabled ⇒ noted and skipped", async () => {
    const state = freshState();
    const before = requestUploads().length;
    const media = pipeline(state, fakeApi(), { enabled: false });
    expect(await media.processMessage(photoMessage(301))).toEqual([]);
    expect(requestUploads().length - before).toBe(0);
  });

  it("a failed upload never fails the event (zero refs, no throw)", async () => {
    const state = freshState();
    const broken = fakeApi();
    broken.getFile = async () => {
      throw new Error("telegram getFile failed: HTTP 500");
    };
    const media = pipeline(state, broken);
    expect(await media.processMessage(photoMessage(401))).toEqual([]);
  });

  it("the ticket request carries no workId/assignmentId", async () => {
    const state = freshState();
    const media = pipeline(state, fakeApi());
    await media.processMessage(photoMessage(501));
    const ticket = requestUploads().at(-1);
    expect(ticket).toBeDefined();
    expect(ticket?.["workId"]).toBeUndefined();
    expect(ticket?.["assignmentId"]).toBeUndefined();
    expect(ticket?.["retentionPolicy"]).toBe("90d");
    expect(ticket?.["actorEvidenceRef"]).toBe("workload:tg-adapter");
  });

  it("artifactsFor replays the ledger ref for a re-forwarded file", async () => {
    const state = freshState();
    const media = pipeline(state, fakeApi());
    await media.processMessage(photoMessage(601));
    // A fresh pipeline over the SAME ledger (restart): the dedupe ledger
    // still answers the stored artifactId.
    const restarted = pipeline(state, fakeApi());
    const refs = await restarted.processMessage(photoMessage(602));
    const recorded = state.findFile("uniqPNG");
    expect(recorded).toBeDefined();
    expect(refs[0]?.artifactId).toBe(recorded?.artifactId);
    expect(restarted.artifactsFor("602")).toEqual(refs);
  });
});
