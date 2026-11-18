// The shared MEDIA LAW, tested as the kit tests everything else: in
// process, against the stub kernel's real byte path. Every export gets its
// leg — the `files`-table ledger (default and custom columns), the digest
// law, the ArtifactRef construction law, the download helper (happy leg +
// honest throw), and the full ingestMedia decision table: disabled →
// size-cap (declared and actual) → dedupe → upload+remember → load-failure
// never failing the event.
import { createHash } from "node:crypto";
import { describe, expect, it } from "vitest";
import {
  createMediaLedger,
  ensureDigest,
  fetchArtifactContent,
  ingestMedia,
  providerArtifactRef,
  type DatabaseLike,
  type MediaLedger,
  type MediaLedgerEntry,
  type MediaLogger,
} from "../src/media-law.js";
import { createArtifactClient } from "../src/artifacts.js";
import { startStubKernel, type StubKernel } from "../src/testing.js";

const sha = (bytes: Uint8Array): string =>
  "sha256:" + createHash("sha256").update(bytes).digest("hex");

// ── the ledger: a hand-rolled DatabaseLike stands in for better-sqlite3 ────

/** A table the test owns: row per uniqueId, plus the SQL it was written with. */
function fakeDb(): {
  db: DatabaseLike;
  rows: Map<string, Record<string, unknown>>;
  sql: string[];
} {
  const rows = new Map<string, Record<string, unknown>>();
  const sql: string[] = [];
  const db: DatabaseLike = {
    prepare(statement: string) {
      sql.push(statement);
      const isSelect = statement.startsWith("SELECT");
      return {
        get(...params: unknown[]): unknown {
          if (!isSelect) throw new Error("get on a non-select");
          return rows.get(String(params[0]));
        },
        run(...params: unknown[]): unknown {
          if (isSelect) throw new Error("run on a select");
          const row = Object.fromEntries(
            statement
              .slice(statement.indexOf("(") + 1, statement.indexOf(")"))
              .split(",")
              .map((column) => column.trim())
              .map((column, index) => [column, params[index]]),
          );
          rows.set(String(params[0]), row);
          return { changes: 1 };
        },
      };
    },
  };
  return { db, rows, sql };
}

describe("createMediaLedger", () => {
  const entry: MediaLedgerEntry = {
    uniqueId: "uniq-1",
    artifactId: "art-1",
    mediaType: "image/png",
    size: 1234,
    digest: "sha256:" + "a".repeat(64),
  };

  it("remembers then knows a row through the default `files` columns", () => {
    const { db, rows, sql } = fakeDb();
    const ledger = createMediaLedger(db);
    expect(ledger.known("uniq-1")).toBeUndefined();
    ledger.remember(entry);
    // The law's SQL: the files table, file_unique_id keys, seen_at stamped.
    expect(sql[0]).toContain("SELECT file_unique_id, artifact_id, media_type, size, digest FROM files");
    expect(sql[1]).toContain("INSERT INTO files");
    expect(sql[1]).toContain("ON CONFLICT(file_unique_id) DO UPDATE");
    expect(rows.get("uniq-1")?.["seen_at"]).toEqual(expect.any(Number));
    expect(ledger.known("uniq-1")).toEqual(entry);
  });

  it("honours custom table and column names (each adapter's schema stays its own)", () => {
    const { db, sql } = fakeDb();
    const ledger = createMediaLedger(db, {
      table: "attachments",
      uniqueId: "provider_id",
      artifactId: "kernel_artifact",
      mediaType: "mime",
      size: "bytes",
      digest: "sha",
      createdAt: "created_at",
    });
    ledger.remember(entry);
    // The insert is the first prepared statement in this case.
    expect(sql[0]).toContain("INSERT INTO attachments (provider_id, kernel_artifact, mime, bytes, sha, created_at)");
    expect(ledger.known("uniq-1")?.artifactId).toBe("art-1");
  });

  it("fills honest defaults for a row missing optional facts", () => {
    const { db } = fakeDb();
    const ledger = createMediaLedger(db);
    ledger.remember({ ...entry, mediaType: undefined as unknown as string, size: undefined as unknown as number });
    // The fake stores undefined params; known() must fall back lawfully.
    const known = ledger.known("uniq-1");
    expect(known?.mediaType).toBe("application/octet-stream");
    expect(known?.size).toBe(0);
  });
});

describe("ensureDigest", () => {
  it("passes a kernel-verified sha256 through untouched", () => {
    const bytes = new TextEncoder().encode("x");
    const digest = "sha256:" + "0".repeat(64);
    expect(ensureDigest(bytes, digest)).toBe(digest);
  });

  it("computes the client-side sha256 when the store answers none", () => {
    const bytes = new TextEncoder().encode("client-side");
    expect(ensureDigest(bytes, "")).toBe(sha(bytes));
    // A malformed answer is no answer.
    expect(ensureDigest(bytes, "md5:nope")).toBe(sha(bytes));
  });
});

describe("providerArtifactRef", () => {
  it("builds the common.v1 ref with the logical storageUrl law", () => {
    const ref = providerArtifactRef({
      uniqueId: "uniq-9",
      artifactId: "art-9",
      mediaType: "image/png",
      size: 10,
      digest: "sha256:" + "b".repeat(64),
      endpointId: "ep-1",
    });
    expect(ref.storageUrl).toBe("artifact://provider/uniq-9/art-9");
    expect(ref.createdBy).toBe("link:ep-1");
    expect(ref.kind).toBe("chat.attachment");
    expect(Date.parse(ref.createdAt)).not.toBeNaN();
    // The seven required common.v1 artifactRef fields + kind, all present.
    expect(Object.keys(ref).sort()).toEqual(
      ["artifactId", "createdAt", "createdBy", "digest", "kind", "mediaType", "size", "storageUrl"].sort(),
    );
  });

  it("honours an explicit kind and a consumer-supplied storageUrl", () => {
    const ref = providerArtifactRef({
      uniqueId: "u",
      artifactId: "a",
      mediaType: "text/plain",
      size: 1,
      digest: "sha256:" + "c".repeat(64),
      endpointId: "ep",
      kind: "forgejo.attachment",
      storageUrl: "artifact://custom/u/a",
    });
    expect(ref.kind).toBe("forgejo.attachment");
    expect(ref.storageUrl).toBe("artifact://custom/u/a");
  });
});

describe("fetchArtifactContent", () => {
  it("GETs the link-plane content route with the live bearer token", async () => {
    const kernel: StubKernel = await startStubKernel();
    try {
      const client = createArtifactClient({ baseUrl: kernel.url, token: "unused" });
      const bytes = new TextEncoder().encode("reply with this artifact");
      const upload = await client.upload(bytes, { mediaType: "text/markdown" });
      const fetched = await fetchArtifactContent(
        { baseUrl: kernel.url, token: async () => "rotating-token", fetchImpl: fetch },
        upload.artifactId,
      );
      expect(new TextDecoder().decode(fetched)).toBe("reply with this artifact");
    } finally {
      await kernel.close();
    }
  });

  it("throws honestly on a 404 (foreign or missing row)", async () => {
    const kernel: StubKernel = await startStubKernel();
    try {
      await expect(
        fetchArtifactContent({ baseUrl: kernel.url, token: async () => "t" }, "0123456789abcdef"),
      ).rejects.toThrow(/content GET failed: HTTP 404/);
    } finally {
      await kernel.close();
    }
  });
});

// ── ingestMedia: the full decision table ────────────────────────────────────

function recordingLogger(): MediaLogger & { lines: { level: string; message: string; fields: Record<string, unknown> | undefined }[] } {
  const lines: { level: string; message: string; fields: Record<string, unknown> | undefined }[] = [];
  return {
    lines,
    info: (message, fields) => lines.push({ level: "info", message, fields }),
    warn: (message, fields) => lines.push({ level: "warn", message, fields }),
  };
}

function inMemoryLedger(seed: MediaLedgerEntry[] = []): MediaLedger & { stored: MediaLedgerEntry[] } {
  const rows = new Map(seed.map((row) => [row.uniqueId, row]));
  const stored: MediaLedgerEntry[] = [];
  return {
    stored,
    known: (uniqueId) => rows.get(uniqueId),
    remember: (entry) => {
      rows.set(entry.uniqueId, entry);
      stored.push(entry);
    },
  };
}

describe("ingestMedia", () => {
  const PNG = new TextEncoder().encode("png-bytes");

  it("disabled media is noted and skipped, ledger and client untouched", async () => {
    const logger = recordingLogger();
    const ledger = inMemoryLedger();
    const outcome = await ingestMedia({
      bytes: PNG,
      mediaType: "image/png",
      uniqueId: "u-off",
      maxBytes: 1_000,
      enabled: false,
      ledger,
      client: createArtifactClient({ baseUrl: "http://127.0.0.1:1", token: "t" }),
      logger,
      endpointId: "ep-1",
      provenance: { messageId: "msg-1" },
    });
    expect(outcome).toEqual({ kind: "skipped", reason: "disabled" });
    expect(logger.lines[0]?.fields?.["messageId"]).toBe("msg-1");
    expect(ledger.stored).toHaveLength(0);
  });

  it("a known provider id reuses the stored artifact: one upload, no client call", async () => {
    const seed: MediaLedgerEntry = {
      uniqueId: "u-dup",
      artifactId: "art-known",
      mediaType: "image/png",
      size: 9,
      digest: "sha256:" + "d".repeat(64),
    };
    const outcome = await ingestMedia({
      // No bytes, no loader: a dedupe hit must cost zero downloads.
      mediaType: "image/png",
      uniqueId: "u-dup",
      maxBytes: 1_000,
      enabled: true,
      ledger: inMemoryLedger([seed]),
      client: createArtifactClient({ baseUrl: "http://127.0.0.1:1", token: "t" }),
      logger: recordingLogger(),
      endpointId: "ep-9",
      kind: "chat.attachment",
    });
    expect(outcome.kind).toBe("stored");
    if (outcome.kind !== "stored") return;
    expect(outcome.deduped).toBe(true);
    expect(outcome.ref.artifactId).toBe("art-known");
    expect(outcome.ref.storageUrl).toBe("artifact://provider/u-dup/art-known");
    expect(outcome.ref.createdBy).toBe("link:ep-9");
  });

  it("skips on the DECLARED size before spending a download", async () => {
    const logger = recordingLogger();
    let loaded = false;
    const outcome = await ingestMedia({
      loadBytes: async () => {
        loaded = true;
        return { bytes: PNG };
      },
      mediaType: "image/png",
      uniqueId: "u-decl",
      declaredBytes: 5_000,
      maxBytes: 1_000,
      enabled: true,
      ledger: inMemoryLedger(),
      client: createArtifactClient({ baseUrl: "http://127.0.0.1:1", token: "t" }),
      logger,
      endpointId: "ep-1",
    });
    expect(outcome).toEqual({ kind: "skipped", reason: "size-cap", detail: "declared" });
    expect(loaded).toBe(false);
    expect(logger.lines[0]?.fields?.["declared"]).toBe(5_000);
  });

  it("skips on the ACTUAL size after a load the provider could not predict", async () => {
    const kernel = await startStubKernel();
    try {
      const outcome = await ingestMedia({
        loadBytes: async () => ({ bytes: new Uint8Array(4_000), mediaType: "video/mp4" }),
        mediaType: "application/octet-stream",
        uniqueId: "u-actual",
        maxBytes: 1_000,
        enabled: true,
        ledger: inMemoryLedger(),
        client: createArtifactClient({ baseUrl: kernel.url, token: "t" }),
        logger: recordingLogger(),
        endpointId: "ep-1",
      });
      expect(outcome).toEqual({ kind: "skipped", reason: "size-cap", detail: "actual" });
      // Nothing was uploaded: no request_upload ever rode the plane.
      expect(kernel.events).toHaveLength(0);
    } finally {
      await kernel.close();
    }
  });

  it("stores through the artifact client, records BEFORE returning the ref", async () => {
    const kernel = await startStubKernel();
    try {
      const ledger = inMemoryLedger();
      const outcome = await ingestMedia({
        bytes: PNG,
        mediaType: "image/png",
        uniqueId: "u-store",
        maxBytes: 1_000,
        enabled: true,
        ledger,
        client: createArtifactClient({ baseUrl: kernel.url, token: "t" }),
        logger: recordingLogger(),
        endpointId: "ep-3",
        retentionPolicy: "archive",
        provenance: { comment: "forwarded" },
      });
      expect(outcome.kind).toBe("stored");
      if (outcome.kind !== "stored") return;
      expect(outcome.deduped).toBe(false);
      expect(outcome.ref.digest).toBe(sha(PNG));
      expect(outcome.ref.size).toBe(PNG.byteLength);
      expect(outcome.ref.mediaType).toBe("image/png");
      expect(outcome.ref.storageUrl).toBe(`artifact://provider/u-store/${outcome.ref.artifactId}`);
      // Recorded, so a redelivered event converges without a second upload.
      expect(ledger.stored).toHaveLength(1);
      expect(ledger.stored[0]?.artifactId).toBe(outcome.ref.artifactId);
      // The kernel really holds the bytes (declared retention rode along).
      expect(kernel.artifacts.get(outcome.ref.artifactId)?.bytes).toEqual(Buffer.from(PNG));
      const upload = kernel.events[0]?.["data"] as Record<string, unknown>;
      expect(upload["retentionPolicy"]).toBe("archive");
      expect(upload["actorEvidenceRef"]).toBe("workload:ep-3");
    } finally {
      await kernel.close();
    }
  });

  it("a load failure NEVER fails the event: warn + skipped(error)", async () => {
    const logger = recordingLogger();
    const outcome = await ingestMedia({
      loadBytes: async () => {
        throw new Error("provider GET blew up");
      },
      mediaType: "image/png",
      uniqueId: "u-fail",
      maxBytes: 1_000,
      enabled: true,
      ledger: inMemoryLedger(),
      client: createArtifactClient({ baseUrl: "http://127.0.0.1:1", token: "t" }),
      logger,
      endpointId: "ep-1",
      provenance: { messageId: "msg-9" },
    });
    expect(outcome.kind).toBe("skipped");
    if (outcome.kind !== "skipped") return;
    expect(outcome.reason).toBe("error");
    expect(outcome.detail).toContain("provider GET blew up");
    expect(logger.lines[0]?.fields?.["messageId"]).toBe("msg-9");
  });

  it("a loader may redeclare the media type; the store rides what it reports", async () => {
    const kernel = await startStubKernel();
    try {
      const outcome = await ingestMedia({
        loadBytes: async () => ({ bytes: PNG, mediaType: "audio/ogg" }),
        mediaType: "application/octet-stream",
        uniqueId: "u-rediscover",
        maxBytes: 1_000,
        enabled: true,
        ledger: inMemoryLedger(),
        client: createArtifactClient({ baseUrl: kernel.url, token: "t" }),
        logger: recordingLogger(),
        endpointId: "ep-1",
      });
      expect(outcome.kind).toBe("stored");
      if (outcome.kind !== "stored") return;
      expect(outcome.ref.mediaType).toBe("audio/ogg");
    } finally {
      await kernel.close();
    }
  });
});
