// The trace seam: `instrument` wraps exactly the handler invocation of one
// delivery (name "link.deliver", envelope as carrier) — and when the option
// is absent, every consumer stays byte-compatible (identity wrapper: the
// suites above all run untouched).
import { describe, expect, it } from "vitest";
import type { EnvelopeCommand, LinkDeliveryV1 } from "@ofac/protocol";
import {
  createLinkClient,
  createMemoryLogger,
  MemoryLedger,
  type LinkHandler,
} from "../../src/index.js";
import { MockTransport, flush } from "../transport/mock-transport.js";

const DIGEST = `sha256:${"ab".repeat(32)}`;

function envelope(commandId: string, traceparent?: string): EnvelopeCommand {
  return {
    specversion: "1.0",
    id: `env-${commandId}`,
    source: "urn:facilitator:instance:main",
    type: "org.facilitator.command.work.assign.v1",
    subject: `work/work-123/assignment/${commandId}`,
    time: "2026-10-22T18:00:00Z",
    datacontenttype: "application/json",
    dataschema: "https://schemas.openfacilitator.org/v1/command.work.assign.v1.json",
    ...(traceparent !== undefined ? { traceparent } : {}),
    data: {
      commandId,
      workId: "work-123",
      assignmentId: commandId,
      targetId: "gw-1",
      capabilityId: "research.analyze",
      expectedContextVersion: 1,
      executionGeneration: 1,
      actorEvidenceRef: "identity-evidence:ref-123",
      deadline: "2026-10-23T18:00:00Z",
      inputs: { objective: "compare", sourceRefs: [] },
      expectedDeliverables: [{ kind: "analysis.report", mediaType: "text/markdown" }],
    },
  };
}

function deliveryOf(deliveryId: string, env: EnvelopeCommand): LinkDeliveryV1 {
  return { deliveryId, envelope: env, deliveredAt: "2026-10-22T18:00:01Z" };
}

const accepted: LinkHandler = {
  async onCommand() {
    return { kind: "accepted" };
  },
};

describe("instrument seam", () => {
  it("absent instrument: identity behavior, delivery still completes", async () => {
    const transport = new MockTransport();
    const client = createLinkClient({
      transport,
      ledger: new MemoryLedger(),
      endpointId: "gw-1",
      capabilityManifestDigest: DIGEST,
      token: { getToken: async () => "t" },
      handler: accepted,
      logger: createMemoryLogger(),
    });
    await client.start();
    transport.pushDeliver(deliveryOf("del-1", envelope("cmd-1")));
    await flush(8);
    expect(transport.receipts).toHaveLength(1);
    expect(transport.receipts[0].status).toBe("accepted");
    await client.stop();
  });

  it("instrument sees one wrap per delivery, named link.deliver, with the carrier envelope", async () => {
    const transport = new MockTransport();
    const seen: { name: string; traceparent: string | undefined; ran: boolean }[] = [];
    const client = createLinkClient({
      transport,
      ledger: new MemoryLedger(),
      endpointId: "gw-1",
      capabilityManifestDigest: DIGEST,
      token: { getToken: async () => "t" },
      handler: accepted,
      logger: createMemoryLogger(),
      async instrument(name, env, run) {
        const before = seen.length;
        const result = await run();
        seen.push({
          name,
          traceparent: env.traceparent,
          ran: before === 0 && transport.receipts.length === 0,
        });
        return result;
      },
    });
    await client.start();
    transport.pushDeliver(
      deliveryOf(
        "del-1",
        envelope("cmd-1", "00-0af1e2d3c4b5a69788796a5b4c3d2e1f-4d0c0b0a09080706-01"),
      ),
    );
    await flush(8);
    expect(seen).toHaveLength(1);
    expect(seen[0].name).toBe("link.deliver");
    expect(seen[0].traceparent).toBe("00-0af1e2d3c4b5a69788796a5b4c3d2e1f-4d0c0b0a09080706-01");
    // The wrapper runs the handler BEFORE the receipt ships (it wraps the
    // invocation, not the whole pipeline).
    expect(seen[0].ran).toBe(true);
    expect(transport.receipts).toHaveLength(1);
    await client.stop();
  });
});
