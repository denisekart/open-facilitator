// The in-memory Ledger mirrors SqliteLedger semantics exactly (the SQLite
// suite proves the durable twin): fresh/duplicate on either identity, the
// retryable-rejection law (a `failed` row re-freshes on re-offer while
// processed/unsupported stay duplicate), state/stats/pending, the newest-first
// processed hint, and the outbound-event outbox.
import { describe, expect, it } from "vitest";
import { MemoryLedger, dedupeKeyOf } from "../../src/index.js";
import { KNOWN_COMMAND_TYPES } from "@ofac/protocol";

describe("MemoryLedger inbox", () => {
  it("fresh, then duplicate on deliveryId and on dedupeKey", () => {
    const ledger = new MemoryLedger();
    expect(ledger.insertIfNew("del-1", { id: "e1" }, "cmd-1")).toBe("fresh");
    expect(ledger.insertIfNew("del-1", { id: "e1" }, "cmd-other")).toBe("duplicate");
    expect(ledger.insertIfNew("del-2", { id: "e2" }, "cmd-1")).toBe("duplicate");
  });

  it("a failed row re-freshes on re-offer; processed/unsupported stay duplicate", () => {
    const ledger = new MemoryLedger();
    ledger.insertIfNew("del-a", { v: 1 }, "k-a");
    ledger.markState("del-a", "failed", "child-busy");
    expect(ledger.insertIfNew("del-a", { v: 2 }, "k-a")).toBe("fresh");
    // The retry replaced the envelope and cleared the error.
    ledger.markState("del-a", "processed");
    expect(ledger.insertIfNew("del-a", { v: 3 }, "k-a")).toBe("duplicate");
    ledger.insertIfNew("del-b", {}, "k-b");
    ledger.markState("del-b", "unsupported");
    expect(ledger.insertIfNew("del-b", {}, "k-b")).toBe("duplicate");
  });

  it("markState on an unknown delivery throws", () => {
    const ledger = new MemoryLedger();
    expect(() => ledger.markState("nope", "processed")).toThrow(/unknown delivery nope/);
  });

  it("stats counts each state; pending lists the receipts still owed", () => {
    const ledger = new MemoryLedger();
    ledger.insertIfNew("d1", {}, "k1");
    ledger.insertIfNew("d2", {}, "k2");
    ledger.insertIfNew("d3", {}, "k3");
    ledger.insertIfNew("d4", {}, "k4");
    ledger.markState("d2", "processed");
    ledger.markState("d3", "failed", "boom");
    ledger.markState("d4", "unsupported");
    expect(ledger.stats()).toEqual({ received: 1, processed: 1, failed: 1, unsupported: 1 });
    expect(ledger.pending()).toEqual([{ deliveryId: "d1", state: "received" }]);
  });

  it("processedDeliveries is newest-first and honors the limit", () => {
    const ledger = new MemoryLedger();
    for (const id of ["a", "b", "c"]) ledger.insertIfNew(id, {}, `k-${id}`);
    ledger.markState("a", "processed");
    ledger.markState("b", "processed");
    ledger.markState("c", "processed");
    expect(ledger.processedDeliveries(10)).toEqual(["c", "b", "a"]);
    expect(ledger.processedDeliveries(2)).toEqual(["c", "b"]);
    expect(ledger.processedDeliveries(0)).toEqual([]);
  });
});

describe("MemoryLedger outbox", () => {
  it("queues events, lists only unsent ones, and remembers sent ones", () => {
    const ledger = new MemoryLedger();
    ledger.queueEvent("e1", { id: "e1" });
    ledger.queueEvent("e2", { id: "e2" });
    ledger.queueEvent("e3", { id: "e3" });
    expect(ledger.unsentEvents(2)).toEqual([
      { id: "e1", envelope: { id: "e1" } },
      { id: "e2", envelope: { id: "e2" } },
    ]);
    ledger.markEventSent("e1");
    expect(ledger.unsentEvents(10).map((e) => e.id)).toEqual(["e2", "e3"]);
    // Re-queueing an id replaces the pending envelope; marking an unknown id
    // sent is a no-op (the drain raced a requeue).
    ledger.queueEvent("e2", { id: "e2", v: 2 });
    expect(ledger.unsentEvents(10)[0]?.envelope).toEqual({ id: "e2", v: 2 });
    expect(() => ledger.markEventSent("ghost")).not.toThrow();
    ledger.close(); // durable nothing, but part of the seam
  });
});

describe("dedupeKeyOf (the ledger's shared identity law)", () => {
  it("uses commandId for known command types", () => {
    const env = {
      type: KNOWN_COMMAND_TYPES[0]!,
      source: "s",
      id: "e",
      data: { commandId: "cmd-9" },
    };
    expect(dedupeKeyOf(env, KNOWN_COMMAND_TYPES)).toBe("cmd-9");
  });

  it("falls back to the prefix-guarded (source,id) join", () => {
    const env = (source: string, id: string) => ({ type: "com.example", source, id });
    expect(dedupeKeyOf(env("a", "b|c"), KNOWN_COMMAND_TYPES)).toBe(
      dedupeKeyOf(env("a", "b|c"), KNOWN_COMMAND_TYPES),
    );
    expect(dedupeKeyOf(env("a", "b|c"), KNOWN_COMMAND_TYPES)).not.toBe(
      dedupeKeyOf(env("a|b", "c"), KNOWN_COMMAND_TYPES),
    );
  });
});
