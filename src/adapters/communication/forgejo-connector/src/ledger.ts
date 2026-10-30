// The connector_actions intent ledger (decision 5, appendix §5.4; the
// gateway ledger.ts schema pattern): durable op-intent truth, a separate
// SQLite file from any link ledger. `command_id` is the PRIMARY KEY — the
// commandId repeats on every delivery retry, so a replayed command reads the
// recorded provider_object_id and NEVER re-fires the provider mutation.
// `payload_sha256` is the canonical-input digest: a same-commandId payload
// that DIGERS is a routing bug and surfaces loudly instead of silently
// re-executing. `state` tracks the reconcile law: `pending` = the mutation
// was attempted but its outcome is unobserved (ambiguous timeout); a later
// attempt reconciles by read before touching the forge again.
import { chmodSync, existsSync, mkdirSync } from "node:fs";
import { dirname } from "node:path";
import Database from "better-sqlite3";

const SCHEMA_VERSION = 1;

const DDL_ACTIONS = `
CREATE TABLE IF NOT EXISTS connector_actions (
  command_id        TEXT PRIMARY KEY,           -- the work.assign commandId (dedupe identity)
  op                TEXT NOT NULL,              -- capabilityId, e.g. vcs.change_request.create
  payload_sha256    TEXT NOT NULL,              -- canonical inputs digest
  provider_object_id TEXT,                     -- 'PR 7' / 'comment 42' once observed
  state             TEXT NOT NULL CHECK (state IN ('pending','executed')),
  updated_at        TEXT NOT NULL
);
`;

export type ActionState = "pending" | "executed";

export interface ActionRow {
  commandId: string;
  op: string;
  payloadSha256: string;
  providerObjectId: string | null;
  state: ActionState;
  updatedAt: string;
}

interface DbRow {
  command_id: string;
  op: string;
  payload_sha256: string;
  provider_object_id: string | null;
  state: ActionState;
  updated_at: string;
}

function toRow(row: DbRow): ActionRow {
  return {
    commandId: row.command_id,
    op: row.op,
    payloadSha256: row.payload_sha256,
    providerObjectId: row.provider_object_id,
    state: row.state,
    updatedAt: row.updated_at,
  };
}

export class ActionLedgerConflictError extends Error {
  constructor(commandId: string) {
    super(`connector_actions: '${commandId}' recorded for a DIFFERENT payload`);
    this.name = "ActionLedgerConflictError";
  }
}

export class ActionLedger {
  private readonly db: Database.Database;

  constructor(path: string) {
    mkdirSync(dirname(path), { recursive: true, mode: 0o700 });
    this.db = new Database(path);
    if (existsSync(path)) chmodSync(path, 0o600);
    this.db.pragma("journal_mode = WAL");
    this.db.pragma("busy_timeout = 5000");
    const version = this.db.pragma("user_version", { simple: true }) as number;
    if (version === 0) {
      this.db.exec(`BEGIN IMMEDIATE;${DDL_ACTIONS}PRAGMA user_version = ${SCHEMA_VERSION};COMMIT;`);
    } else if (version !== SCHEMA_VERSION) {
      throw new Error(
        `connector_actions: unsupported schema version ${version} (this build writes ${SCHEMA_VERSION})`,
      );
    }
  }

  /** The recorded intent for a commandId, if any. */
  find(commandId: string): ActionRow | undefined {
    const row = this.db
      .prepare<[string], DbRow>("SELECT * FROM connector_actions WHERE command_id = ?")
      .get(commandId);
    return row === undefined ? undefined : toRow(row);
  }

  /** Open the intent BEFORE the first provider call. A duplicate commandId
   *  with a different payload digest is a conflict, never a silent re-run. */
  insert(commandId: string, op: string, payloadSha256: string): void {
    const now = new Date().toISOString();
    const info = this.db
      .prepare(
        "INSERT OR IGNORE INTO connector_actions (command_id, op, payload_sha256, provider_object_id, state, updated_at) VALUES (?, ?, ?, NULL, 'pending', ?)",
      )
      .run(commandId, op, payloadSha256, now);
    if (info.changes === 0) {
      const existing = this.find(commandId);
      if (existing !== undefined && existing.payloadSha256 !== payloadSha256) {
        throw new ActionLedgerConflictError(commandId);
      }
    }
  }

  /** Stamp the observed provider object id and settle the intent. */
  settle(commandId: string, providerObjectId: string): void {
    this.db
      .prepare(
        "UPDATE connector_actions SET provider_object_id = ?, state = 'executed', updated_at = ? WHERE command_id = ?",
      )
      .run(providerObjectId, new Date().toISOString(), commandId);
  }

  /** Widen for tests/reconcile introspection. */
  all(): ActionRow[] {
    return this.db
      .prepare<[], DbRow>("SELECT * FROM connector_actions ORDER BY updated_at")
      .all()
      .map(toRow);
  }

  close(): void {
    this.db.close();
  }
}
