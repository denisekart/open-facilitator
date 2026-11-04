// The full loop minus sockets: run() against a stubbed global fetch (enroll →
// capabilities), a MockTransport link session, and the FakeEcs backend.
// Pins: the enroll body shape (token/endpointId/instanceId/trustMode/role/
// team — the contract's verbatim), capabilities POSTed Bearer-tokened with
// the two provision ids, a delivered provision.start whose spec env carries
// the RAW `$NAME` on the wire (values never cross it) yields a RunTask with
// the resolved value + an `accepted` receipt, and an unknown workloadId
// yields a `rejected` receipt over the transport.
import { afterAll, beforeAll, describe, expect, it, vi } from "vitest";
import type { LinkLogger } from "@ofac/workspace-link";
import { run, type RunHandle } from "../src/index.js";
import { parseLauncherConfig, type LauncherConfig } from "../src/config.js";
import { FakeEcs } from "./fake-ecs.js";
import { MockTransport, fakeKernel, provisionDelivery, type FakeKernel } from "./fake-link.js";

const silent: LinkLogger = {
  debug: () => undefined,
  info: () => undefined,
  warn: () => undefined,
  error: () => undefined,
};

const RULES = JSON.stringify({
  "gw.team-alpha": {
    taskDefinition: "td-gw",
    cluster: "cl-team",
    subnets: ["subnet-1a"],
  },
});

let kernel: FakeKernel;
let ecs: FakeEcs;
let transport: MockTransport;
let handle: RunHandle;

async function waitForReceipt(deliveryId: string): Promise<{ status: string; reason?: string }> {
  // The receipt arrives on a microtask chain after pushDeliver; await the
  // ledger+dispatcher chain by draining, then assert — the probe bounded by
  // the deadline only trips on a genuinely lost receipt.
  const deadline = Date.now() + 5_000;
  for (;;) {
    const receipt = transport.receipts.find((r) => r.deliveryId === deliveryId);
    if (receipt !== undefined) return receipt;
    if (Date.now() > deadline) throw new Error(`timeout waiting for receipt ${deliveryId}`);
    await new Promise((resolve) => setTimeout(resolve, 10));
  }
}

beforeAll(async () => {
  kernel = fakeKernel();
  ecs = new FakeEcs();
  transport = new MockTransport();
  // The enroll path uses global fetch (workspace-link's law: plain fetch, no
  // injectable seam at this call site) — stub it for the whole loop.
  vi.stubGlobal("fetch", kernel.fetchImpl);
  const config: LauncherConfig = parseLauncherConfig(
    {
      OFAC_KERNEL_URL: "https://kernel.example.org",
      OFAC_ENROLL_TOKEN: "enroll-token-1",
      OFAC_ENDPOINT_ID: "launcher.team-alpha",
      OFAC_TEAM: "team-alpha",
      OFAC_RULES_FILE: "/rules.json",
      OFAC_INSTANCE_ID: "lnk-fixed-1",
      OFAC_STATE_PATH: ":memory:",
    },
    () => RULES,
  );
  handle = await run(config, silent, { ecs, transport, env: { TEAM_TOKEN: "wire-secret" } });
  await handle.ready;
});

afterAll(async () => {
  await handle?.shutdown();
  vi.unstubAllGlobals();
});

describe("launcher loop (fake kernel + fake ECS)", () => {
  it("enrolled with the contract's body and published capabilities Bearer-tokened", () => {
    const enrollCall = kernel.calls.find((c) => c.path.endsWith("/api/link/v1/enroll"));
    expect(enrollCall?.body).toEqual({
      token: "enroll-token-1",
      endpointId: "launcher.team-alpha",
      instanceId: "lnk-fixed-1",
      trustMode: "isolated-executor",
      role: "child",
      team: "team-alpha",
    });
    const caps = kernel.calls.find((c) => c.path.endsWith("/api/link/v1/capabilities"));
    expect(caps?.authorization).toBe("Bearer tok-fleet-1");
    const manifest = caps?.body as { capabilities: { id: string }[] };
    expect(manifest.capabilities.map((c) => c.id)).toEqual([
      "org.facilitator.command.provision.start.v1",
      "org.facilitator.command.provision.stop.v1",
    ]);
    // The hello rode the transport with the manifest digest.
    expect(transport.hellos).toHaveLength(1);
    expect(transport.hellos[0]?.endpointId).toBe("launcher.team-alpha");
  });

  it("start over the wire: $NAME crosses untouched, RunTask gets the value, receipt accepted", async () => {
    const delivery = provisionDelivery("start", "del-start-1", {
      commandId: "cmd-w1",
      operationId: "op-w1",
      workloadId: "gw.team-alpha",
      team: "team-alpha",
      actorEvidenceRef: "identity-evidence:unit",
      deadline: "2036-10-23T18:00:00Z",
      spec: { image: "pinned", env: { TEAM_TOKEN: "$TEAM_TOKEN" } },
    });
    // The wire body itself: the raw name only, never the value.
    expect(JSON.stringify(delivery.envelope.data)).toContain("$TEAM_TOKEN");
    expect(JSON.stringify(delivery.envelope.data)).not.toContain("wire-secret");
    transport.pushDeliver(delivery);

    const receipt = await waitForReceipt("del-start-1");
    expect(receipt.status).toBe("accepted");
    expect(ecs.runs).toHaveLength(1);
    expect(ecs.runs[0]?.environment).toEqual([{ name: "TEAM_TOKEN", value: "wire-secret" }]);
  });

  it("unknown workloadId over the wire ⇒ rejected receipt", async () => {
    const delivery = provisionDelivery("start", "del-start-2", {
      commandId: "cmd-w2",
      operationId: "op-w2",
      workloadId: "gw.denied",
      team: "team-alpha",
      actorEvidenceRef: "identity-evidence:unit",
      deadline: "2036-10-23T18:00:00Z",
      spec: { image: "pinned" },
    });
    transport.pushDeliver(delivery);

    const receipt = await waitForReceipt("del-start-2");
    expect(receipt.status).toBe("rejected");
    expect(receipt.reason).toBe("workload-not-allowed");
    expect(ecs.runs).toHaveLength(1);
  });

  it("stop over the wire ⇒ StopTask of the tracked task, accepted receipt", async () => {
    const delivery = provisionDelivery("stop", "del-stop-1", {
      commandId: "cmd-w3",
      operationId: "op-w3",
      workloadId: "gw.team-alpha",
      team: "team-alpha",
      actorEvidenceRef: "identity-evidence:unit",
      deadline: "2036-10-23T18:00:00Z",
    });
    transport.pushDeliver(delivery);

    const receipt = await waitForReceipt("del-stop-1");
    expect(receipt.status).toBe("accepted");
    expect(ecs.stops).toEqual([
      { cluster: "cl-team", taskArn: "arn:aws:ecs:eu-west-1:222222222222:task/fleet-1" },
    ]);
  });
});
