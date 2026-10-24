// E2E 1 (Plan 05 step 6): the full happy path against a live kernel —
// enroll, capabilities POST, SignalR delivery of a seeded work.assign,
// echo-child completion, and the child's event settling `processed` in the
// kernel inbox.
import { beforeAll, describe, expect, it } from "vitest";
import {
  E2E,
  assignEnvelope,
  gatewayConfig,
  gatewayStatus,
  gatewayToken,
  inboxRowsByType,
  linkRow,
  seedDelivery,
  startGateway,
  useFacilitator,
  waitFor,
  type GatewayHandleConfig,
  type Harness,
} from "./gateway-harness.js";
import { linkGet } from "../../../workspace-link/test/e2e/harness.js";

describe.skipIf(!E2E)("gateway assign e2e", () => {
  const harness: Harness = useFacilitator();
  let gw: GatewayHandleConfig;

  beforeAll(async () => {
    gw = await gatewayConfig({ facilitatorUrl: harness.baseUrl });
  });

  it("ready line, POSTed manifest, delivered assign, completed event processed", async () => {
    const gateway = startGateway(gw.configPath);
    try {
      const ready = await gateway.ready();
      expect(ready.epoch).toBe(1);
      expect(ready.line).toContain(`endpointId=${gw.gatewayId}`);

      // The manifest the Gateway POSTed is what the kernel serves back.
      const caps = await linkGet(
        harness.baseUrl,
        "/api/link/v1/capabilities",
        gatewayToken(gw.stateDir),
      );
      expect(caps.status).toBe(200);
      const manifest = caps.body as {
        ownerTeam: string;
        capabilities: { id: string }[];
      };
      expect(manifest.ownerTeam).toBe("default");
      expect(manifest.capabilities.map((c) => c.id)).toEqual(["research.analyze"]);

      // Seed a work.assign for this endpoint and watch it flow end to end.
      const commandId = `cmd-${Math.random().toString(36).slice(2, 10)}`;
      const outboxId = await seedDelivery(
        harness.baseUrl,
        gw.gatewayId,
        assignEnvelope({
          commandId,
          workId: `work-${commandId}`,
          assignmentId: `asg-${commandId}`,
        }),
      );
      // As-built law: `/api/dev/deliveries` reads the in-memory DeliveryLedger,
      // which only the kernel-log sink feeds (ISink.cs) — the link sink records
      // nothing there. The kernel-side truth for link dispatch is the
      // link_deliveries row itself (Plan 04's own e2e polls it via psql).
      const row = await waitFor("link row acked", async () => {
        const r = await linkRow(harness.dbUrl, outboxId);
        // Frozen state law: pending | in_flight | acked — receipt acceptance
        // terminalises the row as `acked`, never a `delivered` literal.
        return r !== null && r.state === "acked" ? r : false;
      });
      expect(row.receiptStatus).toBe("accepted");

      // The Gateway's own diagnostics show the execution settling completed.
      const status = await waitFor("execution completed", async () => {
        const s = await gatewayStatus(gw.diagnosticsPort);
        return s.executions.completed >= 1 ? s : false;
      });
      expect(status.executions.running).toBe(0);
      expect(status.bindings).toHaveLength(1);
      expect(status.bindings[0]?.state).toBe("completed");
      expect(status.bindings[0]?.connectionGeneration).toBe(1);

      // The CHILD's event reached the kernel inbox and settled processed.
      const inbox = await waitFor("completed event processed", async () => {
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
      expect(payload.source).toBe(`urn:workspace:${gw.gatewayId}/echo`);
      expect(payload.data.assignmentId).toBe(`asg-${commandId}`);
    } finally {
      await gateway.kill("SIGTERM");
    }
  });
});
