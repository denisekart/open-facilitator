// The forgejo media leg (plan 2.2) against a fake ForgejoApi + the stub
// kernel from @ofac/adapter/testing: OUTBOUND — a reply carrying
// data.artifacts uploads each artifact as an issue attachment and folds the
// comment body with `![name](download_url)` markdown; a target with no
// resolvable issue number (or a failed download/upload, or the facet caps)
// degrades to a public link line and still posts the comment. INBOUND — a
// webhook comment carrying attachments[] downloads through downloadAsset +
// ingestMedia (uniqueId = attachment id, files ledger) so the mention event
// publishes with sourceRefs of kind chat.attachment; a second sighting of
// the same attachment id costs zero uploads (dedupe).
import { createHash, createHmac } from "node:crypto";
import { mkdtempSync, rmSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { afterAll, beforeAll, afterEach, describe, expect, it } from "vitest";
import { createMemoryLogger } from "@ofac/workspace-link";
import { startStubKernel, type StubKernel } from "@ofac/adapter/testing";
import type { ArtifactRef, ProviderMentionEventV1Data } from "@ofac/protocol";
import type { CommandContext, CommandOutcome, LinkLogger } from "@ofac/workspace-link";
import type { EnvelopeCommand } from "@ofac/protocol";
import type { WebhookDelivery, WebhookNormalizer } from "@ofac/provider-core/node";
import { AdapterState, type FileLedgerEntry } from "../src/comment-ledger.js";
import {
  ForgejoClient,
  ForgejoError,
  type ForgejoApi,
  type ForgejoAttachment,
} from "../src/forgejo-client.js";
import { MediaPipeline } from "../src/media.js";
import type { NormalizeContext } from "../src/normalize.js";
import { createForgejoNormalizer } from "../src/webhook.js";
import {
  CMD_REPLY,
  MEDIA_FACET,
  createForgejoHandler,
  forgejoAdapterManifest,
} from "../src/handler.js";

const silent: LinkLogger = {
  debug: () => undefined,
  info: () => undefined,
  warn: () => undefined,
  error: () => undefined,
};

// The fixed "PNG": deterministic bytes whose sha256 the stub kernel echoes.
const PNG = Uint8Array.from(
  Buffer.from(
    "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==",
    "base64",
  ),
);
const PNG_DIGEST = "sha256:" + createHash("sha256").update(PNG).digest("hex");

let kernel: StubKernel;
const dirs: string[] = [];

function freshState(): AdapterState {
  const dir = mkdtempSync(join(tmpdir(), "ofac-fj-media-"));
  dirs.push(dir);
  return new AdapterState(join(dir, "state.sqlite3"));
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

// ── fake api + webhook harness ─────────────────────────────────────────────

class FakeApi implements ForgejoApi {
  posts: { repo: string; number: number; body: string }[] = [];
  uploads: {
    repo: string;
    number: number;
    filename: string;
    bytes: number;
    mediaType: string;
  }[] = [];
  downloads: string[] = [];
  nextId = 100;
  uploadFails = false;

  async postComment(repo: string, number: number, body: string) {
    this.posts.push({ repo, number, body });
    return { id: this.nextId++, html_url: "http://git.localhost/c" };
  }
  async uploadIssueAttachment(
    repo: string,
    number: number,
    filename: string,
    bytes: Uint8Array,
    mediaType: string,
  ): Promise<ForgejoAttachment> {
    if (this.uploadFails) throw new ForgejoError("uploadIssueAttachment", 500, "boom");
    this.uploads.push({ repo, number, filename, bytes: bytes.byteLength, mediaType });
    return {
      id: 42,
      url: "http://git.localhost/api/v1/attachments/42",
      download_url: "http://git.localhost/attachments/42?download=true",
    };
  }
  async downloadAsset(url: string): Promise<Uint8Array> {
    this.downloads.push(url);
    return PNG;
  }

  async listIssueComments(): Promise<never> {
    throw new Error("unused");
  }

  reactToComment = async (): Promise<void> => undefined;
  updateComment = async (): Promise<void> => undefined;
  async createIssue(): Promise<number> {
    throw new Error("unused");
  }
  async closeIssue(): Promise<void> {
    /* unused */
  }
  async findIssueByMarker(): Promise<number | undefined> {
    throw new Error("unused");
  }
  async mergePr(): Promise<boolean> {
    throw new Error("unused");
  }
  async getPrState(): Promise<never> {
    throw new Error("unused");
  }
  async openPr(): Promise<never> {
    throw new Error("unused");
  }
  async prsForHeadSha(): Promise<number[]> {
    return [];
  }
  async commitStatus(): Promise<void> {
    /* unused */
  }
  async openPrsForHead(): Promise<number[]> {
    throw new Error("unused");
  }
  async findCommentByMarker(): Promise<never> {
    throw new Error("unused");
  }
  async getIssue(): Promise<never> {
    throw new Error("unused");
  }
  async createBranch(): Promise<void> {
    /* unused */
  }
  async addLabels(): Promise<void> {
    throw new Error("unused");
  }
  async getPullRequest(): Promise<never> {
    throw new Error("unused");
  }
  async reviewPullRequest(): Promise<never> {
    throw new Error("unused");
  }
}

const SECRET = "s3cret-hook";
const CTX: NormalizeContext = {
  providerInstanceId: "local-git",
  tenantOrOrganizationId: "git.localhost",
  workLabels: new Set(["ofac:work"]),
  botUser: "ofac-bot",
  mediaArtifactsFor: () => [],
};

function commentPayload(attachmentId: number, size: number): Record<string, unknown> {
  return {
    action: "created",
    repository: { full_name: "demo/widgets" },
    sender: { login: "bob" },
    comment: {
      id: 10,
      body: "@ofac-bot take a look at this screenshot",
      attachments: [
        {
          id: attachmentId,
          name: "shot.png",
          size,
          content_type: "image/png",
          download_url: `http://git.localhost/attachments/${String(attachmentId)}?download=true`,
          full_url: `http://git.localhost/attachments/${String(attachmentId)}`,
        },
      ],
    },
    issue: { number: 1 },
  };
}

function delivery(body: string, deliveryId: string): WebhookDelivery {
  return {
    provider: "forgejo",
    contentType: "application/json",
    rawBody: Buffer.from(body, "utf8"),
    headers: {
      "x-forgejo-signature": createHmac("sha256", SECRET).update(body, "utf8").digest("hex"),
      "x-forgejo-delivery": deliveryId,
      "x-forgejo-event": "issue_comment",
    },
  };
}

function webhookHarness(
  state: AdapterState,
  api: ForgejoApi,
): {
  media: MediaPipeline;
  normalizer: WebhookNormalizer;
} {
  const ctx: NormalizeContext = {
    ...CTX,
    // Lazy: the pipeline below exists by the time the normalizer reads refs.
    mediaArtifactsFor: (objectId) => media.artifactsFor(objectId),
  };
  const media = new MediaPipeline({
    api,
    state,
    kernelUrl: kernel.url,
    token: async () => "link-token",
    endpointId: "forgejo-adapter",
    maxBytes: 8388608,
    enabled: true,
    logger: createMemoryLogger(),
  });
  return {
    media,
    normalizer: createForgejoNormalizer({
      ctx,
      webhookSecret: SECRET,
      api,
      media,
      logger: silent,
    }),
  };
}

/** The mention event's sourceRefs (the wire type the event data carries). */
function mentionRefs(events: { type: string; data: unknown }[]): ArtifactRef[] {
  const mention = events.find((e) => e.type.endsWith("mention.v1"));
  // Named-const cast: the normalizer authors this event as the wire's
  // ProviderMentionEventV1Data; reading its sourceRefs slot needs the shape.
  const data = mention?.data as ProviderMentionEventV1Data | undefined;
  return data?.sourceRefs ?? [];
}

// ── inbound ────────────────────────────────────────────────────────────────

describe("inbound attachments", () => {
  it("comment attachments[] ⇒ download → ingest → mention sourceRefs (chat.attachment)", async () => {
    const state = freshState();
    const api = new FakeApi();
    const { normalizer } = webhookHarness(state, api);
    const before = requestUploads().length;

    const body = JSON.stringify(commentPayload(41, PNG.byteLength));
    const verdict = await normalizer(delivery(body, "dlv-1"));

    expect(api.downloads).toEqual(["http://git.localhost/attachments/41"]);
    expect(verdict.kind).toBe("normalized");
    const events = verdict.kind === "normalized" ? verdict.events : [];
    const refs = mentionRefs(events);
    expect(refs).toHaveLength(1);
    expect(refs[0]).toMatchObject({
      mediaType: "image/png",
      size: PNG.byteLength,
      digest: PNG_DIGEST,
      storageUrl: `artifact://provider/41/${refs[0]?.artifactId}`,
      createdBy: "link:forgejo-adapter",
      kind: "chat.attachment",
    });
    // The ledger recorded BEFORE publish: the row answers the same facts.
    const row: FileLedgerEntry | undefined = state.findFile("41");
    expect(row?.artifactId).toBe(refs[0]?.artifactId);
    // The upload really landed at the stub kernel without a workId.
    expect(requestUploads().length - before).toBe(1);
    expect(kernel.artifacts.get(refs[0]?.artifactId ?? "")?.bytes.equals(Buffer.from(PNG))).toBe(
      true,
    );
    const ticket = requestUploads().at(-1);
    expect(ticket?.["workId"]).toBeUndefined();
  });

  it("dedupe: a second sighting of the same attachment id costs zero uploads", async () => {
    const state = freshState();
    const api = new FakeApi();
    const first = webhookHarness(state, api);
    const body = JSON.stringify(commentPayload(42, PNG.byteLength));
    await first.normalizer(delivery(body, "dlv-1"));
    const before = requestUploads().length;

    // Redelivery through a FRESH pipeline (restart law): the files ledger
    // still answers the stored artifactId — no second upload.
    const second = webhookHarness(state, new FakeApi());
    const verdict = await second.normalizer(delivery(body, "dlv-2"));
    const events = verdict.kind === "normalized" ? verdict.events : [];
    const stored = first.media.artifactsFor("10")[0]?.artifactId;
    expect(mentionRefs(events)[0]?.artifactId).toBe(stored);
    expect(requestUploads().length - before).toBe(0);
  });

  it("oversized attachment (declared) ⇒ no download, no ref, event still publishes", async () => {
    const state = freshState();
    const api = new FakeApi();
    const { normalizer } = webhookHarness(state, api);
    const before = requestUploads().length;

    const body = JSON.stringify(commentPayload(43, 20_000_000));
    const verdict = await normalizer(delivery(body, "dlv-3"));
    expect(verdict.kind).toBe("normalized");
    const events = verdict.kind === "normalized" ? verdict.events : [];
    expect(events.length).toBeGreaterThan(0);
    expect(mentionRefs(events)).toEqual([]);
    expect(api.downloads).toEqual([]);
    expect(requestUploads().length - before).toBe(0);
  });

  it("a failing download never fails the event (text-only publish)", async () => {
    const state = freshState();
    const api = new FakeApi();
    api.downloadAsset = async () => {
      throw new ForgejoError("downloadAsset", 500, "boom");
    };
    const { normalizer } = webhookHarness(state, api);
    const body = JSON.stringify(commentPayload(44, PNG.byteLength));
    const verdict = await normalizer(delivery(body, "dlv-4"));
    expect(verdict.kind).toBe("normalized");
    const events = verdict.kind === "normalized" ? verdict.events : [];
    expect(mentionRefs(events)).toEqual([]);
  });
});

// ── outbound ───────────────────────────────────────────────────────────────

const REF: ArtifactRef = {
  artifactId: "01JB0YA1B2C3D4E5F6G7H8J9K0",
  mediaType: "image/png",
  size: PNG.byteLength,
  digest: PNG_DIGEST,
  storageUrl: "artifact://reports/shot.png",
  createdBy: "link.gw-coding",
  createdAt: "2026-11-15T09:05:00Z",
  kind: "chat.attachment",
};

function replyWithArtifacts(
  commandId: string,
  target: Record<string, unknown>,
  artifacts: ArtifactRef[],
): EnvelopeCommand {
  return {
    type: CMD_REPLY,
    data: {
      commandId,
      idempotencyKey: commandId,
      text: "Work reached completed. Screenshot below.",
      target: {
        provider: "forgejo",
        conversationKind: "comment",
        channelOrResourceId: "demo/widgets",
        ...target,
      },
      artifacts,
    },
  } as unknown as EnvelopeCommand;
}

const cmdCtx = { publishEvent: async () => undefined } as unknown as CommandContext;

function handlerHarness(
  api: ForgejoApi,
  source?: { download: (id: string) => Promise<Uint8Array> },
) {
  const state = freshState();
  return {
    state,
    handler: createForgejoHandler({
      api,
      state,
      ...(source === undefined ? {} : { media: source }),
      logger: silent,
    }),
  };
}

describe("outbound reply-with-artifact", () => {
  it("artifacts upload as issue attachments folded into the comment markdown", async () => {
    const api = new FakeApi();
    const { handler, state } = handlerHarness(api, { download: async () => PNG });
    try {
      const outcome: CommandOutcome = await handler.onCommand(
        replyWithArtifacts("cmd-a1", { rootThreadId: "1" }, [REF]),
        cmdCtx,
      );
      expect(outcome).toEqual({ kind: "accepted" });
      expect(api.uploads).toEqual([
        {
          repo: "demo/widgets",
          number: 1,
          filename: "shot.png",
          bytes: PNG.byteLength,
          mediaType: "image/png",
        },
      ]);
      expect(api.posts).toHaveLength(1);
      expect(api.posts[0]?.body).toBe(
        "Work reached completed. Screenshot below.\n\n![shot.png](http://git.localhost/attachments/42?download=true)",
      );
    } finally {
      state.close();
    }
  });

  it("no issue context (non-numeric target) ⇒ degrade to a public link line", async () => {
    const api = new FakeApi();
    const { handler, state } = handlerHarness(api, { download: async () => PNG });
    try {
      const outcome = await handler.onCommand(
        replyWithArtifacts("cmd-a2", { rootThreadId: "review-99" }, [REF]),
        cmdCtx,
      );
      expect(outcome).toEqual({ kind: "accepted" });
      expect(api.uploads).toHaveLength(0);
      expect(api.posts[0]?.body).toContain("attachment available: artifact://reports/shot.png");
      expect(api.posts[0]?.body).not.toContain("![");
    } finally {
      state.close();
    }
  });

  it("no content route (media seam absent) ⇒ every artifact degrades to a link line", async () => {
    const api = new FakeApi();
    const { handler, state } = handlerHarness(api);
    try {
      await handler.onCommand(replyWithArtifacts("cmd-a3", { rootThreadId: "1" }, [REF]), cmdCtx);
      expect(api.uploads).toHaveLength(0);
      expect(api.posts[0]?.body).toContain("attachment available: artifact://reports/shot.png");
    } finally {
      state.close();
    }
  });

  it("a failed upload degrades to a link line, never fails the reply", async () => {
    const api = new FakeApi();
    api.uploadFails = true;
    const { handler, state } = handlerHarness(api, { download: async () => PNG });
    try {
      const outcome = await handler.onCommand(
        replyWithArtifacts("cmd-a4", { rootThreadId: "1" }, [REF]),
        cmdCtx,
      );
      expect(outcome).toEqual({ kind: "accepted" });
      expect(api.posts[0]?.body).toContain("attachment available: artifact://reports/shot.png");
    } finally {
      state.close();
    }
  });

  it("the facet caps hold: the 9th artifact rides a capped link line", async () => {
    const api = new FakeApi();
    const { handler, state } = handlerHarness(api, { download: async () => PNG });
    try {
      const nine = Array.from({ length: 9 }, (_, i) => ({
        ...REF,
        artifactId: `art-${String(i)}`,
        storageUrl: `artifact://reports/shot-${String(i)}.png`,
      }));
      await handler.onCommand(replyWithArtifacts("cmd-a5", { rootThreadId: "1" }, nine), cmdCtx);
      expect(api.uploads).toHaveLength(8);
      expect(api.posts[0]?.body).toContain(
        "attachment available: artifact://reports/shot-8.png (over the 8-item cap)",
      );
    } finally {
      state.close();
    }
  });
});

// ── client legs + facet ────────────────────────────────────────────────────

describe("ForgejoClient media legs", () => {
  interface Call {
    url: string;
    init: RequestInit;
  }
  const calls: Call[] = [];
  let status = 201;
  let json = "{}";
  const fetchImpl = (async (url: string | URL | Request, init?: RequestInit) => {
    calls.push({ url: String(url), init: init ?? {} });
    return new Response(json, {
      status,
      headers: { "content-type": "application/json" },
    });
  }) as unknown as typeof fetch;

  afterEach(() => {
    calls.length = 0;
    status = 201;
    json = "{}";
  });

  it("uploadIssueAttachment: multipart POST to the attachments route with the token", async () => {
    json = JSON.stringify({
      id: 7,
      url: "http://git.localhost/api/v1/attachments/7",
      download_url: "http://git.localhost/attachments/7?download=true",
    });
    const client = new ForgejoClient("http://git.localhost", "tok", fetchImpl);
    const created = await client.uploadIssueAttachment(
      "demo/widgets",
      1,
      "shot.png",
      PNG,
      "image/png",
    );
    expect(created).toEqual({
      id: 7,
      url: "http://git.localhost/api/v1/attachments/7",
      download_url: "http://git.localhost/attachments/7?download=true",
    });
    const call = calls[0];
    expect(call?.url).toBe("http://git.localhost/api/v1/repos/demo/widgets/issues/1/attachments");
    expect(call?.init["method"]).toBe("POST");
    // Named-const cast: the client authors plain header records.
    const headers = call?.init["headers"] as Record<string, string>;
    expect(headers["authorization"]).toBe("token tok");
    // No content-type: fetch mints the multipart boundary itself.
    expect(headers["content-type"]).toBeUndefined();
    const form = call?.init["body"] as FormData;
    expect(form).toBeInstanceOf(FormData);
    const file = form.get("file") as File;
    expect(file.name).toBe("shot.png");
    expect(file.type).toBe("image/png");
    expect((await file.arrayBuffer()).byteLength).toBe(PNG.byteLength);
  });

  it("uploadIssueAttachment: a non-2xx answers a ForgejoError naming the call", async () => {
    status = 422;
    json = "field 'file' required";
    const client = new ForgejoClient("http://git.localhost", "tok", fetchImpl);
    await expect(
      client.uploadIssueAttachment("demo/widgets", 1, "a.png", PNG, "image/png"),
    ).rejects.toMatchObject({ name: "ForgejoError", call: "uploadIssueAttachment", status: 422 });
  });

  it("downloadAsset: GET the ABSOLUTE url with the token header, bytes out", async () => {
    status = 200;
    const client = new ForgejoClient("http://git.localhost", "tok", fetchImpl);
    const bytes = await client.downloadAsset("http://git.localhost/attachments/9");
    const call = calls[0];
    expect(call?.url).toBe("http://git.localhost/attachments/9");
    // Named-const cast: the client authors plain header records.
    const headers = call?.init["headers"] as Record<string, string>;
    expect(headers["authorization"]).toBe("token tok");
    expect(bytes).toBeInstanceOf(Uint8Array);
    expect(calls.length).toBe(1);
  });

  it("downloadAsset: a non-2xx answers a ForgejoError naming the call", async () => {
    status = 404;
    const client = new ForgejoClient("http://git.localhost", "tok", fetchImpl);
    await expect(client.downloadAsset("http://git.localhost/attachments/9")).rejects.toMatchObject({
      name: "ForgejoError",
      call: "downloadAsset",
      status: 404,
    });
  });
});

describe("declared media facet", () => {
  it("the manifest reply descriptor carries the plan-verbatim facet", () => {
    expect(MEDIA_FACET).toEqual({
      directions: ["in", "out"],
      kinds: ["image/*", "application/pdf", "text/*"],
      maxCount: 8,
      maxBytesPerItem: 8388608,
      semantics: { caption: false, attachmentUpload: true },
    });
    const manifest = forgejoAdapterManifest("team-alpha");
    const reply = manifest.capabilities.find((c) => c.id === CMD_REPLY);
    expect(reply?.media).toEqual(MEDIA_FACET);
    // The rest stay honest text-only.
    const react = manifest.capabilities.find(
      (c) => c.id === "org.facilitator.command.communication.react.v1",
    );
    expect(react?.media).toEqual({ directions: ["in", "out"], kinds: [] });
  });
});
