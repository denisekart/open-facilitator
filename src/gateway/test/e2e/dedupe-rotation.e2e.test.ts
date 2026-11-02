// E2E 3: the restart-law pair + token rotation.
// (a) GRACEFUL stop sends goodbye ⇒ the kernel session row is DELETED ⇒
//     the next hello re-INSERTs at epoch 1 (harness header law); the SDK
//     ledger in link.sqlite3 survived, so a server-forced REDELIVERY of the
//     original delivery answers `duplicate` and no new binding appears.
//     The kernel's law (delivery.e2e): the frozen kernel dedupes commands by
//     command_id (outbox_command_id_key UNIQUE), so a re-seed cannot mint a
//     second delivery — forcing the row back to `pending` via psql drives
//     the exact redelivery path (the pump's 1 s safety-net poll re-pushes).
// (b) As-built rotation law (conforms to reality; the premise "the
//     gateway's next send 401s" does NOT hold over SignalR):
//     · Hub auth runs at WebSocket-upgrade time only (LinkBearerHandler);
//       Context.User claims are FROZEN for the socket's lifetime and the
//       hub never re-checks the token store per invocation. So an
//       out-of-band re-enroll does not disturb a live session at all.
//     · When the session does die (lease revoked ⇒ Heartbeat false +
//       goodbye), the reconnect's upgrade-401 surfaces to mapHubError as a
//       generic "WebSocket failed to connect" (no statusCode, no "401" in
//       the text — proven against the dist/cjs transport), so
//       TransportError.unauthorized never fires and the SDK cannot detect
//       rotation mid-reconnect. Recorded SDK gap.
//     · Recovery law: a restart re-enrolls at startup (workload block
//       configured — mode (b)) and writes the token file BEFORE any
//       connect, then flows.
import { beforeAll, describe, expect, it } from "vitest";
import {
  E2E,
  TEAM,
  assignEnvelope,
  gatewayConfig,
  gatewayStatus,
  gatewayToken,
  linkRow,
  psql,
  seedDelivery,
  startGateway,
  useFacilitator,
  waitFor,
  type GatewayHandleConfig,
  type Harness,
} from "./gateway-harness.js";
import { enroll } from "../../../workspace-link/src/index.js";
import { workloadToken } from "../../../workspace-link/test/e2e/harness.js";

describe.skipIf(!E2E)("gateway dedupe + rotation e2e", () => {
  // Link__HeartbeatSeconds=5: the welcome-advertised lease cadence, so a
  // revoked session row is noticed (and goodbyed) fast in test (b).
  const harness: Harness = useFacilitator({ HeartbeatSeconds: "5" });
  let gw: GatewayHandleConfig;

  beforeAll(async () => {
    gw = await gatewayConfig({ facilitatorUrl: harness.baseUrl });
  });

  async function seedAssign(commandId: string): Promise<string> {
    return seedDelivery(
      harness.baseUrl,
      gw.gatewayId,
      assignEnvelope({
        commandId,
        workId: `work-${commandId}`,
        assignmentId: `asg-${commandId}`,
      }),
    );
  }

  // Receipt terminalisation law: the row settles `acked` (pending | in_flight
  // | acked is the frozen check constraint); receiptStatus carries the answer.
  async function waitAcked(outboxId: string): Promise<string> {
    const row = await waitFor("delivery acked", async () => {
      const r = await linkRow(harness.dbUrl, outboxId);
      return r !== null && r.state === "acked" ? r : false;
    });
    return row.receiptStatus;
  }

  async function waitCompleted(commandId: string): Promise<void> {
    await waitFor("assign completes", async () => {
      const s = await gatewayStatus(gw.diagnosticsPort);
      return s.bindings.some(
        (b) => b.assignmentId === `asg-${commandId}` && b.state === "completed",
      );
    });
  }

  it("(a) graceful restart: epoch restarts at 1, forced redelivery answers duplicate, no new binding", async () => {
    const first = startGateway(gw.configPath);
    const ready1 = await first.ready();
    expect(ready1.epoch).toBe(1);

    const commandId = `cmd-${Math.random().toString(36).slice(2, 10)}`;
    const outboxId = await seedAssign(commandId);
    expect(await waitAcked(outboxId)).toBe("accepted");
    await waitCompleted(commandId);

    // SIGTERM ⇒ goodbye ⇒ kernel DELETES the session row.
    const code = await first.kill("SIGTERM");
    expect(code).toBe(0);

    const second = startGateway(gw.configPath);
    try {
      const ready2 = await second.ready();
      expect(ready2.epoch).toBe(1); // re-INSERT law after a clean goodbye

      // Force the original row back to pending: the pump re-pushes the SAME
      // delivery id; the SDK ledger (link.sqlite3) survived the restart, so
      // the client answers duplicate without re-invoking the child.
      await psql(
        harness.dbUrl,
        `UPDATE facilitator.link_deliveries SET state='pending', receipt=NULL, delivered_at=NULL WHERE delivery_id = '${outboxId}'`,
      );
      expect(await waitAcked(outboxId)).toBe("duplicate");
      const dupeRow = await linkRow(harness.dbUrl, outboxId);
      expect(dupeRow?.attempts).toBe(2); // the kernel counts the redelivery

      // The Gateway's bindings ledger stays at the ONE original completed row.
      const status = await gatewayStatus(gw.diagnosticsPort);
      expect(status.bindings).toHaveLength(1);
      expect(status.executions.completed).toBe(1);
      expect(status.executions.running).toBe(0);
    } finally {
      await second.kill("SIGTERM");
    }
  });

  it("(b) rotation: live claims survive re-enroll; revoke forces a dead-token reconnect that cannot self-heal; a restart re-enrolls and flows", async () => {
    const gateway = startGateway(gw.configPath);
    try {
      await gateway.ready();
      const tokenBefore = gatewayToken(gw.stateDir);

      // Out-of-band mode-(b) re-enroll, same endpointId: a fresh OIDC CC
      // JWT for the bound client, the stored hash is replaced ⇒ the
      // gateway's token is dead for any NEW authentication.
      const rotated = await enroll({
        baseUrl: harness.baseUrl,
        token: await workloadToken(),
        endpointId: gw.gatewayId,
        instanceId: `rotate-${Math.random().toString(36).slice(2, 10)}`,
        trustMode: "shared-runtime",
        team: TEAM,
      });
      expect(rotated.linkToken).toBeDefined();
      expect(rotated.linkToken).not.toBe(tokenBefore);

      // Frozen-claims law: the live hub socket keeps serving — a fresh
      // assign completes even though the token behind it is already dead,
      // and no mid-session refresh oracle exists (token file unchanged).
      const flowing = `cmd-${Math.random().toString(36).slice(2, 10)}`;
      await seedAssign(flowing);
      await waitCompleted(flowing);
      expect(gatewayToken(gw.stateDir)).toBe(tokenBefore);

      // Revoke the session lease server-side: the next hub Heartbeat finds
      // no matching row ⇒ false + goodbye(session-superseded) ⇒ the SDK
      // supervises a reconnect, whose upgrade 401 (dead token) is
      // indistinguishable from a network failure ⇒ retry loop, no refresh.
      await psql(
        harness.dbUrl,
        `DELETE FROM facilitator.link_sessions WHERE logical_endpoint_id = '${gw.gatewayId}'`,
      );
      await waitFor(
        "session down, reconnect retrying",
        () => gateway.log().includes("link session down; retrying") || false,
        30_000,
      );
      await new Promise((r) => setTimeout(r, 2_000)); // more backoff lapses
      expect(gatewayToken(gw.stateDir)).toBe(tokenBefore); // no self-heal
      const down = await gatewayStatus(gw.diagnosticsPort);
      expect(down.session?.state).toBe("reconnecting");

      // Recovery law: a restart re-enrolls at startup (secret configured) —
      // the token file is rewritten BEFORE any connect — and delivery flows.
      await gateway.kill("SIGKILL");
      const revived = startGateway(gw.configPath);
      try {
        await revived.ready();
        const tokenAfter = gatewayToken(gw.stateDir);
        expect(tokenAfter).not.toBe(tokenBefore);

        const commandId = `cmd-${Math.random().toString(36).slice(2, 10)}`;
        const outboxId = await seedAssign(commandId);
        await waitCompleted(commandId);
        expect(await waitAcked(outboxId)).toBe("accepted");
      } finally {
        await revived.kill("SIGTERM");
      }
    } finally {
      await gateway.kill("SIGKILL").catch(() => undefined);
    }
  });
});
