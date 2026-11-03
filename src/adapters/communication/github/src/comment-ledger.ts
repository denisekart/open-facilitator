// The adapter's durable state on better-sqlite3, alongside (not inside) the
// SDK's SqliteLedger which owns the delivery ledger in the SAME file:
//   comments — the idempotency law: a command's idempotencyKey
//              maps to the provider comment it created; replay answers with
//              the stored id without touching GitHub.
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

  recordComment(entry: CommentLedgerEntry): void {
    this.db
      .prepare(
        "INSERT OR IGNORE INTO comments (idempotency_key, provider_object_id, repo, number, created_at) VALUES (?, ?, ?, ?, ?)",
      )
      .run(entry.idempotencyKey, entry.providerObjectId, entry.repo, entry.number, entry.createdAt);
  }

  close(): void {
    this.db.close();
  }
}
