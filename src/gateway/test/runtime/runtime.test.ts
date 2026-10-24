// Runtime unit tier: mock LinkTransport (copies the SDK's mock pattern),
// real temp-file ledgers, injected fetch for enroll/capabilities. No network.
import { mkdtempSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { validateEnvelope, validateReceipt } from "@ofac/protocol";
import type {
  CapabilityManifestV1,
  EnvelopeCommand,
  EnvelopeEvent,
  EnvelopeObject,
  LinkDeliveryV1,
  ReceiptV1,
} from "@ofac/protocol";
import { createMemoryLogger } from "@ofac/workspace-link";
import type { LinkHandler, LinkTransport } from "@ofac/workspace-link";
import type { EnvelopeAck, GoodbyeFrame, HelloFrame, WelcomeFrame } from "@ofac/workspace-link";
import { SqliteLedger, digestOfManifest } from "@ofac/workspace-link/node";
import { afterEach, describe, expect, it } from "vitest";
import { parseConfig, type GatewayConfig } from "../../src/config.js";
import { ExecutionLedger, type BindingRow, type GatewayPaths } from "../../src/ledger.js";
import { createGatewayRuntime, type GatewayRuntime } from "../../src/runtime.js";

class MockTransport implements LinkTransport {
  connectCount = 0;
  hellos: HelloFrame[] = [];
  receipts: ReceiptV1[] = [];
  envelopes: EnvelopeObject[] = [];
  goodbyes: GoodbyeFrame[] = [];
  closed = 0;
  welcome: WelcomeFrame = {
    kind: "welcome",
    sessionId: "sess-1",
    leaseEpoch: 1,
    maxInFlight: 8,
    heartbeatSeconds: 300,
    protocolVersion: "v1",
  };
  private readonly deliverCbs: ((d: LinkDeliveryV1) => void)[] = [];
  private readonly disconnectCbs: ((g: GoodbyeFrame) => void)[] = [];

  async connect(): Promise<void> {
    this.connectCount += 1;
  }
  async hello(frame: HelloFrame): Promise<WelcomeFrame | null> {
    this.hellos.push(frame);
    return this.welcome;
  }
  async envelope(env: EnvelopeObject): Promise<EnvelopeAck> {
    this.envelopes.push(env);
    return { status: "accepted", deliveryId: "inbox-1" };
  }
  async receipt(r: ReceiptV1): Promise<void> {
    this.receipts.push(r);
  }
  async heartbeat(): Promise<boolean> {
    return true;
  }
  async goodbye(g: GoodbyeFrame): Promise<void> {
    this.goodbyes.push(g);
  }
  async close(): Promise<void> {
    this.closed += 1;
  }
  onDeliver(cb: (d: LinkDeliveryV1) => void): void {
    this.deliverCbs.push(cb);
  }
  onFlow(): void {
    /* flow control is the SDK's concern; the mock never throttles */
  }
  onDisconnect(cb: (g: GoodbyeFrame) => void): void {
    this.disconnectCbs.push(cb);
  }
  pushDeliver(d: LinkDeliveryV1): void {
    for (const cb of this.deliverCbs) cb(d);
  }
  pushDisconnect(g: GoodbyeFrame): void {
    for (const cb of this.disconnectCbs) cb(g);
  }
}

async function waitFor<T>(
  what: string,
  probe: () => T | false | null | Promise<T | false | null>,
): Promise<T> {
  const deadline = Date.now() + 5_000;
  for (;;) {
    const value = await probe();
    if (value) return value;
    if (Date.now() > deadline) throw new Error(`timeout waiting for ${what}`);
    await new Promise((r) => setTimeout(r, 20));
  }
}

interface FakeKernel {
  calls: string[];
  fetchImpl: typeof fetch;
  capabilitiesBody: () => string;
}

function fakeKernel(): FakeKernel {
  const calls: string[] = [];
  let capabilitiesBody = "";
  const fetchImpl = (async (url: unknown, init?: RequestInit) => {
    const path = String(url);
    if (path.endsWith("/api/link/v1/enroll")) {
      calls.push("enroll");
      return new Response(
        JSON.stringify({
          enrollmentId: "enr-1",
          trustMode: "shared-runtime",
          linkToken: "tok-1",
        }),
        { status: 201, headers: { "content-type": "application/json" } },
      );
    }
    if (path.endsWith("/api/link/v1/capabilities")) {
      calls.push("capabilities");
      capabilitiesBody = String(init?.body ?? "");
      return new Response(null, { status: 204 });
    }
    return new Response("no route", { status: 500 });
  }) as typeof fetch;
  return { calls, fetchImpl, capabilitiesBody: () => capabilitiesBody };
}

function config(): GatewayConfig {
  return parseConfig(
    `
apiVersion: facilitator.framework/v1
kind: Gateway
gatewayId: gw.unit-test
team: default
facilitatorUrl: http://kernel.test
enrollmentSecret: enroll-dev
trustMode: shared-runtime
stateDir: ${mkdtempSync(join(tmpdir(), "gw-runtime-"))}
child:
  name: echo
  mode: echo
heartbeatSeconds: 3600
`,
    { env: {} },
  );
}

function testPaths(stateDir: string): GatewayPaths {
  return {
    link: new SqliteLedger(join(stateDir, "link.sqlite3")),
    execution: new ExecutionLedger(join(stateDir, "gateway.sqlite3")),
  };
}

function assignDelivery(overrides: {
  commandId: string;
  capabilityId?: string;
  deliveryId?: string;
}): LinkDeliveryV1 {
  return {
    deliveryId: overrides.deliveryId ?? `del-${overrides.commandId}`,
    deliveredAt: new Date().toISOString(),
    envelope: {
      specversion: "1.0",
      id: `env-${overrides.commandId}`,
      source: "urn:facilitator:instance:unit",
      type: "org.facilitator.command.work.assign.v1",
      subject: `work/work-${overrides.commandId}/assignment/asg-${overrides.commandId}`,
      time: new Date().toISOString(),
      datacontenttype: "application/json",
      dataschema: "https://schemas.openfacilitator.org/v1/command.work.assign.v1.json",
      data: {
        commandId: overrides.commandId,
        actorEvidenceRef: "identity-evidence:unit",
        workId: `work-${overrides.commandId}`,
        targetId: "unit",
        capabilityId: overrides.capabilityId ?? "research.analyze",
        expectedContextVersion: 1,
        inputs: { objective: "unit", sourceRefs: [] },
        expectedDeliverables: [{ kind: "analysis.report", mediaType: "text/markdown" }],
        deadline: "2036-10-23T18:00:00Z",
        assignmentId: `asg-${overrides.commandId}`,
      },
    },
  };
}

interface Booted {
  rt: GatewayRuntime;
  mock: MockTransport;
  server: FakeKernel;
  lines: string[];
  paths: GatewayPaths;
}

let live: Booted | null = null;

async function booted(
  options: {
    paths?: GatewayPaths;
    handlerFactory?: () => LinkHandler;
  } = {},
): Promise<Booted> {
  const cfg = config();
  const mock = new MockTransport();
  const server = fakeKernel();
  const logger = createMemoryLogger();
  const paths = options.paths ?? testPaths(cfg.stateDir);
  const rt = createGatewayRuntime(cfg, {
    transportFactory: () => mock,
    fetchImpl: server.fetchImpl,
    ledgerFactory: () => paths,
    logger,
    ...(options.handlerFactory !== undefined ? { handlerFactory: options.handlerFactory } : {}),
  });
  live = { rt, mock, server, lines: logger.lines, paths };
  await rt.start();
  return live;
}

afterEach(async () => {
  const current = live;
  live = null;
  if (current !== null) {
    try {
      await current.rt.stop();
    } catch {
      /* ledgers may already be closed by the test */
    }
  }
});

describe("GatewayRuntime startup", () => {
  it("enrolls, POSTs capabilities, hello carries the POSTed-bytes digest, ready after reconcile", async () => {
    const { rt, mock, server, paths } = await booted();
    expect(server.calls).toEqual(["enroll", "capabilities"]);

    // Startup-order law: hello digest == digest over the EXACT POSTed bytes.
    const hello = mock.hellos[0];
    expect(hello?.capabilityManifestDigest).toBe(digestOfManifest(server.capabilitiesBody()));
    expect(hello?.endpointId).toBe("gw.unit-test");

    // The POSTed manifest: the child's capabilities, the team owns it.
    const posted = JSON.parse(server.capabilitiesBody()) as CapabilityManifestV1;
    expect(posted.ownerTeam).toBe("default");
    expect(posted.capabilities.map((c) => c.id)).toEqual(["research.analyze"]);
    expect(rt.aggregatedManifest).toEqual(posted);

    expect(rt.state).toBe("ready");
    expect(rt.reconciled).toBe(true);
    expect(rt.gatewayStatus().session).toMatchObject({
      leaseEpoch: 1,
      sessionId: "sess-1",
      state: "ready",
    });
    expect(paths.execution.all()).toHaveLength(0);
  });
});

describe("work.assign routing", () => {
  it("policy allow: binding row (tuple, epoch) + child start + accepted receipt + child events forwarded verbatim", async () => {
    const { rt, mock } = await booted();
    mock.pushDeliver(assignDelivery({ commandId: "cmd-a" }));
    const receipt = await waitFor("receipt", () => mock.receipts[0] ?? false);
    expect(receipt.status).toBe("accepted");
    expect(receipt.deliveryId).toBe("del-cmd-a");
    expect(validateReceipt(receipt).ok).toBe(true);

    const rows = rt.executionLedger.all();
    expect(rows).toHaveLength(1);
    const row = rows[0] as BindingRow;
    expect(row).toMatchObject({
      deliveryId: "del-cmd-a",
      dedupeKey: "cmd-a",
      gatewayId: "gw.unit-test",
      childId: "gw.unit-test/echo",
      assignmentId: "asg-cmd-a",
      workId: "work-cmd-a",
      connectionGeneration: 1,
    });
    expect(row.executionId).toMatch(/^exec-[0-9a-f]{16}$/);

    // The echo child completes: its bytes reach the kernel, the row settles.
    const completed = await waitFor("completed event forwarded", () => {
      const found = mock.envelopes.find(
        (e) => e.type === "org.facilitator.event.assignment.completed.v1",
      );
      return found ?? false;
    });
    expect(completed.source).toBe("urn:workspace:gw.unit-test/echo"); // child's own source, unmodified
    expect(completed.data).toMatchObject({
      executionId: row.executionId,
      executionGeneration: 1,
    });
    await waitFor("row settled", () => {
      const r = rt.executionLedger.byExecution(row.executionId);
      return r !== null && r.state === "completed" ? r : false;
    });
  });

  it("policy deny: rejected receipt `policy-denied: <cap>`, NO bindings row", async () => {
    const { rt, mock } = await booted();
    mock.pushDeliver(assignDelivery({ commandId: "cmd-d", capabilityId: "not.advertised" }));
    const receipt = await waitFor("reject receipt", () => mock.receipts[0] ?? false);
    expect(receipt.status).toBe("rejected");
    expect(receipt.reason).toBe("policy-denied: not.advertised");
    expect(rt.executionLedger.all()).toHaveLength(0);
  });

  it("redelivery law: same deliveryId ⇒ duplicate; new deliveryId, same commandId ⇒ duplicate, one row", async () => {
    const { rt, mock } = await booted();
    mock.pushDeliver(assignDelivery({ commandId: "cmd-r" }));
    await waitFor("first receipt", () => mock.receipts[0] ?? false);

    mock.pushDeliver(assignDelivery({ commandId: "cmd-r" })); // same deliveryId
    const dup = await waitFor("duplicate receipt", () => mock.receipts[1] ?? false);
    expect(dup.status).toBe("duplicate");

    mock.pushDeliver(assignDelivery({ commandId: "cmd-r", deliveryId: "del-redelivered" }));
    const re = await waitFor("re-delivery receipt", () => mock.receipts[2] ?? false);
    // SDK law (dedupeKeyOf == commandId for known command types): the
    // redelivered command answers `duplicate` WITHOUT reaching the handler.
    expect(re.status).toBe("duplicate");
    expect(rt.executionLedger.all()).toHaveLength(1);
  });
});

describe("reconcile-on-restart (§6.3)", () => {
  it("running row + fresh child `unknown` ⇒ row lost, ONE retryable assignment.failed from the GATEWAY, before ready", async () => {
    const cfg = config();
    const paths = testPaths(cfg.stateDir);
    paths.execution.insert({
      deliveryId: "del-seed",
      dedupeKey: "cmd-seed",
      gatewayId: cfg.gatewayId,
      childId: `${cfg.gatewayId}/echo`,
      assignmentId: "asg-seed",
      executionId: "exec-seed-1",
      workId: "work-seed",
      connectionGeneration: 1,
    });
    const mock = new MockTransport();
    const logger = createMemoryLogger();
    const rt = createGatewayRuntime(cfg, {
      transportFactory: () => mock,
      fetchImpl: fakeKernel().fetchImpl,
      ledgerFactory: () => paths,
      logger,
    });
    live = { rt, mock, server: fakeKernel(), lines: logger.lines, paths };
    await rt.start();

    expect(paths.execution.byExecution("exec-seed-1")?.state).toBe("lost");
    const failed = mock.envelopes.filter(
      (e) => e.type === "org.facilitator.event.assignment.failed.v1",
    );
    expect(failed).toHaveLength(1);
    const env = failed[0];
    expect(env?.data).toMatchObject({
      workId: "work-seed",
      assignmentId: "asg-seed",
      executionId: "exec-seed-1",
      executionGeneration: 1, // the epoch the execution ran under
      reason: "gateway-restart-lost",
      retryable: true,
    });
    expect(env?.source).toBe("urn:workspace:gw.unit-test"); // Gateway is the reporting authority
    expect(env?.subject).toBe("work/work-seed/assignment/asg-seed");
    expect(validateEnvelope(env as EnvelopeEvent).ok).toBe(true);
    expect(logger.lines.some((l) => l.includes("reconcile: execution lost"))).toBe(true);
    // start() resolves only after the reconcile pass ran:
    expect(rt.reconciled).toBe(true);
  });
});

describe("connectionGeneration fencing (S:329)", () => {
  it("event against an old generation: stale-generation log, row unchanged, nothing forwarded", async () => {
    const captured: ((env: EnvelopeEvent) => Promise<EnvelopeAck>)[] = [];
    const { rt, mock, lines } = await booted({
      handlerFactory: () => ({
        onCommand: async (_cmd: EnvelopeCommand, ctx) => {
          captured.push(ctx.publishEvent);
          return { kind: "accepted" }; // silent accept ⇒ row stays running
        },
      }),
    });
    mock.pushDeliver(assignDelivery({ commandId: "cmd-f" }));
    await waitFor("receipt", () => mock.receipts[0] ?? false);
    const row = rt.executionLedger.all()[0] as BindingRow;
    expect(row.state).toBe("running");

    // Reconnect at epoch 2: the recorded generation goes stale.
    mock.welcome.leaseEpoch = 2;
    mock.pushDisconnect({ kind: "goodbye", sessionId: "sess-1", reason: "superseded" });
    await waitFor("re-hello at epoch 2", () =>
      mock.hellos.length >= 2 && rt.linkClient?.session?.leaseEpoch === 2 ? true : false,
    );

    const before = mock.envelopes.length;
    await captured[0]?.({
      specversion: "1.0",
      id: "evt-stale",
      source: "urn:workspace:gw.unit-test/echo",
      type: "org.facilitator.event.assignment.completed.v1",
      subject: "work/work-cmd-f/assignment/asg-cmd-f",
      time: new Date().toISOString(),
      datacontenttype: "application/json",
      dataschema: "https://schemas.openfacilitator.org/v1/event.assignment.completed.v1.json",
      data: {
        workId: "work-cmd-f",
        assignmentId: "asg-cmd-f",
        executionId: row.executionId,
        executionGeneration: 1,
        resultManifest: { manifestId: "man-1", outputs: [] },
      },
    });
    expect(mock.envelopes.length).toBe(before); // nothing forwarded
    expect(rt.executionLedger.byExecution(row.executionId)?.state).toBe("running"); // row untouched
    expect(lines.some((l) => l.includes("stale-generation") && l.includes(row.executionId))).toBe(
      true,
    );
  });
});

describe("SIGTERM drain path", () => {
  it("stop(): draining heartbeat, goodbye, closed", async () => {
    const { rt, mock } = await booted();
    await rt.stop();
    expect(rt.state).toBe("closed");
    expect(mock.goodbyes).toHaveLength(1);
    expect(mock.goodbyes[0]?.reason).toBe("gateway-shutdown");
    const heartbeat = mock.envelopes.find(
      (e) => e.type === "org.facilitator.event.workspace.heartbeat.v1",
    );
    expect(heartbeat?.data).toMatchObject({
      workspaceId: "gw.unit-test",
      status: "draining",
      activeAssignments: 0,
    });
  });
});
