// E2E fleet-assign (Plan 06 step 6): a SHARED gateway with two REAL child
// processes. Laws proven across process boundaries: boot with zero
// connected children and NO manifest at the kernel; union POST only after
// child churn; per-capability routing to the advertising child; the echo
// child's event settling `processed` in the kernel inbox.
import { beforeAll, describe, expect, it } from "vitest";
import type { ChildProcess } from "node:child_process";
import {
  E2E,
  assignEnvelope,
  childReady,
  gatewayConfig,
  gatewayStatus,
  gatewayToken,
  inboxRowsByType,
  linkRow,
  seedDelivery,
  startChild,
  startGateway,
  useFacilitator,
  waitFor,
  type GatewayHandleConfig,
  type Harness,
} from "./gateway-harness.js";
import { linkGet } from "../../../workspace-link/test/e2e/harness.js";

function assignFor(commandId: string, capabilityId: string): Record<string, unknown> {
  const env = assignEnvelope({
    commandId,
    workId: `work-${commandId}`,
    assignmentId: `asg-${commandId}`,
  });
  return {
    ...env,
    data: {
      ...(env.data as Record<string, unknown>),
      capabilityId,
      expectedDeliverables: [
        { kind: `${capabilityId.replaceAll(".", "-")}.result`, mediaType: "text/markdown" },
      ],
    },
  };
}

describe.skipIf(!E2E)("gateway fleet assign e2e", () => {
  const harness: Harness = useFacilitator();
  let gw: GatewayHandleConfig;
  const children: ChildProcess[] = [];

  beforeAll(async () => {
    gw = await gatewayConfig({
      facilitatorUrl: harness.baseUrl,
      fleet: [{ name: "a" }, { name: "b" }],
      gatewayMode: "shared",
      heartbeatSeconds: 3,
    });
  });

  it("boot idle, union POST on churn, routes per advertiser", async () => {
    const gateway = startGateway(gw.configPath);
    try {
      const ready = await gateway.ready();
      expect(ready.epoch).toBe(1);
      expect(ready.children).toBe(0); // listener boots empty: fleet modes POST nothing

      // The kernel holds NO manifest: fleet gateways POST nothing at boot.
      const before = await linkGet(
        harness.baseUrl,
        "/api/link/v1/capabilities",
        gatewayToken(gw.stateDir),
      );
      expect(before.status).toBe(404);

      const a = startChild({
        childWorkspaceId: `${gw.gatewayId}/a`,
        socket: gw.socketPath ?? "",
        mode: "complete",
        capability: "research.analyze",
      });
      const b = startChild({
        childWorkspaceId: `${gw.gatewayId}/b`,
        socket: gw.socketPath ?? "",
        mode: "silent",
        capability: "report.render",
      });
      children.push(a, b);
      await childReady(a);
      await childReady(b);

      // The debounced union POST reaches the kernel: BOTH capabilities,
      // rewritten to the Gateway's team.
      const caps = await waitFor("union POST lands", async () => {
        const r = await linkGet(
          harness.baseUrl,
          "/api/link/v1/capabilities",
          gatewayToken(gw.stateDir),
        );
        if (r.status !== 200) return false;
        const m = r.body as { capabilities: { id: string }[]; ownerTeam: string };
        return m.capabilities.length === 2 ? m : false;
      });
      expect(caps.ownerTeam).toBe("default");
      expect(caps.capabilities.map((c) => c.id).sort()).toEqual([
        "report.render",
        "research.analyze",
      ]);

      const online = await waitFor("both children online", async () => {
        const s = await gatewayStatus(gw.diagnosticsPort);
        return s.children?.length === 2 && s.children.every((c) => c.state === "connected")
          ? s
          : false;
      });
      expect(online.children?.map((c) => c.childGeneration)).toEqual([1, 1]);

      // Assign for report.render → the row binds to child b (silent: stays
      // running) and ONLY b's handler is reached.
      const cmdB = `cmd-${Math.random().toString(36).slice(2, 10)}`;
      const outboxB = await seedDelivery(
        harness.baseUrl,
        gw.gatewayId,
        assignFor(cmdB, "report.render"),
      );
      const rowB = await waitFor("b delivery acked", async () => {
        const r = await linkRow(harness.dbUrl, outboxB);
        return r !== null && r.state === "acked" ? r : false;
      });
      expect(rowB.receiptStatus).toBe("accepted");

      // Assign for research.analyze → child a echoes + publishes completed.
      const cmdA = `cmd-${Math.random().toString(36).slice(2, 10)}`;
      const outboxA = await seedDelivery(
        harness.baseUrl,
        gw.gatewayId,
        assignFor(cmdA, "research.analyze"),
      );
      const rowA = await waitFor("a delivery acked", async () => {
        const r = await linkRow(harness.dbUrl, outboxA);
        return r !== null && r.state === "acked" ? r : false;
      });
      expect(rowA.receiptStatus).toBe("accepted");

      // a's CHILD event settles `processed` in the kernel inbox.
      const inbox = await waitFor("a completed event processed", async () => {
        const rows = await inboxRowsByType(
          harness.dbUrl,
          "org.facilitator.event.assignment.completed.v1",
        );
        return rows.length > 0 ? rows : false;
      });
      expect(inbox[0]?.state).toBe("processed");
      const payload = JSON.parse(inbox[0]?.payload ?? "{}") as {
        source: string;
        data: { assignmentId: string };
      };
      expect(payload.source).toBe(`urn:workspace:${gw.gatewayId}/a`);
      expect(payload.data.assignmentId).toBe(`asg-${cmdA}`);

      // a's execution completed; b's silently runs — one row each, to the
      // RIGHT child.
      const final = await waitFor("routing settled", async () => {
        const s = await gatewayStatus(gw.diagnosticsPort);
        return s.executions.completed === 1 && s.executions.running === 1 ? s : false;
      });
      const bindings = new Map(final.bindings.map((b) => [b.childId, b.state]));
      expect(bindings.get(`${gw.gatewayId}/a`)).toBe("completed");
      expect(bindings.get(`${gw.gatewayId}/b`)).toBe("running");
    } finally {
      for (const c of children) c.kill("SIGKILL");
      await gateway.kill("SIGTERM");
    }
  });
});
