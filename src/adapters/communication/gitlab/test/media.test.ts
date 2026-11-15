// The gitlab media + context legs against a fake GitLabApi (and, for the
// client legs, the REAL GitLabClient behind a stubbed fetch) + the stub
// kernel from @ofac/adapter/testing: OUTBOUND — a reply carrying
// data.artifacts uploads each file into the project's upload store and folds
// GitLab's OWN markdown line into the note body; a target with no resolvable
// IID (or a failed download/upload, or the facet caps, or no seam) degrades
// to a public link line and still posts the note. INBOUND — a webhook note
// whose markdown carries a `/uploads/` image downloads through
// downloadAsset + ingestMedia (uniqueId = sha256-of-URL first 16, files
// ledger) so the mention event publishes with sourceRefs of kind
// chat.attachment; a second sighting of the same URL costs zero uploads
// (dedupe). CONTEXT — a routed thread://gitlab/<repo>/<iid> fetch answers
// event.context.fetched.v1 with the platform window, honest error items for
// anything unresolvable.
import { createHash } from "node:crypto";
import { mkdtempSync, rmSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { afterEach, beforeAll, afterAll, describe, expect, it } from "vitest";
import { createMemoryLogger } from "@ofac/workspace-link";
import { startStubKernel, type StubKernel } from "@ofac/adapter/testing";
import type { ArtifactRef, EnvelopeCommand, ProviderMentionEventV1Data } from "@ofac/protocol";
import type { CommandContext, CommandOutcome, LinkLogger, LinkHandler } from "@ofac/workspace-link";
import type { WebhookDelivery, WebhookNormalizer } from "@ofac/provider-core/node";
import { AdapterState } from "../src/comment-ledger.js";
import {
  GitLabClient,
  GitLabError,
  type GitLabApi,
  type GitLabIssueNote,
} from "../src/gitlab-client.js";
import { MediaPipeline, uploadUniqueId } from "../src/media.js";
import { CONTEXT_FETCH_COMMAND, parseThreadUri } from "../src/context.js";
import type { NormalizeContext, NormalizedEvent } from "../src/normalize.js";
import {
  CMD_REPLY,
  MEDIA_FACET,
  createGitLabHandler,
  createGitLabNormalizer,
  gitlabAdapterManifest,
} from "../src/index.js";

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

const TOKEN = "s3cret-hook";
const dirs: string[] = [];

function freshState(): AdapterState {
  const dir = mkdtempSync(join(tmpdir(), "ofac-gl-media-"));
  dirs.push(dir);
  return new AdapterState(join(dir, "state.sqlite3"));
}

let kernel: StubKernel;

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

class FakeApi implements GitLabApi {
  issueNotes: { repo: string; iid: number; body: string; id: number }[] = [];
  mrNotes: { repo: string; iid: number; body: string; id: number }[] = [];
  uploads: { repo: string; filename: string; bytes: number; mediaType: string }[] = [];
  downloads: string[] = [];
  uploadFails = false;
  downloadFails = false;
  notes: GitLabIssueNote[] = [];
  notesRepo: { repo: string; iid: number; limit: number }[] = [];
  notesFail = false;
  private next = 100;

  postIssueNote = async (repo: string, iid: number, body: string) => {
    const note = { repo, iid, body, id: this.next++ };
    this.issueNotes.push(note);
    return { id: note.id, web_url: "" };
  };
  postMrNote = async (repo: string, iid: number, body: string) => {
    const note = { repo, iid, body, id: this.next++ };
    this.mrNotes.push(note);
    return { id: note.id, web_url: "" };
  };
  uploadProjectFile = async (
    repo: string,
    filename: string,
    bytes: Uint8Array,
    mediaType: string,
  ) => {
    if (this.uploadFails) throw new GitLabError("uploadProjectFile", 500, "boom");
    this.uploads.push({ repo, filename, bytes: bytes.byteLength, mediaType });
    const secret = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    return {
      alt: filename,
      url: `/uploads/${secret}/${filename}`,
      markdown: `![${filename}](/uploads/${secret}/${filename})`,
    };
  };
  downloadAsset = async (url: string): Promise<Uint8Array> => {
    this.downloads.push(url);
    if (this.downloadFails) throw new GitLabError("downloadAsset", 500, "boom");
    return PNG;
  };
  listIssueNotes = async (repo: string, iid: number, limit: number): Promise<GitLabIssueNote[]> => {
    this.notesRepo.push({ repo, iid, limit });
    if (this.notesFail) throw new GitLabError("listIssueNotes", 404, "no such issue");
    return this.notes;
  };

  awardIssueNoteEmoji = async (): Promise<void> => undefined;
  awardMrNoteEmoji = async (): Promise<void> => undefined;
  editIssueNote = async (): Promise<void> => undefined;
  editMrNote = async (): Promise<void> => undefined;
  openMr = async () => ({ iid: 1, source_branch: "" });
  openMrsForSource = async () => [] as number[];
  openMrsForSha = async () => [] as number[];
  findNoteByMarker = async () => undefined;
  getIssue = async () => ({ iid: 1, state: "opened", title: "" });
  createIssue = async () => 1;
  closeIssue = async (): Promise<void> => undefined;
  findIssueByMarker = async () => undefined;
  mergeMr = async () => true;
  addLabels = async (): Promise<void> => undefined;
  getMergeRequest = async () => ({
    iid: 1,
    state: "opened",
    title: "",
    merged: false,
    sha: "",
    source_branch: "",
  });
  createBranch = async (): Promise<void> => undefined;
  commitStatus = async (): Promise<void> => undefined;
  reviewMergeRequest = async () => ({ id: 1 });
  replyDiscussion = async () => "1";
  resolveDiscussion = async (): Promise<void> => undefined;
}

function noteHookWithUpload(uploadPath: string, noteId: number): Record<string, unknown> {
  return {
    object_kind: "note",
    event_type: "note",
    user: { id: 2, username: "bob" },
    project: { id: 5, path_with_namespace: "gitlab-org/gitlab-test" },
    object_attributes: {
      id: noteId,
      note: `@facilitator-bot look: ![image.png](${uploadPath})`,
      noteable_type: "Issue",
      author_id: 2,
      system: false,
    },
    issue: { id: 92, iid: 23 },
  };
}

function delivery(body: string, deliveryId: string): WebhookDelivery {
  return {
    provider: "gitlab",
    contentType: "application/json",
    rawBody: Buffer.from(body, "utf8"),
    headers: {
      "x-gitlab-token": TOKEN,
      "x-gitlab-event": "Note Hook",
      "x-request-id": deliveryId,
    },
  };
}

function webhookHarness(
  state: AdapterState,
  api: GitLabApi,
): { media: MediaPipeline; normalizer: WebhookNormalizer } {
  const ctx: NormalizeContext = {
    providerInstanceId: "local-gitlab",
    tenantOrOrganizationId: "git.localhost",
    workLabels: new Set(["ofac:work"]),
    botUser: "facilitator-bot",
    // Lazy: the pipeline below exists by the time the normalizer reads refs.
    mediaArtifactsFor: (objectId) => media.artifactsFor(objectId),
  };
  const media = new MediaPipeline({
    api,
    state,
    kernelUrl: kernel.url,
    token: async () => "link-token",
    endpointId: "gitlab-adapter",
    maxBytes: 10485760,
    enabled: true,
    logger: createMemoryLogger(),
  });
  return {
    media,
    normalizer: createGitLabNormalizer({ ctx, webhookToken: TOKEN, api, media, logger: silent }),
  };
}

/** The mention event's sourceRefs (the wire type the event data carries). */
function mentionRefs(events: NormalizedEvent[]): ArtifactRef[] {
  const mention = events.find((e) => e.type.endsWith("mention.v1"));
  // Named-const cast: the normalizer authors this event as the wire's
  // ProviderMentionEventV1Data; reading its sourceRefs slot needs the shape.
  const data = mention?.data as ProviderMentionEventV1Data | undefined;
  return data?.sourceRefs ?? [];
}

// ── inbound ────────────────────────────────────────────────────────────────

describe("inbound /uploads markdown", () => {
  it("note markdown image ⇒ download → ingest → mention sourceRefs (chat.attachment)", async () => {
    const state = freshState();
    const api = new FakeApi();
    const { normalizer } = webhookHarness(state, api);
    const before = requestUploads().length;

    const body = JSON.stringify(noteHookWithUpload("/uploads/secret1/image.png", 1241));
    const verdict = await normalizer(delivery(body, "dlv-1"));

    expect(api.downloads).toEqual(["/uploads/secret1/image.png"]);
    expect(verdict.kind).toBe("normalized");
    const events = verdict.kind === "normalized" ? verdict.events : [];
    const refs = mentionRefs(events);
    expect(refs).toHaveLength(1);
    const uniqueId = uploadUniqueId("/uploads/secret1/image.png");
    // The dedupe key law: sha256 of the full URL, hex first 16.
    expect(uniqueId).toBe(
      createHash("sha256").update("/uploads/secret1/image.png").digest("hex").slice(0, 16),
    );
    expect(refs[0]).toMatchObject({
      mediaType: "image/png",
      size: PNG.byteLength,
      digest: PNG_DIGEST,
      storageUrl: `artifact://provider/${uniqueId}/${refs[0]?.artifactId}`,
      createdBy: "link:gitlab-adapter",
      kind: "chat.attachment",
    });
    // The ledger recorded BEFORE publish: the row answers the same facts.
    const row = state.findFile(uniqueId);
    expect(row?.artifactId).toBe(refs[0]?.artifactId);
    // The upload really landed at the stub kernel without a workId.
    expect(requestUploads().length - before).toBe(1);
    expect(kernel.artifacts.get(refs[0]?.artifactId ?? "")?.bytes.equals(Buffer.from(PNG))).toBe(
      true,
    );
    const ticket = requestUploads().at(-1);
    expect(ticket?.["workId"]).toBeUndefined();
  });

  it("dedupe: a second sighting of the same URL costs zero uploads (restart law)", async () => {
    const state = freshState();
    const api = new FakeApi();
    const first = webhookHarness(state, api);
    const body = JSON.stringify(noteHookWithUpload("/uploads/secret2/image.png", 1242));
    await first.normalizer(delivery(body, "dlv-1"));
    const before = requestUploads().length;

    // Redelivery through a FRESH pipeline (restart law): the files ledger
    // still answers the stored artifactId — no second upload.
    const second = webhookHarness(state, new FakeApi());
    const verdict = await second.normalizer(delivery(body, "dlv-2"));
    const events = verdict.kind === "normalized" ? verdict.events : [];
    const stored = first.media.artifactsFor("1242")[0]?.artifactId;
    expect(mentionRefs(events)[0]?.artifactId).toBe(stored);
    expect(requestUploads().length - before).toBe(0);
  });

  it("a failing download never fails the event (text-only publish)", async () => {
    const state = freshState();
    const api = new FakeApi();
    api.downloadFails = true;
    const { normalizer } = webhookHarness(state, api);
    const body = JSON.stringify(noteHookWithUpload("/uploads/secret3/image.png", 1243));
    const verdict = await normalizer(delivery(body, "dlv-3"));
    expect(verdict.kind).toBe("normalized");
    const events = verdict.kind === "normalized" ? verdict.events : [];
    // The events still publish — the text is whole, only the ref is gone.
    expect(events.length).toBeGreaterThan(0);
    expect(mentionRefs(events)).toEqual([]);
  });

  it("oversized bytes (actual) ⇒ skipped, event publishes without the ref", async () => {
    const state = freshState();
    const api = new FakeApi();
    api.downloadAsset = async () => new Uint8Array(11 * 1024 * 1024);
    const { normalizer } = webhookHarness(state, api);
    const before = requestUploads().length;
    const body = JSON.stringify(noteHookWithUpload("/uploads/secret4/big.png", 1244));
    const verdict = await normalizer(delivery(body, "dlv-4"));
    expect(verdict.kind).toBe("normalized");
    const events = verdict.kind === "normalized" ? verdict.events : [];
    expect(events.length).toBeGreaterThan(0);
    expect(mentionRefs(events)).toEqual([]);
    expect(requestUploads().length - before).toBe(0);
  });

  it("non-/uploads markdown rides no download at all", async () => {
    const state = freshState();
    const api = new FakeApi();
    const { normalizer } = webhookHarness(state, api);
    const payload = noteHookWithUpload("/uploads/secret5/image.png", 1245);
    // Swap the upload markdown for a plain link image + a link.
    const attrs = payload["object_attributes"] as Record<string, unknown>;
    attrs["note"] =
      "@facilitator-bot see ![logo](http://cdn.localhost/logo.png) and [x](/uploads/)";
    const verdict = await normalizer(delivery(JSON.stringify(payload), "dlv-5"));
    expect(verdict.kind).toBe("normalized");
    // /uploads/ alone (no path after it) still matches the contains-law; the
    // CDN image never does — one download at most, for the /uploads/ URL.
    expect(api.downloads.every((u) => u.includes("/uploads/"))).toBe(true);
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
        provider: "gitlab",
        conversationKind: "comment",
        channelOrResourceId: "gitlab-org/gitlab-test",
        ...target,
      },
      artifacts,
    },
  } as unknown as EnvelopeCommand;
}

const cmdCtx = { publishEvent: async () => undefined } as unknown as CommandContext;

function handlerHarness(
  api: GitLabApi,
  source?: { download: (id: string) => Promise<Uint8Array> },
): { state: AdapterState; handler: LinkHandler } {
  const state = freshState();
  return {
    state,
    handler: createGitLabHandler({
      api,
      state,
      providerInstanceId: "local-gitlab",
      ...(source === undefined ? {} : { media: source }),
      logger: silent,
    }),
  };
}

describe("outbound reply-with-artifact", () => {
  it("artifacts upload as project files folded in as GitLab's markdown line", async () => {
    const api = new FakeApi();
    const { handler, state } = handlerHarness(api, { download: async () => PNG });
    try {
      const outcome: CommandOutcome = await handler.onCommand(
        replyWithArtifacts("cmd-a1", { rootThreadId: "23" }, [REF]),
        cmdCtx,
      );
      expect(outcome).toEqual({ kind: "accepted" });
      expect(api.uploads).toEqual([
        {
          repo: "gitlab-org/gitlab-test",
          filename: "shot.png",
          bytes: PNG.byteLength,
          mediaType: "image/png",
        },
      ]);
      expect(api.issueNotes).toHaveLength(1);
      expect(api.issueNotes[0]?.body).toContain(
        "![shot.png](/uploads/aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa/shot.png)",
      );
    } finally {
      state.close();
    }
  });

  it("no resolvable IID (non-numeric target) ⇒ degrade to a public link line", async () => {
    const api = new FakeApi();
    const { handler, state } = handlerHarness(api, { download: async () => PNG });
    try {
      const outcome = await handler.onCommand(
        replyWithArtifacts("cmd-a2", { rootThreadId: "discussion-99" }, [REF]),
        cmdCtx,
      );
      expect(outcome).toEqual({ kind: "accepted" });
      expect(api.uploads).toHaveLength(0);
      expect(api.issueNotes[0]?.body).toContain(
        "attachment available: artifact://reports/shot.png",
      );
      expect(api.issueNotes[0]?.body).not.toContain("![");
    } finally {
      state.close();
    }
  });

  it("no content route (media seam absent) ⇒ every artifact degrades to a link line", async () => {
    const api = new FakeApi();
    const { handler, state } = handlerHarness(api);
    try {
      await handler.onCommand(replyWithArtifacts("cmd-a3", { rootThreadId: "23" }, [REF]), cmdCtx);
      expect(api.uploads).toHaveLength(0);
      expect(api.issueNotes[0]?.body).toContain(
        "attachment available: artifact://reports/shot.png",
      );
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
        replyWithArtifacts("cmd-a4", { rootThreadId: "23" }, [REF]),
        cmdCtx,
      );
      expect(outcome).toEqual({ kind: "accepted" });
      expect(api.issueNotes[0]?.body).toContain(
        "attachment available: artifact://reports/shot.png",
      );
    } finally {
      state.close();
    }
  });

  it("the facet caps hold: the 11th artifact rides a capped link line", async () => {
    const api = new FakeApi();
    const { handler, state } = handlerHarness(api, { download: async () => PNG });
    try {
      const eleven = Array.from({ length: 11 }, (_, i) => ({
        ...REF,
        artifactId: `art-${String(i)}`,
        storageUrl: `artifact://reports/shot-${String(i)}.png`,
      }));
      await handler.onCommand(replyWithArtifacts("cmd-a5", { rootThreadId: "23" }, eleven), cmdCtx);
      expect(api.uploads).toHaveLength(10);
      expect(api.issueNotes[0]?.body).toContain(
        "attachment available: artifact://reports/shot-10.png (over the 10-item cap)",
      );
    } finally {
      state.close();
    }
  });

  it("the MR fallback leg carries the folded body too (issue 404 ⇒ MR note)", async () => {
    const api = new FakeApi();
    const throwingIssue = new Proxy(api, {
      get(target, prop) {
        if (prop === "postIssueNote") {
          return async (): Promise<never> => {
            throw new GitLabError("postIssueNote", 404, "not an issue");
          };
        }
        const value = Reflect.get(target, prop, target);
        return typeof value === "function" ? (value as () => unknown).bind(target) : value;
      },
    }) as unknown as GitLabApi;
    const { handler, state } = handlerHarness(throwingIssue, { download: async () => PNG });
    try {
      const outcome = await handler.onCommand(
        replyWithArtifacts("cmd-a6", { rootThreadId: "16" }, [REF]),
        cmdCtx,
      );
      expect(outcome).toEqual({ kind: "accepted" });
      expect(api.mrNotes).toHaveLength(1);
      expect(api.mrNotes[0]?.body).toContain("![shot.png](/uploads/");
    } finally {
      state.close();
    }
  });
});

// ── client legs ────────────────────────────────────────────────────────────

describe("GitLabClient media legs (fetch-stubbed real client)", () => {
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

  it("uploadProjectFile: multipart POST to the uploads route with the token", async () => {
    json = JSON.stringify({
      alt: "shot.png",
      url: "/uploads/secretA/shot.png",
      markdown: "![shot.png](/uploads/secretA/shot.png)",
    });
    const client = new GitLabClient("http://git.localhost", "tok", fetchImpl);
    const created = await client.uploadProjectFile("group/proj", "shot.png", PNG, "image/png");
    expect(created).toEqual({
      alt: "shot.png",
      url: "/uploads/secretA/shot.png",
      markdown: "![shot.png](/uploads/secretA/shot.png)",
    });
    const call = calls[0];
    expect(call?.url).toBe("http://git.localhost/api/v4/projects/group%2Fproj/uploads");
    expect(call?.init["method"]).toBe("POST");
    // Named-const cast: the client authors plain header records.
    const headers = call?.init["headers"] as Record<string, string>;
    expect(headers["private-token"]).toBe("tok");
    // No content-type: fetch mints the multipart boundary itself.
    expect(headers["content-type"]).toBeUndefined();
    const form = call?.init["body"] as FormData;
    expect(form).toBeInstanceOf(FormData);
    const file = form.get("file") as File;
    expect(file.name).toBe(encodeURIComponent("shot.png"));
    expect(file.type).toBe("image/png");
    expect((await file.arrayBuffer()).byteLength).toBe(PNG.byteLength);
  });

  it("uploadProjectFile: a non-2xx answers a GitLabError naming the call", async () => {
    status = 422;
    json = "file is required";
    const client = new GitLabClient("http://git.localhost", "tok", fetchImpl);
    await expect(
      client.uploadProjectFile("group/proj", "a.png", PNG, "image/png"),
    ).rejects.toMatchObject({ name: "GitLabError", call: "uploadProjectFile", status: 422 });
  });

  it("downloadAsset: absolute URL rides verbatim, root-relative joins the site base", async () => {
    status = 200;
    const client = new GitLabClient("http://git.localhost", "tok", fetchImpl);
    const bytes = await client.downloadAsset("http://git.localhost/uploads/s/x.png");
    expect(calls[0]?.url).toBe("http://git.localhost/uploads/s/x.png");
    // Named-const cast: the client authors plain header records.
    const headers = calls[0]?.init["headers"] as Record<string, string>;
    expect(headers["private-token"]).toBe("tok");
    expect(bytes).toBeInstanceOf(Uint8Array);

    await client.downloadAsset("/uploads/s/x.png");
    expect(calls[1]?.url).toBe("http://git.localhost/uploads/s/x.png");
  });

  it("downloadAsset: a non-2xx answers a GitLabError naming the call", async () => {
    status = 404;
    const client = new GitLabClient("http://git.localhost", "tok", fetchImpl);
    await expect(client.downloadAsset("/uploads/s/gone.png")).rejects.toMatchObject({
      name: "GitLabError",
      call: "downloadAsset",
      status: 404,
    });
  });
});

// ── context plane ──────────────────────────────────────────────────────────

function contextFetchCmd(refs: unknown[], deadline?: string): EnvelopeCommand {
  return {
    specversion: "1.0",
    id: "cmd-envelope-1",
    type: CONTEXT_FETCH_COMMAND,
    source: "kernel",
    subject: "work/w-1",
    time: new Date().toISOString(),
    datacontenttype: "application/json",
    dataschema: "https://schemas.openfacilitator.org/v1/command.context.fetch.v1.json",
    data: {
      commandId: "context-fetch:fetch-1",
      workId: "w-1",
      actorEvidenceRef: "kernel:context-fetch:fetch-1",
      contextRefs: refs,
      ...(deadline === undefined ? {} : { deadline }),
    },
  };
}

const contextHarness = () => {
  const published: unknown[] = [];
  const ctx = {
    publishEvent: async (env: unknown) => {
      published.push(env);
      return { kind: "accepted" as const };
    },
  } as unknown as CommandContext;
  return { published, ctx };
};

describe("context plane (thread://gitlab)", () => {
  it("parseThreadUri: multi-segment repo, numeric IID, limit capped at 50", () => {
    expect(parseThreadUri("thread://gitlab/group/proj/42")).toEqual({
      repo: "group/proj",
      iid: "42",
      limit: 20,
    });
    expect(parseThreadUri("thread://gitlab/g/sub/p/7?limit=99")).toEqual({
      repo: "g/sub/p",
      iid: "7",
      limit: 50,
    });
    expect(parseThreadUri("thread://gitlab/group/proj/not-a-number")).toBeUndefined();
    expect(parseThreadUri("thread://gitlab/42")).toBeUndefined();
    expect(parseThreadUri("thread://telegram/1/2")).toBeUndefined();
  });

  it("routed fetch ⇒ platform window published as event.context.fetched.v1", async () => {
    const api = new FakeApi();
    api.notes = [
      { id: 3, author: "bob", body: "newest", createdAt: "2026-11-15T10:00:00Z", system: false },
      {
        id: 2,
        author: "alice",
        body: "x".repeat(2500),
        createdAt: "2026-11-15T09:00:00Z",
        system: false,
      },
      {
        id: 1,
        author: "gitlab",
        body: "added label",
        createdAt: "2026-11-15T08:00:00Z",
        system: true,
      },
    ];
    const { published, ctx } = contextHarness();
    const handler = createGitLabHandler({
      api,
      state: freshState(),
      providerInstanceId: "local-gitlab",
      logger: silent,
    });
    const outcome = await handler.onCommand(
      contextFetchCmd(["thread://gitlab/gitlab-org/gitlab-test/23?limit=10"]),
      ctx,
    );
    expect(outcome).toEqual({ kind: "accepted" });
    expect(api.notesRepo).toEqual([{ repo: "gitlab-org/gitlab-test", iid: 23, limit: 10 }]);
    // Named-const cast: the handler publishes the wire envelope.
    const env = published[0] as Record<string, unknown>;
    expect(env["type"]).toBe("org.facilitator.event.context.fetched.v1");
    const data = env["data"] as Record<string, unknown>;
    expect(data["fetchId"]).toBe("context-fetch:fetch-1");
    const items = data["items"] as { uri: string; content: Record<string, unknown> }[];
    const content = items[0]?.content as { messages: Record<string, unknown>[] };
    // System notes drop; the window reads oldest-first; bodies clamp at 2000.
    expect(content.messages.map((m) => m["messageId"])).toEqual(["2", "3"]);
    const clamped = content.messages[0]?.["text"] as string;
    expect(clamped.length).toBe(2000);
    expect(clamped.endsWith("…")).toBe(true);
  });

  it("unanswerable refs ⇒ honest error items, never a failed command", async () => {
    const api = new FakeApi();
    api.notesFail = true;
    const { published, ctx } = contextHarness();
    const handler = createGitLabHandler({
      api,
      state: freshState(),
      providerInstanceId: "local-gitlab",
      logger: silent,
    });
    const outcome = await handler.onCommand(
      contextFetchCmd(["thread://gitlab/does/not/exist/9", "thread://gitlab/x/1", "garbage"]),
      ctx,
    );
    expect(outcome).toEqual({ kind: "accepted" });
    // Named-const cast: the handler publishes the wire envelope.
    const env = published[0] as Record<string, unknown>;
    const data = env["data"] as Record<string, unknown>;
    const items = data["items"] as { uri: string; content: Record<string, unknown> }[];
    expect(items).toHaveLength(3);
    // Unparseable ⇒ thread-unresolvable; the failing read ⇒ the named error.
    expect(items[0]).toMatchObject({ content: { error: "GitLabError" } });
    expect(items[2]).toMatchObject({ content: { error: "thread-unresolvable" } });
  });

  it("malformed command data is the only rejection", async () => {
    const api = new FakeApi();
    const { ctx } = contextHarness();
    const handler = createGitLabHandler({
      api,
      state: freshState(),
      providerInstanceId: "local-gitlab",
      logger: silent,
    });
    const bad = contextFetchCmd([]);
    bad.data = { workId: "w" };
    expect(await handler.onCommand(bad, ctx)).toEqual({
      kind: "rejected",
      reason: "malformed-context-fetch-data",
    });
  });
});

// ── ledger + facet ─────────────────────────────────────────────────────────

describe("files ledger + declared facet", () => {
  it("the v2 mediaLedger rows answer the shared-law lookup", async () => {
    const state = freshState();
    const api = new FakeApi();
    const { normalizer } = webhookHarness(state, api);
    const body = JSON.stringify(noteHookWithUpload("/uploads/secretL/image.png", 1250));
    await normalizer(delivery(body, "dlv-L"));
    const uniqueId = uploadUniqueId("/uploads/secretL/image.png");
    const entry = state.findFile(uniqueId);
    expect(entry).toBeDefined();
    // The shared law and the direct read answer the same row.
    expect(state.mediaLedger().known(uniqueId)?.artifactId).toBe(entry?.artifactId);
    expect(entry?.mediaType).toBe("image/png");
    expect(entry?.size).toBe(PNG.byteLength);
    expect(entry?.digest).toBe(PNG_DIGEST);
  });

  it("the manifest reply descriptor carries the plan-verbatim facet + context arm", () => {
    expect(MEDIA_FACET).toEqual({
      directions: ["in", "out"],
      kinds: ["image/*", "application/pdf", "text/*"],
      maxCount: 10,
      maxBytesPerItem: 10485760,
      semantics: { caption: false, attachmentUpload: true },
    });
    const manifest = gitlabAdapterManifest("team-alpha");
    const reply = manifest.capabilities.find((c) => c.id === CMD_REPLY);
    expect(reply?.media).toEqual(MEDIA_FACET);
    // The rest stay honest text-only.
    const react = manifest.capabilities.find(
      (c) => c.id === "org.facilitator.command.communication.react.v1",
    );
    expect(react?.media).toEqual({ directions: ["in", "out"], kinds: [] });
    // The context plane's asynchronous read arm.
    const context = manifest.capabilities.find((c) => c.id === CONTEXT_FETCH_COMMAND);
    expect(context?.execution).toBe("asynchronous");
  });
});
