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

const SCHEMA_VERSION = 4;

const DDL_BINDINGS = `
CREATE TABLE IF NOT EXISTS bindings (
  delivery_id   TEXT PRIMARY KEY,             -- Link deliveryId the command arrived on
  dedupe_key    TEXT NOT NULL UNIQUE,         -- commandId of the business command
  gateway_id    TEXT NOT NULL,
  child_id      TEXT NOT NULL,
  assignment_id TEXT NOT NULL,
  execution_id  TEXT NOT NULL,                -- gateway-issued, assigned at accept time
  work_id       TEXT NOT NULL,                -- reconcile's assignment.failed needs it
  connection_generation INTEGER NOT NULL,     -- leaseEpoch at write time (S:329 freshness fence)
  execution_generation  INTEGER NOT NULL DEFAULT 1, -- assign envelope's executionGeneration (event stamp)
  child_generation INTEGER NOT NULL DEFAULT 0, -- child-connection fence (v2); 0 = pre-child-era row
  state         TEXT NOT NULL CHECK (state IN ('running','completed','failed','lost')),
  error         TEXT,
  started_at    TEXT NOT NULL,
  updated_at    TEXT NOT NULL
);
`;

// Per-child fleet truth (v2): the child connection fence is Gateway-internal —
// `child_generation` bumps on every re-register and fences inbound frames;
// `connection_generation` stays the Link leaseEpoch (spec §6.3 tuple).
const DDL_CHILD_STATE = `
CREATE TABLE IF NOT EXISTS child_state (
  child_id         TEXT PRIMARY KEY,
  manifest_digest  TEXT NOT NULL,
  manifest_json    TEXT NOT NULL,
  child_generation INTEGER NOT NULL,
  state            TEXT NOT NULL CHECK (state IN ('connected','offline')),
  connected_at     TEXT,
  offline_at       TEXT
);
`;

// Plan 12 v3: confirm-held assignments park here while an approval is
// pending — a side table, NOT a bindings-state widening: reconcile,
// probeChild and the quota law all read `running`, and a disguised-pending
// binding would be reported lost on restart.
const DDL_PENDING = `
CREATE TABLE IF NOT EXISTS pending_actions (
  approval_id            TEXT PRIMARY KEY,
  delivery_id            TEXT NOT NULL,
  envelope               TEXT NOT NULL,          -- the assign envelope, verbatim
  work_id                TEXT,
  assignment_id          TEXT NOT NULL,
  capability_id          TEXT NOT NULL,
  resource               TEXT NOT NULL,
  child_id               TEXT NOT NULL,
  approval_digest_record TEXT NOT NULL,          -- the exact digest record sent
  policy_id              TEXT NOT NULL,
  state                  TEXT NOT NULL CHECK (state IN ('awaiting-approval','executing','completed','failed','expired','revoked')),
  required_at            TEXT NOT NULL,
  expires_at             TEXT NOT NULL,
  epoch                  INTEGER NOT NULL,
  execution_id           TEXT,                 -- set at consume: the binding row it spawned
  updated_at             TEXT NOT NULL
);
`;

export type PendingState =
  "awaiting-approval" | "executing" | "completed" | "failed" | "expired" | "revoked";

export interface PendingRow {
  approvalId: string;
  deliveryId: string;
  envelope: string;
  workId: string | null;
  assignmentId: string;
  capabilityId: string;
  resource: string;
  childId: string;
  approvalDigestRecord: string;
  policyId: string;
  state: PendingState;
  requiredAt: string;
  expiresAt: string;
  epoch: number;
  executionId: string | null;
  updatedAt: string;
}

export interface NewPending {
  approvalId: string;
  deliveryId: string;
  envelope: string;
  workId: string | null;
  assignmentId: string;
  capabilityId: string;
  resource: string;
  childId: string;
  approvalDigestRecord: string;
  policyId: string;
  requiredAt: string;
  expiresAt: string;
  epoch: number;
}

interface DbPendingRow {
  approval_id: string;
  delivery_id: string;
  envelope: string;
  work_id: string | null;
  assignment_id: string;
  capability_id: string;
  resource: string;
  child_id: string;
  approval_digest_record: string;
  policy_id: string;
  state: PendingState;
  required_at: string;
  expires_at: string;
  epoch: number;
  execution_id: string | null;
  updated_at: string;
}

function toPending(row: DbPendingRow): PendingRow {
  return {
    approvalId: row.approval_id,
    deliveryId: row.delivery_id,
    envelope: row.envelope,
    workId: row.work_id,
    assignmentId: row.assignment_id,
    capabilityId: row.capability_id,
    resource: row.resource,
    childId: row.child_id,
    approvalDigestRecord: row.approval_digest_record,
    policyId: row.policy_id,
    state: row.state,
    requiredAt: row.required_at,
    expiresAt: row.expires_at,
    epoch: row.epoch,
    executionId: row.execution_id,
    updatedAt: row.updated_at,
  };
}

export type BindingState = "running" | "completed" | "failed" | "lost";

export type ChildConnectionState = "connected" | "offline";

export interface ChildStateRow {
  childId: string;
  manifestDigest: string;
  manifestJson: string;
  childGeneration: number;
  state: ChildConnectionState;
  connectedAt: string | null;
  offlineAt: string | null;
}

export interface BindingRow {
  deliveryId: string;
  dedupeKey: string;
  gatewayId: string;
  childId: string;
  assignmentId: string;
  executionId: string;
  workId: string;
  connectionGeneration: number;
  executionGeneration: number;
  childGeneration: number;
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
  /** B(4) event stamp; a pre-B envelope sat at generation 1 (DEFAULT 1). */
  executionGeneration?: number;
  childGeneration: number;
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
  execution_generation: number;
  child_generation: number;
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
    executionGeneration: row.execution_generation,
    childGeneration: row.child_generation,
    state: row.state,
    error: row.error,
    startedAt: row.started_at,
    updatedAt: row.updated_at,
  };
}

interface DbChildRow {
  child_id: string;
  manifest_digest: string;
  manifest_json: string;
  child_generation: number;
  state: ChildConnectionState;
  connected_at: string | null;
  offline_at: string | null;
}

function toChildState(row: DbChildRow): ChildStateRow {
  return {
    childId: row.child_id,
    manifestDigest: row.manifest_digest,
    manifestJson: row.manifest_json,
    childGeneration: row.child_generation,
    state: row.state,
    connectedAt: row.connected_at,
    offlineAt: row.offline_at,
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
      this.db.exec(
        `BEGIN IMMEDIATE;${DDL_BINDINGS}${DDL_CHILD_STATE}${DDL_PENDING}PRAGMA user_version = ${SCHEMA_VERSION};COMMIT;`,
      );
    } else if (version === 1) {
      // v1 (plan 05) files carry bindings WITHOUT the child fence and no
      // child_state table; the ALTER's DEFAULT 0 keeps every pre-v2 row
      // honest (generation 0 = never connected through a child transport).
      this.db.exec(
        `BEGIN IMMEDIATE;` +
          `ALTER TABLE bindings ADD COLUMN child_generation INTEGER NOT NULL DEFAULT 0;` +
          DDL_CHILD_STATE +
          DDL_PENDING +
          `PRAGMA user_version = ${SCHEMA_VERSION};COMMIT;`,
      );
    } else if (version === 2) {
      // v2 (plan 06/10) files gain only the Plan 12 pending side table.
      this.db.exec(`BEGIN IMMEDIATE;${DDL_PENDING}PRAGMA user_version = ${SCHEMA_VERSION};COMMIT;`);
    } else if (version === 3) {
      // v3 (plan 12) files: the execution_generation ALTER below (idempotent).
    } else if (version !== SCHEMA_VERSION) {
      throw new Error(
        `bindings: unsupported schema version ${version} (this build writes ${SCHEMA_VERSION})`,
      );
    }
    // Plan 35 B(4), idempotent for every pre-v4 file (v1/v2/v3-era): the
    // event stamp splits from the S:329 freshness fence. DEFAULT 1: every
    // pre-B envelope rode generation 1 (the kernel's pre-B constant).
    const cols = this.db.pragma("table_info(bindings)", { simple: false }) as {
      name: string;
    }[];
    if (!cols.some((c) => c.name === "execution_generation")) {
      this.db.exec(
        `BEGIN IMMEDIATE;` +
          `ALTER TABLE bindings ADD COLUMN execution_generation INTEGER NOT NULL DEFAULT 1;` +
          `PRAGMA user_version = ${SCHEMA_VERSION};COMMIT;`,
      );
    }
  }

  /** Park a confirm-held assignment awaiting its approval decision. */
  insertPending(p: NewPending): void {
    this.db
      .prepare(
        `INSERT INTO pending_actions (approval_id, delivery_id, envelope, work_id, assignment_id,
                                      capability_id, resource, child_id, approval_digest_record,
                                      policy_id, state, required_at, expires_at, epoch, updated_at)
         VALUES (@approvalId, @deliveryId, @envelope, @workId, @assignmentId, @capabilityId,
                 @resource, @childId, @approvalDigestRecord, @policyId,
                 'awaiting-approval', @requiredAt, @expiresAt, @epoch, @requiredAt)`,
      )
      .run({ ...p });
  }

  getPending(approvalId: string): PendingRow | null {
    const row = this.db
      .prepare("SELECT * FROM pending_actions WHERE approval_id = @approvalId")
      .get({ approvalId }) as DbPendingRow | undefined;
    return row === undefined ? null : toPending(row);
  }

  pendingByState(state: PendingState): PendingRow[] {
    const rows = this.db
      .prepare("SELECT * FROM pending_actions WHERE state = @state ORDER BY required_at")
      .all({ state }) as DbPendingRow[];
    return rows.map(toPending);
  }

  markPending(approvalId: string, state: PendingState): void {
    this.db
      .prepare(
        "UPDATE pending_actions SET state = @state, updated_at = @now WHERE approval_id = @approvalId",
      )
      .run({ approvalId, state, now: new Date().toISOString() });
  }

  /** Consume succeeded: park the spawned execution's id and move to `executing`. */
  bindPendingExecution(approvalId: string, executionId: string): void {
    this.db
      .prepare(
        `UPDATE pending_actions SET state = 'executing', execution_id = @executionId, updated_at = @now
         WHERE approval_id = @approvalId`,
      )
      .run({ approvalId, executionId, now: new Date().toISOString() });
  }

  pendingByExecution(executionId: string): PendingRow | null {
    const row = this.db
      .prepare("SELECT * FROM pending_actions WHERE execution_id = @executionId")
      .get({ executionId }) as DbPendingRow | undefined;
    return row === undefined ? null : toPending(row);
  }

  pendingByAssignment(assignmentId: string): PendingRow | null {
    const row = this.db
      .prepare(
        "SELECT * FROM pending_actions WHERE assignment_id = @assignmentId ORDER BY required_at DESC LIMIT 1",
      )
      .get({ assignmentId }) as DbPendingRow | undefined;
    return row === undefined ? null : toPending(row);
  }

  pendingCounts(): { awaitingApproval: number } {
    const row = this.db
      .prepare(
        "SELECT COUNT(*) AS n FROM pending_actions WHERE state IN ('awaiting-approval','executing')",
      )
      .get() as { n: number };
    return { awaitingApproval: row.n };
  }

  /** Insert a fresh `running` binding; a dedupe_key hit throws BindingConflictError. */
  insert(binding: NewBinding): void {
    const now = new Date().toISOString();
    try {
      this.db
        .prepare(
          `INSERT INTO bindings (delivery_id, dedupe_key, gateway_id, child_id, assignment_id,
                                 execution_id, work_id, connection_generation, execution_generation,
                                 child_generation, state, error, started_at, updated_at)
           VALUES (@deliveryId, @dedupeKey, @gatewayId, @childId, @assignmentId,
                   @executionId, @workId, @connectionGeneration, @executionGeneration,
                   @childGeneration, 'running', NULL, @now, @now)`,
        )
        .run({ executionGeneration: 1, ...binding, now });
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

  /** Bindings for one child in a state, oldest first (re-register probe reads `running`). */
  byChildState(childId: string, state: BindingState): BindingRow[] {
    const rows = this.db
      .prepare(
        "SELECT * FROM bindings WHERE child_id = @childId AND state = @state ORDER BY started_at",
      )
      .all({ childId, state }) as DbRow[];
    return rows.map(toBinding);
  }

  /**
   * Register (or re-register) a child connection: refresh the manifest, mark
   * `connected`. `bumpGeneration` advances the child fence — the first upsert
   * starts at 1 when bumping, 0 when not; a re-register without bump keeps the
   * generation (the frame came on the connection that already owns the child).
   */
  upsertChild(
    childId: string,
    manifestDigest: string,
    manifestJson: string,
    bumpGeneration: boolean,
  ): void {
    const now = new Date().toISOString();
    const existing = this.db
      .prepare("SELECT child_generation FROM child_state WHERE child_id = @childId")
      .get({ childId }) as { child_generation: number } | undefined;
    const generation =
      existing === undefined
        ? bumpGeneration
          ? 1
          : 0
        : existing.child_generation + (bumpGeneration ? 1 : 0);
    this.db
      .prepare(
        `INSERT INTO child_state (child_id, manifest_digest, manifest_json, child_generation,
                                  state, connected_at, offline_at)
         VALUES (@childId, @manifestDigest, @manifestJson, @generation, 'connected', @now, NULL)
         ON CONFLICT(child_id) DO UPDATE SET
           manifest_digest = excluded.manifest_digest,
           manifest_json = excluded.manifest_json,
           child_generation = excluded.child_generation,
           state = 'connected',
           connected_at = excluded.connected_at,
           offline_at = NULL`,
      )
      .run({ childId, manifestDigest, manifestJson, generation, now });
  }

  /** Connection-state transition; no-op for a child that never registered. */
  setChildState(childId: string, state: ChildConnectionState): void {
    const now = new Date().toISOString();
    if (state === "connected") {
      this.db
        .prepare(
          `UPDATE child_state SET state = 'connected', connected_at = @now, offline_at = NULL
           WHERE child_id = @childId`,
        )
        .run({ childId, now });
    } else {
      this.db
        .prepare(
          `UPDATE child_state SET state = 'offline', offline_at = @now WHERE child_id = @childId`,
        )
        .run({ childId, now });
    }
  }

  children(): ChildStateRow[] {
    const rows = this.db
      .prepare("SELECT * FROM child_state ORDER BY child_id")
      .all() as DbChildRow[];
    return rows.map(toChildState);
  }

  /** Current child fence for `childId`; 0 when the child never registered. */
  childGeneration(childId: string): number {
    const row = this.db
      .prepare("SELECT child_generation FROM child_state WHERE child_id = @childId")
      .get({ childId }) as { child_generation: number } | undefined;
    return row === undefined ? 0 : row.child_generation;
  }

  close(): void {
    this.db.close();
  }
}
