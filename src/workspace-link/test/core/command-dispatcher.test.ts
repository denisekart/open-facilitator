// CommandDispatcher in isolation (no client, no transport): every outcome
// maps onto the receipt + ledger state + counter the pipeline law demands,
// the ledger flips BEFORE the receipt ships, kernel-pushed approval.decided
// routes to the handler's onDecided arm, and the instrument seam wraps exactly
// one handler call.
import { afterAll, describe, expect, it, vi } from "vitest";
import type { EnvelopeCommand, EnvelopeEvent, LinkDeliveryV1, ReceiptV1 } from "@ofac/protocol";
import { CommandDispatcher } from "../../src/core/command-dispatcher.js";
import {
  MemoryLedger,
  type EnvelopeAck,
  type LinkHandler,
  type LinkLogger,
  type LinkStats,
} from "../../src/index.js";
import { DECIDED_TYPE, assignEnvelope, cleanUp, deliveryOf } from "./fixtures.js";

afterAll(() => {
  cleanUp();
});

const silent: LinkLogger = { debug() {}, info() {}, warn() {}, error() {} };

function harness(
  handler?: LinkHandler,
  instrument?: <T>(name: string, env: EnvelopeCommand, run: () => Promise<T>) => Promise<T>,
): {
  dispatcher: CommandDispatcher;
  ledger: MemoryLedger;
  receipts: { deliveryId: string; receipt: ReceiptV1 }[];
  events: EnvelopeEvent[];
  stats: LinkStats;
  order: string[];
} {
  const ledger = new MemoryLedger();
  const receipts: { deliveryId: string; receipt: ReceiptV1 }[] = [];
  const events: EnvelopeEvent[] = [];
  const stats: LinkStats = {
    delivered: 0,
    duplicate: 0,
    processed: 0,
    failed: 0,
    unsupported: 0,
    reconnects: 0,
  };
  // Records "mark" then "receipt" so the ordering law is assertable.
  const order: string[] = [];
  const markOriginal = ledger.markState.bind(ledger);
  vi.spyOn(ledger, "markState").mockImplementation((deliveryId, state, error) => {
    order.push("mark");
    if (error === undefined) markOriginal(deliveryId, state);
    else markOriginal(deliveryId, state, error);
  });
  const dispatcher = new CommandDispatcher({
    ...(handler === undefined ? {} : { handler }),
    ledger,
    logger: silent,
    counters: stats,
    endpointId: "gw-1",
    ...(instrument === undefined ? {} : { instrument }),
    sendReceipt: async (deliveryId, partial) => {
      order.push("receipt");
      receipts.push({ deliveryId, receipt: { deliveryId, ...partial } as ReceiptV1 });
    },
    publishEvent: async (env) => {
      events.push(env);
      return { status: "accepted" } satisfies EnvelopeAck;
    },
  });
  return { dispatcher, ledger, receipts, events, stats, order };
}

function seed(ledger: MemoryLedger, delivery: LinkDeliveryV1, dedupeKey = "cmd-1"): void {
  ledger.insertIfNew(delivery.deliveryId, delivery.envelope, dedupeKey);
}

describe("CommandDispatcher outcomes", () => {
  it("accepted: processed state, counter, accepted receipt echoing the epoch", async () => {
    const h = harness({ onCommand: async () => ({ kind: "accepted" }) });
    const delivery = deliveryOf("del-a", assignEnvelope());
    seed(h.ledger, delivery);
    await h.dispatcher.dispatch(delivery);
    expect(h.receipts).toHaveLength(1);
    expect(h.receipts[0]!.receipt).toMatchObject({
      deliveryId: "del-a",
      status: "accepted",
      executionGeneration: 2,
    });
    expect(h.ledger.stats()).toEqual({ received: 0, processed: 1, failed: 0, unsupported: 0 });
    expect(h.stats.processed).toBe(1);
    // The law: ledger flip BEFORE the receipt.
    expect(h.order).toEqual(["mark", "receipt"]);
  });

  it("rejected: failed state, failed counter, reason on the receipt", async () => {
    const h = harness({ onCommand: async () => ({ kind: "rejected", reason: "child-busy" }) });
    const delivery = deliveryOf("del-r", assignEnvelope());
    seed(h.ledger, delivery);
    await h.dispatcher.dispatch(delivery);
    expect(h.receipts[0]!.receipt).toEqual({
      deliveryId: "del-r",
      status: "rejected",
      reason: "child-busy",
      executionGeneration: 2,
    });
    expect(h.ledger.stats().failed).toBe(1);
    expect(h.stats.failed).toBe(1);
  });

  it("unsupported: unsupported state with the default reason", async () => {
    const h = harness({ onCommand: async () => ({ kind: "unsupported" }) });
    const delivery = deliveryOf("del-u", assignEnvelope());
    seed(h.ledger, delivery);
    await h.dispatcher.dispatch(delivery);
    expect(h.receipts[0]!.receipt).toMatchObject({
      deliveryId: "del-u",
      status: "unsupported",
      reason: "capability not supported",
    });
    expect(h.ledger.stats().unsupported).toBe(1);
    expect(h.stats.unsupported).toBe(1);
  });

  it("a throwing handler becomes a rejected receipt (and the ledger says failed)", async () => {
    const h = harness({
      onCommand: async () => {
        throw new Error("handler exploded");
      },
    });
    const delivery = deliveryOf("del-x", assignEnvelope());
    seed(h.ledger, delivery);
    await h.dispatcher.dispatch(delivery);
    const receipt = h.receipts[0]!.receipt;
    expect(receipt["status"]).toBe("rejected");
    if (receipt["status"] === "rejected") expect(receipt["reason"]).toContain("handler exploded");
    expect(h.ledger.stats().failed).toBe(1);
  });

  it("no handler mounted fail-closes to unsupported", async () => {
    const h = harness();
    const delivery = deliveryOf("del-n", assignEnvelope());
    seed(h.ledger, delivery);
    await h.dispatcher.dispatch(delivery);
    expect(h.receipts[0]!.receipt).toMatchObject({
      status: "unsupported",
      reason: "no handler mounted",
    });
  });

  it("an unknown command type is unsupported without touching the handler", async () => {
    let called = 0;
    const h = harness({
      onCommand: async () => {
        called += 1;
        return { kind: "accepted" };
      },
    });
    const delivery = deliveryOf("del-k", {
      ...assignEnvelope(),
      type: "com.example.unknown",
    });
    seed(h.ledger, delivery, "evt-unknown");
    await h.dispatcher.dispatch(delivery);
    expect(called).toBe(0);
    expect(h.receipts[0]!.receipt).toMatchObject({
      status: "unsupported",
      reason: "unknown command type com.example.unknown",
    });
  });
});

describe("approval.decided routing", () => {
  const decided = (): EnvelopeCommand => ({ ...assignEnvelope("cmd-d"), type: DECIDED_TYPE });

  it("routes to onDecided and accepts", async () => {
    const seen: EnvelopeEvent[] = [];
    const h = harness({
      onCommand: async () => ({ kind: "accepted" }),
      onDecided: async (env) => {
        seen.push(env);
      },
    });
    const delivery = deliveryOf("del-d", decided());
    seed(h.ledger, delivery, "evt-1");
    await h.dispatcher.dispatch(delivery);
    expect(seen).toHaveLength(1);
    expect(seen[0]!.type).toBe(DECIDED_TYPE);
    expect(h.receipts[0]!.receipt).toMatchObject({ status: "accepted" });
    expect(h.stats.processed).toBe(1);
  });

  it("a throwing onDecided answers rejected", async () => {
    const h = harness({
      onCommand: async () => ({ kind: "accepted" }),
      onDecided: async () => {
        throw new Error("decided exploded");
      },
    });
    const delivery = deliveryOf("del-d2", decided());
    seed(h.ledger, delivery, "evt-2");
    await h.dispatcher.dispatch(delivery);
    const receipt = h.receipts[0]!.receipt;
    expect(receipt["status"]).toBe("rejected");
    if (receipt["status"] === "rejected") expect(receipt["reason"]).toContain("decided exploded");
  });

  it("a decided push with no onDecided arm lands unsupported (fail-closed)", async () => {
    const h = harness({ onCommand: async () => ({ kind: "accepted" }) });
    const delivery = deliveryOf("del-d3", decided());
    seed(h.ledger, delivery, "evt-3");
    await h.dispatcher.dispatch(delivery);
    expect(h.receipts[0]!.receipt).toMatchObject({ status: "unsupported" });
  });
});

describe("the instrument seam", () => {
  it("wraps exactly one handler invocation and passes the result through", async () => {
    const names: string[] = [];
    const h = harness(
      { onCommand: async () => ({ kind: "accepted" }) },
      async (name, _env, run) => {
        names.push(name);
        return run();
      },
    );
    const delivery = deliveryOf("del-i", assignEnvelope());
    seed(h.ledger, delivery);
    await h.dispatcher.dispatch(delivery);
    expect(names).toEqual(["link.deliver"]);
    expect(h.stats.processed).toBe(1);
  });
});
