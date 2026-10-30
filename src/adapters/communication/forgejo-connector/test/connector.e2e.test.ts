// E2E connector tier (Plan 23 step 4): the REAL forgejo-connector binary as
// a fleet child of a live Gateway + Facilitator, against a fake forge
// (in-process HTTP). Proves the decision-5 loop end-to-end: a confirm-scoped
// `forgejo.pr.create` assign rides the kernel to the Gateway, the human
// approval round-trip happens over REST, the Gateway consumes and forwards,
// the connector opens EXACTLY ONE PR at the forge, the child's completed
// event carries the provider object id, and the child's own SQLite ledger
// row settled with it.
import { spawn, type ChildProcess } from "node:child_process";
import { randomUUID } from "node:crypto";
import { mkdtempSync, rmSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { DatabaseSync } from "node:sqlite";
import { afterAll, beforeAll, describe, expect, it } from "vitest";
import {
  E2E,
  assignEnvelope,
  evidenceToken,
  gatewayConfig,
  gatewayStatus,
  inboxRowsByType,
  seedDelivery,
  startGateway,
  useFacilitator,
  userToken,
  waitFor,
  type GatewayHandle,
  type GatewayHandleConfig,
  type Harness,
} from "../../../../gateway/test/e2e/gateway-harness.js";
import { startFakeForge, type FakeForge } from "./fake-forge.js";

const ROOT = new URL("../../../../../", import.meta.url).pathname;
const CONNECTOR_BIN = join(
  ROOT,
  "src/adapters/communication/forgejo-connector/dist/bin/connector.js",
);

const POLICY_YAML = `
owner: team-alpha
policies:
  - capability: forgejo.pr.create
    resources: ["repository:*"]
    actorAnyOf: ["group:/teams/team-alpha"]
    effect: confirm
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

function approvalIdOf(row: { payload: string }): string {
  const data = JSON.parse(row.payload)["data"] as Record<string, unknown>;
  return String(data["approvalId"]);
}

function connectorReady(child: ChildProcess): Promise<string> {
  return new Promise((resolve, reject) => {
    let buffer = "";
    child.stdout?.on("data", (c: Buffer) => {
      buffer += c.toString();
      const line = buffer.split("\n")[0];
      if (line !== undefined && line.startsWith("connector ready")) resolve(line);
    });
    child.once("exit", (code) =>
      reject(new Error(`connector exited early: ${String(code)}\n${buffer}`)),
    );
  });
}

describe.skipIf(!E2E)("forgejo connector approval e2e (Plan 23)", () => {
  const harness: Harness = useFacilitator();
  let forge: FakeForge;
  let gw: GatewayHandleConfig;
  let handle: GatewayHandle;
  let connector: ChildProcess;
  let stateDir: string;

  beforeAll(async () => {
    forge = await startFakeForge();
    gw = await gatewayConfig({
      facilitatorUrl: harness.baseUrl,
      policy: POLICY_YAML,
      fleet: [{ name: "forgejo" }],
    });
    handle = startGateway(gw.configPath);
    // The connector talks to the gateway socket, so boot is sequential.
    await handle.ready();
    stateDir = mkdtempSync(join(tmpdir(), "fjc-e2e-"));
    const configPath = join(stateDir, "connector.yaml");
    writeFileSync(
      configPath,
      `childWorkspaceId: gw.team-alpha/forgejo
team: team-alpha
kernelUrl: ${harness.baseUrl}
statePath: ${join(stateDir, "actions.sqlite3")}
transport:
  socket:
    path: ${gw.socketPath as string}
heartbeatSeconds: 5
`,
    );
    connector = spawn("node", [CONNECTOR_BIN, "run", "--config", configPath], {
      env: {
        ...process.env,
        FORGEJO_URL: forge.url,
        FORGEJO_TOKEN: "bot-pat",
        FORGEJO_ENABLE_VCS: "true",
        OFAC_CHILD_SECRET: "tok",
      },
      stdio: ["ignore", "pipe", "pipe"],
    });
    await connectorReady(connector);
  }, 120_000);

  afterAll(async () => {
    connector.kill("SIGKILL");
    await handle.kill();
    await forge.close();
    rmSync(stateDir, { recursive: true, force: true });
  });

  it("confirm round-trip executes pr.create once and completes with the PR id", async () => {
    const evidence = await evidenceToken("alice", "alice");
    const commandId = `cmd-${randomUUID().slice(0, 8)}`;
    const workId = randomUUID();
    const env = assignEnvelope({
      commandId,
      workId,
      assignmentId: `asg-${commandId}`,
    });
    const assign = {
      ...env,
      data: {
        ...(env.data as Record<string, unknown>),
        capabilityId: "forgejo.pr.create",
        actorEvidenceRef: `oidc:${evidence}`,
        inputs: {
          repo: "demo/widgets",
          head: "bot/topic",
          base: "main",
          title: "Add summarizer",
          body: "generated",
          resource: "repository:demo/widgets",
        },
        expectedDeliverables: [{ kind: "forgejo-pr.result", mediaType: "application/json" }],
      },
    };
    await seedDelivery(harness.baseUrl, "gw.team-alpha", assign);

    // Held for confirm: approval.required recorded, nothing executed yet.
    const row = await waitFor("approval required recorded", async () => {
      const rows = await inboxRowsByType(
        harness.dbUrl,
        "org.facilitator.event.approval.required.v1",
      );
      return rows.find((r) => r.payload.includes(workId)) ?? false;
    });
    const approvalId = approvalIdOf(row);
    await waitFor("gateway parks one approval", async () =>
      (await gatewayStatus(gw.diagnosticsPort)).awaitingApproval === 1 ? true : false,
    );
    expect(forge.prs).toHaveLength(0);

    // The human answers; the gateway consumes and forwards to the child.
    const alice = await userToken("alice", "alice");
    const responded = await api(
      harness.baseUrl,
      `/api/works/${workId}/approvals/${approvalId}/respond`,
      alice,
      { method: "POST", body: { decision: "approved", comment: "ship" } },
    );
    expect(responded.status).toBe(200);

    const completed = await waitFor("connector completed landed", async () => {
      const rows = await inboxRowsByType(
        harness.dbUrl,
        "org.facilitator.event.assignment.completed.v1",
      );
      return rows.find((r) => r.payload.includes(`asg-${commandId}`)) ?? false;
    });
    const payload = JSON.parse(completed.payload) as { data: Record<string, unknown> };
    const manifest = payload.data["resultManifest"] as {
      summary: string;
      structuredResult: Record<string, unknown>;
    };
    expect(manifest.summary).toBe("forgejo.pr.create -> PR demo/widgets#1");
    expect(manifest.structuredResult["number"]).toBe(1);

    // Exactly ONE PR at the forge; the gateway ledger says one completed run.
    expect(forge.prs).toHaveLength(1);
    expect(forge.prPosts).toBe(1);
    const status = await gatewayStatus(gw.diagnosticsPort);
    expect(status.executions.completed).toBe(1);
    expect(status.awaitingApproval).toBe(0);

    // The durable connector_actions row settled with the provider object id.
    const db = new DatabaseSync(join(stateDir, "actions.sqlite3"), { readOnly: true });
    const action = db
      .prepare("SELECT op, state, provider_object_id FROM connector_actions WHERE command_id = ?")
      .get(commandId) as { op: string; state: string; provider_object_id: string } | undefined;
    db.close();
    expect(action).toEqual({
      op: "forgejo.pr.create",
      state: "executed",
      provider_object_id: "PR demo/widgets#1",
    });
  }, 90_000);
});
