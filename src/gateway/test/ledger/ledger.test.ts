import { mkdtempSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { afterEach, describe, expect, it } from "vitest";

import {
  BindingConflictError,
  ExecutionLedger,
  type NewBinding,
} from "../../src/ledger.js";

let ledger: ExecutionLedger | null = null;

function open(file?: string): ExecutionLedger {
  ledger = new ExecutionLedger(file ?? join(mkdtempSync(join(tmpdir(), "gw-ledger-")), "gateway.sqlite3"));
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
    expect(again.byAssignment("asg-1")).toMatchObject({ state: "completed", executionId: "exec-abc123" });
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
