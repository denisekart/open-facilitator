// The github media + context legs against a fake GitHubApi + the stub kernel
// from @ofac/adapter/testing. OUTBOUND — a reply carrying data.artifacts
// uploads each artifact as an issue IMAGE on the uploads host (the real
// client, fetch-stubbed: the api.github.com → uploads.github.com derivation
// is asserted on the wire) and folds the comment body with
// `![name](asset_url)` markdown; a target with no resolvable issue number (or
// a failed download/upload, or the facet caps) degrades to a public link line
// and still posts the comment. INBOUND — GitHub's webhook payloads carry NO
// structured attachments field, so a comment body carrying an uploads-host
// image URL downloads through downloadAsset + ingestMedia (uniqueId = the URL
// sha256, files ledger) and the mention event publishes with sourceRefs of
// kind chat.attachment; a second sighting costs zero uploads (dedupe).
import { createHash, createHmac } from "node:crypto";
import { mkdtempSync, rmSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { afterAll, beforeAll, afterEach, describe, expect, it } from "vitest";
import Database from "better-sqlite3";
import { createMemoryLogger } from "@ofac/workspace-link";
import { startStubKernel, type StubKernel } from "@ofac/adapter/testing";
import type { ArtifactRef, ProviderMentionEventV1Data } from "@ofac/protocol";
import type { CommandContext, CommandOutcome, LinkLogger } from "@ofac/workspace-link";
import type { EnvelopeCommand } from "@ofac/protocol";
import type { WebhookDelivery, WebhookNormalizer } from "@ofac/provider-core/node";
import { AdapterState, type FileLedgerEntry } from "../src/comment-ledger.js";
import { CONTEXT_FETCH_COMMAND } from "../src/context.js";
import {
  GitHubClient,
  GitHubError,
  deriveUploadsBase,
  type GitHubApi,
  type GitHubAsset,
} from "../src/github-client.js";
import { MediaPipeline, assetHostsFor } from "../src/media.js";
import { collectAssetUrls, type NormalizeContext } from "../src/normalize.js";
import { createGitHubNormalizer } from "../src/webhook.js";
import {
  CMD_POST,
  CMD_REPLY,
  MEDIA_FACET,
  createGitHubHandler,
  githubAdapterManifest,
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
  const dir = mkdtempSync(join(tmpdir(), "ofac-gh-media-"));
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

class FakeApi implements GitHubApi {
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
  assetUrl = "https://uploads.github.com/repos/demo/widgets/issues/7/images/4242";

  async postComment(repo: string, number: number, body: string) {
    this.posts.push({ repo, number, body });
    return { id: this.nextId++, html_url: "https://github.com/x" };
  }
  async uploadIssueImage(
    repo: string,
    number: number,
    filename: string,
    bytes: Uint8Array,
    mediaType: string,
  ): Promise<GitHubAsset> {
    if (this.uploadFails) throw new GitHubError("uploadIssueImage", 500, "boom");
    this.uploads.push({ repo, number, filename, bytes: bytes.byteLength, mediaType });
    return { asset_url: this.assetUrl };
  }
  async downloadAsset(url: string): Promise<Uint8Array> {
    this.downloads.push(url);
    return PNG;
  }
  issue = { number: 7, state: "open", title: "Ship the media leg" };
  comments: { author: string; body: string; created: string }[] = [];
  reviewComments: { id: number; body: string; user: string }[] = [];
  async getIssue(): Promise<{ number: number; state: string; title: string }> {
    return this.issue;
  }
  async listIssueComments(): Promise<{ author: string; body: string; created: string }[]> {
    return this.comments;
  }
  async listReviewComments(): Promise<{ id: number; body: string; user: string }[]> {
    return this.reviewComments;
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
  async replyToReviewComment(): Promise<never> {
    throw new Error("unused");
  }
  async resolveReviewThread(): Promise<void> {
    /* unused */
  }
}

const SECRET = "gh-s3cret-hook";
const UPLOADS_URL = "https://uploads.github.com/repos/demo/widgets/issues/7/images/4242";
const CTX: NormalizeContext = {
  providerInstanceId: "github-com",
  tenantOrOrganizationId: "api.github.com",
  workLabels: new Set(["ofac:work"]),
  botUser: "ofac-bot",
  mediaArtifactsFor: () => [],
};

function commentPayload(body: string): Record<string, unknown> {
  return {
    action: "created",
    repository: { full_name: "demo/widgets" },
    sender: { login: "bob" },
    comment: { id: 10, body },
    issue: { number: 1 },
  };
}

function delivery(body: string, deliveryId: string): WebhookDelivery {
  return {
    provider: "github",
    contentType: "application/json",
    rawBody: Buffer.from(body, "utf8"),
    headers: {
      "x-hub-signature-256": `sha256=${createHmac("sha256", SECRET).update(body, "utf8").digest("hex")}`,
      "x-github-delivery": deliveryId,
      "x-github-event": "issue_comment",
    },
  };
}

function webhookHarness(
  state: AdapterState,
  api: GitHubApi,
  overrides: { maxBytes?: number } = {},
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
    endpointId: "github-adapter",
    maxBytes: overrides.maxBytes ?? 5242880,
    enabled: true,
    assetHosts: assetHostsFor("https://api.github.com"),
    logger: createMemoryLogger(),
  });
  return {
    media,
    normalizer: createGitHubNormalizer({
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

const uniqueIdOf = (url: string): string =>
  createHash("sha256").update(url).digest("hex").slice(0, 16);

// ── uploads-host derivation (the client's own law) ─────────────────────────

describe("uploads base derivation", () => {
  it("the public API uploads on its own host", () => {
    expect(deriveUploadsBase("https://api.github.com")).toBe("https://uploads.github.com");
    expect(deriveUploadsBase("https://api.github.com/")).toBe("https://uploads.github.com");
  });

  it("a GHE install uploads on the SAME host under the SAME base", () => {
    expect(deriveUploadsBase("https://ghe.example/api/v3")).toBe("https://ghe.example/api/v3");
    expect(deriveUploadsBase("http://127.0.0.1:9000/api/v3")).toBe("http://127.0.0.1:9000/api/v3");
  });

  it("the real client POSTs the raw bytes to the uploads host route", async () => {
    const calls: { url: string; init: RequestInit }[] = [];
    const client = new GitHubClient("https://api.github.com", "tok", async (url, init) => {
      calls.push({ url: String(url), init: init as RequestInit });
      return Response.json({ asset_url: UPLOADS_URL });
    });
    const asset = await client.uploadIssueImage("demo/widgets", 7, "shot.png", PNG, "image/png");
    expect(asset.asset_url).toBe(UPLOADS_URL);
    expect(calls).toHaveLength(1);
    const call = calls[0] as { url: string; init: RequestInit };
    expect(call.url).toBe("https://uploads.github.com/repos/demo/widgets/issues/7/images");
    expect(call.init.method).toBe("POST");
    const headers = call.init.headers as Record<string, string>;
    expect(headers["authorization"]).toBe("Bearer tok");
    // The uploads law: raw body (not multipart), media type + form `name`.
    expect(headers["content-type"]).toBe('image/png; name="shot.png"');
    expect(new Uint8Array(call.init.body as Uint8Array)).toEqual(PNG);
  });

  it("a GHE client keeps the /api/v3 base on the same host", async () => {
    const urls: string[] = [];
    const client = new GitHubClient("https://ghe.example/api/v3", "tok", async (url) => {
      urls.push(String(url));
      return Response.json({ asset_url: "https://ghe.example/uploads/1" });
    });
    await client.uploadIssueImage("demo/widgets", 3, "a.png", PNG, "image/png");
    expect(urls).toEqual(["https://ghe.example/api/v3/repos/demo/widgets/issues/3/images"]);
  });

  it("downloadAsset GETs an absolute URL with the token and octet-stream accept", async () => {
    const calls: { url: string; init: RequestInit }[] = [];
    const client = new GitHubClient("https://api.github.com", "tok", async (url, init) => {
      calls.push({ url: String(url), init: init as RequestInit });
      return new Response(PNG);
    });
    const bytes = await client.downloadAsset(UPLOADS_URL);
    expect(bytes).toEqual(PNG);
    const call = calls[0] as { url: string; init: RequestInit };
    expect(call.url).toBe(UPLOADS_URL);
    const headers = call.init.headers as Record<string, string>;
    expect(headers["authorization"]).toBe("Bearer tok");
    expect(headers["accept"]).toBe("application/octet-stream");
  });
});

// ── markdown asset extraction ──────────────────────────────────────────────

describe("collectAssetUrls", () => {
  const hosts = assetHostsFor("https://api.github.com");

  it("pulls uploads/github-hosted image URLs, keyed by the URL sha256", () => {
    const found = collectAssetUrls(
      `look: ![shot](${UPLOADS_URL}) and ![html](https://github.com/files/1.png)`,
      hosts,
    );
    expect(found.map((asset) => asset.url)).toEqual([
      UPLOADS_URL,
      "https://github.com/files/1.png",
    ]);
    expect(found[0]?.uniqueId).toBe(uniqueIdOf(UPLOADS_URL));
    expect(found[0]?.uniqueId).toHaveLength(16);
  });

  it("strips fragments, folds titles, drops foreign hosts and duplicates", () => {
    const found = collectAssetUrls(
      `![a](${UPLOADS_URL}#fragment) ![b](https://blog.example/x.png) ![c](${UPLOADS_URL} "t") ![rel](/uploads/1.png)`,
      hosts,
    );
    // The fragment-stripped URL repeats the first: one ref, one key.
    expect(found.map((asset) => asset.url)).toEqual([UPLOADS_URL]);
  });
});

// ── inbound ────────────────────────────────────────────────────────────────

describe("inbound markdown-hosted images", () => {
  it("comment image URL ⇒ download → ingest → mention sourceRefs (chat.attachment)", async () => {
    const state = freshState();
    const api = new FakeApi();
    const { normalizer } = webhookHarness(state, api);
    const before = requestUploads().length;

    const body = JSON.stringify(commentPayload(`@ofac-bot take a look ![shot](${UPLOADS_URL})`));
    const verdict = await normalizer(delivery(body, "dlv-1"));

    expect(api.downloads).toEqual([UPLOADS_URL]);
    expect(verdict.kind).toBe("normalized");
    const events = verdict.kind === "normalized" ? verdict.events : [];
    const refs = mentionRefs(events);
    expect(refs).toHaveLength(1);
    const uniqueId = uniqueIdOf(UPLOADS_URL);
    expect(refs[0]).toMatchObject({
      mediaType: "image/png",
      size: PNG.byteLength,
      digest: PNG_DIGEST,
      storageUrl: `artifact://provider/${uniqueId}/${refs[0]?.artifactId}`,
      createdBy: "link:github-adapter",
      kind: "chat.attachment",
    });
    // The ledger recorded BEFORE publish: the row answers the same facts.
    const row: FileLedgerEntry | undefined = state.findFile(uniqueId);
    expect(row?.artifactId).toBe(refs[0]?.artifactId);
    // The upload really landed at the stub kernel without a workId.
    expect(requestUploads().length - before).toBe(1);
    expect(kernel.artifacts.get(refs[0]?.artifactId ?? "")?.bytes.equals(Buffer.from(PNG))).toBe(
      true,
    );
    const ticket = requestUploads().at(-1);
    expect(ticket?.["workId"]).toBeUndefined();
  });

  it("dedupe: a second sighting of the same URL costs zero uploads", async () => {
    const state = freshState();
    const api = new FakeApi();
    const first = webhookHarness(state, api);
    const body = JSON.stringify(commentPayload(`@ofac-bot ![shot](${UPLOADS_URL})`));
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

  it("oversized download ⇒ no ref, event still publishes text-only", async () => {
    const state = freshState();
    const api = new FakeApi();
    const { normalizer } = webhookHarness(state, api, { maxBytes: 8 });
    const before = requestUploads().length;

    const body = JSON.stringify(commentPayload(`@ofac-bot ![shot](${UPLOADS_URL})`));
    const verdict = await normalizer(delivery(body, "dlv-3"));
    expect(verdict.kind).toBe("normalized");
    const events = verdict.kind === "normalized" ? verdict.events : [];
    expect(events.length).toBeGreaterThan(0);
    expect(mentionRefs(events)).toEqual([]);
    expect(requestUploads().length - before).toBe(0);
  });

  it("a failing download never fails the event (text-only publish)", async () => {
    const state = freshState();
    const api = new FakeApi();
    api.downloadAsset = async () => {
      throw new GitHubError("downloadAsset", 500, "boom");
    };
    const { normalizer } = webhookHarness(state, api);
    const body = JSON.stringify(commentPayload(`@ofac-bot ![shot](${UPLOADS_URL})`));
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
  type: string = CMD_REPLY,
): EnvelopeCommand {
  return {
    type,
    data: {
      commandId,
      idempotencyKey: commandId,
      text: "Work reached completed. Screenshot below.",
      target: {
        provider: "github",
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
  api: GitHubApi,
  source?: { download: (id: string) => Promise<Uint8Array> },
) {
  const state = freshState();
  return {
    state,
    handler: createGitHubHandler({
      api,
      state,
      ...(source === undefined ? {} : { media: source }),
      logger: silent,
    }),
  };
}

const pngSource = { download: async () => PNG };

describe("outbound reply-with-artifact", () => {
  it("the real client (fetch-stubbed) uploads to uploads.github.com and folds the markdown", async () => {
    const urls: string[] = [];
    const methods: string[] = [];
    const client = new GitHubClient("https://api.github.com", "tok", async (url, init) => {
      const target = String(url);
      urls.push(target);
      methods.push((init as RequestInit).method ?? "");
      if (target.includes("/images")) {
        return Response.json({ asset_url: UPLOADS_URL });
      }
      return Response.json({ id: 5, html_url: "https://github.com/x" });
    });
    const { handler } = handlerHarness(client, pngSource);
    const outcome = await handler.onCommand(
      replyWithArtifacts("r1", { rootThreadId: "7" }, [REF]),
      cmdCtx,
    );
    expect(outcome).toEqual({ kind: "accepted" });
    expect(urls).toEqual([
      "https://uploads.github.com/repos/demo/widgets/issues/7/images",
      "https://api.github.com/repos/demo/widgets/issues/7/comments",
    ]);
    expect(methods).toEqual(["POST", "POST"]);
  });

  it("an upload failure degrades to a link line and still posts the comment", async () => {
    const api = new FakeApi();
    api.uploadFails = true;
    const { handler } = handlerHarness(api, pngSource);
    const outcome = await handler.onCommand(
      replyWithArtifacts("r2", { rootThreadId: "7" }, [REF]),
      cmdCtx,
    );
    expect(outcome).toEqual({ kind: "accepted" });
    const posted = api.posts[0];
    expect(posted?.body).toContain(`attachment available: ${REF.storageUrl}`);
    expect(posted?.body).not.toContain("![");
  });

  it("no resolvable issue number ⇒ link line, the comment still posts", async () => {
    const api = new FakeApi();
    const { handler } = handlerHarness(api, pngSource);
    const outcome = await handler.onCommand(
      replyWithArtifacts("r3", { rootThreadId: "not-a-number" }, [REF]),
      cmdCtx,
    );
    expect(outcome).toEqual({ kind: "accepted" });
    expect(api.uploads).toHaveLength(0);
    expect(api.posts[0]?.body).toContain(`attachment available: ${REF.storageUrl}`);
  });

  it("no download seam ⇒ link line (CMD_POST rides the same law)", async () => {
    const api = new FakeApi();
    const { handler } = handlerHarness(api);
    const outcome = await handler.onCommand(
      replyWithArtifacts("r4", { rootThreadId: "7", conversationKind: "channel" }, [REF], CMD_POST),
      cmdCtx,
    );
    expect(outcome).toEqual({ kind: "accepted" });
    expect(api.uploads).toHaveLength(0);
    const posted = api.posts[0];
    expect(posted?.body).toContain(`attachment available: ${REF.storageUrl}`);
    // The degrade line rides the echo: what was posted IS what ledgered.
    expect(posted?.repo).toBe("demo/widgets");
  });

  it("the count cap degrades honestly past the facet's maxCount", async () => {
    const api = new FakeApi();
    const { handler } = handlerHarness(api, pngSource);
    const six = Array.from({ length: 6 }, (_, index) => ({
      ...REF,
      storageUrl: `artifact://reports/shot-${String(index)}.png`,
    }));
    const outcome = await handler.onCommand(
      replyWithArtifacts("r5", { rootThreadId: "7" }, six),
      cmdCtx,
    );
    expect(outcome).toEqual({ kind: "accepted" });
    expect(api.uploads).toHaveLength(5);
    const body = api.posts[0]?.body ?? "";
    expect(body).toContain(
      "attachment available: artifact://reports/shot-5.png (over the 5-item cap)",
    );
    expect(body.match(/!\[shot-\d\.png\]/g)).toHaveLength(5);
  });

  it("an over-size-cap artifact gets the size-cap link line, never an upload", async () => {
    const api = new FakeApi();
    const { handler } = handlerHarness(api, {
      download: async () => new Uint8Array(6 * 1024 * 1024),
    });
    const outcome = await handler.onCommand(
      replyWithArtifacts("r6", { rootThreadId: "7" }, [REF]),
      cmdCtx,
    );
    expect(outcome).toEqual({ kind: "accepted" });
    expect(api.uploads).toHaveLength(0);
    expect(api.posts[0]?.body).toContain(
      "attachment available: artifact://reports/shot.png (over the size cap)",
    );
  });

  it("a successful upload folds `![name](asset_url)` into the comment", async () => {
    const api = new FakeApi();
    const { handler, state } = handlerHarness(api, pngSource);
    const outcome = await handler.onCommand(
      replyWithArtifacts("r7", { rootThreadId: "7" }, [REF]),
      cmdCtx,
    );
    expect(outcome).toEqual({ kind: "accepted" });
    expect(api.uploads).toEqual([
      {
        repo: "demo/widgets",
        number: 7,
        filename: "shot.png",
        bytes: PNG.byteLength,
        mediaType: "image/png",
      },
    ]);
    expect(api.posts[0]?.body).toBe(
      `Work reached completed. Screenshot below.\n\n![shot.png](${UPLOADS_URL})`,
    );
    // The ledger recorded the comment; a replay mutates nothing.
    expect(state.findComment("r7")?.providerObjectId).toBe("100");
    const replay = await handler.onCommand(
      replyWithArtifacts("r7", { rootThreadId: "7" }, [REF]),
      cmdCtx,
    );
    expect(replay).toEqual({ kind: "accepted" });
    expect(api.posts).toHaveLength(1);
  });
});

// ── declared facet ──────────────────────────────────────────────────────────

describe("state schema", () => {
  it("a v1 state file migrates additively: comments survive, files appears", () => {
    const dir = mkdtempSync(join(tmpdir(), "ofac-gh-mig-"));
    dirs.push(dir);
    const path = join(dir, "state.sqlite3");
    // Forge the v1 shape by hand: the comments table, no files, user_version 1.
    const raw = new Database(path);
    raw.exec(
      "CREATE TABLE comments (idempotency_key TEXT PRIMARY KEY, provider_object_id TEXT NOT NULL, repo TEXT NOT NULL, number TEXT NOT NULL, created_at INTEGER NOT NULL); PRAGMA user_version = 1;",
    );
    raw
      .prepare("INSERT INTO comments VALUES (?, ?, ?, ?, ?)")
      .run("k1", "5", "demo/widgets", "7", 1);
    raw.close();

    const state = new AdapterState(path);
    expect(state.findComment("k1")).toMatchObject({ providerObjectId: "5", repo: "demo/widgets" });
    state.mediaLedger().remember({
      uniqueId: "u1",
      artifactId: "a1",
      mediaType: "image/png",
      size: PNG.byteLength,
      digest: PNG_DIGEST,
    });
    expect(state.findFile("u1")?.artifactId).toBe("a1");
    expect(new Database(path).pragma("user_version", { simple: true })).toBe(2);
    state.close();
  });
});

describe("declared media facet", () => {
  it("the manifest reply/post descriptors carry the plan-verbatim facet", () => {
    expect(MEDIA_FACET).toEqual({
      directions: ["in", "out"],
      kinds: ["image/*", "application/pdf", "text/*"],
      maxCount: 5,
      maxBytesPerItem: 5242880,
      semantics: { caption: false, attachmentUpload: true },
    });
    const manifest = githubAdapterManifest("team-alpha");
    const reply = manifest.capabilities.find((c) => c.id === CMD_REPLY);
    expect(reply?.media).toEqual(MEDIA_FACET);
    const post = manifest.capabilities.find((c) => c.id === CMD_POST);
    expect(post?.media).toEqual(MEDIA_FACET);
    // The rest stay honest text-only.
    const react = manifest.capabilities.find(
      (c) => c.id === "org.facilitator.command.communication.react.v1",
    );
    expect(react?.media).toEqual({ directions: ["in", "out"], kinds: [] });
  });
});

// ── context plane ───────────────────────────────────────────────────────────

describe("context.fetch answer arm", () => {
  const fetchCommand = (contextRefs: string[]): EnvelopeCommand =>
    ({
      id: "env-1",
      type: CONTEXT_FETCH_COMMAND,
      data: { commandId: "ctx-1", workId: "work-9", contextRefs },
    }) as unknown as EnvelopeCommand;

  it("answers a thread://github ref with the platform window + review leg", async () => {
    const api = new FakeApi();
    api.comments = [{ author: "bob", body: "first reaction", created: "2026-11-15T09:00:00Z" }];
    api.reviewComments = [{ id: 3, body: "inline nit", user: "carol" }];
    const published: Record<string, unknown>[] = [];
    const ctx = {
      publishEvent: async (env: unknown) => void published.push(env as Record<string, unknown>),
    };
    const handler = createGitHubHandler({
      api,
      state: freshState(),
      providerInstanceId: "github-com",
      logger: silent,
    });
    const outcome: CommandOutcome = await handler.onCommand(
      fetchCommand(["thread://github/demo/widgets/7?limit=5"]),
      ctx as unknown as CommandContext,
    );
    expect(outcome).toEqual({ kind: "accepted" });
    const env = published[0] as Record<string, unknown>;
    expect(env["type"]).toBe("org.facilitator.event.context.fetched.v1");
    const data = env["data"] as Record<string, unknown>;
    expect(data["fetchId"]).toBe("ctx-1");
    expect(data["responder"]).toBe("github-github-com");
    const items = data["items"] as { uri: string; content: { messages: unknown[] } }[];
    expect(items[0]?.uri).toBe("thread://github/demo/widgets/7?limit=5");
    // The window: the thread root + the comment + the inline review leg.
    expect(items[0]?.content.messages).toHaveLength(3);
  });

  it("unanswerable refs ride honest error items, the command still accepts", async () => {
    const published: Record<string, unknown>[] = [];
    const ctx = {
      publishEvent: async (env: unknown) => void published.push(env as Record<string, unknown>),
    };
    const handler = createGitHubHandler({
      api: undefined,
      state: freshState(),
      providerInstanceId: "github-com",
      logger: silent,
    });
    const outcome = await handler.onCommand(
      fetchCommand(["artifact://w/a/b", "thread://github/demo/widgets/notanumber"]),
      ctx as unknown as CommandContext,
    );
    expect(outcome).toEqual({ kind: "accepted" });
    const data = (published[0] as Record<string, unknown>)["data"] as Record<string, unknown>;
    const items = data["items"] as { content: Record<string, unknown> }[];
    expect(items).toHaveLength(2);
    expect(items[0]?.content["error"]).toBe("thread-unresolvable");
    expect(items[1]?.content["error"]).toBe("thread-unresolvable");
  });
});
