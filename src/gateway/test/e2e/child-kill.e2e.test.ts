// E2E child-kill: SIGKILL a socket child mid-flight. The
// Gateway marks it offline at once (socket close), the kernel keeps getting
// service from the SURVIVING child, and when the dead child's name
// reconnects, the re-register probe gets an honest `unknown` from the fresh
// process: the row goes `lost` + exactly ONE retryable assignment.failed —
// never re-executed.
import { describe, expect, it } from "vitest";
import {
  E2E,
  assignEnvelope,
  childReady,
  gatewayConfig,
  gatewayStatus,
  inboxRowsByType,
  linkRow,
  seedDelivery,
  startChild,
  startGateway,
  useFacilitator,
  waitFor,
  type Harness,
} from "./gateway-harness.js";

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

describe.skipIf(!E2E)("gateway child-kill e2e", () => {
  const harness: Harness = useFacilitator();

  it("SIGKILL the running child: offline honesty, survivor keeps flowing, probe settles lost", async () => {
    const gw = await gatewayConfig({
      facilitatorUrl: harness.baseUrl,
      fleet: [{ name: "a" }, { name: "b" }],
      gatewayMode: "shared",
      heartbeatSeconds: 3,
    });
    const gateway = startGateway(gw.configPath);
    const kids = [];
    try {
      await gateway.ready();
      const a = startChild({
        childWorkspaceId: `${gw.gatewayId}/a`,
        socket: gw.socketPath ?? "",
        mode: "complete",
        capability: "research.analyze",
      });
      let b = startChild({
        childWorkspaceId: `${gw.gatewayId}/b`,
        socket: gw.socketPath ?? "",
        mode: "silent",
        capability: "report.render",
      });
      kids.push(a);
      await childReady(a);
      await childReady(b);

      // b takes a silent assignment: the row sits `running`.
      const cmdB = `cmd-${Math.random().toString(36).slice(2, 10)}`;
      const outboxB = await seedDelivery(
        harness.baseUrl,
        gw.gatewayId,
        assignFor(cmdB, "report.render"),
      );
      await waitFor("b delivery acked", async () => {
        const r = await linkRow(harness.dbUrl, outboxB);
        return r !== null && r.state === "acked" ? r : false;
      });
      const running = await waitFor("b row running", async () => {
        const s = await gatewayStatus(gw.diagnosticsPort);
        return s.executions.running === 1 ? s : false;
      });
      const executionId = running.bindings[0]?.executionId ?? "";
      expect(executionId).toMatch(/^exec-/);

      // SIGKILL b: the socket close is the truth — offline, route survives.
      b.kill("SIGKILL");
      await waitFor("b marked offline", async () => {
        const s = await gatewayStatus(gw.diagnosticsPort);
        return s.children?.some(
          (c) => c.childWorkspaceId === `${gw.gatewayId}/b` && c.state === "offline",
        )
          ? true
          : false;
      });

      // The survivor still serves: a's assignment completes normally.
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
      await waitFor("a completed", async () => {
        const s = await gatewayStatus(gw.diagnosticsPort);
        return s.executions.completed === 1 ? s : false;
      });

      // A fresh b re-registers: generation bumps, the probe asks about the
      // dead predecessor's execution and honestly answers `unknown`.
      b = startChild({
        childWorkspaceId: `${gw.gatewayId}/b`,
        socket: gw.socketPath ?? "",
        mode: "silent",
        capability: "report.render",
      });
      kids.push(b);
      await childReady(b);

      const lost = await waitFor("probe settled the row lost", async () => {
        const s = await gatewayStatus(gw.diagnosticsPort);
        const binding = s.bindings.find((x) => x.executionId === executionId);
        return binding !== undefined && binding.state === "lost" ? s : false;
      });
      expect(lost.executions.lost).toBe(1);
      const probeLog = await waitFor("probe logged", async () =>
        gateway.log().includes("reconcile: execution lost") ? true : false,
      );
      expect(probeLog).toBe(true);

      // Exactly ONE honest retryable failed event for the dead execution.
      const rows = await waitFor("failed event lands", async () => {
        const found = await inboxRowsByType(
          harness.dbUrl,
          "org.facilitator.event.assignment.failed.v1",
        );
        return found.length === 1 ? found : false;
      });
      expect(rows[0]?.state).toBe("processed");
      const payload = JSON.parse(rows[0]?.payload ?? "{}") as {
        source: string;
        data: Record<string, unknown>;
      };
      expect(payload.source).toBe(`urn:workspace:${gw.gatewayId}`);
      expect(payload.data).toMatchObject({
        assignmentId: `asg-${cmdB}`,
        executionId,
        reason: "child-lost-execution",
        retryable: true,
      });
    } finally {
      for (const c of kids) c.kill("SIGKILL");
      await gateway.kill("SIGTERM");
    }
  });
});
