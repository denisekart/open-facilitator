// The adapter's durable state on better-sqlite3, alongside (not inside) the
// SDK's SqliteLedger which owns the delivery ledger in the SAME file:
//   comments — the idempotency law: a command's idempotencyKey
//              maps to the provider note it created; replay answers with
//              the stored id without touching GitLab.
//   files    — the media dedupe ledger (upload-URL digest → artifactId): an
//              image pasted into two notes costs one upload and one ref.
import { chmodSync, existsSync } from "node:fs";
import { createMediaLedger, type MediaLedger, type MediaLedgerEntry } from "@ofac/adapter";
import Database from "better-sqlite3";

const SCHEMA_VERSION = 2;

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

const FILES_DDL =
  "CREATE TABLE IF NOT EXISTS files (file_unique_id TEXT PRIMARY KEY, artifact_id TEXT NOT NULL, media_type TEXT NOT NULL, size INTEGER NOT NULL, digest TEXT NOT NULL, created_at INTEGER NOT NULL);";

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

export class AdapterState {
  private readonly db: Database.Database;

  constructor(path: string) {
    this.db = new Database(path);
    this.db.pragma("journal_mode = WAL");
    const version = this.db.pragma("user_version", { simple: true }) as number;
    if (version === 0) {
      this.db.exec(`BEGIN IMMEDIATE;${DDL}PRAGMA user_version = ${SCHEMA_VERSION};COMMIT;`);
    } else if (version === 1) {
      // The media leg: the v1 state files carry the comments table but no
      // `files` — additive migration, never a rebuild.
      this.db.exec(`BEGIN IMMEDIATE;${FILES_DDL}PRAGMA user_version = 2;COMMIT;`);
    } else if (version !== SCHEMA_VERSION) {
      throw new Error(
        `adapter state: unsupported schema version ${version} (this build writes ${SCHEMA_VERSION})`,
      );
    }
    if (existsSync(path)) {
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

  /** The stored dedupe row of one inbound upload (the ledger-row proof in
   *  tests and the restart law's read). */
  findFile(fileUniqueId: string): MediaLedgerEntry | undefined {
    return this.mediaLedger().known(fileUniqueId);
  }

  close(): void {
    this.db.close();
  }
}
