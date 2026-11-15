// The Mattermost media leg (plan Wave 2 §2.1) against a fake MattermostApi +
// the stub kernel from @ofac/adapter/testing (it answers request_upload with
// 202 + a ticket and accepts PUT /api/link/v1/artifacts/<hex>/content). The
// four laws this file exists to prove:
//   1. ingest dedupe: the same Mattermost file id sighted twice costs ONE
//      upload (the post-ledger `files` table answers the stored artifactId);
//   2. the size cap SKIPS (structured log, zero uploads) and never fails the
//      event — the post still publishes without a ref;
//   3. an outbound reply carrying data.artifacts uploads each artifact and
//      posts with file_ids;
//   4. the degrade law: an artifact over the local cap (or an upload failure)
//      appends `attachment available: <storageUrl>` to the post text instead
//      of failing the command.
import { mkdtempSync, rmSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { createHash } from "node:crypto";
import { beforeAll, afterAll, afterEach, describe, expect, it } from "vitest";
import { createMemoryLogger, type CommandContext } from "@ofac/workspace-link";
import { startStubKernel, type StubKernel } from "@ofac/adapter/testing";
import type { ArtifactRef, EnvelopeCommand, EnvelopeEvent } from "@ofac/protocol";
import { AdapterState } from "../src/post-ledger.js";
import { MediaPipeline } from "../src/media.js";
import { createAdapterHandler, type AdapterIdentity } from "../src/handler.js";
import type { MmPost } from "../src/normalize.js";
import type { MattermostApi, WsEvent } from "../src/mm-client.js";

// The fixed "PNG": deterministic bytes whose sha256 the stub kernel echoes.
const PNG = Uint8Array.from(
  Buffer.from(
    "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==",
    "base64",
  ),
);

const IDENTITY: AdapterIdentity = {
  botUserId: "bot-id",
  providerInstanceId: "local-chat",
  tenantOrOrganizationId: "chat.localhost",
};

interface FakeApi extends MattermostApi {
  calls: {
    createPost: number;
    createPostWithFiles: number;
    uploads: string[];
    downloads: string[];
  };
  posted: { channelId: string; message: string; fileIds: readonly string[]; rootId?: string }[];
  failUpload: boolean;
}

function fakeApi(): FakeApi {
  let n = 0;
  const api: FakeApi = {
    calls: { createPost: 0, createPostWithFiles: 0, uploads: [], downloads: [] },
    posted: [],
    failUpload: false,
    me: async () => ({ id: "bot-id", username: "facilitator" }),
    channel: async (id) => ({ id, type: "O" as const }),
    createPost: async (channelId, message, rootId) => {
      api.calls.createPost += 1;
      api.posted.push({
        channelId,
        message,
        fileIds: [],
        ...(rootId === undefined ? {} : { rootId }),
      });
      return `made-post-${n++}`;
    },
    createPostWithFiles: async (channelId, message, fileIds, rootId) => {
      api.calls.createPostWithFiles += 1;
      api.posted.push({
        channelId,
        message,
        fileIds: [...fileIds],
        ...(rootId === undefined ? {} : { rootId }),
      });
      return `made-post-${n++}`;
    },
    uploadFile: async (channelId, filename) => {
      if (api.failUpload) throw new Error("mattermost uploadFile failed: HTTP 501");
      api.calls.uploads.push(`${channelId}/${filename}`);
      return { id: `file-${api.calls.uploads.length}` };
    },
    downloadFile: async (fileId) => {
      api.calls.downloads.push(fileId);
      return PNG;
    },
    createReaction: async () => "bot-id:post:emoji",
    botChannels: async () => [],
    channelPosts: async () => [],
    getPostThread: async () => ({ order: [], posts: {} }),
    missedEvents: async () => [] as WsEvent[],
    editPost: async () => undefined,
    dmChannel: async (userId) => `dm-${userId}`,
  };
  return api;
}

function filePost(postId: string, fileId: string, size = PNG.byteLength): MmPost {
  return {
    id: postId,
    channel_id: "chan-1",
    user_id: "human-id",
    message: "@facilitator look at this",
    file_infos: [{ id: fileId, name: "shot.png", size, mime_type: "image/png" }],
  };
}

let kernel: StubKernel;
const dirs: string[] = [];

function freshState(): AdapterState {
  const dir = mkdtempSync(join(tmpdir(), "mm-media-"));
  dirs.push(dir);
  return new AdapterState(join(dir, "state.sqlite3"));
}

function pipeline(
  state: AdapterState,
  api: MattermostApi,
  over: { maxBytes?: number; enabled?: boolean } = {},
): MediaPipeline {
  return new MediaPipeline({
    api,
    state,
    kernelUrl: kernel.url,
    token: async () => "link-token",
    endpointId: "mm-adapter",
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

function replyCommand(artifacts: ArtifactRef[]): EnvelopeCommand {
  return {
    specversion: "1.0",
    id: "env-media-1",
    source: "urn:ofac:kernel",
    type: "org.facilitator.command.communication.reply.v1",
    subject: "cmd-media-1",
    time: new Date().toISOString(),
    datacontenttype: "application/json",
    dataschema: "https://schemas.openfacilitator.org/v1/command.communication.reply.v1.json",
    data: {
      commandId: "cmd-media-1",
      idempotencyKey: "idem-media-1",
      text: "Work accepted.",
      artifacts,
      target: {
        provider: "mattermost",
        channelOrResourceId: "chan-1",
        rootThreadId: "root-1",
        conversationKind: "thread",
      },
    },
  };
}

function fakeCtx(published: EnvelopeEvent[]): CommandContext {
  return {
    deliveryId: "delivery-media",
    envelope: replyCommand([]),
    signal: new AbortController().signal,
    publishEvent: async (env) => {
      published.push(env);
      return { status: "accepted" };
    },
  };
}

function artifactRef(artifactId: string, size = PNG.byteLength): ArtifactRef {
  return {
    artifactId,
    mediaType: "image/png",
    size,
    digest: "sha256:" + "0".repeat(64),
    storageUrl: `artifact://provider/files/${artifactId}`,
    createdBy: "link:harness-1",
    createdAt: new Date().toISOString(),
    kind: "result.image",
  };
}

describe("mattermost media pipeline", () => {
  it("inbound: the same file id twice ⇒ one upload, refs carry the sha256", async () => {
    const state = freshState();
    const api = fakeApi();
    const media = pipeline(state, api);
    const before = requestUploads().length;

    const refsA = await media.processPost(filePost("post-a", "mmfile-1"));
    const refsB = await media.processPost(filePost("post-b", "mmfile-1"));

    expect(refsA).toHaveLength(1);
    expect(refsB).toHaveLength(1);
    expect(refsB[0]?.artifactId).toBe(refsA[0]?.artifactId);
    // The dedupe law: the second sighting costs zero downloads and zero uploads.
    expect(requestUploads().length - before).toBe(1);
    expect(api.calls.downloads).toEqual(["mmfile-1"]);
    // The stored blob's digest matches the sha256 the ref carries.
    const stored = kernel.artifacts.get(refsA[0]!.artifactId);
    expect(stored).toBeDefined();
    expect(refsA[0]?.digest).toBe("sha256:" + createHash("sha256").update(PNG).digest("hex"));
    expect(stored?.bytes.equals(Buffer.from(PNG))).toBe(true);
    // The ArtifactRef construction law.
    expect(refsA[0]).toMatchObject({
      mediaType: "image/png",
      size: PNG.byteLength,
      storageUrl: `artifact://provider/mmfile-1/${refsA[0]?.artifactId}`,
      createdBy: "link:mm-adapter",
      kind: "chat.attachment",
    });
    // The ledger row exists for the restart law.
    expect(state.mediaLedger().known("mmfile-1")?.artifactId).toBe(refsA[0]?.artifactId);
    // A restarted pipeline over the SAME ledger: still zero new uploads.
    const restarted = pipeline(state, api);
    const refsC = await restarted.processPost(filePost("post-c", "mmfile-1"));
    expect(refsC[0]?.artifactId).toBe(refsA[0]?.artifactId);
    expect(requestUploads().length - before).toBe(1);
  });

  it("oversized inbound file ⇒ logged skip, zero uploads, the event survives", async () => {
    const state = freshState();
    const api = fakeApi();
    const before = requestUploads().length;
    const media = pipeline(state, api, { maxBytes: PNG.byteLength - 1 });
    // Declared size over the cap: skipped before any download (never a throw).
    const refs = await media.processPost(filePost("post-big", "mmfile-big"));
    expect(refs).toEqual([]);
    expect(requestUploads().length - before).toBe(0);
    expect(api.calls.downloads).toEqual([]);
    // No declared size ⇒ the download-time gate catches it, still no throw.
    const noDeclared = filePost("post-big-2", "mmfile-big-2");
    noDeclared.file_infos = [{ id: "mmfile-big-2", name: "shot.png", mime_type: "image/png" }];
    expect(await media.processPost(noDeclared)).toEqual([]);
    expect(requestUploads().length - before).toBe(0);
  });

  it("outbound: a reply with two artifacts uploads both and posts with file_ids", async () => {
    const api = fakeApi();
    const state = freshState();
    const handler = createAdapterHandler({
      api,
      state,
      identity: IDENTITY,
      media: { download: async () => PNG },
      logger: createMemoryLogger(),
    });
    const published: EnvelopeEvent[] = [];
    const outcome = await handler.onCommand(
      replyCommand([artifactRef("a".repeat(32)), artifactRef("b".repeat(32))]),
      fakeCtx(published),
    );
    expect(outcome).toEqual({ kind: "accepted" });
    expect(api.calls.uploads).toEqual([
      "chan-1/artifact-aaaaaaaa.png",
      "chan-1/artifact-bbbbbbbb.png",
    ]);
    expect(api.calls.createPostWithFiles).toBe(1);
    expect(api.calls.createPost).toBe(0);
    const post = api.posted[0]!;
    expect(post.fileIds).toEqual(["file-1", "file-2"]);
    expect(post.message).toBe("Work accepted.");
    expect(post.rootId).toBe("root-1");
    // The echo carries the FINAL text (attachments law mirrors the text law).
    const echo = published[0];
    expect((echo?.data as Record<string, unknown>)["text"]).toBe("Work accepted.");
  });

  it("degrade: an over-cap artifact never fails the command — text tail instead", async () => {
    const api = fakeApi();
    const handler = createAdapterHandler({
      api,
      state: freshState(),
      identity: IDENTITY,
      media: { download: async () => PNG },
      logger: createMemoryLogger(),
    });
    const published: EnvelopeEvent[] = [];
    // 60 MB declared: over the facet's 50 MB law, so no download, no upload.
    const outcome = await handler.onCommand(
      replyCommand([artifactRef("c".repeat(32), 60 * 1024 * 1024)]),
      fakeCtx(published),
    );
    expect(outcome).toEqual({ kind: "accepted" });
    expect(api.calls.uploads).toEqual([]);
    expect(api.calls.createPostWithFiles).toBe(0);
    expect(api.calls.createPost).toBe(1);
    const post = api.posted[0]!;
    expect(post.message).toBe(
      "Work accepted.\nattachment available: artifact://provider/files/" + "c".repeat(32),
    );
    // The degrade line also rides the echo event (honesty law).
    const echo = published[0];
    expect((echo?.data as Record<string, unknown>)["text"]).toContain("attachment available: ");

    // A failed upload degrades the same way.
    const api2 = fakeApi();
    api2.failUpload = true;
    const handler2 = createAdapterHandler({
      api: api2,
      state: freshState(),
      identity: IDENTITY,
      media: { download: async () => PNG },
      logger: createMemoryLogger(),
    });
    const published2: EnvelopeEvent[] = [];
    expect(
      await handler2.onCommand(replyCommand([artifactRef("d".repeat(32))]), fakeCtx(published2)),
    ).toEqual({ kind: "accepted" });
    expect(api2.calls.createPost).toBe(1);
    expect(api2.posted[0]?.message).toContain(
      "attachment available: artifact://provider/files/" + "d".repeat(32),
    );
  });
});
