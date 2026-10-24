// E2E 4 — receipt loss (plan step 6 item 4, S:302, A:156). Client drops the
// receipt for delivery X ⇒ the server's in-flight lease (shortened to 2 s
// for this file's server) expires ⇒ the sweeper releases X to pending ⇒
// re-claim re-pushes (attempts=2) ⇒ the client's ledger answers `duplicate`
// WITHOUT re-running the handler ⇒ the server acks and the loop terminates.
import { describe, expect, it } from "vitest";
import { FaultInjector, type LinkHandler } from "../../src/index.js";
import type { EnvelopeCommand, WorkAssignCommandV1Data } from "@ofac/protocol";
import { SignalrTransport } from "../../src/node/index.js";
import {
  assignEnvelope,
  E2E,
  linkRow,
  sdkClient,
  seedDelivery,
  useFacilitator,
  waitFor,
} from "./harness.js";

const h = useFacilitator({ InFlightLeaseSeconds: "2" });

describe.skipIf(!E2E)("receipt loss redelivery", () => {
  it("redelivers after a lost receipt and terminates on duplicate", async () => {
    const endpointId = `e2e-rloss-${Math.random().toString(36).slice(2, 8)}`;
    let handlerRuns = 0;
    const counting: LinkHandler = {
      async onCommand(cmd: EnvelopeCommand) {
        void (cmd.data as unknown as WorkAssignCommandV1Data);
        handlerRuns += 1;
        return { kind: "accepted" };
      },
    };
    // Drop exactly the FIRST receipt (this file has one delivery), armed
    // from construction so no race against the push.
    let drop = true;
    const a = await sdkClient(h, {
      endpointId,
      handler: counting,
      transportFactory: (token: string) =>
        new FaultInjector(
          new SignalrTransport({ baseUrl: h.baseUrl, token: { getToken: async () => token } }),
          {
            dropReceipt: () => {
              if (drop) {
                drop = false;
                return true;
              }
              return false;
            },
          },
        ),
    });

    const envelope = assignEnvelope({
      commandId: `cmd-${Math.random().toString(36).slice(2, 8)}`,
      workId: "77777777-7777-4777-8777-777777777777",
      assignmentId: "88888888-8888-4888-8888-888888888888",
    });
    const d1 = await seedDelivery(h.baseUrl, endpointId, envelope);

    // Loop must terminate: row reaches acked with a duplicate receipt and
    // attempts=2 (delivered exactly once more than the first attempt).
    const row = await waitFor(
      "d1 acked via duplicate after redelivery",
      async () => {
        const r = await linkRow(h.dbUrl, d1);
        return r?.state === "acked" && r.receiptStatus === "duplicate" ? r : null;
      },
      30_000,
    );
    expect(row.attempts).toBe(2);
    // The handler ran EXACTLY once despite the redelivery (ledger law).
    expect(handlerRuns).toBe(1);
    expect(a.client.counters.duplicate).toBe(1);
    expect(a.client.counters.processed).toBe(1);

    // Terminal state: nothing left to redeliver.
    const after = await linkRow(h.dbUrl, d1);
    expect(after?.state).toBe("acked");

    await a.client.stop("test-done");
  });
});
