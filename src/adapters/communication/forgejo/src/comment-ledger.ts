// The adapter's durable state on better-sqlite3, alongside (not inside) the
// SDK's SqliteLedger which owns the delivery ledger in the SAME file:
//   comments — the idempotency law: a command's idempotencyKey
//              maps to the provider comment it created; replay answers with
//              the stored id without touching Forgejo.
//   files    — the media dedupe ledger (attachment id → artifactId): an
//              image edited into two comments costs one upload and one ref.
import { chmodSync, existsSync } from "node:fs";
import Database from "better-sqlite3";

const SCHEMA_VERSION = 1;

const DDL = `
CREATE TABLE IF NOT EXISTS comments (
  idempotency_key TEXT PRIMARY KEY,
  provider_object_id TEXT NOT NULL,
  repo          TEXT NOT NULL,
  number        TEXT NOT NULL,
  created_at    INTEGER NOT NULL
);
CREATE TABLE IF NOT EXISTS files (
  file_unique_id TEXT PRIMARY KEY,
  artifact_id   TEXT NOT NULL,
  media_type    TEXT NOT NULL,
  size          INTEGER NOT NULL,
  digest        TEXT NOT NULL,
  created_at    INTEGER NOT NULL
);
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

/** One row of the media dedupe ledger: the artifact an issue attachment became. */
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
    this.db.pragma(`user_version = ${SCHEMA_VERSION}`);
    this.db.exec(DDL);
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

  /** Record BEFORE publish: a redelivered delivery converges without a
   *  second upload. */
  recordFile(entry: FileLedgerEntry): void {
    this.db
      .prepare(
        "INSERT INTO files (file_unique_id, artifact_id, media_type, size, digest, created_at) VALUES (?, ?, ?, ?, ?, ?) ON CONFLICT(file_unique_id) DO UPDATE SET artifact_id = excluded.artifact_id, media_type = excluded.media_type, size = excluded.size, digest = excluded.digest, created_at = excluded.created_at",
      )
      .run(
        entry.fileUniqueId,
        entry.artifactId,
        entry.mediaType,
        entry.size,
        entry.digest,
        Date.now(),
      );
  }

  close(): void {
    this.db.close();
  }
}
