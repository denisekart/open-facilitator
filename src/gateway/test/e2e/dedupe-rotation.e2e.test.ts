// E2E 3: the restart-law pair + token rotation.
// (a) GRACEFUL stop sends goodbye ⇒ the kernel session row is DELETED ⇒
//     the next hello re-INSERTs at epoch 1 (harness header law); the SDK
//     ledger in link.sqlite3 survived, so a server-forced REDELIVERY of the
//     original delivery answers `duplicate` and no new binding appears.
//     The kernel's law (delivery.e2e): the frozen kernel dedupes commands by
//     command_id (outbox_command_id_key UNIQUE), so a re-seed cannot mint a
//     second delivery — forcing the row back to `pending` via psql drives
//     the exact redelivery path (the pump's 1 s safety-net poll re-pushes).
// (b) Credential-law pair, rewritten for the session-JWT model:
//     · Hub auth runs at WebSocket-upgrade time only (LinkBearerHandler);
//       Context.User claims are FROZEN for the socket's lifetime. An
//       out-of-band re-enroll therefore does not disturb a live session:
//       there is no shared long-lived bearer left to invalidate.
//     · When the SESSION dies (lease row deleted ⇒ Heartbeat false +
//       goodbye), the reconnect re-PROVES and self-heals with no restart:
//       the credential is re-derivable from the key/workload secret, which
//       is exactly what the deleted bearer could not do. This is the
//       behaviour the old token model could only reach by restarting.
import { beforeAll, describe, expect, it } from "vitest";
import {
  E2E,
  TEAM,
  assignEnvelope,
  gatewayConfig,
  gatewayStatus,
  gatewayIdentity,
  gatewaySession,
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

  it("(b) sessions: live claims survive re-enroll; a revoked session self-heals by re-proving; a restart re-proves too", async () => {
    const gateway = startGateway(gw.configPath);
    try {
      await gateway.ready();
      const keyBefore = gatewayIdentity(gw.stateDir);
      // Out-of-band mode-(b) re-enroll, same endpointId: a fresh OIDC CC JWT for
      // the bound client. Nothing about the live session changes — no bearer is
      // shared between them any more.
      const rotated = await enroll({
        baseUrl: harness.baseUrl,
        token: await workloadToken(),
        endpointId: gw.gatewayId,
        instanceId: `rotate-${Math.random().toString(36).slice(2, 10)}`,
        trustMode: "shared-runtime",
        team: TEAM,
        publicKey: keyBefore.publicKeyBase64Url,
        keyAlgorithm: "ed25519",
      });
      expect(rotated.endpointKeyId).toBeTruthy();
      expect(rotated.seq).toBeGreaterThanOrEqual(1);

      // Frozen-claims law: the live hub socket keeps serving across the
      // re-enroll — a fresh assign completes with no reconnect at all.
      const flowing = `cmd-${Math.random().toString(36).slice(2, 10)}`;
      await seedAssign(flowing);
      await waitCompleted(flowing);
      expect(gatewayIdentity(gw.stateDir).keyId).toBe(keyBefore.keyId);

      // Kill the session row server-side: the next hub Heartbeat finds no
      // matching row ⇒ false + goodbye(session-superseded) ⇒ the SDK reconnects.
      // The reconnect now re-mints a session from the workload credential, so it
      // SELF-HEALS — the recovery the deleted bearer model needed a restart for.
      await psql(
        harness.dbUrl,
        `DELETE FROM facilitator.link_sessions WHERE logical_endpoint_id = '${gw.gatewayId}'`,
      );
      const commandId = `cmd-${Math.random().toString(36).slice(2, 10)}`;
      const outboxId = await seedAssign(commandId);
      await waitCompleted(commandId);
      expect(await waitAcked(outboxId)).toBe("accepted");
      const up = await gatewayStatus(gw.diagnosticsPort);
      expect(up.session?.state).toBe("connected");

      // A restart re-proves with the same key (the identity file is unchanged)
      // and flows: no enrollment code, no stored bearer, no rotation needed.
      await gateway.kill("SIGKILL");
      const revived = startGateway(gw.configPath);
      try {
        await revived.ready();
        expect(gatewayIdentity(gw.stateDir).keyId).toBe(keyBefore.keyId);
        // The harness can mint its own session from the same identity file:
        // the key in that directory IS the endpoint's credential.
        const session = await gatewaySession(gw.stateDir, harness.baseUrl, gw.gatewayId);
        expect(session.split(".")).toHaveLength(3);
        const second = `cmd-${Math.random().toString(36).slice(2, 10)}`;
        const outbox2 = await seedAssign(second);
        await waitCompleted(second);
        expect(await waitAcked(outbox2)).toBe("accepted");
      } finally {
        await revived.kill("SIGTERM");
      }
    } finally {
      await gateway.kill("SIGKILL").catch(() => undefined);
    }
  });
});
