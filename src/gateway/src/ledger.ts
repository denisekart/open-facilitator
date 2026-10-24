// The Gateway execution ledger: one SQLite file (<stateDir>/gateway.sqlite3)
// holding the §6.1 binding tuple per accepted command. Delivery-level dedupe
// stays in the SDK's SqliteLedger (link.sqlite3, second file, second
// concern); THIS table is the execution truth reconcile-on-restart reads.
// Room preserved for a sibling `connector_actions` table (appendix §5.4) —
// separate tables, no overloading (A:146).
import type { Ledger } from "@ofac/workspace-link";
import { chmodSync, existsSync, mkdirSync } from "node:fs";
import { dirname } from "node:path";
import Database from "better-sqlite3";

/** The two ledgers a Gateway mounts: SDK delivery dedupe + Gateway execution truth. */
export interface GatewayPaths {
  link: Ledger;
  execution: ExecutionLedger;
}

const SCHEMA_VERSION = 1;

const DDL = `
CREATE TABLE IF NOT EXISTS bindings (
  delivery_id   TEXT PRIMARY KEY,             -- Link deliveryId the command arrived on
  dedupe_key    TEXT NOT NULL UNIQUE,         -- commandId of the business command
  gateway_id    TEXT NOT NULL,
  child_id      TEXT NOT NULL,
  assignment_id TEXT NOT NULL,
  execution_id  TEXT NOT NULL,                -- gateway-issued, assigned at accept time
  work_id       TEXT NOT NULL,                -- reconcile's assignment.failed needs it
  connection_generation INTEGER NOT NULL,     -- leaseEpoch at write time
  state         TEXT NOT NULL CHECK (state IN ('running','completed','failed','lost')),
  error         TEXT,
  started_at    TEXT NOT NULL,
  updated_at    TEXT NOT NULL
);
`;

export type BindingState = "running" | "completed" | "failed" | "lost";

export interface BindingRow {
  deliveryId: string;
  dedupeKey: string;
  gatewayId: string;
  childId: string;
  assignmentId: string;
  executionId: string;
  workId: string;
  connectionGeneration: number;
  state: BindingState;
  error: string | null;
  startedAt: string;
  updatedAt: string;
}

export interface NewBinding {
  deliveryId: string;
  dedupeKey: string;
  gatewayId: string;
  childId: string;
  assignmentId: string;
  executionId: string;
  workId: string;
  connectionGeneration: number;
}

/** dedupe_key UNIQUE hit: the caller treats it as already-bound (no double execution). */
export class BindingConflictError extends Error {
  constructor(dedupeKey: string) {
    super(`bindings: dedupe_key '${dedupeKey}' already bound`);
    this.name = "BindingConflictError";
  }
}

interface DbRow {
  delivery_id: string;
  dedupe_key: string;
  gateway_id: string;
  child_id: string;
  assignment_id: string;
  execution_id: string;
  work_id: string;
  connection_generation: number;
  state: BindingState;
  error: string | null;
  started_at: string;
  updated_at: string;
}

function toBinding(row: DbRow): BindingRow {
  return {
    deliveryId: row.delivery_id,
    dedupeKey: row.dedupe_key,
    gatewayId: row.gateway_id,
    childId: row.child_id,
    assignmentId: row.assignment_id,
    executionId: row.execution_id,
    workId: row.work_id,
    connectionGeneration: row.connection_generation,
    state: row.state,
    error: row.error,
    startedAt: row.started_at,
    updatedAt: row.updated_at,
  };
}

export class ExecutionLedger {
  private readonly db: Database.Database;

  constructor(path: string) {
    mkdirSync(dirname(path), { recursive: true, mode: 0o700 });
    this.db = new Database(path);
    // The ledger carries assignment identity + failure reasons: owner-only
    // from the first byte (mirrors SqliteLedger's chmod law).
    if (existsSync(path)) chmodSync(path, 0o600);
    this.db.pragma("journal_mode = WAL");
    this.db.pragma("busy_timeout = 5000");
    const version = this.db.pragma("user_version", { simple: true }) as number;
    if (version === 0) {
      this.db.exec(`BEGIN IMMEDIATE;${DDL}PRAGMA user_version = ${SCHEMA_VERSION};COMMIT;`);
    } else if (version !== SCHEMA_VERSION) {
      throw new Error(
        `bindings: unsupported schema version ${version} (this build writes ${SCHEMA_VERSION})`,
      );
    }
  }

  /** Insert a fresh `running` binding; a dedupe_key hit throws BindingConflictError. */
  insert(binding: NewBinding): void {
    const now = new Date().toISOString();
    try {
      this.db
        .prepare(
          `INSERT INTO bindings (delivery_id, dedupe_key, gateway_id, child_id, assignment_id,
                                 execution_id, work_id, connection_generation, state, error,
                                 started_at, updated_at)
           VALUES (@deliveryId, @dedupeKey, @gatewayId, @childId, @assignmentId,
                   @executionId, @workId, @connectionGeneration, 'running', NULL,
                   @now, @now)`,
        )
        .run({ ...binding, now });
    } catch (error) {
      if (error instanceof Error && error.message.includes("UNIQUE constraint failed")) {
        throw new BindingConflictError(binding.dedupeKey);
      }
      throw error;
    }
  }

  markState(executionId: string, state: BindingState, error?: string | null): void {
    this.db
      .prepare(
        `UPDATE bindings SET state = @state, error = @error, updated_at = @now
         WHERE execution_id = @executionId`,
      )
      .run({ executionId, state, error: error ?? null, now: new Date().toISOString() });
  }

  byState(state: BindingState): BindingRow[] {
    const rows = this.db
      .prepare("SELECT * FROM bindings WHERE state = @state ORDER BY started_at")
      .all({ state }) as DbRow[];
    return rows.map(toBinding);
  }

  byAssignment(assignmentId: string): BindingRow | null {
    const row = this.db
      .prepare(
        "SELECT * FROM bindings WHERE assignment_id = @assignmentId ORDER BY started_at DESC LIMIT 1",
      )
      .get({ assignmentId }) as DbRow | undefined;
    return row === undefined ? null : toBinding(row);
  }

  byExecution(executionId: string): BindingRow | null {
    const row = this.db
      .prepare("SELECT * FROM bindings WHERE execution_id = @executionId")
      .get({ executionId }) as DbRow | undefined;
    return row === undefined ? null : toBinding(row);
  }

  all(): BindingRow[] {
    const rows = this.db.prepare("SELECT * FROM bindings ORDER BY started_at").all() as DbRow[];
    return rows.map(toBinding);
  }

  counts(): Record<BindingState, number> {
    const counts: Record<BindingState, number> = { running: 0, completed: 0, failed: 0, lost: 0 };
    const rows = this.db
      .prepare("SELECT state, COUNT(*) AS n FROM bindings GROUP BY state")
      .all() as { state: BindingState; n: number }[];
    for (const row of rows) counts[row.state] = row.n;
    return counts;
  }

  close(): void {
    this.db.close();
  }
}
