// The shared MEDIA LAW: every communication adapter that speaks bytes runs the
// same rules, so the kernel sees one behavior from many providers. Extracted
// verbatim in law from the Telegram prototype (`telegram/src/media.ts`):
//
//   1. dedupe on the PROVIDER unique id via the adapter's `files` ledger —
//      a known id reuses the stored artifactId (the same image forwarded
//      twice costs one upload and one ref);
//   2. SKIP (never fail the event) when the declared or actual size exceeds
//      the cap, or when media is disabled — always a structured log;
//   3. upload through the REUSED @ofac/adapter artifact client (request_upload
//      → kernel-proxied or presigned PUT → confirm), deliberately with NO
//      workId/assignmentId when no Work exists yet for the message;
//   4. build the ArtifactRef exactly per common.v1: `storageUrl` is the
//      GUID-free logical URI `artifact://provider/<uniqueId>/<artifactId>`,
//      kind `chat.attachment`;
//   5. record in the ledger BEFORE publish so a redelivered event converges
//      without a second upload.
//
// The ledger helper speaks SQL against a table the ADAPTER owns (its schema,
// its migration); the caller hands in a live handle, so this package gains no
// sqlite dependency.
import { createHash } from "node:crypto";
import type { ArtifactRef } from "@ofac/protocol";
import { createArtifactClient, type ArtifactClient } from "./artifacts.js";

/** The structural slice of a better-sqlite3 handle the ledger helper needs. */
export interface DatabaseLike {
  prepare(sql: string): {
    get(...params: unknown[]): unknown;
    run(...params: unknown[]): unknown;
  };
}

/** One row of a media dedupe ledger: the artifact a provider file became. */
export interface MediaLedgerEntry {
  readonly uniqueId: string;
  readonly artifactId: string;
  readonly mediaType: string;
  readonly size: number;
  readonly digest: string;
}

export interface MediaLedger {
  /** The dedupe lookup: a provider file id already stored answers its ref facts. */
  known(uniqueId: string): MediaLedgerEntry | undefined;
  /** Record BEFORE publish: a redelivered event converges without an upload. */
  remember(entry: MediaLedgerEntry): void;
}

export interface MediaLedgerColumns {
  /** Table name; `files` is the law both Telegram and the wave-2 adapters use. */
  readonly table?: string;
  readonly uniqueId?: string;
  readonly artifactId?: string;
  readonly mediaType?: string;
  readonly size?: string;
  readonly digest?: string;
  /** Timestamp column written on remember (the adapters' `seen_at` / `created_at`). */
  readonly createdAt?: string;
}

/** The `files`-table media dedupe ledger, generalized from the Telegram chat
 *  ledger: same column law (`file_unique_id → artifactId` + media facts), table
 *  and column names configurable so each adapter's own schema stays its own. */
export function createMediaLedger(db: DatabaseLike, columns: MediaLedgerColumns = {}): MediaLedger {
  const table = columns.table ?? "files";
  const id = columns.uniqueId ?? "file_unique_id";
  const artifact = columns.artifactId ?? "artifact_id";
  const mediaType = columns.mediaType ?? "media_type";
  const size = columns.size ?? "size";
  const digest = columns.digest ?? "digest";
  const created = columns.createdAt ?? "seen_at";
  const select = `SELECT ${id}, ${artifact}, ${mediaType}, ${size}, ${digest} FROM ${table} WHERE ${id} = ?`;
  const insert =
    `INSERT INTO ${table} (${id}, ${artifact}, ${mediaType}, ${size}, ${digest}, ${created}) ` +
    `VALUES (?, ?, ?, ?, ?, ?) ON CONFLICT(${id}) DO UPDATE SET ` +
    `${artifact} = excluded.${artifact}, ${mediaType} = excluded.${mediaType}, ` +
    `${size} = excluded.${size}, ${digest} = excluded.${digest}, ${created} = excluded.${created}`;
  return {
    known(uniqueId: string): MediaLedgerEntry | undefined {
      const row = db.prepare(select).get(uniqueId) as
        Record<string, string | number | undefined> | undefined;
      if (row === undefined) return undefined;
      return {
        uniqueId: String(row[id]),
        artifactId: String(row[artifact]),
        mediaType: String(row[mediaType] ?? "application/octet-stream"),
        size: Number(row[size] ?? 0),
        digest: String(row[digest]),
      };
    },
    remember(entry: MediaLedgerEntry): void {
      db.prepare(insert).run(
        entry.uniqueId,
        entry.artifactId,
        entry.mediaType,
        entry.size,
        entry.digest,
        Date.now(),
      );
    },
  };
}

/** The kernel answers the stored digest; a store that answers none gets the
 *  client-side sha256 (identical bytes, identical law). */
export function ensureDigest(bytes: Uint8Array, digest: string): string {
  if (/^sha256:[0-9a-f]{64}$/.test(digest)) return digest;
  return "sha256:" + createHash("sha256").update(bytes).digest("hex");
}

/** The ArtifactRef construction law (common.v1 artifactRef): the seven
 *  required fields + kind; the storageUrl is a logical URI keyed by the
 *  PROVIDER unique id, so it stays non-authoritative and stable across
 *  re-forwards. */
export function providerArtifactRef(input: {
  uniqueId: string;
  artifactId: string;
  mediaType: string;
  size: number;
  digest: string;
  endpointId: string;
  kind?: string;
  /** GUID-free logical URI law: `artifact://provider/<uniqueId>/<artifactId>`. */
  storageUrl?: string;
}): ArtifactRef {
  return {
    artifactId: input.artifactId,
    mediaType: input.mediaType,
    size: input.size,
    digest: input.digest,
    storageUrl: input.storageUrl ?? `artifact://provider/${input.uniqueId}/${input.artifactId}`,
    createdBy: `link:${input.endpointId}`,
    createdAt: new Date().toISOString(),
    kind: input.kind ?? "chat.attachment",
  };
}

export interface ArtifactClientOptions {
  /** Kernel base URL — the link-plane artifact client + content GET ride it. */
  readonly baseUrl: string;
  /** The live link token (rotates on 401 with the enrollment). */
  readonly token: () => Promise<string>;
  readonly fetchImpl?: typeof fetch;
}

/** Outbound download helper (the reply-with-artifact path): GET the
 *  link-plane content route with the link bearer token, following a 302 to
 *  a presigned URL when the kernel redirects. */
export async function fetchArtifactContent(
  client: ArtifactClientOptions,
  artifactId: string,
): Promise<Uint8Array> {
  const doFetch: typeof fetch = client.fetchImpl ?? ((...args) => fetch(...args));
  const response = await doFetch(
    `${client.baseUrl.replace(/\/+$/, "")}/api/link/v1/artifacts/${artifactId}/content`,
    { headers: { authorization: `Bearer ${await client.token()}` }, redirect: "follow" },
  );
  if (!response.ok) {
    throw new Error(`artifact ${artifactId} content GET failed: HTTP ${response.status}`);
  }
  return new Uint8Array(await response.arrayBuffer());
}

export interface MediaLogger {
  info(message: string, fields?: Record<string, unknown>): void;
  warn(message: string, fields?: Record<string, unknown>): void;
}

export interface IngestMediaDeps {
  /** Bytes already downloaded (the provider leg may hand a loader instead,
   *  so a dedupe hit or a declared-size skip costs zero downloads). */
  readonly bytes?: Uint8Array;
  readonly loadBytes?: () => Promise<{ bytes: Uint8Array; mediaType?: string }>;
  readonly mediaType: string;
  /** The PROVIDER's stable id for this file (Telegram file_unique_id,
   *  Mattermost file id, Forgejo attachment id). */
  readonly uniqueId: string;
  /** Size the provider declared, when it declares one: skipping on that is
   *  free, skipping after a 5 GB download is not. */
  readonly declaredBytes?: number;
  readonly maxBytes: number;
  readonly enabled: boolean;
  readonly ledger: MediaLedger;
  readonly client: ArtifactClient;
  readonly logger: MediaLogger;
  /** The endpoint whose link identity created the artifact. */
  readonly endpointId: string;
  /** `permanent` | `archive` | `<n>d`; inbound chat media defaults to 90d. */
  readonly retentionPolicy?: string;
  readonly kind?: string;
  /** Non-authoritative provenance pointer, e.g. the message the file rode. */
  readonly provenance?: { comment?: string; messageId?: string };
}

export type IngestOutcome =
  | { kind: "stored"; ref: ArtifactRef; deduped: boolean }
  | { kind: "skipped"; reason: "size-cap" | "disabled" | "error"; detail?: string };

/** The inbound media law, in one place: dedupe → size gates → upload →
 *  ledger → ref. NEVER throws: media failures log and shrink the ref list,
 *  they never fail the event (the text event still publishes). */
export async function ingestMedia(deps: IngestMediaDeps): Promise<IngestOutcome> {
  const { uniqueId, ledger, logger, maxBytes, enabled } = deps;
  const messageId = deps.provenance?.messageId;
  if (!enabled) {
    logger.info("media disabled: noted and skipped", {
      fileUniqueId: uniqueId,
      ...(messageId === undefined ? {} : { messageId }),
    });
    return { kind: "skipped", reason: "disabled" };
  }
  const known = ledger.known(uniqueId);
  if (known !== undefined) {
    return {
      kind: "stored",
      deduped: true,
      ref: providerArtifactRef({
        uniqueId: known.uniqueId,
        artifactId: known.artifactId,
        mediaType: known.mediaType,
        size: known.size,
        digest: known.digest,
        endpointId: deps.endpointId,
        ...(deps.kind === undefined ? {} : { kind: deps.kind }),
      }),
    };
  }
  if ((deps.declaredBytes ?? 0) > maxBytes) {
    logger.warn("media oversized at declaration: skipped", {
      fileUniqueId: uniqueId,
      ...(messageId === undefined ? {} : { messageId }),
      declared: deps.declaredBytes ?? 0,
      maxBytes,
    });
    return { kind: "skipped", reason: "size-cap", detail: "declared" };
  }
  try {
    const loaded =
      deps.bytes !== undefined
        ? { bytes: deps.bytes, mediaType: deps.mediaType }
        : await (deps.loadBytes as () => Promise<{ bytes: Uint8Array; mediaType?: string }>)();
    const bytes = loaded.bytes;
    if (bytes.byteLength > maxBytes) {
      logger.warn("media oversized on download: skipped", {
        fileUniqueId: uniqueId,
        ...(messageId === undefined ? {} : { messageId }),
        actual: bytes.byteLength,
        maxBytes,
      });
      return { kind: "skipped", reason: "size-cap", detail: "actual" };
    }
    const mediaType = loaded.mediaType ?? deps.mediaType;
    const uploaded = await deps.client.upload(bytes, {
      mediaType,
      retentionPolicy: deps.retentionPolicy ?? "90d",
      actorEvidenceRef: `workload:${deps.endpointId}`,
    });
    const digest = ensureDigest(bytes, uploaded.digest);
    const ref = providerArtifactRef({
      uniqueId,
      artifactId: uploaded.artifactId,
      mediaType,
      size: bytes.byteLength,
      digest,
      endpointId: deps.endpointId,
      ...(deps.kind === undefined ? {} : { kind: deps.kind }),
    });
    ledger.remember({
      uniqueId,
      artifactId: uploaded.artifactId,
      mediaType,
      size: bytes.byteLength,
      digest,
    });
    return { kind: "stored", ref, deduped: false };
  } catch (error) {
    // The never-fail-the-event law: media errors are a log, the text
    // event still publishes (its text falls back to "(attachment)").
    logger.warn("inbound media upload failed: event publishes without the ref", {
      fileUniqueId: uniqueId,
      ...(messageId === undefined ? {} : { messageId }),
      error: String(error),
    });
    return { kind: "skipped", reason: "error", detail: String(error) };
  }
}

/** The artifact client every media leg rides (re-exported so adapters import
 *  media law from one place). */
export { createArtifactClient };
export type { ArtifactClient };
