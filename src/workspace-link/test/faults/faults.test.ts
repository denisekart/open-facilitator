// FaultInjector (test mode, A:360): dropped receipts, partitions, duplicate
// Deliver pushes — against the real client state machine + MemoryLedger.
import { describe, expect, it } from "vitest";
import type { LinkDeliveryV1 } from "@ofac/protocol";
import {
  createLinkClient,
  FaultInjector,
  MemoryLedger,
  createMemoryLogger,
} from "../../src/index.js";
import { MockTransport, flush } from "../transport/mock-transport.js";

const ENVELOPE = {
  specversion: "1.0" as const,
  id: "env-1",
  source: "urn:facilitator:instance:main",
  type: "org.facilitator.command.work.assign.v1",
  subject: "work/w-1/assignment/a-1",
  time: "2026-10-22T18:00:00Z",
  datacontenttype: "application/json" as const,
  dataschema: "https://schemas.openfacilitator.org/v1/command.work.assign.v1.json",
  data: {
    commandId: "cmd-1",
    workId: "w-1",
    assignmentId: "a-1",
    targetId: "gw-1",
    capabilityId: "research.analyze",
    expectedContextVersion: 1,
    executionGeneration: 1,
    actorEvidenceRef: "identity-evidence:r",
    deadline: "2026-10-23T18:00:00Z",
    inputs: { objective: "x" },
    expectedDeliverables: [{ kind: "analysis.report", mediaType: "text/markdown" }],
  },
};

function makeDelivery(deliveryId: string): LinkDeliveryV1 {
  return { deliveryId, envelope: ENVELOPE, deliveredAt: "2026-10-22T18:00:01Z" };
}

describe("FaultInjector", () => {
  it("dropReceipt swallows the send; the redelivery then answers duplicate", async () => {
    const inner = new MockTransport();
    let dropBudget = 1;
    const faults = new FaultInjector(inner, { dropReceipt: () => dropBudget-- > 0 });
    let calls = 0;
    const client = createLinkClient({
      transport: faults,
      ledger: new MemoryLedger(),
      endpointId: "gw-1",
      capabilityManifestDigest: `sha256:${"ab".repeat(32)}`,
      token: { getToken: async () => "t" },
      handler: {
        async onCommand() {
          calls += 1;
          return { kind: "accepted" };
        },
      },
      logger: createMemoryLogger(),
    });
    await client.start();
    // The dropped-receipt loop (S:302): deliver → handler runs → receipt
    // swallowed → server redelivers → duplicate, handler NOT re-run.
    inner.pushDeliver(makeDelivery("del-1"));
    await flush(8);
    expect(calls).toBe(1);
    expect(inner.receipts).toHaveLength(0); // swallowed by the injector
    inner.pushDeliver(makeDelivery("del-1"));
    await flush(8);
    expect(calls).toBe(1);
    expect(inner.receipts.map((r) => r.status)).toEqual(["duplicate"]);
    await client.stop();
  });

  it("duplicateDeliver replays the push; pipeline dedupes it", async () => {
    const inner = new MockTransport();
    const faults = new FaultInjector(inner, { duplicateDeliver: () => true });
    let calls = 0;
    const client = createLinkClient({
      transport: faults,
      ledger: new MemoryLedger(),
      endpointId: "gw-1",
      capabilityManifestDigest: `sha256:${"ab".repeat(32)}`,
      token: { getToken: async () => "t" },
      handler: {
        async onCommand() {
          calls += 1;
          return { kind: "accepted" };
        },
      },
      logger: createMemoryLogger(),
    });
    await client.start();
    inner.pushDeliver(makeDelivery("del-2"));
    await flush(12);
    expect(calls).toBe(1);
    expect(inner.receipts.map((r) => r.status).sort()).toEqual(["accepted", "duplicate"]);
    await client.stop();
  });

  it("partition buffers outbound events; the post-welcome drain resends them", async () => {
    const inner = new MockTransport();
    let partitioned = false;
    const faults = new FaultInjector(inner, { partition: () => partitioned });
    const ledger = new MemoryLedger();
    const client = createLinkClient({
      transport: faults,
      ledger,
      endpointId: "gw-1",
      capabilityManifestDigest: `sha256:${"ab".repeat(32)}`,
      token: { getToken: async () => "t" },
      logger: createMemoryLogger(),
      backoffMs: [1],
    });
    await client.start();
    partitioned = true;
    const ack = await client.publishEvent({
      specversion: "1.0",
      id: "evt-buffered",
      source: "urn:workspace:gw-1",
      type: "org.facilitator.event.work.progress.v1",
      subject: "work/w-1/assignment/a-1",
      time: "2026-10-22T18:00:02Z",
      datacontenttype: "application/json",
      dataschema: "https://schemas.openfacilitator.org/v1/event.work.progress.v1.json",
      data: {
        workId: "w-1",
        assignmentId: "a-1",
        executionGeneration: 1,
        progressNote: "buffered",
      },
    });
    // Partitioned publish resolves accepted (business outcome stands) and
    // buffers durably for the next session.
    expect(ack.status).toBe("accepted");
    expect(ledger.unsentEvents(10).map((e) => e.id)).toEqual(["evt-buffered"]);
    // Heal + session bounce: the drain after the re-hello resends it.
    partitioned = false;
    inner.pushDisconnect({ kind: "goodbye", sessionId: "sess-1", reason: "session-superseded" });
    await new Promise((r) => setTimeout(r, 20));
    await flush(8);
    expect(inner.envelopes.map((e) => e.id)).toContain("evt-buffered");
    expect(ledger.unsentEvents(10)).toEqual([]);
    await client.stop();
  });
});
