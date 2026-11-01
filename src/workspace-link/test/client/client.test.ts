// Client core unit tier: the mock transport is the only wire. Proofs mirror
// the plan's verification list — hello validity, terminal protocol mismatch,
// the pipeline law, duplicate answering, fail-closed unsupported, advisory
// flow control, heartbeat cadence.
import { describe, expect, it, vi } from "vitest";
import {
  validateControlFrame,
  validateReceipt,
  type EnvelopeCommand,
  type LinkDeliveryV1,
} from "@ofac/protocol";
import {
  createLinkClient,
  MemoryLedger,
  ProtocolMismatchError,
  createMemoryLogger,
  type LinkHandler,
} from "../../src/index.js";
import { MockTransport, flush } from "../transport/mock-transport.js";

const ASSIGN = "org.facilitator.command.work.assign.v1";
const PROGRESS = "org.facilitator.event.work.progress.v1";
const DIGEST = `sha256:${"ab".repeat(32)}`;

function assignEnvelope(commandId: string, id = `env-${commandId}`): EnvelopeCommand {
  return {
    specversion: "1.0",
    id,
    source: "urn:facilitator:instance:main",
    type: ASSIGN,
    subject: `work/work-123/assignment/${commandId}`,
    time: "2026-10-22T18:00:00Z",
    datacontenttype: "application/json",
    dataschema: "https://schemas.openfacilitator.org/v1/command.work.assign.v1.json",
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
      inputs: { objective: "compare", sourceRefs: ["artifact://s/1"] },
      expectedDeliverables: [{ kind: "analysis.report", mediaType: "text/markdown" }],
    },
  };
}

function delivery(deliveryId: string, env: EnvelopeCommand): LinkDeliveryV1 {
  return { deliveryId, envelope: env, deliveredAt: "2026-10-22T18:00:01Z" };
}

function clientWith(transport: MockTransport, handler?: LinkHandler) {
  return createLinkClient({
    transport,
    ledger: new MemoryLedger(),
    endpointId: "gw-1",
    capabilityManifestDigest: DIGEST,
    token: { getToken: async () => "t" },
    handler,
    logger: createMemoryLogger(),
    backoffMs: [1],
  });
}

describe("handshake", () => {
  it("builds a hello that passes validateControlFrame and reaches ready", async () => {
    const transport = new MockTransport();
    const client = clientWith(transport);
    const session = await client.start();
    expect(session.leaseEpoch).toBe(1);
    expect(transport.hellos).toHaveLength(1);
    expect(validateControlFrame(transport.hellos[0])).toEqual({ ok: true });
    expect(transport.hellos[0].protocolVersions).toEqual(["v1"]);
    expect(transport.hellos[0].endpointId).toBe("gw-1");
    // lastKnownAck omitted on an empty ledger.
    expect(transport.hellos[0].lastKnownAck).toBeUndefined();
    await client.stop();
  });

  it("hints lastKnownAck from the newest processed ledger row", async () => {
    const transport = new MockTransport();
    const ledger = new MemoryLedger();
    ledger.insertIfNew("del-9", {}, "cmd-9");
    ledger.markState("del-9", "processed");
    const client = createLinkClient({
      transport,
      ledger,
      endpointId: "gw-1",
      capabilityManifestDigest: DIGEST,
      token: { getToken: async () => "t" },
      logger: createMemoryLogger(),
    });
    await client.start();
    expect(transport.hellos[0].lastKnownAck).toBe("del-9");
    await client.stop();
  });

  it("null welcome + incompatible goodbye is terminal: no retry, typed error", async () => {
    const transport = new MockTransport();
    transport.welcome = null;
    const client = clientWith(transport);
    const failures: Error[] = [];
    client.on("error", (e) => failures.push(e));
    await expect(client.start()).rejects.toBeInstanceOf(ProtocolMismatchError);
    await flush();
    expect(transport.connectCount).toBe(1); // exactly one connect, never retried
    expect(client.state).toBe("closed");
    expect(failures[0]).toBeInstanceOf(ProtocolMismatchError);
  });
});

describe("delivery pipeline", () => {
  it("fresh command → handler accepted → receipt accepted + processedAt", async () => {
    const transport = new MockTransport();
    const client = clientWith(transport, {
      async onCommand() {
        return { kind: "accepted" };
      },
    });
    await client.start();
    transport.pushDeliver(delivery("del-1", assignEnvelope("cmd-1")));
    await flush(8);
    expect(transport.receipts).toHaveLength(1);
    const receipt = transport.receipts[0];
    expect(receipt.status).toBe("accepted");
    expect(typeof receipt.processedAt).toBe("string");
    expect(validateReceipt(receipt)).toEqual({ ok: true });
    expect(client.stats.processed).toBe(1);
    await client.stop();
  });

  it("redelivered deliveryId → duplicate receipt, handler ran once", async () => {
    const transport = new MockTransport();
    let calls = 0;
    const client = clientWith(transport, {
      async onCommand() {
        calls += 1;
        return { kind: "accepted" };
      },
    });
    await client.start();
    const d = delivery("del-1", assignEnvelope("cmd-1"));
    transport.pushDeliver(d);
    await flush(8);
    transport.pushDeliver({ ...d, deliveredAt: "2026-10-22T18:05:00Z" });
    await flush(8);
    expect(calls).toBe(1);
    expect(transport.receipts.map((r) => r.status)).toEqual(["accepted", "duplicate"]);
    expect(validateReceipt(transport.receipts[1])).toEqual({ ok: true });
    await client.stop();
  });

  it("same commandId on a different deliveryId dedupes on business identity", async () => {
    const transport = new MockTransport();
    let calls = 0;
    const client = clientWith(transport, {
      async onCommand() {
        calls += 1;
        return { kind: "accepted" };
      },
    });
    await client.start();
    transport.pushDeliver(delivery("del-1", assignEnvelope("cmd-x")));
    await flush(8);
    transport.pushDeliver(delivery("del-2", assignEnvelope("cmd-x")));
    await flush(8);
    expect(calls).toBe(1);
    expect(transport.receipts[1]).toMatchObject({ deliveryId: "del-2", status: "duplicate" });
    await client.stop();
  });

  it("handler rejection → rejected receipt carrying the reason", async () => {
    const transport = new MockTransport();
    const client = clientWith(transport, {
      async onCommand() {
        return { kind: "rejected", reason: "budget exceeded" };
      },
    });
    await client.start();
    transport.pushDeliver(delivery("del-r", assignEnvelope("cmd-r")));
    await flush(8);
    expect(transport.receipts[0]).toMatchObject({ status: "rejected", reason: "budget exceeded" });
    expect(validateReceipt(transport.receipts[0])).toEqual({ ok: true });
    await client.stop();
  });

  it("receipt echoes the command's executionGeneration when it carried one (Plan 35 fence)", async () => {
    const transport = new MockTransport();
    const client = clientWith(transport, {
      async onCommand() {
        return { kind: "accepted" };
      },
    });
    await client.start();
    const env = assignEnvelope("cmd-gen");
    (env.data as Record<string, unknown>)["executionGeneration"] = 3;
    transport.pushDeliver(delivery("del-gen", env));
    await flush(8);
    expect(transport.receipts[0]).toMatchObject({ status: "accepted", executionGeneration: 3 });
    expect(validateReceipt(transport.receipts[0])).toEqual({ ok: true });
    await client.stop();
  });

  it("receipt omits executionGeneration when the command data lacks it", async () => {
    const transport = new MockTransport();
    const client = clientWith(transport, {
      async onCommand() {
        return { kind: "accepted" };
      },
    });
    await client.start();
    // A cancel carries no epoch on the wire: its receipt must not invent one.
    transport.pushDeliver({
      deliveryId: "del-nogen",
      deliveredAt: "2026-10-22T18:00:01Z",
      envelope: {
        specversion: "1.0",
        id: "env-nogen",
        source: "urn:facilitator:instance:main",
        type: "org.facilitator.command.work.cancel.v1",
        subject: "work/work-123/assignment/cmd-nogen",
        time: "2026-10-22T18:00:00Z",
        datacontenttype: "application/json",
        dataschema: "https://schemas.openfacilitator.org/v1/command.work.cancel.v1.json",
        data: {
          commandId: "cmd-nogen",
          workId: "work-123",
          assignmentId: "cmd-nogen",
          targetId: "gw-1",
          actorEvidenceRef: "identity-evidence:ref-123",
          deadline: "2026-10-23T18:00:00Z",
          reason: "user cancelled",
        },
      },
    });
    await flush(8);
    expect(transport.receipts[0].status).toBe("accepted");
    expect("executionGeneration" in transport.receipts[0]).toBe(false);
    expect(validateReceipt(transport.receipts[0])).toEqual({ ok: true });
    await client.stop();
  });

  it("unknown command type fails closed with unsupported", async () => {
    const transport = new MockTransport();
    const client = clientWith(transport, {
      async onCommand() {
        return { kind: "accepted" };
      },
    });
    await client.start();
    const env = assignEnvelope("cmd-u");
    // A command-class envelope whose type is outside the frozen table.
    env.type = "org.facilitator.command.quantum.entangle.v1";
    transport.pushDeliver(delivery("del-u", env));
    await flush(8);
    expect(transport.receipts[0].status).toBe("unsupported");
    expect(client.stats.unsupported).toBe(1);
    await client.stop();
  });

  it("invalid envelope → rejected receipt, no ledger row", async () => {
    const transport = new MockTransport();
    const ledger = new MemoryLedger();
    const client = createLinkClient({
      transport,
      ledger,
      endpointId: "gw-1",
      capabilityManifestDigest: DIGEST,
      token: { getToken: async () => "t" },
      logger: createMemoryLogger(),
    });
    await client.start();
    const broken = assignEnvelope("cmd-b") as Record<string, unknown>;
    delete broken.dataschema;
    transport.pushDeliver({ deliveryId: "del-b", envelope: broken as never, deliveredAt: "x" });
    await flush(8);
    expect(transport.receipts[0].status).toBe("rejected");
    expect(ledger.stats()).toEqual({ received: 0, processed: 0, failed: 0, unsupported: 0 });
    await client.stop();
  });

  it("event-class delivery auto-acks accepted without a handler", async () => {
    const transport = new MockTransport();
    const client = clientWith(transport);
    await client.start();
    transport.pushDeliver({
      deliveryId: "del-e",
      envelope: {
        specversion: "1.0",
        id: "evt-1",
        source: "urn:facilitator:instance:main",
        type: PROGRESS,
        subject: "work/work-123/assignment/a-1",
        time: "2026-10-22T18:00:00Z",
        datacontenttype: "application/json",
        dataschema: "https://schemas.openfacilitator.org/v1/event.work.progress.v1.json",
        data: {
          workId: "work-123",
          assignmentId: "a-1",
          executionGeneration: 1,
          progressNote: "hi",
        },
      },
      deliveredAt: "2026-10-22T18:00:01Z",
    });
    await flush(8);
    expect(transport.receipts[0]).toMatchObject({ deliveryId: "del-e", status: "accepted" });
    await client.stop();
  });
});

describe("flow control", () => {
  it("FlowControl 0 pauses dispatch; non-zero resumes within the window", async () => {
    const transport = new MockTransport();
    const gate = Promise.withResolvers<void>();
    const seen: string[] = [];
    const client = clientWith(transport, {
      async onCommand(_cmd, ctx) {
        seen.push(ctx.deliveryId);
        await gate.promise;
        return { kind: "accepted" };
      },
    });
    await client.start();
    transport.pushFlow({ kind: "flow_control", sessionId: "sess-1", maxInFlight: 0 });
    // 17 deliveries against a window of 16: the 17th queues behind the
    // window even after resume (client self-enforces the cap).
    for (let i = 1; i <= 17; i += 1) {
      transport.pushDeliver(delivery(`del-${i}`, assignEnvelope(`cmd-${i}`)));
    }
    await flush(8);
    // The pause hint holds ALL dispatch (advisory, but self-enforced).
    expect(seen).toHaveLength(0);
    transport.pushFlow({ kind: "flow_control", sessionId: "sess-1", maxInFlight: 16 });
    await flush(8);
    expect(seen).toHaveLength(16); // 17th waits for a free slot
    gate.resolve();
    await flush(16);
    expect(seen).toHaveLength(17);
    expect(client.stats.processed).toBe(17);
    await client.stop();
  });
});

describe("heartbeat + supervisor", () => {
  it("heartbeats at welcome.heartbeatSeconds cadence", async () => {
    vi.useFakeTimers();
    try {
      const transport = new MockTransport();
      transport.welcome = { ...transport.welcome!, heartbeatSeconds: 5 };
      const heartbeatCalls: number[] = [];
      transport.heartbeat = async () => {
        heartbeatCalls.push(1);
        return true;
      };
      const client = clientWith(transport);
      const started = client.start();
      await vi.waitFor(() => expect(transport.hellos).toHaveLength(1));
      await started;
      await vi.advanceTimersByTimeAsync(12_500);
      expect(heartbeatCalls).toHaveLength(2); // t=5s, t=10s (not 15s)
      await client.stop();
    } finally {
      vi.useRealTimers();
    }
  });

  it("heartbeat false drops to the reconnect loop and re-hellos with a new epoch", async () => {
    const transport = new MockTransport();
    transport.welcome = { ...transport.welcome!, heartbeatSeconds: 5 };
    let hellos = 0;
    transport.hello = async (frame) => {
      hellos += 1;
      transport.hellos.push(frame);
      return { ...transport.welcome!, leaseEpoch: hellos };
    };
    const client = clientWith(transport);
    // Fake timers FIRST: the heartbeat interval is created inside start().
    vi.useFakeTimers();
    const started = client.start();
    await vi.waitFor(() => expect(transport.hellos).toHaveLength(1));
    await started;
    transport.heartbeatAnswer = false;
    try {
      await vi.advanceTimersByTimeAsync(5_100); // heartbeat death
      transport.heartbeatAnswer = true;
      await vi.advanceTimersByTimeAsync(50); // backoff rung (1ms) + re-hello
    } finally {
      vi.useRealTimers();
    }
    await flush(8);
    expect(hellos).toBe(2);
    expect(client.session?.leaseEpoch).toBe(2);
    expect(client.stats.reconnects).toBe(1);
    await client.stop();
  });

  it("session-superseded goodbye triggers reconnect with a fresh instanceId", async () => {
    const transport = new MockTransport();
    const client = clientWith(transport);
    await client.start();
    const firstInstance = transport.hellos[0].instanceId;
    transport.pushDisconnect({
      kind: "goodbye",
      sessionId: "sess-1",
      reason: "session-superseded",
    });
    vi.useFakeTimers();
    try {
      await vi.advanceTimersByTimeAsync(50); // 1ms backoff rung + microtasks
    } finally {
      vi.useRealTimers();
    }
    await flush(8);
    expect(transport.hellos).toHaveLength(2);
    expect(transport.hellos[1].instanceId).not.toBe(firstInstance);
    await client.stop();
  });
});
