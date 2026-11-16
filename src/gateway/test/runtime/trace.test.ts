// The trace leg of the Gateway: with OTEL_EXPORTER_OTLP_ENDPOINT set, a
// delivery carrying a traceparent must produce an audit line stamped with
// THAT trace id (the link.deliver span was parented on the carrier) — the
// assertion needs no span exporter, and the kernel-side join of Step 2
// continues it. Port 1 black-holes the export; the never-throw law says the
// delivery must still complete.
import { mkdtempSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";
import type { EnvelopeCommand, LinkDeliveryV1, ReceiptV1 } from "@ofac/protocol";
import { createMemoryLogger } from "@ofac/workspace-link";
import type { LinkTransport } from "@ofac/workspace-link";
import type { EnvelopeAck, HelloFrame, WelcomeFrame } from "@ofac/workspace-link";
import { SqliteLedger } from "@ofac/workspace-link/node";
import { afterEach, describe, expect, it } from "vitest";
import { fakeKernel as makeFakeKernel } from "../fake-kernel.js";
import { parseConfig, type GatewayConfig } from "../../src/config.js";
import { ExecutionLedger, type GatewayPaths } from "../../src/ledger.js";
import { createGatewayRuntime, type GatewayRuntime } from "../../src/runtime.js";

const TRACE_ID = "0f1e2d3c4b5a69788796a5b4c3d2e1f0";
const CARRIER = `00-${TRACE_ID}-4d0c0b0a09080706-01`;

class MockTransport implements LinkTransport {
  receipts: ReceiptV1[] = [];
  envelopes: unknown[] = [];
  // The awaited signal, not a poll: first receipt resolves this.
  readonly firstReceipt = Promise.withResolvers<ReceiptV1>();
  private readonly deliverCbs: ((d: LinkDeliveryV1) => void)[] = [];
  async connect(): Promise<void> {}
  async hello(frame: HelloFrame): Promise<WelcomeFrame | null> {
    void frame;
    return {
      kind: "welcome",
      sessionId: "sess-tp",
      leaseEpoch: 1,
      maxInFlight: 8,
      heartbeatSeconds: 300,
      protocolVersion: "v1",
    };
  }
  async envelope(env: unknown): Promise<EnvelopeAck> {
    this.envelopes.push(env);
    return { status: "accepted", deliveryId: "inbox-tp" };
  }
  async receipt(r: ReceiptV1): Promise<void> {
    this.receipts.push(r);
    this.firstReceipt.resolve(r);
  }
  async heartbeat(): Promise<boolean> {
    return true;
  }
  async goodbye(): Promise<void> {}
  async close(): Promise<void> {}
  onDeliver(cb: (d: LinkDeliveryV1) => void): void {
    this.deliverCbs.push(cb);
  }
  onFlow(): void {}
  onDisconnect(): void {}
  pushDeliver(d: LinkDeliveryV1): void {
    for (const cb of this.deliverCbs) cb(d);
  }
}

function config(): GatewayConfig {
  return parseConfig(
    `
apiVersion: facilitator.framework/v1
kind: Gateway
gatewayId: gw.trace-test
team: default
facilitatorUrl: http://kernel.test
enrollmentSecret: enroll-dev
trustMode: shared-runtime
stateDir: ${mkdtempSync(join(tmpdir(), "gw-trace-"))}
child:
  name: echo
  mode: echo
heartbeatSeconds: 3600
`,
    { env: {} },
  );
}

function assignDeliveryWithCarrier(commandId: string): LinkDeliveryV1 {
  const envelope = {
    specversion: "1.0",
    id: `env-${commandId}`,
    source: "urn:facilitator:instance:unit",
    type: "org.facilitator.command.work.assign.v1",
    subject: `work/work-${commandId}/assignment/asg-${commandId}`,
    time: new Date().toISOString(),
    datacontenttype: "application/json",
    dataschema: "https://schemas.openfacilitator.org/v1/command.work.assign.v1.json",
    traceparent: CARRIER,
    data: {
      commandId,
      actorEvidenceRef: "identity-evidence:unit",
      workId: `work-${commandId}`,
      targetId: "unit",
      capabilityId: "research.analyze",
      expectedContextVersion: 1,
      inputs: { objective: "unit", sourceRefs: [] },
      expectedDeliverables: [{ kind: "analysis.report", mediaType: "text/markdown" }],
      deadline: "2036-10-23T18:00:00Z",
      executionGeneration: 1,
      assignmentId: `asg-${commandId}`,
    },
  } as EnvelopeCommand;
  return { deliveryId: `del-${commandId}`, deliveredAt: new Date().toISOString(), envelope };
}

let rt: GatewayRuntime | null = null;

afterEach(async () => {
  const current = rt;
  rt = null;
  delete process.env["OTEL_EXPORTER_OTLP_ENDPOINT"];
  delete process.env["OTEL_SDK_DISABLED"];
  delete process.env["OTEL_LOGS_EXPORTER"];
  if (current !== null) {
    try {
      await current.stop();
    } catch {
      /* ledgers may already be closed */
    }
  }
});

describe("Gateway trace leg (endpoint set)", () => {
  it(
    "a delivery with a traceparent stamps the policy audit line with that trace id",
    { timeout: 60_000 },
    async () => {
      process.env["OTEL_EXPORTER_OTLP_ENDPOINT"] = "http://127.0.0.1:1";
      const cfg = config();
      const mock = new MockTransport();
      const logger = createMemoryLogger();
      const paths: GatewayPaths = {
        link: new SqliteLedger(join(cfg.stateDir, "link.sqlite3")),
        execution: new ExecutionLedger(join(cfg.stateDir, "gateway.sqlite3")),
      };
      rt = createGatewayRuntime(cfg, {
        transportFactory: () => mock,
        // The shared link plane: enroll pins the key, proofs buy sessions;
        // everything else the gateway touches answers 204 here.
        fetchImpl: makeFakeKernel({
          fallback: () => new Response(null, { status: 204 }),
        }).fetchImpl,
        ledgerFactory: () => paths,
        logger,
      });
      await rt.telemetryReady();
      expect(rt.gatewayStatus().telemetry.emitting).toBe(true);
      await rt.start();

      mock.pushDeliver(assignDeliveryWithCarrier("cmd-tp"));
      // Await the receipt the pipeline ships (real event, no polling).
      await mock.firstReceipt.promise;
      expect(mock.receipts).toHaveLength(1);

      const auditLines = logger.lines.filter((l) => l.includes("authorization.evaluated"));
      expect(auditLines.length).toBeGreaterThanOrEqual(1);
      const fields = JSON.parse(auditLines[0]) as { traceId?: string; decision?: string };
      // The join proof: the audit line names the DELIVERED carrier's trace.
      expect(fields["traceId"]).toBe(TRACE_ID);
      expect(["allow", "deny", "confirm"]).toContain(fields["decision"]);
      await rt.stop();
      rt = null;
    },
  );
});
