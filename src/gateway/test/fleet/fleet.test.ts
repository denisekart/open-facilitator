// Fleet-mode unit tier: a SHARED-mode runtime (mock kernel link + real unix
// socket listener + two `connectChild` children) exercises the routing laws
// the 05 embedded suite cannot reach: per-child routing, quota, offline
// honesty, union re-POST, and the re-register reconcile probe.
import { mkdtempSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";
import type {
  CapabilityDescriptor,
  CapabilityManifestV1,
  EnvelopeCommand,
  LinkDeliveryV1,
  ReceiptV1,
} from "@ofac/protocol";
import { createMemoryLogger } from "@ofac/workspace-link";
import type { LinkTransport } from "@ofac/workspace-link";
import type { EnvelopeAck, HelloFrame, WelcomeFrame } from "@ofac/workspace-link";
import { SqliteLedger, digestOfManifest } from "@ofac/workspace-link/node";
import { afterEach, describe, expect, it } from "vitest";
import { parseConfig, type GatewayConfig } from "../../src/config.js";
import { builtinChildManifest } from "../../src/child.js";
import { digestOf } from "../../src/child-link.js";
import { ExecutionLedger, type GatewayPaths } from "../../src/ledger.js";
import { createGatewayRuntime, type GatewayRuntime } from "../../src/runtime.js";
import { connectChild, type ChildConnection } from "../../src/connect.js";

class MockTransport implements LinkTransport {
  hellos: HelloFrame[] = [];
  receipts: ReceiptV1[] = [];
  envelopes: { type: string; data?: Record<string, unknown> }[] = [];
  welcome: WelcomeFrame = {
    kind: "welcome",
    sessionId: "sess-1",
    leaseEpoch: 1,
    maxInFlight: 8,
    heartbeatSeconds: 300,
    protocolVersion: "v1",
  };
  private deliverCbs: ((d: LinkDeliveryV1) => void)[] = [];
  async connect(): Promise<void> {}
  async hello(frame: HelloFrame): Promise<WelcomeFrame | null> {
    this.hellos.push(frame);
    return this.welcome;
  }
  async envelope(env: { type: string; data?: Record<string, unknown> }): Promise<EnvelopeAck> {
    this.envelopes.push(env);
    return { status: "accepted", deliveryId: "inbox-1" };
  }
  async receipt(r: ReceiptV1): Promise<void> {
    this.receipts.push(r);
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

function fakeKernel(): {
  calls: string[];
  bodies: string[];
  fetchImpl: typeof fetch;
} {
  const calls: string[] = [];
  const bodies: string[] = [];
  const fetchImpl = (async (url: unknown, init?: RequestInit) => {
    const path = String(url);
    if (path.endsWith("/api/link/v1/enroll")) {
      calls.push("enroll");
      return new Response(
        JSON.stringify({ enrollmentId: "enr-1", trustMode: "shared-runtime", linkToken: "tok-1" }),
        { status: 201, headers: { "content-type": "application/json" } },
      );
    }
    if (path.endsWith("/api/link/v1/capabilities")) {
      calls.push("capabilities");
      bodies.push(String(init?.body ?? ""));
      return new Response(null, { status: 204 });
    }
    return new Response("no route", { status: 500 });
  }) as typeof fetch;
  return { calls, bodies, fetchImpl };
}

function manifestAdvert(capIds: string[], childName: string): CapabilityManifestV1 {
  const base = builtinChildManifest("echo");
  return {
    ...base,
    ownerTeam: `child-${childName}`,
    capabilities: capIds.map((id) => ({
      ...base.capabilities[0],
      id,
      outputKinds: [`${id.replaceAll(".", "-")}.result`],
    })) as [CapabilityDescriptor, ...CapabilityDescriptor[]],
  };
}

function assign(commandId: string, capabilityId: string, deliveryId?: string): LinkDeliveryV1 {
  return {
    deliveryId: deliveryId ?? `del-${commandId}`,
    deliveredAt: new Date().toISOString(),
    envelope: {
      specversion: "1.0",
      id: `env-${commandId}`,
      source: "urn:facilitator:instance:unit",
      type: "org.facilitator.command.work.assign.v1",
      subject: `work/work-${commandId}/assignment/asg-${commandId}`,
      time: new Date().toISOString(),
      datacontenttype: "application/json",
      dataschema: "https://schemas.openfacilitator.org/v1/command.work.assign.v1.json",
      data: {
        commandId,
        actorEvidenceRef: "identity-evidence:unit",
        workId: `work-${commandId}`,
        targetId: "unit",
        capabilityId,
        expectedContextVersion: 1,
        executionGeneration: 1,
        inputs: { objective: "unit", sourceRefs: [] },
        expectedDeliverables: [{ kind: "analysis.report", mediaType: "text/markdown" }],
        deadline: "2036-10-23T18:00:00Z",
        assignmentId: `asg-${commandId}`,
      },
    },
  };
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

interface Fleet {
  cfg: GatewayConfig;
  rt: GatewayRuntime;
  mock: MockTransport;
  kernel: ReturnType<typeof fakeKernel>;
  lines: string[];
  paths: GatewayPaths;
  socketPath: string;
  attach(
    name: string,
    manifest: CapabilityManifestV1,
    handler?: (cmd: EnvelopeCommand, executionId: string) => void,
  ): Promise<{ conn: ChildConnection; answered: string[] }>;
}

let live: Fleet | null = null;

async function fleetBoot(
  yamlChildren: string,
  transportBlock: string,
  deps: { childOnlineGraceMs?: number; seedOfflineChild?: CapabilityManifestV1 } = {},
): Promise<Fleet> {
  const stateDir = mkdtempSync(join(tmpdir(), "gw-fleet-"));
  const cfg = parseConfig(
    `
apiVersion: facilitator.framework/v1
kind: Gateway
gatewayId: gw.fleet-test
team: default
facilitatorUrl: http://kernel.test
enrollmentSecret: enroll-dev
trustMode: shared-runtime
stateDir: ${stateDir}
mode: shared
children:
${yamlChildren}
childTransport:
${transportBlock}
heartbeatSeconds: 3600
`,
    { env: {} },
  );
  const mock = new MockTransport();
  const kernel = fakeKernel();
  const logger = createMemoryLogger();
  const paths: GatewayPaths = {
    link: new SqliteLedger(join(stateDir, "link.sqlite3")),
    execution: new ExecutionLedger(join(stateDir, "gateway.sqlite3")),
  };
  if (deps.seedOfflineChild !== undefined) {
    // A fleet shadow from a previous life (restart): the runtime seeds it
    // offline, so an assign during the boot window routes to a known child.
    paths.execution.upsertChild(
      `${cfg.gatewayId}/alpha`,
      digestOf(deps.seedOfflineChild),
      JSON.stringify(deps.seedOfflineChild),
      true,
    );
  }
  const rt = createGatewayRuntime(cfg, {
    transportFactory: () => mock,
    fetchImpl: kernel.fetchImpl,
    ledgerFactory: () => paths,
    logger,
    ...deps,
  });
  const fleet: Fleet = {
    cfg,
    rt,
    mock,
    kernel,
    lines: logger.lines,
    paths,
    socketPath: cfg.childTransport?.socket.path ?? join(stateDir, "child.sock"),
    async attach(name, manifest, onSeen) {
      const answered: string[] = [];
      const conn = await connectChild({
        childWorkspaceId: `${cfg.gatewayId}/${name}`,
        token: "tok",
        manifest,
        path: fleet.socketPath,
        heartbeatSeconds: 1,
        onCommand: async (frame) => {
          answered.push(`${frame.message.kind}:${frame.message.executionId}`);
          onSeen?.(
            "command" in frame.message ? frame.message.command : ({} as EnvelopeCommand),
            frame.message.executionId,
          );
          return { kind: "accepted", executionId: frame.message.executionId };
        },
      });
      return { conn, answered };
    },
  };
  live = fleet;
  await rt.start();
  return fleet;
}

afterEach(async () => {
  const current = live;
  live = null;
  if (current !== null) {
    try {
      await current.rt.stop();
    } catch {
      /* ledgers may already be closed */
    }
  }
});

const TWO_CHILDREN = `  - name: alpha
    token: tok
    maxInFlight: 1
    timeoutSeconds: 120
  - name: bravo
    token: tok
    maxInFlight: 1
    timeoutSeconds: 120
`;
const TRANSPORT = `  socket:
    path: child.sock
  handshakeTimeoutSeconds: 5
  leaseMissedBeats: 100
`;

describe("shared-mode fleet routing", () => {
  it("boots with NO capabilities POST: hello carries the placeholder digest", async () => {
    const f = await fleetBoot(TWO_CHILDREN, TRANSPORT);
    expect(f.kernel.calls).toEqual(["enroll"]); // no POST at boot
    const hello = f.mock.hellos[0];
    const placeholder = digestOfManifest(
      JSON.stringify({
        apiVersion: "facilitator.framework/v1",
        kind: "CapabilityManifest",
        ownerTeam: "default",
        capabilities: [],
      }),
    );
    expect(hello?.capabilityManifestDigest).toBe(placeholder);
    const status = f.rt.gatewayStatus();
    expect(status.mode).toBe("shared");
    expect(status.children).toEqual([]);
  });

  it("union POST on child churn carries both capabilities, team-owned; debounced once", async () => {
    const f = await fleetBoot(TWO_CHILDREN, TRANSPORT);
    const a = await f.attach("alpha", manifestAdvert(["research.analyze"], "alpha"));
    const b = await f.attach("bravo", manifestAdvert(["report.render"], "bravo"));
    await waitFor(
      "union POST",
      () => f.kernel.calls.filter((c) => c === "capabilities").length >= 1,
    );
    // Debounce coalescing: churn settled into exactly ONE POST.
    await new Promise((r) => setTimeout(r, 300));
    const posts = f.kernel.bodies.map((body) => JSON.parse(body) as CapabilityManifestV1);
    expect(posts).toHaveLength(1);
    expect(posts[0]?.ownerTeam).toBe("default");
    expect(posts[0]?.capabilities.map((c) => c.id).sort()).toEqual([
      "report.render",
      "research.analyze",
    ]);
    // The follow-up hello digest law: digest over the EXACT POSTed bytes.
    expect(f.rt.gatewayStatus().children.map((c) => c.state)).toEqual(["connected", "connected"]);
    await a.conn.close();
    await b.conn.close();
  });

  it("assign routes to the advertising child's row; other child idle", async () => {
    const f = await fleetBoot(TWO_CHILDREN, TRANSPORT);
    const a = await f.attach("alpha", manifestAdvert(["research.analyze"], "alpha"));
    const b = await f.attach("bravo", manifestAdvert(["report.render"], "bravo"));
    f.mock.pushDeliver(assign("cmd-b", "report.render"));
    const receipt = await waitFor("receipt", () => f.mock.receipts[0] ?? false);
    expect(receipt.status).toBe("accepted");
    const row = f.paths.execution.all()[0];
    expect(row?.childId).toBe("gw.fleet-test/bravo");
    expect(row?.childGeneration).toBe(1);
    await waitFor("bravo answered", () => b.answered.length > 0);
    expect(a.answered).toEqual([]);
    await a.conn.close();
    await b.conn.close();
  });

  it("no advertiser: unsupported no-child-advertises, no row", async () => {
    const f = await fleetBoot(TWO_CHILDREN, TRANSPORT);
    const a = await f.attach("alpha", manifestAdvert(["research.analyze"], "alpha"));
    f.mock.pushDeliver(assign("cmd-x", "never.advertised"));
    const receipt = await waitFor("receipt", () => f.mock.receipts[0] ?? false);
    expect(receipt.status).toBe("unsupported");
    expect(receipt.reason).toBe("no-child-advertises: never.advertised");
    expect(f.paths.execution.all()).toHaveLength(0);
    await a.conn.close();
  });

  it("offline advertiser: rejected child-disconnected after the grace, no row", async () => {
    // A truly-dead child never registers: the bounded hold expires and the
    // honest rejection still answers (the seam keeps the test fast).
    const f = await fleetBoot(TWO_CHILDREN, TRANSPORT, { childOnlineGraceMs: 250 });
    const a = await f.attach("alpha", manifestAdvert(["research.analyze"], "alpha"));
    await a.conn.close();
    await waitFor("fleet marks offline", () => {
      const rows = f.rt.gatewayStatus().children;
      return rows.length > 0 && rows.every((r) => r.state === "offline") ? rows : false;
    });
    f.mock.pushDeliver(assign("cmd-off", "research.analyze"));
    const receipt = await waitFor("receipt", () => f.mock.receipts[0] ?? false);
    expect(receipt.status).toBe("rejected");
    expect(receipt.reason).toBe("child-disconnected");
    expect(f.paths.execution.all()).toHaveLength(0);
  });

  it("boot-race law: assign pushed on session-open EXECUTES once the child dials within the grace", async () => {
    // Plan 24 restart leg: after a restart the kernel pushes the queued
    // re-hydrated assign the instant the link opens, while the worker's
    // socket child is still mid-boot. The listener is bound BEFORE the
    // session and the offline route holds, so the command runs instead of
    // dying as an acked child-disconnected receipt.
    const manifest = manifestAdvert(["research.analyze"], "alpha");
    const f = await fleetBoot(TWO_CHILDREN, TRANSPORT, { seedOfflineChild: manifest });
    f.mock.pushDeliver(assign("cmd-boot", "research.analyze"));
    // The child dials 150 ms into the boot window (after rt.start resolved).
    await new Promise((r) => setTimeout(r, 150));
    const a = await f.attach("alpha", manifest);
    const receipt = await waitFor("accepted receipt", () => f.mock.receipts[0] ?? false);
    expect(receipt.status).toBe("accepted");
    expect(f.paths.execution.all()).toHaveLength(1);
    await a.conn.close();
  });

  it("quota: second assign while first runs answers child-busy", async () => {
    const f = await fleetBoot(TWO_CHILDREN, TRANSPORT);
    const a = await f.attach("alpha", manifestAdvert(["research.analyze"], "alpha"));
    f.mock.pushDeliver(assign("cmd-q1", "research.analyze"));
    await waitFor("first receipt", () => f.mock.receipts[0] ?? false);
    // The child accepted silently (no terminal event): the row stays running.
    f.mock.pushDeliver(assign("cmd-q2", "research.analyze", "del-q2"));
    const second = await waitFor("second receipt", () => f.mock.receipts[1] ?? false);
    expect(second.status).toBe("rejected");
    expect(second.reason).toBe("child-busy: gw.fleet-test/alpha");
    expect(f.paths.execution.all()).toHaveLength(1);
    await a.conn.close();
  });

  it("re-register probe: rows the child forgot go lost + ONE retryable failed", async () => {
    const f = await fleetBoot(TWO_CHILDREN, TRANSPORT);
    // Seed a running row as if child alpha ran it before a Gateway crash.
    f.paths.execution.upsertChild(
      "gw.fleet-test/alpha",
      digestOfManifest(JSON.stringify(manifestAdvert(["research.analyze"], "alpha"))),
      JSON.stringify(manifestAdvert(["research.analyze"], "alpha")),
      false,
    );
    f.paths.execution.insert({
      deliveryId: "del-seed",
      dedupeKey: "cmd-seed",
      gatewayId: "gw.fleet-test",
      childId: "gw.fleet-test/alpha",
      assignmentId: "asg-seed",
      executionId: "exec-seed",
      workId: "work-seed",
      connectionGeneration: 1,
      childGeneration: 0,
    });
    // Boot-time reconcile: alpha is offline ⇒ lost immediately.
    // (The probe path covers a child that reconnects AFTER boot.)
    const a = await f.attach("alpha", manifestAdvert(["research.analyze"], "alpha"));
    // The probe fires on register: alpha's handler answers accepted-to-everything
    // but `status` is answered here as unknown by NOT tracking executions.
    // Wait for the probe: row flips lost + one failed event.
    const lost = await waitFor("probe settled row", () => {
      const row = f.paths.execution.byExecution("exec-seed");
      return row !== null && row.state === "lost" ? row : false;
    });
    expect(lost.error).toBe("child-lost-execution");
    const failed = f.mock.envelopes.filter(
      (e) => e.type === "org.facilitator.event.assignment.failed.v1",
    );
    expect(failed).toHaveLength(1);
    expect(failed[0]?.data).toMatchObject({
      reason: "child-lost-execution",
      retryable: true,
      assignmentId: "asg-seed",
    });
    await a.conn.close();
  });
});

describe("standalone-mode boot", () => {
  it("zero children at boot: ready, empty fleet, no POST", async () => {
    const stateDir = mkdtempSync(join(tmpdir(), "gw-solo-"));
    const cfg = parseConfig(
      `
apiVersion: facilitator.framework/v1
kind: Gateway
gatewayId: gw.solo-test
team: default
facilitatorUrl: http://kernel.test
enrollmentSecret: enroll-dev
trustMode: shared-runtime
stateDir: ${stateDir}
mode: standalone
childTransport:
  socket:
    path: child.sock
heartbeatSeconds: 3600
`,
      { env: {} },
    );
    const mock = new MockTransport();
    const kernel = fakeKernel();
    const rt = createGatewayRuntime(cfg, {
      transportFactory: () => mock,
      fetchImpl: kernel.fetchImpl,
      logger: createMemoryLogger(),
    });
    live = {
      cfg,
      rt,
      mock,
      kernel,
      lines: [],
      paths: {
        link: new SqliteLedger(join(stateDir, "link.sqlite3")),
        execution: new ExecutionLedger(join(stateDir, "gateway.sqlite3")),
      },
      socketPath: cfg.childTransport?.socket.path ?? "",
      attach: async () => ({ conn: null as unknown as ChildConnection, answered: [] }),
    };
    const ready = await rt.start();
    expect(ready.children).toBe(0);
    expect(kernel.calls).toEqual(["enroll"]);
    // A later-attaching child is accepted even though config named none.
    const conn = await connectChild({
      childWorkspaceId: "gw.solo-test/late",
      token: "anything",
      manifest: builtinChildManifest("echo"),
      path: cfg.childTransport?.socket.path ?? "",
      heartbeatSeconds: 1,
      onCommand: async () => ({ kind: "accepted", executionId: "" }),
    });
    live.rt = rt;
    await waitFor("child online", () =>
      rt.gatewayStatus().children.some((c) => c.state === "connected"),
    );
    await conn.close();
    await rt.stop();
    live = null;
  });
});
