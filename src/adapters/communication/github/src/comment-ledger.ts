// The adapter's durable state on better-sqlite3, alongside (not inside) the
// SDK's SqliteLedger which owns the delivery ledger in the SAME file:
//   comments — the idempotency law: a command's idempotencyKey
//              maps to the provider comment it created; replay answers with
//              the stored id without touching GitHub.
//   files    — the media dedupe ledger (asset-URL id → artifactId): an
//              image edited into two comments costs one upload and one ref.
import { chmodSync, existsSync } from "node:fs";
import { createMediaLedger, type MediaLedger } from "@ofac/adapter";
import Database from "better-sqlite3";

const SCHEMA_VERSION = 2;

const FILES_DDL =
  "CREATE TABLE IF NOT EXISTS files (file_unique_id TEXT PRIMARY KEY, artifact_id TEXT NOT NULL, media_type TEXT NOT NULL, size INTEGER NOT NULL, digest TEXT NOT NULL, created_at INTEGER NOT NULL);";

const DDL = `
CREATE TABLE IF NOT EXISTS comments (
  idempotency_key TEXT PRIMARY KEY,
  provider_object_id TEXT NOT NULL,
  repo          TEXT NOT NULL,
  number        TEXT NOT NULL,
  created_at    INTEGER NOT NULL
);
${FILES_DDL}
`;

export interface CommentLedgerEntry {
  idempotencyKey: string;
  providerObjectId: string;
  repo: string;
  number: string;
  createdAt: number;
}

interface CommentRow {
  idempotency_key: string;
  provider_object_id: string;
  repo: string;
  number: string;
  created_at: number;
}

/** One row of the media dedupe ledger: the artifact an uploaded asset became. */
export interface FileLedgerEntry {
  fileUniqueId: string;
  artifactId: string;
  mediaType: string;
  size: number;
  digest: string;
}

interface FileRow {
  file_unique_id: string;
  artifact_id: string;
  media_type: string;
  size: number;
  digest: string;
}

export class AdapterState {
  private readonly db: Database.Database;

  constructor(path: string) {
    const fresh = !existsSync(path);
    this.db = new Database(path);
    this.db.pragma("journal_mode = WAL");
    const version = this.db.pragma("user_version", { simple: true }) as number;
    if (version === 0) {
      this.db.exec(`BEGIN IMMEDIATE;${DDL}PRAGMA user_version = ${SCHEMA_VERSION};COMMIT;`);
    } else if (version === 1) {
      // The media leg: v1 state files carry the comments table but no
      // `files` — additive migration, never a rebuild.
      this.db.exec(`BEGIN IMMEDIATE;${FILES_DDL}PRAGMA user_version = 2;COMMIT;`);
    } else if (version !== SCHEMA_VERSION) {
      throw new Error(
        `adapter state: unsupported schema version ${version} (this build writes ${SCHEMA_VERSION})`,
      );
    }
    if (fresh) {
      chmodSync(path, 0o600);
    }
  }

  findComment(idempotencyKey: string): CommentLedgerEntry | undefined {
    const row = this.db
      .prepare(
        "SELECT idempotency_key, provider_object_id, repo, number, created_at FROM comments WHERE idempotency_key = ?",
      )
      .get(idempotencyKey) as CommentRow | undefined;
    return row === undefined
      ? undefined
      : {
          idempotencyKey: row.idempotency_key,
          providerObjectId: row.provider_object_id,
          repo: row.repo,
          number: row.number,
          createdAt: row.created_at,
        };
  }

  /** The update law's check: was THIS provider object created by us? */
  findCommentByObjectId(providerObjectId: string): CommentLedgerEntry | undefined {
    const row = this.db
      .prepare(
        "SELECT idempotency_key, provider_object_id, repo, number, created_at FROM comments WHERE provider_object_id = ? LIMIT 1",
      )
      .get(providerObjectId) as CommentRow | undefined;
    return row === undefined
      ? undefined
      : {
          idempotencyKey: row.idempotency_key,
          providerObjectId: row.provider_object_id,
          repo: row.repo,
          number: row.number,
          createdAt: row.created_at,
        };
  }

  recordComment(entry: CommentLedgerEntry): void {
    this.db
      .prepare(
        "INSERT OR IGNORE INTO comments (idempotency_key, provider_object_id, repo, number, created_at) VALUES (?, ?, ?, ?, ?)",
      )
      .run(entry.idempotencyKey, entry.providerObjectId, entry.repo, entry.number, entry.createdAt);
  }

  // ── media dedupe ledger ──────────────────────────────────────────────────

  /** The `files`-table media dedupe ledger, built on the shared law
   *  (`@ofac/adapter` media-law): this adapter's schema stays its own —
   *  the timestamp column is `created_at`, not the law's `seen_at` default. */
  mediaLedger(): MediaLedger {
    return createMediaLedger(this.db, { createdAt: "created_at" });
  }

  /** The test/ops inspection read of one ledger row (the law itself reads
   *  the table through `mediaLedger()`). */
  findFile(fileUniqueId: string): FileLedgerEntry | undefined {
    const row = this.db
      .prepare(
        "SELECT file_unique_id, artifact_id, media_type, size, digest FROM files WHERE file_unique_id = ?",
      )
      .get(fileUniqueId) as FileRow | undefined;
    return row === undefined
      ? undefined
      : {
          fileUniqueId: row.file_unique_id,
          artifactId: row.artifact_id,
          mediaType: row.media_type,
          size: row.size,
          digest: row.digest,
        };
  }

  close(): void {
    this.db.close();
  }
}
