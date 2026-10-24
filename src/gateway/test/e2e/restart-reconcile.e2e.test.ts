// E2E 2 (§26.1 item 7 basis): SIGKILL mid-flight, restart, reconcile.
// Deterministic recipe: a `stub` child accepts and NEVER completes, so an
// execution sits `running`; SIGKILL (no goodbye ⇒ the kernel session row
// survives); restart on the same stateDir/gatewayId ⇒ epoch bumps, the
// fresh child answers `unknown`, the row goes `lost` with exactly ONE
// retryable assignment.failed — and the log proves zero re-execution.
import { beforeAll, describe, expect, it } from "vitest";
import {
  E2E,
  assignEnvelope,
  gatewayConfig,
  gatewayStatus,
  inboxRowsByType,
  linkRow,
  seedDelivery,
  startGateway,
  useFacilitator,
  waitFor,
  type GatewayHandleConfig,
  type Harness,
} from "./gateway-harness.js";

describe.skipIf(!E2E)("gateway restart reconcile e2e", () => {
  const harness: Harness = useFacilitator({ InFlightLeaseSeconds: "2" });
  let gw: GatewayHandleConfig;

  beforeAll(async () => {
    gw = await gatewayConfig({ facilitatorUrl: harness.baseUrl, mode: "stub" });
  });

  it("SIGKILL ⇒ epoch bump; reconcile: row lost, ONE retryable failed, zero re-execution", async () => {
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

    const first = startGateway(gw.configPath);
    const ready1 = await first.ready();
    expect(ready1.epoch).toBe(1);

    // The stub accepts: one execution sits `running`, row generated 1.
    const running = await waitFor("execution running", async () => {
      const s = await gatewayStatus(gw.diagnosticsPort);
      return s.executions.running === 1 ? s : false;
    });
    const executionId = running.bindings[0]?.executionId ?? "";
    expect(executionId).toMatch(/^exec-/);
    const delivered = await waitFor("delivery acked", async () => {
      const r = await linkRow(harness.dbUrl, outboxId);
      return r !== null && r.state === "acked" ? r : false;
    });
    expect(delivered.receiptStatus).toBe("accepted");

    // SIGKILL: no goodbye ⇒ the kernel's session row survives ⇒ the next
    // hello's ON CONFLICT path bumps the epoch.
    await first.kill("SIGKILL");
    const second = startGateway(gw.configPath);
    try {
      const ready2 = await second.ready();
      expect(ready2.epoch).toBe(2);

      // Reconcile ran before the ready line: by now the row is `lost`.
      const lost = await waitFor("execution lost", async () => {
        const s = await gatewayStatus(gw.diagnosticsPort);
        return s.executions.lost === 1 ? s : false;
      });
      expect(lost.executions.running).toBe(0);
      expect(lost.bindings[0]).toMatchObject({
        executionId,
        state: "lost",
        connectionGeneration: 1, // the generation it ran under, untouched
      });

      // Exactly ONE honest assignment.failed, retryable, generation-faithful.
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
      expect(payload.source).toBe(`urn:workspace:${gw.gatewayId}`); // the Gateway's own report
      expect(payload.data).toMatchObject({
        assignmentId: `asg-${commandId}`,
        executionId,
        executionGeneration: 1,
        reason: "gateway-restart-lost",
        retryable: true,
      });

      // Zero re-execution: the fresh stub never saw a work.assign (the SDK
      // ledger answers duplicate to the redelivery); its only contact with
      // the execution was the reconcile status probe.
      expect(second.log()).toContain("reconcile: execution lost");
    } finally {
      await second.kill("SIGTERM");
    }
  });
});
