// The confirm round-trip at the runtime unit tier. A
// `confirm` verdict parks a pending row + publishes approval.required and
// answers accepted; the decided-push then consumes (approved) or settles
// (denied/expired/revoked) — never a silent drop. Cancel on a pending action
// revokes; steer refuses honestly; reconcile re-resolves crashed rows against
// the kernel REST plane. Mock transport + injected fetch, no network.
import { mkdtempSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";
import type { EnvelopeObject, LinkDeliveryV1, ReceiptV1 } from "@ofac/protocol";
import { createMemoryLogger } from "@ofac/workspace-link";
import type { EnvelopeAck, LinkTransport, WelcomeFrame } from "@ofac/workspace-link";
import { SqliteLedger } from "@ofac/workspace-link/node";
import { afterEach, describe, expect, it } from "vitest";
import { parseConfig, type GatewayConfig } from "../../src/config.js";
import { ExecutionLedger, type GatewayPaths } from "../../src/ledger.js";
import { createGatewayRuntime, type GatewayRuntime } from "../../src/runtime.js";

const FAILED = "org.facilitator.event.assignment.failed.v1";
const REQUIRED = "org.facilitator.event.approval.required.v1";
const DECIDED = "org.facilitator.event.approval.decided.v1";
const CONSUME = "org.facilitator.command.approval.consume.v1";
const REVOKED = "org.facilitator.event.approval.revoked.v1";

class MockTransport implements LinkTransport {
  receipts: ReceiptV1[] = [];
  envelopes: EnvelopeObject[] = [];
  consumeAck: EnvelopeAck = { status: "accepted", deliveryId: "inbox-c" };
  welcome: WelcomeFrame = {
    kind: "welcome",
    sessionId: "sess-1",
    leaseEpoch: 1,
    maxInFlight: 8,
    heartbeatSeconds: 300,
    protocolVersion: "v1",
  };
  private readonly deliverCbs: ((d: LinkDeliveryV1) => void)[] = [];
  async connect(): Promise<void> {}
  async hello(): Promise<WelcomeFrame | null> {
    return this.welcome;
  }
  async envelope(env: EnvelopeObject): Promise<EnvelopeAck> {
    this.envelopes.push(env);
    if (env.type === CONSUME) return this.consumeAck;
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

/** Injected kernel: enroll/capabilities/token + the approvals poll answer. */
function kernel(approvals: { approvalId: string; state: string }[]) {
  const fetchImpl = (async (url: unknown) => {
    const path = String(url);
    if (path.includes("/token")) {
      return new Response(JSON.stringify({ access_token: "cc-jwt", expires_in: 300 }), {
        status: 200,
        headers: { "content-type": "application/json" },
      });
    }
    if (path.endsWith("/api/link/v1/enroll")) {
      return new Response(
        JSON.stringify({ enrollmentId: "e1", trustMode: "shared-runtime", linkToken: "tok" }),
        { status: 201 },
      );
    }
    if (path.endsWith("/api/link/v1/capabilities")) return new Response(null, { status: 204 });
    if (path.includes("/api/works/"))
      return new Response(JSON.stringify(approvals), {
        status: 200,
        headers: { "content-type": "application/json" },
      });
    return new Response("no route", { status: 500 });
  }) as typeof fetch;
  return fetchImpl;
}

/**
 * `confirm` on research.analyze forces the hold. With `workload` the gateway
 * also carries REST credentials, enabling the reconcile poll.
 */
function config(workload: boolean): GatewayConfig {
  return parseConfig(
    `
apiVersion: facilitator.framework/v1
kind: Gateway
gatewayId: gw.unit-test
team: team-alpha
facilitatorUrl: http://kernel.test
${
  workload
    ? `workload:
  tokenUrl: http://kernel.test/token
  clientId: gw-unit
  clientSecret: cc`
    : "enrollmentSecret: enroll-dev"
}
trustMode: shared-runtime
stateDir: ${mkdtempSync(join(tmpdir(), "gw-approval-"))}
policy:
  inline: |
    owner: team-alpha
    policies:
      - capability: research.analyze
        resources: ["*"]
        actorAnyOf: []
        effect: confirm
child:
  name: echo
  mode: echo
heartbeatSeconds: 3600
`,
    { env: {} },
  );
}

function assignDelivery(commandId: string): LinkDeliveryV1 {
  return {
    deliveryId: `del-${commandId}`,
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
        capabilityId: "research.analyze",
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

function interaction(
  kind: "steer" | "cancel",
  commandId: string,
  assignmentId: string,
): LinkDeliveryV1 {
  const type = `org.facilitator.command.work.${kind}.v1`;
  return {
    deliveryId: `del-${commandId}`,
    deliveredAt: new Date().toISOString(),
    envelope: {
      specversion: "1.0",
      id: `env-${commandId}`,
      source: "urn:facilitator:instance:unit",
      type,
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
    },
  };
}

function decidedDelivery(approvalId: string, decision: string, createdAt: string): LinkDeliveryV1 {
  return {
    deliveryId: `del-decided-${approvalId}`,
    deliveredAt: new Date().toISOString(),
    envelope: {
      specversion: "1.0",
      id: `env-decided-${approvalId}`,
      source: "urn:facilitator:instance:unit",
      type: DECIDED,
      subject: `approval/${approvalId}`,
      time: new Date().toISOString(),
      datacontenttype: "application/json",
      dataschema: "https://schemas.openfacilitator.org/v1/event.approval.decided.v1.json",
      data: {
        approvalId,
        decision,
        decidedBy: "user:alice",
        evidenceRef: decision === "approved" ? "kernel:ev-1" : "",
        expiresAt: "2036-10-23T18:00:00Z",
        createdAt,
        workId: "work-cmd-a",
      },
    },
  };
}

interface Booted {
  rt: GatewayRuntime;
  mock: MockTransport;
  paths: GatewayPaths;
}

let live: Booted | null = null;

/** Boot a gateway over a fresh v3 ledger pair; pending rows may pre-exist. */
async function booted(
  options: {
    workload?: boolean;
    approvals?: { approvalId: string; state: string }[];
    seed?: (paths: GatewayPaths) => void;
  } = {},
): Promise<Booted> {
  const cfg = config(options.workload ?? false);
  const paths: GatewayPaths = {
    link: new SqliteLedger(join(cfg.stateDir, "link.sqlite3")),
    execution: new ExecutionLedger(join(cfg.stateDir, "gateway.sqlite3")),
  };
  options.seed?.(paths);
  const mock = new MockTransport();
  const rt = createGatewayRuntime(cfg, {
    transportFactory: () => mock,
    fetchImpl: kernel(options.approvals ?? []),
    ledgerFactory: () => paths,
    logger: createMemoryLogger(),
  });
  live = { rt, mock, paths };
  await rt.start();
  return live;
}

/** Poll-until-condition (the mock has no event hook to await directly). */
async function waitFor(what: string, probe: () => boolean): Promise<void> {
  const deadline = Date.now() + 5_000;
  for (;;) {
    if (probe()) return;
    if (Date.now() > deadline) throw new Error(`timeout waiting for ${what}`);
    await new Promise((resolve) => setTimeout(resolve, 20));
  }
}

function receiptFor(mock: MockTransport, deliveryId: string): ReceiptV1 | undefined {
  return mock.receipts.findLast((r) => r.deliveryId === deliveryId);
}

function requiredData(mock: MockTransport): Record<string, unknown> {
  const env = mock.envelopes.find((e) => e.type === REQUIRED);
  if (env === undefined) throw new Error("no approval.required published");
  return env.data as Record<string, unknown>;
}

function pendingRecord(approvalId: string, expiresAt: string): string {
  return JSON.stringify({
    approvalId,
    action: {
      capabilityId: "research.analyze",
      resource: "research.analyze",
      assignmentId: "asg-cmd-a",
      workId: "work-cmd-a",
    },
    eligibleActors: ["workload:gw.unit-test"],
    expiresAt,
    createdAt: "2026-01-01T00:00:00Z",
  });
}

/** A crashed run's parked row, as holdForApproval would have written it. */
function seedPending(paths: GatewayPaths, approvalId: string, expiresAt: string): void {
  paths.execution.insertPending({
    approvalId,
    deliveryId: "del-crashed",
    envelope: JSON.stringify(assignDelivery("cmd-a").envelope),
    workId: "work-cmd-a",
    assignmentId: "asg-cmd-a",
    capabilityId: "research.analyze",
    resource: "research.analyze",
    childId: "gw.unit-test/echo",
    approvalDigestRecord: pendingRecord(approvalId, expiresAt),
    policyId: "team-alpha/test",
    requiredAt: "2026-01-01T00:00:00Z",
    expiresAt,
    epoch: 1,
  });
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

describe("confirm hold", () => {
  it("confirm verdict: pending row + approval.required (digest, actors, nonce, correlation) + accepted receipt, no binding row", async () => {
    const { mock, paths } = await booted();
    mock.pushDeliver(assignDelivery("cmd-a"));
    await waitFor("accepted receipt", () => mock.receipts.length === 1);
    expect(receiptFor(mock, "del-cmd-a")?.status).toBe("accepted");
    const pending = paths.execution.pendingByAssignment("asg-cmd-a");
    expect(pending).not.toBeNull();
    expect(pending?.state).toBe("awaiting-approval");
    expect(pending?.capabilityId).toBe("research.analyze");
    expect(paths.execution.byAssignment("asg-cmd-a")).toBeNull(); // no early bind
    expect(paths.execution.pendingCounts().awaitingApproval).toBe(1);
    const data = requiredData(mock);
    const env = mock.envelopes.find((e) => e.type === REQUIRED) as unknown as Record<
      string,
      unknown
    >;
    expect(data["approvalId"]).toBe(pending?.approvalId);
    expect(String(data["actionDigest"])).toMatch(/^sha256:[0-9a-f]{64}$/);
    expect(data["eligibleActors"]).toEqual(["workload:gw.unit-test"]);
    expect(String(data["nonce"])).toMatch(/^n-/);
    expect(env["dataschema"]).toBe(
      "https://schemas.openfacilitator.org/v1/event.approval.required.v1.json",
    );
    expect(env["correlationid"]).toBe("env-cmd-a");
    // the stored digest record is what the decided-push binds against
    const record = JSON.parse(pending?.approvalDigestRecord ?? "{}") as Record<string, unknown>;
    expect(record["createdAt"]).toBe(data["createdAt"]);
  });

  it("redelivered assign under confirm: duplicate receipt, ONE pending row, ONE required event", async () => {
    const { mock, paths } = await booted();
    mock.pushDeliver(assignDelivery("cmd-a"));
    await waitFor("first receipt", () => mock.receipts.length === 1);
    mock.pushDeliver(assignDelivery("cmd-a"));
    await waitFor("second receipt", () => mock.receipts.length === 2);
    expect(receiptFor(mock, "del-cmd-a")?.status).toBe("duplicate");
    expect(mock.envelopes.filter((e) => e.type === REQUIRED)).toHaveLength(1);
    expect(paths.execution.pendingByState("awaiting-approval")).toHaveLength(1);
  });
});

describe("decided-push", () => {
  it("approved: consume command sent, binding row, child start, pending executing, receipt accepted", async () => {
    const { mock, paths } = await booted();
    mock.pushDeliver(assignDelivery("cmd-a"));
    await waitFor("hold", () => paths.execution.pendingByAssignment("asg-cmd-a") !== null);
    const data = requiredData(mock);
    const approvalId = String(data["approvalId"]);
    mock.pushDeliver(decidedDelivery(approvalId, "approved", String(data["createdAt"])));
    await waitFor("consume", () => mock.envelopes.some((e) => e.type === CONSUME));
    const consume = mock.envelopes.find((e) => e.type === CONSUME) as unknown as Record<
      string,
      unknown
    >;
    expect((consume["data"] as Record<string, unknown>)["approvalId"]).toBe(approvalId);
    expect(consume["correlationid"]).toBe("env-cmd-a");
    await waitFor("binding row", () => paths.execution.byAssignment("asg-cmd-a") !== null);
    const pending = paths.execution.getPending(approvalId);
    // the echo child completes instantly: the settle mirror closes the row
    expect(pending?.state).toBe("completed");
    expect(pending?.executionId).not.toBeNull();
    expect(receiptFor(mock, `del-decided-${approvalId}`)?.status).toBe("accepted");
  });

  it("consume refused `approval-revoked`: pending revoked, ONE assignment.failed, no binding", async () => {
    const { mock, paths } = await booted();
    mock.pushDeliver(assignDelivery("cmd-a"));
    await waitFor("hold", () => paths.execution.pendingByAssignment("asg-cmd-a") !== null);
    const data = requiredData(mock);
    const approvalId = String(data["approvalId"]);
    mock.consumeAck = {
      status: "rejected",
      problem: {
        type: "https://errors.openfacilitator.org/v1/approval-revoked",
        title: "revoked",
        status: 409,
      },
    };
    mock.pushDeliver(decidedDelivery(approvalId, "approved", String(data["createdAt"])));
    await waitFor("failed event", () => mock.envelopes.some((e) => e.type === FAILED));
    expect(paths.execution.getPending(approvalId)?.state).toBe("revoked");
    expect(paths.execution.byAssignment("asg-cmd-a")).toBeNull();
    const failed = mock.envelopes.filter((e) => e.type === FAILED);
    expect(failed).toHaveLength(1);
    expect(JSON.stringify(failed[0])).toContain("approval-revoked");
  });

  it("denied: pending failed, ONE assignment.failed `approval-denied`, no consume, receipt accepted", async () => {
    const { mock, paths } = await booted();
    mock.pushDeliver(assignDelivery("cmd-a"));
    await waitFor("hold", () => paths.execution.pendingByAssignment("asg-cmd-a") !== null);
    const data = requiredData(mock);
    const approvalId = String(data["approvalId"]);
    mock.pushDeliver(decidedDelivery(approvalId, "denied", String(data["createdAt"])));
    await waitFor("decided receipt", () =>
      mock.receipts.some((r) => r.deliveryId === `del-decided-${approvalId}`),
    );
    expect(receiptFor(mock, `del-decided-${approvalId}`)?.status).toBe("accepted");
    expect(paths.execution.getPending(approvalId)?.state).toBe("failed");
    expect(mock.envelopes.filter((e) => e.type === FAILED)).toHaveLength(1);
    expect(mock.envelopes.some((e) => e.type === CONSUME)).toBe(false);
    expect(JSON.stringify(mock.envelopes.filter((e) => e.type === FAILED))).toContain(
      "approval-denied",
    );
  });

  it("expired push: pending expired + assignment.failed `approval-expired`", async () => {
    const { mock, paths } = await booted();
    mock.pushDeliver(assignDelivery("cmd-a"));
    await waitFor("hold", () => paths.execution.pendingByAssignment("asg-cmd-a") !== null);
    const data = requiredData(mock);
    const approvalId = String(data["approvalId"]);
    mock.pushDeliver(decidedDelivery(approvalId, "expired", String(data["createdAt"])));
    await waitFor("failed event", () => mock.envelopes.some((e) => e.type === FAILED));
    expect(paths.execution.getPending(approvalId)?.state).toBe("expired");
    expect(JSON.stringify(mock.envelopes.filter((e) => e.type === FAILED))).toContain(
      "approval-expired",
    );
  });

  it("createdAt mismatch against the stored record: push ignored, row still awaiting, nothing sent", async () => {
    const { mock, paths } = await booted();
    mock.pushDeliver(assignDelivery("cmd-a"));
    await waitFor("hold", () => paths.execution.pendingByAssignment("asg-cmd-a") !== null);
    const data = requiredData(mock);
    const approvalId = String(data["approvalId"]);
    mock.pushDeliver(decidedDelivery(approvalId, "approved", "2020-01-01T00:00:00Z"));
    await waitFor("decided receipt", () =>
      mock.receipts.some((r) => r.deliveryId === `del-decided-${approvalId}`),
    );
    expect(paths.execution.getPending(approvalId)?.state).toBe("awaiting-approval");
    expect(mock.envelopes.some((e) => e.type === CONSUME)).toBe(false);
    expect(mock.envelopes.some((e) => e.type === FAILED)).toBe(false);
  });

  it("push for an unknown approvalId: receipt accepted, no-op", async () => {
    const { mock } = await booted();
    mock.pushDeliver(decidedDelivery("nope-1", "approved", "2026-01-01T00:00:00Z"));
    await waitFor("receipt", () => mock.receipts.length === 1);
    expect(receiptFor(mock, "del-decided-nope-1")?.status).toBe("accepted");
    expect(mock.envelopes.some((e) => e.type === CONSUME)).toBe(false);
    expect(mock.envelopes.some((e) => e.type === FAILED)).toBe(false);
  });
});

describe("steer/cancel on a pending action", () => {
  it("cancel revokes: row revoked + approval.revoked published + accepted receipt", async () => {
    const { mock, paths } = await booted();
    mock.pushDeliver(assignDelivery("cmd-a"));
    await waitFor("hold", () => paths.execution.pendingByAssignment("asg-cmd-a") !== null);
    mock.pushDeliver(interaction("cancel", "cmd-x", "asg-cmd-a"));
    await waitFor("cancel receipt", () => mock.receipts.length === 2);
    expect(receiptFor(mock, "del-cmd-x")?.status).toBe("accepted");
    expect(paths.execution.pendingByAssignment("asg-cmd-a")?.state).toBe("revoked");
    expect(mock.envelopes.some((e) => e.type === REVOKED)).toBe(true);
  });

  it("steer on pending: rejected `pending-approval`, row untouched", async () => {
    const { mock, paths } = await booted();
    mock.pushDeliver(assignDelivery("cmd-a"));
    await waitFor("hold", () => paths.execution.pendingByAssignment("asg-cmd-a") !== null);
    mock.pushDeliver(interaction("steer", "cmd-s", "asg-cmd-a"));
    await waitFor("steer receipt", () => mock.receipts.length === 2);
    expect(receiptFor(mock, "del-cmd-s")?.status).toBe("rejected");
    expect(JSON.stringify(mock.receipts)).toContain("pending-approval");
    expect(paths.execution.pendingByAssignment("asg-cmd-a")?.state).toBe("awaiting-approval");
  });
});

describe("approval reconcile (crash recovery)", () => {
  it("awaiting row past expiry: expired + ONE assignment.failed during boot reconcile", async () => {
    const { mock, paths } = await booted({
      seed: (p) => seedPending(p, "ap-old", "2020-01-01T00:00:00Z"),
    });
    expect(paths.execution.getPending("ap-old")?.state).toBe("expired");
    const failed = mock.envelopes.filter((e) => e.type === FAILED);
    expect(failed).toHaveLength(1);
    expect(JSON.stringify(failed[0])).toContain("approval-expired");
  });

  it("kernel says approved at boot: reconcile consumes and executes", async () => {
    const { mock, paths } = await booted({
      workload: true,
      approvals: [{ approvalId: "ap-k", state: "approved" }],
      seed: (p) => seedPending(p, "ap-k", "2036-01-01T00:00:00Z"),
    });
    expect(mock.envelopes.some((e) => e.type === CONSUME)).toBe(true);
    expect(paths.execution.byAssignment("asg-cmd-a")).not.toBeNull();
    expect(paths.execution.getPending("ap-k")?.state).toBe("completed"); // echo child settles
  });

  it("kernel pending/absent: approval.required republished with the SAME approvalId, row stays awaiting", async () => {
    const { mock, paths } = await booted({
      workload: true,
      approvals: [],
      seed: (p) => seedPending(p, "ap-p", "2036-01-01T00:00:00Z"),
    });
    const republished = mock.envelopes.filter((e) => e.type === REQUIRED);
    expect(republished).toHaveLength(1);
    expect((republished[0]?.data as Record<string, unknown>)["approvalId"]).toBe("ap-p");
    expect(paths.execution.getPending("ap-p")?.state).toBe("awaiting-approval");
  });

  it("kernel says denied at boot: pending failed + assignment.failed", async () => {
    const { mock, paths } = await booted({
      workload: true,
      approvals: [{ approvalId: "ap-d", state: "denied" }],
      seed: (p) => seedPending(p, "ap-d", "2036-01-01T00:00:00Z"),
    });
    expect(paths.execution.getPending("ap-d")?.state).toBe("failed");
    expect(JSON.stringify(mock.envelopes.filter((e) => e.type === FAILED))).toContain(
      "approval-denied",
    );
  });

  it("kernel says consumed with no follow-through: pending failed `approval-execution-lost`", async () => {
    const { mock, paths } = await booted({
      workload: true,
      approvals: [{ approvalId: "ap-c", state: "consumed" }],
      seed: (p) => seedPending(p, "ap-c", "2036-01-01T00:00:00Z"),
    });
    expect(paths.execution.getPending("ap-c")?.state).toBe("failed");
    expect(JSON.stringify(mock.envelopes.filter((e) => e.type === FAILED))).toContain(
      "approval-execution-lost",
    );
  });

  it("executing row whose start never shipped: failed + assignment.failed (lost start)", async () => {
    const { mock, paths } = await booted({
      seed: (p) => {
        seedPending(p, "ap-e", "2036-01-01T00:00:00Z");
        p.execution.bindPendingExecution("ap-e", "exec-never-started");
      },
    });
    expect(paths.execution.getPending("ap-e")?.state).toBe("failed");
    expect(JSON.stringify(mock.envelopes.filter((e) => e.type === FAILED))).toContain(
      "approval-execution-lost",
    );
  });

  it("executing row with a live binding row: untouched by reconcile", async () => {
    const { mock, paths } = await booted({
      seed: (p) => {
        seedPending(p, "ap-live", "2036-01-01T00:00:00Z");
        p.execution.insert({
          deliveryId: "del-crashed",
          dedupeKey: "cmd-a",
          gatewayId: "gw.unit-test",
          childId: "gw.unit-test/echo",
          assignmentId: "asg-cmd-a",
          executionId: "exec-live",
          workId: "work-cmd-a",
          connectionGeneration: 1,
          childGeneration: 0,
        });
        p.execution.bindPendingExecution("ap-live", "exec-live");
      },
    });
    expect(paths.execution.getPending("ap-live")?.state).toBe("executing");
    // the 05 reconcile may report the seeded running row lost against the
    // fresh child; the approval law is that reconcile NEVER calls it a lost
    // start when the binding row exists
    expect(JSON.stringify(mock.envelopes.filter((e) => e.type === FAILED))).not.toContain(
      "approval-execution-lost",
    );
  });
});

describe("gateway status surface", () => {
  it("status doc carries awaitingApproval count", async () => {
    const { rt, mock } = await booted();
    mock.pushDeliver(assignDelivery("cmd-a"));
    await waitFor("hold", () => mock.receipts.length === 1);
    expect(rt.gatewayStatus().awaitingApproval).toBe(1);
  });
});
