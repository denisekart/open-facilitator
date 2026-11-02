// THE §12 loop, across real process + DB boundaries. A
// confirm-scoped assign rides the kernel to a shared-mode Gateway with a real
// fleet child; the Gateway records approval.required (kernel recomputes the
// Gateway-computed digest); a human answers over REST as a real user
// principal; the kernel's decided-push wakes the Gateway, which consumes over
// the Link plane and executes. Deny, revoke (cancel-on-pending), and the
// resource-ceiling deny ride the same rig.
import type { ChildProcess } from "node:child_process";
import { randomUUID } from "node:crypto";
import { afterAll, afterEach, beforeAll, describe, expect, it } from "vitest";
import {
  E2E,
  assignEnvelope,
  childReady,
  evidenceToken,
  gatewayConfig,
  gatewayStatus,
  inboxRowsByType,
  linkRow,
  psql,
  seedDelivery,
  startChild,
  startGateway,
  useFacilitator,
  userToken,
  waitFor,
  type GatewayHandle,
  type GatewayHandleConfig,
  type Harness,
} from "./gateway-harness.js";
import type { LinkDeliveryV1 } from "@ofac/protocol";

const POLICY_YAML = `
owner: team-alpha
policies:
  - capability: research.publish
    resources: ["repository:svc-*"]
    actorAnyOf: ["group:/teams/team-alpha"]
    effect: confirm
  - capability: research.analyze
    resources: ["repository:other"]
    actorAnyOf: []
    effect: confirm
  - capability: research.analyze
    resources: ["*"]
    actorAnyOf: []
    effect: allow
`;

async function api(
  baseUrl: string,
  path: string,
  token: string,
  init?: { method?: string; body?: unknown },
): Promise<{ status: number; body: Record<string, unknown> }> {
  const response = await fetch(`${baseUrl}${path}`, {
    method: init?.method ?? "GET",
    headers: { authorization: `Bearer ${token}`, "content-type": "application/json" },
    ...(init?.body !== undefined ? { body: JSON.stringify(init.body) } : {}),
  });
  const text = await response.text();
  return {
    status: response.status,
    body: text === "" ? {} : (JSON.parse(text) as Record<string, unknown>),
  };
}

/** The kernel approvals row for an id, as psql sees it. */
async function approvalRow(
  dbUrl: string,
  approvalId: string,
): Promise<{ state: string; digest: string; endpoint: string } | null> {
  const rows = await psql(
    dbUrl,
    `SELECT state, digest_verification, COALESCE(requested_by_endpoint_id,'-') FROM facilitator.approvals WHERE approval_id = '${approvalId}'`,
  );
  if (rows.length === 0) return null;
  const line = rows[0] as string;
  const a = line.indexOf("|");
  const b = line.indexOf("|", a + 1);
  return { state: line.slice(0, a), digest: line.slice(a + 1, b), endpoint: line.slice(b + 1) };
}

/** The envelope bytes are ours (the Gateway published them); read via named helpers. */
function payloadOf(row: { payload: string }): Record<string, unknown> {
  return JSON.parse(row.payload) as Record<string, unknown>; // parse result, checked per field
}

function approvalIdOf(row: { payload: string }): string {
  const data = payloadOf(row)["data"];
  if (typeof data === "object" && data !== null && "approvalId" in data) {
    return String((data as { approvalId: unknown }).approvalId);
  }
  throw new Error(`approval.required payload without approvalId: ${row.payload}`);
}

function workIdOf(row: { payload: string }): string {
  const data = payloadOf(row)["data"];
  if (typeof data === "object" && data !== null && "workId" in data) {
    return String((data as { workId: unknown }).workId);
  }
  throw new Error(`approval.required payload without workId: ${row.payload}`);
}

function publishAssign(
  evidenceJwt: string,
  resource = "repository:svc-core",
): Record<string, unknown> {
  const commandId = `cmd-${randomUUID().slice(0, 8)}`;
  const env = assignEnvelope({
    commandId,
    workId: randomUUID(),
    assignmentId: `asg-${commandId}`,
  });
  return {
    ...env,
    data: {
      ...(env.data as Record<string, unknown>),
      capabilityId: "research.publish",
      actorEvidenceRef: `oidc:${evidenceJwt}`,
      inputs: { objective: "ship it", sourceRefs: [], resource },
      expectedDeliverables: [{ kind: "research-publish.result", mediaType: "text/markdown" }],
    },
  };
}

function cancelEnvelope(assign: Record<string, unknown>): LinkDeliveryV1["envelope"] & object {
  const data = assign.data as Record<string, unknown>;
  return {
    specversion: "1.0",
    id: `env-cancel-${String(data["commandId"])}`,
    source: "urn:facilitator:instance:e2e",
    type: "org.facilitator.command.work.cancel.v1",
    subject: `work/${String(data["workId"])}/assignment/${String(data["assignmentId"])}`,
    time: new Date().toISOString(),
    datacontenttype: "application/json",
    dataschema: "https://schemas.openfacilitator.org/v1/command.work.cancel.v1.json",
    data: {
      commandId: `cancel-${String(data["commandId"])}`,
      workId: String(data["workId"]),
      targetId: "e2e",
      actorEvidenceRef: "identity-evidence:e2e",
      deadline: "2036-10-23T18:00:00Z",
      assignmentId: String(data["assignmentId"]),
      reason: "changed my mind",
    },
  };
}

describe.skipIf(!E2E)("gateway approval loop e2e", () => {
  const harness: Harness = useFacilitator();
  let gw: GatewayHandleConfig;
  let handle: GatewayHandle;
  let child: ChildProcess;

  beforeAll(async () => {
    gw = await gatewayConfig({
      facilitatorUrl: harness.baseUrl,
      policy: POLICY_YAML,
      fleet: [{ name: "pub" }],
    });
    handle = startGateway(gw.configPath);
    // the child connects to the gateway's unix socket: it exists only once
    // the gateway is listening, so boot is sequential (same law as fleet tests)
    await handle.ready();
    child = startChild({
      childWorkspaceId: "gw.team-alpha/pub",
      socket: gw.socketPath as string,
      mode: "complete",
      capabilities: ["research.analyze", "research.publish"],
    });
    await childReady(child);
  }, 90_000);

  afterEach((ctx) => {
    if (ctx.task.result?.state === "fail") {
      console.log(`GW LOG (${ctx.task.name}):\n${handle.log()}`);
    }
  });

  afterAll(async () => {
    child.kill("SIGKILL");
    await handle.kill();
  });

  it("confirm round-trip: hold, human approve, decided-push, consume, execute", async () => {
    const evidence = await evidenceToken("alice", "alice");
    const assign = publishAssign(evidence);
    const data = assign.data as Record<string, unknown>;
    const deliveryId = await seedDelivery(harness.baseUrl, "gw.team-alpha", assign);

    // The gateway holds + records: accepted receipt + kernel approval row.
    const row = await waitFor("approval recorded", async () => {
      const rows = await inboxRowsByType(
        harness.dbUrl,
        "org.facilitator.event.approval.required.v1",
      );
      return rows.length > 0 ? rows[0] : false;
    });
    expect(workIdOf(row)).toBe(String(data["workId"]));
    await waitFor("delivery acked", async () => {
      const link = await linkRow(harness.dbUrl, deliveryId);
      return link?.receiptStatus === "accepted" ? link : false;
    });
    const approvalId = approvalIdOf(row);
    const recorded = await waitFor("approval row + digest recomputed", async () => {
      const r = await approvalRow(harness.dbUrl, approvalId);
      return r !== null && r.digest === "recomputed" ? r : false;
    });
    expect(recorded.state).toBe("pending");
    // decided-push routing was recorded from the Link-plane caller
    expect(recorded.endpoint).toBe("gw.team-alpha");
    // the gateway's own status agrees the action is parked
    await waitFor("gateway holds one approval", async () =>
      (await gatewayStatus(gw.diagnosticsPort)).awaitingApproval === 1 ? true : false,
    );

    // A human answers with a real user principal (groups carry eligibility).
    const alice = await userToken("alice", "alice");
    const responded = await api(
      harness.baseUrl,
      `/api/works/${String(data["workId"])}/approvals/${approvalId}/respond`,
      alice,
      { method: "POST", body: { decision: "approved", comment: "ship" } },
    );
    expect(responded.status).toBe(200);
    expect(responded.body["state"]).toBe("approved");
    expect(String(responded.body["evidenceRef"])).toContain("kernel:");

    // Decided-push → consume over the Link plane → child starts → completes.
    const consumed = await waitFor("approval consumed", async () => {
      const r = await approvalRow(harness.dbUrl, approvalId);
      return r?.state === "consumed" ? r : false;
    });
    expect(consumed.endpoint).toBe("gw.team-alpha");
    const completed = await waitFor("assignment completed landed", async () => {
      const rows = await inboxRowsByType(
        harness.dbUrl,
        "org.facilitator.event.assignment.completed.v1",
      );
      return rows.find((r) => r.payload.includes(String(data["assignmentId"]))) ?? false;
    });
    expect(completed.state).toBe("processed");
    const status = await gatewayStatus(gw.diagnosticsPort);
    expect(status.executions.completed).toBe(1);
    expect(status.awaitingApproval).toBe(0);

    // Single use: the spent approval answers 409 for every later attempt.
    const again = await api(
      harness.baseUrl,
      `/api/works/${String(data["workId"])}/approvals/${approvalId}/respond`,
      alice,
      { method: "POST", body: { decision: "approved" } },
    );
    expect(again.status).toBe(409);
    expect(String(again.body["type"])).toContain("approval-already-consumed");

    // The consume rode the Link plane exactly once.
    const consumes = await inboxRowsByType(
      harness.dbUrl,
      "org.facilitator.command.approval.consume.v1",
    );
    expect(consumes).toHaveLength(1);
    expect(consumes[0]?.state).toBe("processed");
  }, 60_000);

  it("deny: decided(denied) settles the action with ONE honest failure, zero executions", async () => {
    const evidence = await evidenceToken("alice", "alice");
    const assign = publishAssign(evidence, "repository:svc-edge");
    const data = assign.data as Record<string, unknown>;
    await seedDelivery(harness.baseUrl, "gw.team-alpha", assign);
    const approvalId = await waitFor("second approval recorded", async () => {
      const rows = await inboxRowsByType(
        harness.dbUrl,
        "org.facilitator.event.approval.required.v1",
      );
      for (const r of rows) {
        const id = approvalIdOf(r);
        const kernel = await approvalRow(harness.dbUrl, id);
        if (kernel?.state === "pending" && id) return id;
      }
      return false;
    });
    const alice = await userToken("alice", "alice");
    const responded = await api(
      harness.baseUrl,
      `/api/works/${String(data["workId"])}/approvals/${approvalId}/respond`,
      alice,
      { method: "POST", body: { decision: "denied", comment: "no" } },
    );
    expect(responded.status).toBe(200);
    // gateway settles: assignment.failed carries the honest reason
    const failed = await waitFor("assignment.failed delivered", async () => {
      const rows = await inboxRowsByType(
        harness.dbUrl,
        "org.facilitator.event.assignment.failed.v1",
      );
      return rows.find((r) => r.payload.includes(String(data["assignmentId"]))) ?? false;
    });
    expect(String(failed.payload)).toContain("approval-denied");
    await waitFor("pending settled", async () =>
      (await gatewayStatus(gw.diagnosticsPort)).awaitingApproval === 0 ? true : false,
    );
    // a denied approval was never consumed, and no execution ever existed
    expect((await approvalRow(harness.dbUrl, approvalId))?.state).toBe("denied");
    const status = await gatewayStatus(gw.diagnosticsPort);
    expect(status.executions.completed).toBe(1); // only loop #1's
    expect(status.executions.running).toBe(0);
  }, 60_000);

  it("cancel-on-pending: approval revoked, later respond 409, nothing executed", async () => {
    const evidence = await evidenceToken("alice", "alice");
    const assign = publishAssign(evidence, "repository:svc-later");
    const data = assign.data as Record<string, unknown>;
    await seedDelivery(harness.baseUrl, "gw.team-alpha", assign);
    const approvalId = await waitFor("cancel-variant approval recorded", async () => {
      const rows = await inboxRowsByType(
        harness.dbUrl,
        "org.facilitator.event.approval.required.v1",
      );
      for (const r of rows) {
        const id = approvalIdOf(r);
        const kernel = await approvalRow(harness.dbUrl, id);
        if (kernel?.state === "pending") return id;
      }
      return false;
    });
    await seedDelivery(harness.baseUrl, "gw.team-alpha", cancelEnvelope(assign));
    const revoked = await waitFor("kernel row revoked", async () => {
      const r = await approvalRow(harness.dbUrl, approvalId);
      return r?.state === "revoked" ? r : false;
    });
    expect(revoked.endpoint).toBe("gw.team-alpha");
    const alice = await userToken("alice", "alice");
    const responded = await api(
      harness.baseUrl,
      `/api/works/${String(data["workId"])}/approvals/${approvalId}/respond`,
      alice,
      { method: "POST", body: { decision: "approved" } },
    );
    expect(responded.status).toBe(409);
    expect(String(responded.body["type"])).toContain("approval-revoked");
    // no consume ever rode for this approval
    await new Promise((resolve) => setTimeout(resolve, 1_500)); // settle grace: a late consume would land by now
    const status = await gatewayStatus(gw.diagnosticsPort);
    expect(status.executions.completed).toBe(1); // loop #1 only
    expect(status.awaitingApproval).toBe(0);
  }, 60_000);

  it("resource ceiling: kernel routed it, the team policy still denies", async () => {
    const evidence = await evidenceToken("alice", "alice");
    const assign = publishAssign(evidence, "repository:mine"); // svc-* only
    const deliveryId = await seedDelivery(harness.baseUrl, "gw.team-alpha", assign);
    const link = await waitFor("policy-denied receipt", async () => {
      const row = await linkRow(harness.dbUrl, deliveryId);
      return row?.receiptStatus === "rejected" ? row : false;
    });
    expect(link.state).toBe("acked");
    expect(handle.log()).toContain("policy-denied"); // audit line names it
    // no new pending action: the denial was final, nothing to approve
    expect((await gatewayStatus(gw.diagnosticsPort)).awaitingApproval).toBe(0);
  }, 60_000);

  it("ineligible human: bob (team-beta) cannot answer a team-alpha approval", async () => {
    const evidence = await evidenceToken("alice", "alice");
    const assign = publishAssign(evidence, "repository:svc-beta-try");
    const data = assign.data as Record<string, unknown>;
    await seedDelivery(harness.baseUrl, "gw.team-alpha", assign);
    const approvalId = await waitFor("bob-variant approval recorded", async () => {
      const rows = await inboxRowsByType(
        harness.dbUrl,
        "org.facilitator.event.approval.required.v1",
      );
      for (const r of rows) {
        const id = approvalIdOf(r);
        const kernel = await approvalRow(harness.dbUrl, id);
        if (kernel?.state === "pending") return id;
      }
      return false;
    });
    const bob = await userToken("bob", "bob");
    const responded = await api(
      harness.baseUrl,
      `/api/works/${String(data["workId"])}/approvals/${approvalId}/respond`,
      bob,
      { method: "POST", body: { decision: "approved" } },
    );
    // bob lacks even the respond gate role — 403 (or 409 ineligible at worst)
    expect([403, 409]).toContain(responded.status);
    // settle the action so later files see a clean ledger
    const alice = await userToken("alice", "alice");
    const settled = await api(
      harness.baseUrl,
      `/api/works/${String(data["workId"])}/approvals/${approvalId}/respond`,
      alice,
      { method: "POST", body: { decision: "denied" } },
    );
    expect(settled.status).toBe(200);
    await waitFor("bob-variant settled", async () =>
      (await gatewayStatus(gw.diagnosticsPort)).awaitingApproval === 0 ? true : false,
    );
  }, 60_000);
});
