// E2E 2 — delivery pipeline against the real kernel (plan step 6 item 2).
// Seed work.assign via /api/dev/link-outbox ⇒ hub Deliver ⇒ echo handler
// accepts ⇒ receipt `acked` server-side; the two events the handler published
// reach the kernel inbox (`processed`); re-seeding the SAME envelope (fresh
// outbox row ⇒ fresh deliveryId, same dedupe identity) answers `duplicate`
// without re-invoking the handler (A:303-304, §26.1 item 3 client half).
import { describe, expect, it } from "vitest";
import { createEchoHandler, type LinkHandler } from "../../src/index.js";
import type { EnvelopeCommand, WorkAssignCommandV1Data } from "@ofac/protocol";
import {
  assignEnvelope,
  E2E,
  linkRow,
  psql,
  sdkClient,
  seedDelivery,
  useFacilitator,
  waitFor,
} from "./harness.js";

const h = useFacilitator();

describe.skipIf(!E2E)("link delivery pipeline", () => {
  it("delivers, acks, and dedupes a re-published command", async () => {
    const endpointId = `e2e-delivery-${Math.random().toString(36).slice(2, 8)}`;
    const seen: string[] = [];
    const handler: LinkHandler = {
      async onCommand(cmd: EnvelopeCommand, ctx) {
        const data = cmd.data as unknown as WorkAssignCommandV1Data;
        seen.push(data.commandId);
        await createEchoHandler({ source: `urn:workspace:linkd/${endpointId}` }).onCommand!(
          cmd,
          ctx,
        );
        return { kind: "accepted" };
      },
    };
    const a = await sdkClient(h, { endpointId, handler });

    // ── fresh delivery ────────────────────────────────────────────────────
    const envelope = assignEnvelope({
      commandId: `cmd-${Math.random().toString(36).slice(2, 8)}`,
      workId: "11111111-1111-4111-8111-111111111111",
      assignmentId: "22222222-2222-4222-8222-222222222222",
    });
    const outboxId = await seedDelivery(h.baseUrl, endpointId, envelope);

    await waitFor("d1 handled client-side", () => (seen.length === 1 ? true : null));
    expect(a.client.counters.delivered).toBe(1);
    expect(a.client.counters.duplicate).toBe(0);

    // Server-side: the link ledger row is acked with an `accepted` receipt,
    // and the kernel-side ack ledger saw the delivery.
    const row = await waitFor("d1 acked server-side", async () => {
      const r = await linkRow(h.dbUrl, outboxId);
      return r?.state === "acked" ? r : null;
    });
    expect(row.receiptStatus).toBe("accepted");
    expect(row.attempts).toBe(1);
    // Event envelope ids are generated client-side (buildEvent); correlate
    // by the subject the echo handler copied from the command envelope.
    await waitFor("events processed in inbox", async () => {
      const rows = await psql(
        h.dbUrl,
        `SELECT state FROM facilitator.inbox WHERE payload->>'subject' = '${envelope.subject as string}' AND state = 'processed'`,
      );
      return rows.length === 2 ? rows : null;
    });

    // ── duplicate: the SAME delivery row re-queued server-side ────────────
    // (The frozen kernel dedupes by command_id — a re-published envelope
    // can't even enter the outbox twice — so a redelivery always re-pushes
    // the original delivery id. Forcing the row back to pending drives the
    // exact redelivery path without waiting on a lost-receipt lease.)
    await psql(
      h.dbUrl,
      `UPDATE facilitator.link_deliveries SET state='pending', receipt=NULL, delivered_at=NULL WHERE delivery_id = '${outboxId}'`,
    );
    await waitFor("redelivery acked as duplicate", async () => {
      const r = await linkRow(h.dbUrl, outboxId);
      return r?.state === "acked" && r.receiptStatus === "duplicate" ? r : null;
    });
    expect(a.client.counters.duplicate).toBe(1);
    expect(a.client.counters.delivered).toBe(2);
    // Handler NOT re-invoked for the duplicate (client-side half of §26.1 item 3).
    expect(
      seen.filter((c) => c === (envelope.data as { commandId: string }).commandId),
    ).toHaveLength(1);
    const dupeRow = await linkRow(h.dbUrl, outboxId);
    expect(dupeRow?.receiptStatus).toBe("duplicate");
    expect(dupeRow?.attempts).toBe(2);

    await a.client.stop("test-done");
  });
});
