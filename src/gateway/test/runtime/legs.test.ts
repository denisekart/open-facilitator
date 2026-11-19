// Runtime legs beyond the embedded happy paths: the boot-race window (a
// delivery that beats the first welcome), the heartbeat/status/timer/error-
// latch legs, the standalone command surface (unknown-type passthrough,
// nudge, steer/cancel over socket children), the child reply matrix
// (rejected/unsupported/unknown), the reconcile probe on re-register, the
// approval publish-failure, and child.manifestFile boot. Mock transport +
// injected fetch + real unix-socket children; no live kernel.
import { existsSync, mkdtempSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";
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
import type { EnvelopeAck, HelloFrame, WelcomeFrame } from "@ofac/workspace-link";
import { SqliteLedger } from "@ofac/workspace-link/node";
import { afterEach, describe, expect, it } from "vitest";
import { fakeKernel, type FakeKernel } from "../fake-kernel.js";
import { parseConfig, type GatewayConfig } from "../../src/config.js";
import { builtinChildManifest } from "../../src/child.js";
import { ExecutionLedger, type GatewayPaths } from "../../src/ledger.js";
import { createGatewayRuntime, type GatewayRuntime } from "../../src/runtime.js";
import type { PolicyEvaluator } from "../../src/policy.js";
import { connectChild, type ChildConnection } from "../../src/connect.js";

class MockTransport implements LinkTransport {
  receipts: ReceiptV1[] = [];
  envelopes: EnvelopeObject[] = [];
  hellos: HelloFrame[] = [];
  welcome: WelcomeFrame = {
    kind: "welcome",
    sessionId: "sess-leg",
    leaseEpoch: 1,
    maxInFlight: 8,
    heartbeatSeconds: 300,
    protocolVersion: "v1",
  };
  /** Envelope types whose send throws (transport-dead legs). */
  failTypes = new Set<string>();
  /** Ack payloads by envelope type (consume read-back legs). */
  ackData: Map<string, Record<string, unknown>> = new Map();
  private readonly deliverCbs: ((d: LinkDeliveryV1) => void)[] = [];
  /** Test hook: runs INSIDE hello(), before the welcome is handed back. */
  duringHello?: (() => void) | undefined;
  /** Slows the welcome: widens the window where a delivery beats ready. */
  helloDelayMs = 0;

  async connect(): Promise<void> {}
  async hello(frame: HelloFrame): Promise<WelcomeFrame | null> {
    this.hellos.push(frame);
    this.duringHello?.();
    if (this.helloDelayMs > 0) {
      await new Promise((r) => setTimeout(r, this.helloDelayMs));
    }
    return this.welcome;
  }
  async envelope(env: EnvelopeObject): Promise<EnvelopeAck> {
    this.envelopes.push(env);
    if (this.failTypes.has(env.type)) throw new Error("transport is dead");
    const data = this.ackData.get(env.type);
    return { status: "accepted", deliveryId: "inbox-leg", ...(data !== undefined ? { data } : {}) };
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

const FAILED = "org.facilitator.event.assignment.failed.v1";
const REQUIRED = "org.facilitator.event.approval.required.v1";
const COMPLETED = "org.facilitator.event.assignment.completed.v1";

function baseYaml(dir: string, extra: string, heartbeatSeconds = 3600): string {
  return `
apiVersion: facilitator.framework/v1
kind: Gateway
gatewayId: gw.legs-test
team: default
facilitatorUrl: http://kernel.test
enrollmentSecret: enroll-leg
trustMode: shared-runtime
stateDir: ${dir}
${extra}heartbeatSeconds: ${String(heartbeatSeconds)}
`;
}

const EMBEDDED_EXTRA = `child:
  name: echo
  mode: echo
`;

interface Booted {
  cfg: GatewayConfig;
  rt: GatewayRuntime;
  mock: MockTransport;
  server: FakeKernel;
  lines: string[];
  paths: GatewayPaths;
}

let live: Booted | null = null;

async function booted(
  options: {
    extra?: string;
    heartbeatSeconds?: number;
    policy?: PolicyEvaluator;
    kernel?: FakeKernel;
    handlerFactory?: () => LinkHandler;
    transport?: MockTransport;
    mutate?: (deps: { transportFactory: () => LinkTransport }) => void;
    childOnlineGraceMs?: number;
  } = {},
): Promise<Booted> {
  const dir = mkdtempSync(join(tmpdir(), "gw-legs-"));
  const cfg = parseConfig(
    baseYaml(dir, options.extra ?? EMBEDDED_EXTRA, options.heartbeatSeconds ?? 3600),
    {
      env: {},
    },
  );
  const mock = options.transport ?? new MockTransport();
  const server = options.kernel ?? fakeKernel();
  const logger = createMemoryLogger();
  const paths: GatewayPaths = {
    link: new SqliteLedger(join(dir, "link.sqlite3")),
    execution: new ExecutionLedger(join(dir, "gateway.sqlite3")),
  };
  const rt = createGatewayRuntime(cfg, {
    transportFactory: () => mock,
    fetchImpl: server.fetchImpl,
    ledgerFactory: () => paths,
    logger,
    ...(options.policy !== undefined ? { policy: options.policy } : {}),
    ...(options.handlerFactory !== undefined ? { handlerFactory: options.handlerFactory } : {}),
    ...(options.childOnlineGraceMs !== undefined
      ? { childOnlineGraceMs: options.childOnlineGraceMs }
      : {}),
  });
  live = { cfg, rt, mock, server, lines: logger.lines, paths };
  await rt.start();
  return live;
}

const childConnections: ChildConnection[] = [];

afterEach(async () => {
  const current = live;
  live = null;
  for (const conn of childConnections.splice(0)) await conn.close().catch(() => undefined);
  if (current !== null) {
    try {
      await current.rt.stop();
    } catch {
      /* ledgers may already be closed by the test */
    }
  }
});

async function waitFor<T>(what: string, probe: () => T | false | null): Promise<T> {
  const deadline = Date.now() + 5_000;
  for (;;) {
    const value = probe();
    if (value) return value;
    if (Date.now() > deadline) throw new Error(`timeout waiting for ${what}`);
    await new Promise((r) => setTimeout(r, 20));
  }
}

function assignDelivery(overrides: {
  commandId: string;
  deliveryId?: string;
  capabilityId?: string;
  type?: string;
  data?: Record<string, unknown>;
}): LinkDeliveryV1 {
  return {
    deliveryId: overrides.deliveryId ?? `del-${overrides.commandId}`,
    deliveredAt: new Date().toISOString(),
    envelope: {
      specversion: "1.0",
      id: `env-${overrides.commandId}`,
      source: "urn:facilitator:instance:unit",
      type: overrides.type ?? "org.facilitator.command.work.assign.v1",
      subject: `work/work-${overrides.commandId}/assignment/asg-${overrides.commandId}`,
      time: new Date().toISOString(),
      datacontenttype: "application/json",
      dataschema: "https://schemas.openfacilitator.org/v1/command.work.assign.v1.json",
      data: overrides.data ?? {
        commandId: overrides.commandId,
        actorEvidenceRef: "identity-evidence:unit",
        workId: `work-${overrides.commandId}`,
        targetId: "unit",
        capabilityId: overrides.capabilityId ?? "research.analyze",
        expectedContextVersion: 1,
        inputs: { objective: "unit", sourceRefs: [] },
        expectedDeliverables: [{ kind: "analysis.report", mediaType: "text/markdown" }],
        deadline: "2036-10-23T18:00:00Z",
        executionGeneration: 1,
        assignmentId: `asg-${overrides.commandId}`,
      },
    } as EnvelopeCommand,
  };
}

function receiptFor(mock: MockTransport, deliveryId: string): ReceiptV1 | undefined {
  return mock.receipts.findLast((r) => r.deliveryId === deliveryId);
}

// ── embedded legs ────────────────────────────────────────────────────────────

describe("embedded runtime legs", () => {
  it("a delivery that beats the first welcome still stamps the lease epoch", async () => {
    const transport = new MockTransport();
    // Push INSIDE hello(): the dispatch can run before start()'s welcome
    // continuation, exactly like a real kernel push on session-open.
    transport.duringHello = () => transport.pushDeliver(assignDelivery({ commandId: "cmd-race" }));
    const { paths } = await booted({ transport });
    await waitFor("row stamped at epoch 1", () => {
      const row = paths.execution.byAssignment("asg-cmd-race");
      return row !== null && row.connectionGeneration === 1 ? row : false;
    });
    await waitFor("accepted receipt", () =>
      receiptFor(transport, "del-cmd-race")?.status === "accepted" ? true : false,
    );
  });

  it("a redelivered commandId under a fresh deliveryId answers accepted (already bound)", async () => {
    const { mock, paths } = await booted();
    mock.pushDeliver(assignDelivery({ commandId: "cmd-dup" }));
    await waitFor("first receipt", () => mock.receipts.length === 1);
    mock.pushDeliver(assignDelivery({ commandId: "cmd-dup", deliveryId: "del-replay" }));
    await waitFor("replay receipt", () => mock.receipts.length === 2);
    // The link ledger dedupes by commandId: the replay never re-enters the
    // runtime (duplicate receipt, bindings untouched) — the runtime's
    // BindingConflict branch is only the defensive second line.
    expect(receiptFor(mock, "del-replay")?.status).toBe("duplicate");
    expect(paths.execution.all()).toHaveLength(1);
  });

  it("a handler that throws fails the row honestly (child-disconnected)", async () => {
    const { mock, paths } = await booted({
      handlerFactory: () => ({
        onCommand: async () => {
          throw new Error("child exploded");
        },
      }),
    });
    mock.pushDeliver(assignDelivery({ commandId: "cmd-boom" }));
    await waitFor("receipt", () => mock.receipts.length === 1);
    const row = paths.execution.byAssignment("asg-cmd-boom");
    expect(row?.state).toBe("failed");
    expect(row?.error).toBe("child-disconnected");
    expect(receiptFor(mock, "del-cmd-boom")).toMatchObject({
      status: "rejected",
      reason: "child-disconnected",
    });
  });

  it("child reply matrix: rejected and unsupported settle the row + receipt", async () => {
    const { mock, paths } = await booted({
      handlerFactory: () => ({
        onCommand: async (cmd) =>
          String((cmd.data as Record<string, unknown>)["commandId"]) === "cmd-r"
            ? { kind: "rejected", reason: "no capacity" }
            : { kind: "unsupported", reason: "not mine" },
      }),
    });
    // Serialized: the first row must settle before the second passes quota.
    mock.pushDeliver(assignDelivery({ commandId: "cmd-r" }));
    await waitFor("first receipt", () => mock.receipts.length === 1);
    mock.pushDeliver(assignDelivery({ commandId: "cmd-u" }));
    await waitFor("two receipts", () => mock.receipts.length === 2);
    expect(receiptFor(mock, "del-cmd-r")).toMatchObject({
      status: "rejected",
      reason: "no capacity",
    });
    expect(receiptFor(mock, "del-cmd-u")).toMatchObject({
      status: "unsupported",
      reason: "not mine",
    });
    expect(paths.execution.byAssignment("asg-cmd-r")).toMatchObject({
      state: "failed",
      error: "no capacity",
    });
    expect(paths.execution.byAssignment("asg-cmd-u")).toMatchObject({
      state: "failed",
      error: "not mine",
    });
  });

  it("a child failed-event settles the row with the child's own reason", async () => {
    const { mock, paths } = await booted({
      handlerFactory: () => ({
        onCommand: async (_cmd, ctx) => {
          await ctx.publishEvent({
            specversion: "1.0",
            id: "evt-fail",
            source: "urn:workspace:gw.legs-test/echo",
            type: FAILED,
            subject: "work/work-cmd-f/assignment/asg-cmd-f",
            time: new Date().toISOString(),
            datacontenttype: "application/json",
            dataschema: "https://schemas.openfacilitator.org/v1/event.assignment.failed.v1.json",
            data: {
              workId: "work-cmd-f",
              assignmentId: "asg-cmd-f",
              // The embedded law: the child sees the raw execution token.
              executionId: `exec-${ctx.deliveryId}`,
              executionGeneration: 1,
              reason: "adapter died",
              retryable: false,
            },
          } as EnvelopeEvent);
          return { kind: "accepted" };
        },
      }),
    });
    mock.pushDeliver(assignDelivery({ commandId: "cmd-f" }));
    await waitFor("failed settle", () => {
      const row = paths.execution.byAssignment("asg-cmd-f");
      return row !== null && row.state === "failed" ? row : false;
    });
    expect(paths.execution.byAssignment("asg-cmd-f")?.error).toBe("adapter died");
    expect(
      mock.envelopes.some(
        (e) => e.type === FAILED && e.source === "urn:workspace:gw.legs-test/echo",
      ),
    ).toBe(true); // forwarded verbatim
  });

  it("a deny policy gives the silent event drop its audit line", async () => {
    const policy: PolicyEvaluator = {
      evaluate: (action) => (action === "event.forward" ? "deny" : "allow"),
    };
    const { mock, lines } = await booted({ policy });
    mock.pushDeliver(assignDelivery({ commandId: "cmd-evdeny" }));
    await waitFor("audit line", () =>
      lines.some((l) => l.includes("authorization.evaluated") && l.includes("event-deny")),
    );
    // The completed event never rides out; the row stays running (the child's
    // terminal answer was muted by policy, not by the ledger).
    expect(mock.envelopes.some((e) => e.type === COMPLETED)).toBe(false);
    expect(mock.receipts[0]?.status).toBe("accepted");
  });

  it("heartbeat cadence + error latch + publish failure warning", async () => {
    const { rt, mock, lines } = await booted({ heartbeatSeconds: 1 });
    await waitFor("periodic heartbeat", () =>
      mock.envelopes.some((e) => e.type === "org.facilitator.event.workspace.heartbeat.v1"),
    );
    rt.markChildError();
    await rt.publishHeartbeat("periodic");
    const heartbeats = mock.envelopes.filter(
      (e) => e.type === "org.facilitator.event.workspace.heartbeat.v1",
    );
    expect(heartbeats.at(-1)?.data).toMatchObject({ status: "error" });
    mock.failTypes.add("org.facilitator.event.workspace.heartbeat.v1");
    await rt.publishHeartbeat("shutdown");
    expect(lines.some((l) => l.includes("heartbeat publish failed"))).toBe(true);
  });

  it("confirm hold whose approval.required publish is refused: rejected + assignment.failed", async () => {
    const mock = new MockTransport();
    mock.failTypes.add(REQUIRED);
    await booted({
      transport: mock,
      extra: `child:
  name: echo
  mode: echo
policy:
  inline: |
    owner: default
    policies:
      - capability: research.analyze
        resources: ["*"]
        actorAnyOf: []
        effect: confirm
`,
    });
    mock.pushDeliver(assignDelivery({ commandId: "cmd-apfail" }));
    await waitFor("receipt", () => mock.receipts.length === 1);
    expect(receiptFor(mock, "del-cmd-apfail")).toMatchObject({
      status: "rejected",
      reason: expect.stringMatching(/^approval-publish-failed: /),
    });
    const failed = mock.envelopes.filter((e) => e.type === FAILED);
    expect(failed).toHaveLength(1);
    expect(JSON.stringify(failed[0])).toContain("approval-publish-failed");
  });

  it("child.manifestFile: the advertised capabilities come from the file", async () => {
    const dir = mkdtempSync(join(tmpdir(), "gw-legs-mf-"));
    writeFileSync(
      join(dir, "caps.yaml"),
      `capabilities:
- id: file.capability
  version: 1
  execution: synchronous
  acceptedContexts: ["inline-text"]
  outputKinds: ["file.result"]
  outputMediaTypes: ["text/plain"]
  supportsSteering: false
  supportsCancellation: false
  supportsNudge: false
`,
    );
    const { mock, server } = await booted({
      extra: `child:
  name: filechild
  mode: echo
  manifestFile: ${join(dir, "caps.yaml")}
`,
    });
    const posted = JSON.parse(
      server.bodiesFor("capabilities").at(-1) ?? "",
    ) as CapabilityManifestV1;
    expect(posted.capabilities.map((c) => c.id)).toEqual(["file.capability"]);
    // And the assign routes to it (the union advertises it now).
    mock.pushDeliver(assignDelivery({ commandId: "cmd-file", capabilityId: "file.capability" }));
    await waitFor("receipt", () => mock.receipts.length === 1);
    expect(mock.receipts[0]?.status).toBe("accepted");
  });

  it("child.manifestFile without capabilities[] aborts the boot", () => {
    const dir = mkdtempSync(join(tmpdir(), "gw-legs-mf2-"));
    writeFileSync(join(dir, "caps.yaml"), "owner: nobody\n");
    const cfg = parseConfig(
      baseYaml(
        dir,
        `child:
  name: badmanifest
  mode: echo
  manifestFile: ${join(dir, "caps.yaml")}
`,
      ),
      { env: {} },
    );
    expect(() =>
      createGatewayRuntime(cfg, { transportFactory: () => new MockTransport() }),
    ).toThrow(/manifestFile .* has no capabilities/);
  });

  it("an unknown command type passes through to the child (embedded answers unsupported)", async () => {
    const { mock } = await booted();
    mock.pushDeliver({
      deliveryId: "del-retain",
      deliveredAt: new Date().toISOString(),
      envelope: {
        specversion: "1.0",
        id: "env-retain",
        source: "urn:facilitator:instance:unit",
        type: "org.facilitator.command.memory.retain.v1",
        subject: "work/work-r",
        time: new Date().toISOString(),
        datacontenttype: "application/json",
        dataschema: "https://schemas.openfacilitator.org/v1/command.memory.retain.v1.json",
        data: {
          commandId: "cmd-retain",
          actorEvidenceRef: "identity-evidence:unit",
          scopeSelector: "team:default",
          type: "observed_fact",
          content: "the gate hums",
          provenance: {
            source: "unit",
            evidenceRef: "identity-evidence:unit",
            createdBy: "unit",
            createdAt: "2026-01-01T00:00:00Z",
          },
        },
      } as EnvelopeCommand,
    });
    const receipt = await waitFor("retain receipt", () => mock.receipts[0] ?? false);
    // The echo child only handles work.assign: its own honest answer rides
    // the receipt (05 pass-through law).
    expect(receipt.status).toBe("unsupported");
  });
});

// ── standalone (fleet-mode) legs ─────────────────────────────────────────────

interface Standalone {
  rt: GatewayRuntime;
  mock: MockTransport;
  server: FakeKernel;
  lines: string[];
  paths: GatewayPaths;
  socketPath: string;
  attach(
    name: string,
    handler: (frame: { message: { kind: string; executionId?: string } }) => Promise<{
      kind: "accepted" | "rejected" | "unsupported" | "status" | "unknown";
      executionId?: string;
      reason?: string;
      state?: "running" | "completed" | "failed" | "lost";
    }>,
  ): Promise<ChildConnection>;
}

async function standalone(
  options: { helloDelayMs?: number; childOnlineGraceMs?: number } = {},
): Promise<Standalone> {
  const dir = mkdtempSync(join(tmpdir(), "gw-legs-sa-"));
  const cfg = parseConfig(
    baseYaml(
      dir,
      `mode: standalone
children: []
childTransport:
  socket:
    path: child.sock
`,
    ),
    { env: {} },
  );
  const mock = new MockTransport();
  const server = fakeKernel();
  if (options.helloDelayMs !== undefined) {
    mock.helloDelayMs = options.helloDelayMs;
  }
  const logger = createMemoryLogger();
  const paths: GatewayPaths = {
    link: new SqliteLedger(join(dir, "link.sqlite3")),
    execution: new ExecutionLedger(join(dir, "gateway.sqlite3")),
  };
  const rt = createGatewayRuntime(cfg, {
    transportFactory: () => mock,
    fetchImpl: server.fetchImpl,
    ledgerFactory: () => paths,
    logger,
    ...(options.childOnlineGraceMs !== undefined
      ? { childOnlineGraceMs: options.childOnlineGraceMs }
      : {}),
  });
  live = { cfg, rt, mock, server, lines: logger.lines, paths };
  const standalone_: Standalone = {
    rt,
    mock,
    server,
    lines: logger.lines,
    paths,
    socketPath: cfg.childTransport?.socket.path ?? join(dir, "child.sock"),
    async attach(name, handler) {
      const conn = await connectChild({
        childWorkspaceId: `${cfg.gatewayId}/${name}`,
        token: "t",
        manifest: builtinChildManifest("echo"),
        path: standalone_.socketPath,
        onCommand: (frame) =>
          handler(frame as never) as Promise<
            | { kind: "accepted"; executionId: string }
            | { kind: "rejected"; reason: string }
            | { kind: "unsupported"; reason?: string }
            | {
                kind: "status";
                executionId: string;
                state: "running" | "completed" | "failed" | "lost";
              }
            | { kind: "unknown"; executionId: string }
          >,
        heartbeatSeconds: 1,
      });
      childConnections.push(conn);
      return conn;
    },
  };
  return standalone_;
}

function interactionDelivery(
  kind: "steer" | "cancel",
  commandId: string,
  assignmentId: string,
): LinkDeliveryV1 {
  return {
    deliveryId: `del-${commandId}`,
    deliveredAt: new Date().toISOString(),
    envelope: {
      specversion: "1.0",
      id: `env-${commandId}`,
      source: "urn:facilitator:instance:unit",
      type: `org.facilitator.command.work.${kind}.v1`,
      subject: `work/work-x/assignment/${assignmentId}`,
      time: new Date().toISOString(),
      datacontenttype: "application/json",
      dataschema: `https://schemas.openfacilitator.org/v1/command.work.${kind}.v1.json`,
      data: {
        commandId,
        workId: "work-x",
        targetId: "unit",
        actorEvidenceRef: "identity-evidence:unit",
        deadline: "2036-10-23T18:00:00Z",
        assignmentId,
        ...(kind === "steer"
          ? { directive: "pivot", expectedContextVersion: 1, executionGeneration: 1 }
          : { reason: "no longer needed" }),
      },
    } as EnvelopeCommand,
  };
}

function inputDelivery(commandId: string, assignmentId: string): LinkDeliveryV1 {
  return {
    deliveryId: `del-${commandId}`,
    deliveredAt: new Date().toISOString(),
    envelope: {
      specversion: "1.0",
      id: `env-${commandId}`,
      source: "urn:facilitator:instance:unit",
      type: "org.facilitator.command.assignment.input.v1",
      subject: `work/work-x/assignment/${assignmentId}`,
      time: new Date().toISOString(),
      datacontenttype: "application/json",
      dataschema: "https://schemas.openfacilitator.org/v1/command.assignment.input.v1.json",
      data: {
        commandId,
        requestId: "inp-1",
        answer: "postgres",
        actorEvidenceRef: "kernel:ev-input",
      },
    } as EnvelopeCommand,
  };
}

describe("standalone interaction + input legs", () => {
  it("steer reaches the row's child; cancel settles the row completed", async () => {
    const sa = await standalone();
    await sa.rt.start();
    const kinds: string[] = [];
    await sa.attach("alpha", async (frame) => {
      const kind = frame.message.kind;
      kinds.push(kind);
      return { kind: "accepted", executionId: frame.message.executionId ?? "" };
    });
    sa.mock.pushDeliver(assignDelivery({ commandId: "cmd-1" }));
    await waitFor("assign accepted", () => sa.mock.receipts[0] ?? false);
    expect(sa.paths.execution.byAssignment("asg-cmd-1")?.state).toBe("running");

    sa.mock.pushDeliver(interactionDelivery("steer", "cmd-2", "asg-cmd-1"));
    await waitFor("steer receipt", () => sa.mock.receipts.length === 2);
    expect(receiptFor(sa.mock, "del-cmd-2")?.status).toBe("accepted");
    expect(kinds).toContain("steer");

    sa.mock.pushDeliver(interactionDelivery("cancel", "cmd-3", "asg-cmd-1"));
    await waitFor("cancel receipt", () => sa.mock.receipts.length === 3);
    expect(receiptFor(sa.mock, "del-cmd-3")?.status).toBe("accepted");
    expect(sa.paths.execution.byAssignment("asg-cmd-1")?.state).toBe("completed");

    // Cancel after settle: the row exists but is not running, no pending —
    // a fleet has no fleet-of-one pass-through: honest no-binding.
    sa.mock.pushDeliver(interactionDelivery("cancel", "cmd-4", "asg-cmd-1"));
    await waitFor("post-settle receipt", () => sa.mock.receipts.length === 4);
    expect(receiptFor(sa.mock, "del-cmd-4")).toMatchObject({
      status: "rejected",
      reason: "no-binding",
    });
  });

  it("assignment.input unblocks a live run; the row STAYS running", async () => {
    const sa = await standalone();
    await sa.rt.start();
    const seen: string[] = [];
    await sa.attach("alpha", async (frame) => {
      seen.push(frame.message.kind);
      return { kind: "accepted", executionId: frame.message.executionId ?? "" };
    });
    sa.mock.pushDeliver(assignDelivery({ commandId: "cmd-in" }));
    await waitFor("assign receipt", () => sa.mock.receipts[0] ?? false);
    sa.mock.pushDeliver(inputDelivery("cmd-ans", "asg-cmd-in"));
    await waitFor("input receipt", () => sa.mock.receipts.length === 2);
    expect(receiptFor(sa.mock, "del-cmd-ans")?.status).toBe("accepted");
    expect(seen).toContain("respond");
    expect(sa.paths.execution.byAssignment("asg-cmd-in")?.state).toBe("running");
  });

  it("interactions for a vanished child answer child-disconnected", async () => {
    const sa = await standalone({ childOnlineGraceMs: 100 });
    await sa.rt.start();
    const conn = await sa.attach("alpha", async (frame) => ({
      kind: "accepted",
      executionId: frame.message.executionId ?? "",
    }));
    sa.mock.pushDeliver(assignDelivery({ commandId: "cmd-live" }));
    await waitFor("assign receipt", () => sa.mock.receipts[0] ?? false);
    await conn.close();
    await waitFor("fleet offline", () =>
      sa.rt.gatewayStatus().children.every((c) => c.state === "offline"),
    );

    sa.mock.pushDeliver(interactionDelivery("steer", "cmd-2", "asg-cmd-live"));
    await waitFor("steer receipt", () => sa.mock.receipts.length === 2);
    expect(receiptFor(sa.mock, "del-cmd-2")).toMatchObject({
      status: "rejected",
      reason: "child-disconnected",
    });
    sa.mock.pushDeliver(inputDelivery("cmd-3", "asg-cmd-live"));
    await waitFor("input receipt", () => sa.mock.receipts.length === 3);
    expect(receiptFor(sa.mock, "del-cmd-3")).toMatchObject({
      status: "rejected",
      reason: "child-disconnected",
    });
  });
});

describe("reconcile + probe against a live socket child", () => {
  it("boot reconcile: running answers stand, terminal answers settle; a re-register probe calls the vanished execution lost", async () => {
    const sa = await standalone({ helloDelayMs: 1_000 });
    // A fleet shadow from a previous life with two running rows.
    sa.paths.execution.upsertChild(
      "gw.legs-test/alpha",
      "sha256:0",
      JSON.stringify(builtinChildManifest("echo")),
      false,
    );
    for (const [exec, work] of [
      ["exec-seed-a", "work-a"],
      ["exec-seed-b", "work-b"],
    ] as const) {
      sa.paths.execution.insert({
        deliveryId: `del-${exec}`,
        dedupeKey: `cmd-${exec}`,
        gatewayId: "gw.legs-test",
        childId: "gw.legs-test/alpha",
        assignmentId: `asg-${exec}`,
        executionId: exec,
        workId: work,
        connectionGeneration: 1,
        childGeneration: 0,
      });
    }
    // The child dials while the link handshake is deliberately slow: the
    // listener is bound before client.start(), so reconcile meets a LIVE
    // member (the restart race, won).
    const starting = sa.rt.start();
    await waitFor("listener bound", () => existsSync(sa.socketPath));
    const attaching = sa.attach("alpha", async (frame) =>
      frame.message.executionId === "exec-seed-b"
        ? { kind: "status", executionId: "exec-seed-b", state: "completed" }
        : { kind: "status", executionId: frame.message.executionId ?? "", state: "running" },
    );
    await starting;
    const conn = await attaching;

    // reconcile law: the child's own truth settles b, and a stands.
    expect(sa.paths.execution.byExecution("exec-seed-b")?.state).toBe("completed");
    expect(sa.paths.execution.byExecution("exec-seed-a")?.state).toBe("running");

    // Re-register with a NEW handler: a now answers `unknown` — the probe
    // must call it lost (never re-executed) with ONE retryable failed.
    await conn.close();
    await waitFor("fleet offline", () =>
      sa.rt.gatewayStatus().children.every((c) => c.state === "offline"),
    );
    const before = sa.mock.envelopes.length;
    await sa.attach("alpha", async (frame) => ({
      kind: "unknown",
      executionId: frame.message.executionId ?? "",
    }));
    const failed = await waitFor("probe failed event", () => {
      const found = sa.mock.envelopes
        .slice(before)
        .find((e) => e.type === FAILED && JSON.stringify(e).includes("child-lost-execution"));
      return found ?? false;
    });
    expect(failed.data).toMatchObject({
      executionId: "exec-seed-a",
      reason: "child-lost-execution",
      retryable: true,
    });
    expect(sa.paths.execution.byExecution("exec-seed-a")?.state).toBe("lost");
  });
});
