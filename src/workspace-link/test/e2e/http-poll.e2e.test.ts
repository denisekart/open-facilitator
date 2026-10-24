// E2E 6 — HTTP-poll fallback (plan step 6 item 6). A poll-session lives on a
// keeper SignalR handshake; deliveries arrive via GET /deliveries long-poll,
// receipts are plain POSTs. Frozen-surface laws proven here:
//   · a graceful stop sends goodbye → the session row dies → a bare GET is
//     404 `session-not-found` (no implicit re-bootstrap on the HTTP surface);
//   · recovery = a fresh keeper handshake, after which un-acked work flows
//     again (re-mint + drain).
import { describe, expect, it } from "vitest";
import { createStubHandler } from "../../src/index.js";
import { HttpPollTransport } from "../../src/node/index.js";
import {
  assignEnvelope,
  E2E,
  linkGet,
  linkRow,
  sdkClient,
  seedDelivery,
  useFacilitator,
  waitFor,
} from "./harness.js";

const h = useFacilitator();

describe.skipIf(!E2E)("http-poll transport", () => {
  it("polls deliveries, dies to 404 on goodbye, revives via keeper re-handshake", async () => {
    const endpointId = `e2e-poll-${Math.random().toString(36).slice(2, 8)}`;
    const a = await sdkClient(h, {
      endpointId,
      handler: createStubHandler(),
      transportFactory: (token: string) =>
        new HttpPollTransport(
          { baseUrl: h.baseUrl, waitSeconds: 2 },
          { getToken: async () => token },
        ),
    });
    expect(a.client.session?.protocolVersion).toBe("v1");

    // Seeded work arrives through the long-poll and is acked via POST receipt.
    const d1 = await seedDelivery(
      h.baseUrl,
      endpointId,
      assignEnvelope({
        commandId: `cmd-${Math.random().toString(36).slice(2, 8)}`,
        workId: "bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb",
        assignmentId: "cccccccc-cccc-4ccc-8ccc-cccccccccccc",
      }),
    );
    const row = await waitFor("d1 acked over poll", async () => {
      const r = await linkRow(h.dbUrl, d1);
      return r?.state === "acked" ? r : null;
    });
    expect(row.receiptStatus).toBe("accepted");
    expect(a.client.counters.processed).toBe(1);

    // With a live session, an idle GET answers 200 + [].
    const idle = await linkGet(h.baseUrl, "/api/link/v1/deliveries?wait=0", a.token);
    expect(idle.status).toBe(200);
    expect(idle.body).toEqual([]);

    // Graceful stop ⇒ goodbye ⇒ row deleted ⇒ frozen 404 rule on the surface.
    await a.client.stop("test-done");
    const gone = await linkGet(h.baseUrl, "/api/link/v1/deliveries?wait=0", a.token);
    expect(gone.status).toBe(404);
    expect((gone.body as { type: string }).type).toContain("session-not-found");

    // Recovery: a fresh client (same endpoint + token) re-handshakes the
    // keeper; new work flows over the revived session.
    const c = await sdkClient(h, {
      endpointId,
      token: a.token,
      handler: createStubHandler(),
      transportFactory: (token: string) =>
        new HttpPollTransport(
          { baseUrl: h.baseUrl, waitSeconds: 2 },
          { getToken: async () => token },
        ),
    });
    const d2 = await seedDelivery(
      h.baseUrl,
      endpointId,
      assignEnvelope({
        commandId: `cmd-${Math.random().toString(36).slice(2, 8)}`,
        workId: "dddddddd-dddd-4ddd-8ddd-dddddddddddd",
        assignmentId: "eeeeeeee-eeee-4eee-8eee-eeeeeeeeeeee",
      }),
    );
    await waitFor("d2 acked after revival", async () => {
      const r = await linkRow(h.dbUrl, d2);
      return r?.state === "acked" ? r : null;
    });
    expect(c.client.counters.processed).toBe(1);

    await c.client.stop("test-done");
  });
});
