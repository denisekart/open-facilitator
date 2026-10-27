import { mkdtempSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { afterEach, describe, expect, it } from "vitest";
import Database from "better-sqlite3";

import { BindingConflictError, ExecutionLedger, type NewBinding } from "../../src/ledger.js";

let ledger: ExecutionLedger | null = null;

function open(file?: string): ExecutionLedger {
  ledger = new ExecutionLedger(
    file ?? join(mkdtempSync(join(tmpdir(), "gw-ledger-")), "gateway.sqlite3"),
  );
  return ledger;
}

afterEach(() => {
  ledger?.close();
  ledger = null;
});

function binding(over: Partial<NewBinding> = {}): NewBinding {
  return {
    deliveryId: "del-1",
    dedupeKey: "cmd-1",
    gatewayId: "gw.t",
    childId: "urn:workspace:child-echo",
    assignmentId: "asg-1",
    executionId: "exec-abc123",
    workId: "work-1",
    connectionGeneration: 2,
    childGeneration: 3,
    ...over,
  };
}

describe("ExecutionLedger binding tuple", () => {
  it("inserts and reads back the full §6.1 tuple", () => {
    const l = open();
    l.insert(binding());
    const row = l.byExecution("exec-abc123");
    expect(row).not.toBeNull();
    expect(row).toMatchObject({
      deliveryId: "del-1",
      dedupeKey: "cmd-1",
      gatewayId: "gw.t",
      childId: "urn:workspace:child-echo",
      assignmentId: "asg-1",
      executionId: "exec-abc123",
      workId: "work-1",
      connectionGeneration: 2,
      childGeneration: 3,
      state: "running",
      error: null,
    });
    expect(row?.startedAt).toMatch(/^\d{4}-\d{2}-\d{2}T/);
    expect(row?.updatedAt).toBe(row?.startedAt);
  });

  it("walks running → completed / failed / lost with error capture", () => {
    const l = open();
    l.insert(binding({ executionId: "exec-c" }));
    l.insert(binding({ executionId: "exec-f", dedupeKey: "cmd-f", deliveryId: "del-f" }));
    l.insert(binding({ executionId: "exec-l", dedupeKey: "cmd-l", deliveryId: "del-l" }));

    l.markState("exec-c", "completed");
    l.markState("exec-f", "failed", "child rejected");
    l.markState("exec-l", "lost", "gateway-restart-lost");

    expect(l.byExecution("exec-c")?.state).toBe("completed");
    expect(l.byExecution("exec-f")).toMatchObject({ state: "failed", error: "child rejected" });
    expect(l.byExecution("exec-l")).toMatchObject({ state: "lost", error: "gateway-restart-lost" });
    expect(l.counts()).toEqual({ running: 0, completed: 1, failed: 1, lost: 1 });
    expect(l.byState("running")).toHaveLength(0);
  });

  it("survives close + reopen (rows intact)", () => {
    const file = join(mkdtempSync(join(tmpdir(), "gw-ledger-")), "gateway.sqlite3");
    const first = open(file);
    first.insert(binding());
    first.markState("exec-abc123", "completed");
    first.close();

    const again = open(file);
    expect(again.all()).toHaveLength(1);
    expect(again.byAssignment("asg-1")).toMatchObject({
      state: "completed",
      executionId: "exec-abc123",
    });
  });

  it("dedupe_key UNIQUE conflict surfaces as a typed conflict", () => {
    const l = open();
    l.insert(binding());
    expect(() => l.insert(binding({ deliveryId: "del-2", executionId: "exec-2" }))).toThrow(
      BindingConflictError,
    );
    // the original row stands — the conflict is "already bound", not corrupt
    expect(l.all()).toHaveLength(1);
    expect(l.byExecution("exec-abc123")).not.toBeNull();
  });

  it("indexes lookups: byAssignment resolves rows, byState filters", () => {
    const l = open();
    l.insert(binding({ executionId: "exec-1" }));
    l.insert(
      binding({
        executionId: "exec-2",
        dedupeKey: "cmd-2",
        deliveryId: "del-2",
        assignmentId: "asg-2",
      }),
    );
    expect(l.byAssignment("asg-1")?.executionId).toBe("exec-1");
    expect(l.byAssignment("nope")).toBeNull();
    expect(l.byState("running")).toHaveLength(2);
    expect(l.counts()).toEqual({ running: 2, completed: 0, failed: 0, lost: 0 });
  });
});

/** 05's exact v1 DDL (git HEAD), used to seed a pre-v2 file for the migration test. */
const V1_DDL = `
CREATE TABLE IF NOT EXISTS bindings (
  delivery_id   TEXT PRIMARY KEY,
  dedupe_key    TEXT NOT NULL UNIQUE,
  gateway_id    TEXT NOT NULL,
  child_id      TEXT NOT NULL,
  assignment_id TEXT NOT NULL,
  execution_id  TEXT NOT NULL,
  work_id       TEXT NOT NULL,
  connection_generation INTEGER NOT NULL,
  state         TEXT NOT NULL CHECK (state IN ('running','completed','failed','lost')),
  error         TEXT,
  started_at    TEXT NOT NULL,
  updated_at    TEXT NOT NULL
);
`;

function seedV1(file: string): void {
  const db = new Database(file);
  db.exec(`BEGIN IMMEDIATE;${V1_DDL}PRAGMA user_version = 1;COMMIT;`);
  db.prepare(
    `INSERT INTO bindings (delivery_id, dedupe_key, gateway_id, child_id, assignment_id,
                           execution_id, work_id, connection_generation, state, error,
                           started_at, updated_at)
     VALUES ('del-old', 'cmd-old', 'gw.t', 'child-old', 'asg-old',
             'exec-old', 'work-old', 1, 'running', NULL,
             '2026-10-01T00:00:00.000Z', '2026-10-01T00:00:00.000Z')`,
  ).run();
  db.close();
}

describe("ExecutionLedger schema v2/v3", () => {
  it("migrates a v1 file in place: version 3, child_state + pending_actions exist, old rows read with childGeneration 0", () => {
    const file = join(mkdtempSync(join(tmpdir(), "gw-ledger-")), "gateway.sqlite3");
    seedV1(file);

    const l = open(file);
    const db = new Database(file, { readonly: true });
    expect(db.pragma("user_version", { simple: true })).toBe(3);
    const tables = db
      .prepare("SELECT name FROM sqlite_master WHERE type = 'table' ORDER BY name")
      .all() as { name: string }[];
    expect(tables.map((t) => t.name)).toContain("child_state");
    expect(tables.map((t) => t.name)).toContain("pending_actions");
    db.close();

    // the pre-v2 row survived the ALTER and reads as generation 0
    const old = l.byExecution("exec-old");
    expect(old).not.toBeNull();
    expect(old).toMatchObject({ childGeneration: 0, connectionGeneration: 1, state: "running" });
    // fresh inserts under v2 carry the child fence
    l.insert(binding({ executionId: "exec-new", dedupeKey: "cmd-new", deliveryId: "del-new" }));
    expect(l.byExecution("exec-new")?.childGeneration).toBe(3);
  });

  it("rejects an unsupported schema version (7) with the 05 message pattern", () => {
    const file = join(mkdtempSync(join(tmpdir(), "gw-ledger-")), "gateway.sqlite3");
    const db = new Database(file);
    db.pragma("user_version = 7");
    db.close();
    expect(() => new ExecutionLedger(file)).toThrow(
      "bindings: unsupported schema version 7 (this build writes 3)",
    );
  });

  it("migrates a v2 file in place: pending_actions appears, bindings untouched", () => {
    const file = join(mkdtempSync(join(tmpdir(), "gw-ledger-")), "gateway.sqlite3");
    seedV1(file);
    new ExecutionLedger(file).close(); // v1 → v3
    const v2 = new Database(file);
    v2.exec("DROP TABLE pending_actions; PRAGMA user_version = 2;");
    v2.close();
    const l = open(file);
    const db = new Database(file, { readonly: true });
    expect(db.pragma("user_version", { simple: true })).toBe(3);
    const tables = db
      .prepare("SELECT name FROM sqlite_master WHERE type = 'table' ORDER BY name")
      .all() as { name: string }[];
    expect(tables.map((t) => t.name)).toContain("pending_actions");
    db.close();
    expect(l.byExecution("exec-old")?.state).toBe("running");
  });

  it("upsertChild: first bump starts generation 1, re-register bumps, no-bump keeps it", () => {
    const l = open();
    l.upsertChild("gw.t/a", "dig-1", '{"capabilities":[]}', true);
    expect(l.childGeneration("gw.t/a")).toBe(1);

    l.upsertChild("gw.t/a", "dig-2", '{"capabilities":[1]}', true);
    expect(l.childGeneration("gw.t/a")).toBe(2);
    l.upsertChild("gw.t/a", "dig-3", '{"capabilities":[2]}', false);
    expect(l.childGeneration("gw.t/a")).toBe(2);

    const row = l.children().find((c) => c.childId === "gw.t/a");
    expect(row).toMatchObject({
      manifestDigest: "dig-3",
      manifestJson: '{"capabilities":[2]}',
      state: "connected",
      offlineAt: null,
    });
    expect(row?.connectedAt).toMatch(/^\d{4}-\d{2}-\d{2}T/);
  });

  it("upsertChild without bump on a fresh child starts generation 0", () => {
    const l = open();
    l.upsertChild("gw.t/b", "dig", "{}", false);
    expect(l.childGeneration("gw.t/b")).toBe(0);
    // a later re-register with bump moves 0 → 1
    l.upsertChild("gw.t/b", "dig", "{}", true);
    expect(l.childGeneration("gw.t/b")).toBe(1);
  });

  it("setChildState: offline stamps offline_at; connected refreshes connected_at and clears it", () => {
    const l = open();
    l.upsertChild("gw.t/c", "dig", "{}", true);
    const connectedAt = l.children()[0]?.connectedAt;
    expect(connectedAt).toMatch(/^\d{4}-\d{2}-\d{2}T/);

    l.setChildState("gw.t/c", "offline");
    const off = l.children().find((c) => c.childId === "gw.t/c");
    expect(off).toMatchObject({ state: "offline" });
    expect(off?.offlineAt).toMatch(/^\d{4}-\d{2}-\d{2}T/);

    l.setChildState("gw.t/c", "connected");
    const back = l.children().find((c) => c.childId === "gw.t/c");
    expect(back).toMatchObject({ state: "connected", offlineAt: null });
    // generation untouched by state transitions
    expect(back?.childGeneration).toBe(1);
    // a child that never registered: no-op, no row appears
    l.setChildState("gw.t/ghost", "offline");
    expect(l.children().some((c) => c.childId === "gw.t/ghost")).toBe(false);
  });

  it("childGeneration returns 0 for an unknown child; children() lists the fleet", () => {
    const l = open();
    expect(l.childGeneration("gw.t/none")).toBe(0);
    expect(l.children()).toEqual([]);
    l.upsertChild("gw.t/z", "dz", "{}", true);
    l.upsertChild("gw.t/a", "da", "{}", true);
    expect(l.children().map((c) => c.childId)).toEqual(["gw.t/a", "gw.t/z"]);
  });

  it("byChildState returns only that child's rows in that state, oldest first", () => {
    const l = open();
    l.insert(
      binding({
        executionId: "exec-a1",
        dedupeKey: "cmd-a1",
        deliveryId: "del-a1",
        childId: "gw.t/a",
      }),
    );
    l.insert(
      binding({
        executionId: "exec-a2",
        dedupeKey: "cmd-a2",
        deliveryId: "del-a2",
        childId: "gw.t/a",
      }),
    );
    l.insert(
      binding({
        executionId: "exec-b1",
        dedupeKey: "cmd-b1",
        deliveryId: "del-b1",
        childId: "gw.t/b",
      }),
    );
    l.markState("exec-a2", "completed");

    expect(l.byChildState("gw.t/a", "running").map((r) => r.executionId)).toEqual(["exec-a1"]);
    expect(l.byChildState("gw.t/a", "completed").map((r) => r.executionId)).toEqual(["exec-a2"]);
    expect(l.byChildState("gw.t/b", "running")).toHaveLength(1);
    expect(l.byChildState("gw.t/none", "running")).toEqual([]);
  });
});
