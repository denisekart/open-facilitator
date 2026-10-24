// SQLite ledger on a temp file: the durability proofs (fresh/duplicate/
// state/stats, reopen-persistence, outbox queue). The dedupe identity is
// exercised through SqliteLedger's own insert path (public surface only).
import { mkdtempSync, rmSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { afterAll, beforeAll, describe, expect, it } from "vitest";
import { KNOWN_COMMAND_TYPES } from "@ofac/protocol";
import { dedupeKeyOf } from "../../src/index.js";
import { SqliteLedger } from "../../src/node/index.js";

let dir: string;

beforeAll(() => {
  dir = mkdtempSync(join(tmpdir(), "ofac-ledger-"));
});

afterAll(() => {
  rmSync(dir, { recursive: true, force: true });
});

describe("SqliteLedger", () => {
  it("fresh insert then duplicate on deliveryId and on dedupeKey", () => {
    const ledger = new SqliteLedger(join(dir, "dup.sqlite3"));
    expect(ledger.insertIfNew("del-1", { id: "e1" }, "cmd-1")).toBe("fresh");
    expect(ledger.insertIfNew("del-1", { id: "e1" }, "cmd-other")).toBe("duplicate");
    expect(ledger.insertIfNew("del-2", { id: "e2" }, "cmd-1")).toBe("duplicate");
    ledger.close();
  });

  it("state transitions, stats, processed hint ordering", () => {
    const ledger = new SqliteLedger(join(dir, "states.sqlite3"));
    ledger.insertIfNew("del-a", {}, "k-a");
    ledger.insertIfNew("del-b", {}, "k-b");
    ledger.markState("del-a", "processed");
    ledger.markState("del-b", "failed", "boom");
    expect(ledger.stats()).toEqual({ received: 0, processed: 1, failed: 1, unsupported: 0 });
    expect(ledger.processedDeliveries(10)).toEqual(["del-a"]);
    expect(ledger.pending()).toEqual([]);
    expect(() => ledger.markState("nope", "processed")).toThrow(/unknown delivery/);
    ledger.close();
  });

  it("rows survive a reopen (the crash-proof that underpins replay)", () => {
    const path = join(dir, "persist.sqlite3");
    const first = new SqliteLedger(path);
    first.insertIfNew("del-p", { envelope: 1 }, "k-p");
    first.markState("del-p", "processed");
    first.queueEvent("evt-1", { id: "evt-1" });
    first.close();
    const second = new SqliteLedger(path);
    expect(second.insertIfNew("del-p", {}, "k-p")).toBe("duplicate");
    expect(second.processedDeliveries(5)).toEqual(["del-p"]);
    expect(second.unsentEvents(5)).toEqual([{ id: "evt-1", envelope: { id: "evt-1" } }]);
    second.markEventSent("evt-1");
    expect(second.unsentEvents(5)).toEqual([]);
    second.close();
  });

  it("known-command identity: one row per commandId, delivered twice", () => {
    const ledger = new SqliteLedger(join(dir, "dedupe.sqlite3"));
    const env = {
      type: "org.facilitator.command.work.assign.v1",
      source: "urn:x",
      id: "env-1",
      data: { commandId: "cmd-9" },
    };
    // Redelivery under a fresh deliveryId hits the business identity: the
    // second insert answers `duplicate` and the table stays at one row.
    const key = dedupeKeyOf(env, KNOWN_COMMAND_TYPES);
    expect(ledger.insertIfNew("del-1", env, key)).toBe("fresh");
    expect(ledger.insertIfNew("del-2", env, key)).toBe("duplicate");
    expect(ledger.stats()).toEqual({ received: 1, processed: 0, failed: 0, unsupported: 0 });
    // Non-command identity is the prefix-guarded (source,id) join.
    expect(
      dedupeKeyOf(
        { type: "org.facilitator.event.x.v1", source: "urn:x", id: "env-1" },
        KNOWN_COMMAND_TYPES,
      ),
    ).toBe("\u241furn:x\u241fenv-1");
    ledger.close();
  });
});
