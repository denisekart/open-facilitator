// The durable delivery ledger + outbound-event outbox on better-sqlite3.
// Synchronous API = the persist-before-`accepted` law (S:301) is one
// BEGIN IMMEDIATE transaction, no await gaps. Schema version 1 via
// PRAGMA user_version; WAL + busy_timeout for the linkd/CLI co-existence.
import { chmodSync, existsSync } from "node:fs";
import Database from "better-sqlite3";
import type { InsertResult, Ledger, LedgerRow, LedgerState } from "../core/ledger.js";

const SCHEMA_VERSION = 1;

const DDL = `
CREATE TABLE IF NOT EXISTS ledger (
  delivery_id TEXT PRIMARY KEY,
  dedupe_key  TEXT NOT NULL UNIQUE,
  state       TEXT NOT NULL CHECK (state IN ('received','processed','failed','unsupported')),
  envelope    TEXT NOT NULL,
  error       TEXT,
  received_at TEXT NOT NULL,
  updated_at  TEXT NOT NULL
);
CREATE TABLE IF NOT EXISTS outbox_events (
  id         TEXT PRIMARY KEY,
  envelope   TEXT NOT NULL,
  created_at TEXT NOT NULL,
  sent_at    TEXT
);
`;

export class SqliteLedger implements Ledger {
  private readonly db: Database.Database;

  constructor(path: string) {
    this.db = new Database(path);
    // The ledger carries envelope payloads (work objectives, artifact refs):
    // owner-only from the first byte.
    if (existsSync(path)) chmodSync(path, 0o600);
    this.db.pragma("journal_mode = WAL");
    const version = this.db.pragma("user_version", { simple: true }) as number;
    // DDL is CREATE-IF-NOT-EXISTS, so it runs unconditionally: another
    // handle (e.g. an adapter's own state tables) may have stamped
    // user_version on this same file first, and co-tenancy must still work.
    this.db.exec(`BEGIN IMMEDIATE;${DDL}PRAGMA user_version = ${SCHEMA_VERSION};COMMIT;`);
    if (version !== 0 && version !== SCHEMA_VERSION) {
      throw new Error(
        `ledger: unsupported schema version ${version} (this build writes ${SCHEMA_VERSION})`,
      );
    }
  }

  insertIfNew(deliveryId: string, envelope: unknown, dedupeKey: string): InsertResult {
    const now = new Date().toISOString();
    const run = this.db.transaction((): InsertResult => {
      const hit = this.db
        .prepare(
          "SELECT delivery_id AS rowId, state FROM ledger WHERE delivery_id = ? OR dedupe_key = ?",
        )
        .get(deliveryId, dedupeKey) as { rowId: string; state: string } | undefined;
      if (hit) {
        if (hit.state !== "failed") return "duplicate";
        // Retryable-rejection law: a `failed` row was rejected without side
        // effects (the kernel only re-offers child-busy/child-disconnected/
        // child-offline receipts), so the re-offered delivery re-runs.
        this.db
          .prepare(
            "UPDATE ledger SET state = 'received', envelope = ?, error = NULL, updated_at = ? WHERE delivery_id = ?",
          )
          .run(JSON.stringify(envelope), now, hit.rowId);
        return "fresh";
      }
      this.db
        .prepare(
          "INSERT INTO ledger (delivery_id, dedupe_key, state, envelope, error, received_at, updated_at) VALUES (?, ?, 'received', ?, NULL, ?, ?)",
        )
        .run(deliveryId, dedupeKey, JSON.stringify(envelope), now, now);
      return "fresh";
    });
    return run();
  }

  markState(deliveryId: string, state: LedgerState, error?: string): void {
    const changed = this.db
      .prepare("UPDATE ledger SET state = ?, error = ?, updated_at = ? WHERE delivery_id = ?")
      .run(state, error ?? null, new Date().toISOString(), deliveryId);
    if (changed.changes === 0) throw new Error(`ledger: unknown delivery ${deliveryId}`);
  }

  processedDeliveries(limit: number): string[] {
    return this.db
      .prepare(
        "SELECT delivery_id FROM ledger WHERE state = 'processed' ORDER BY updated_at DESC LIMIT ?",
      )
      .all(limit)
      .map((r) => (r as { delivery_id: string }).delivery_id);
  }

  pending(): LedgerRow[] {
    return this.db
      .prepare(
        "SELECT delivery_id, state FROM ledger WHERE state = 'received' ORDER BY received_at",
      )
      .all()
      .map((r) => {
        const row = r as { delivery_id: string; state: LedgerState };
        return { deliveryId: row.delivery_id, state: row.state };
      });
  }

  stats(): { received: number; processed: number; failed: number; unsupported: number } {
    const rows = this.db
      .prepare("SELECT state, COUNT(*) AS n FROM ledger GROUP BY state")
      .all() as {
      state: LedgerState;
      n: number;
    }[];
    const out = { received: 0, processed: 0, failed: 0, unsupported: 0 };
    for (const r of rows) out[r.state] = r.n;
    return out;
  }

  queueEvent(id: string, envelope: unknown): void {
    this.db
      .prepare(
        "INSERT OR IGNORE INTO outbox_events (id, envelope, created_at, sent_at) VALUES (?, ?, ?, NULL)",
      )
      .run(id, JSON.stringify(envelope), new Date().toISOString());
  }

  unsentEvents(limit: number): { id: string; envelope: unknown }[] {
    return this.db
      .prepare(
        "SELECT id, envelope FROM outbox_events WHERE sent_at IS NULL ORDER BY created_at LIMIT ?",
      )
      .all(limit)
      .map((r) => {
        const row = r as { id: string; envelope: string };
        return { id: row.id, envelope: JSON.parse(row.envelope) as unknown };
      });
  }

  markEventSent(id: string): void {
    this.db
      .prepare("UPDATE outbox_events SET sent_at = ? WHERE id = ?")
      .run(new Date().toISOString(), id);
  }

  close(): void {
    this.db.close();
  }
}
