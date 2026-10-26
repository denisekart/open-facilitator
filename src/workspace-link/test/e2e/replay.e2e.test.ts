// E2E 3 — crash replay (plan step 6 item 3, §26.1 item 2 client half).
// linkd (child process) + two deliveries; freeze the process (SIGSTOP) the
// instant d2 reaches the wire, hard-kill it (SIGKILL) before the handler can
// answer, restart with the SAME state file + same endpoint:
//   · d1 (acked pre-kill) never re-dispatches — no handler line post-restart;
//   · d2 (delivered, un-acked) re-arrives as a FRESH delivery (the frozen
//     process never persisted it) and completes cleanly.
// SIGSTOP instead of a racy "kill right after the POST returns": a frozen
// process keeps the socket open, so the server marks d2 in_flight and pushes
// it; the receipt can never arrive while frozen — the crash window is
// structural, not timed.
import { describe, expect, it } from "vitest";
import {
  assignEnvelope,
  E2E,
  linkRow,
  psql,
  manifestFile,
  makeManifest,
  seedDelivery,
  startLinkd,
  tempFile,
  useFacilitator,
  waitFor,
} from "./harness.js";

const h = useFacilitator({ InFlightLeaseSeconds: "3" });

describe.skipIf(!E2E)("linkd crash replay", () => {
  it("restarts from the state file and replays only the un-acked delivery", async () => {
    const endpointId = `e2e-replay-${Math.random().toString(36).slice(2, 8)}`;
    const state = tempFile("replay.sqlite");
    const manifest = manifestFile(makeManifest("team-alpha"));

    const first = startLinkd([
      "run",
      "--url",
      h.baseUrl,
      "--secret",
      "enroll-dev",
      "--endpoint",
      endpointId,
      "--team",
      "team-alpha",
      "--manifest",
      manifest,
      "--state",
      state,
      "--mode",
      "echo",
    ]);
    expect((await first.ready()).epoch).toBe(1);

    // d1: full round trip before any kill.
    const d1 = assignEnvelope({
      commandId: `cmd-a-${Math.random().toString(36).slice(2, 8)}`,
      workId: "33333333-3333-4333-8333-333333333333",
      assignmentId: "44444444-4444-4444-8444-444444444444",
    });
    const d1Id = await seedDelivery(h.baseUrl, endpointId, d1);
    await waitFor("d1 acked", async () => {
      const r = await linkRow(h.dbUrl, d1Id);
      return r?.state === "acked" ? r : null;
    });

    // d2: freeze FIRST, then seed — the frozen process keeps the socket
    // open, so the server marks d2 in_flight and pushes it, but the client
    // cannot read/persist/answer. The crash window is structural, not timed.
    first.child.kill("SIGSTOP");
    const d2 = assignEnvelope({
      commandId: `cmd-b-${Math.random().toString(36).slice(2, 8)}`,
      workId: "55555555-5555-4555-8555-555555555555",
      assignmentId: "66666666-6666-4666-8666-666666666666",
    });
    const d2Id = await seedDelivery(h.baseUrl, endpointId, d2);
    const row = await waitFor(
      "d2 in flight while frozen",
      async () => {
        const r = await linkRow(h.dbUrl, d2Id);
        return r && r.state === "in_flight" ? r : null;
      },
      15_000,
    );
    expect(row.receiptStatus).toBe("");
    first.child.kill("SIGKILL");
    await waitFor("linkd dead", () =>
      first.child.exitCode !== null || first.child.signalCode !== null ? true : null,
    );

    // Client ledger: d1 processed; d2 never even received — the frozen
    // process never read the frame, so nothing was persisted for it.
    const { SqliteLedger } = await import("../../src/node/index.js");
    const dead = new SqliteLedger(state);
    expect(dead.stats().processed).toBe(1);
    expect(dead.pending()).toEqual([]);
    expect(dead.processedDeliveries(10)).toEqual([d1Id]);
    dead.close();

    // Restart: same state file, same endpoint. The stale session row (no
    // goodbye) makes the re-hello bump the epoch 1 → 2; d2 is redelivered
    // (in-flight lease expiry or the hello-time drain — both paths push
    // un-acked rows; the drain is instant, so no lease wait).
    const second = startLinkd([
      "run",
      "--url",
      h.baseUrl,
      "--secret",
      "enroll-dev",
      "--endpoint",
      endpointId,
      "--team",
      "team-alpha",
      "--manifest",
      manifest,
      "--state",
      state,
      "--mode",
      "echo",
    ]);
    expect((await second.ready()).epoch).toBe(2);

    await waitFor("d2 acked after restart", async () => {
      const r = await linkRow(h.dbUrl, d2Id);
      return r?.state === "acked" ? r : null;
    });

    // Law: the acked work (d1) was NEVER re-dispatched post-restart. Its
    // row stayed acked/attempts=1 (below), and d2 completed FRESH: acked
    // with an `accepted` (not `duplicate`) receipt on attempt 2, plus the
    // two echo events for d2's subject in the kernel inbox.
    const d2Row = await linkRow(h.dbUrl, d2Id);
    expect(d2Row?.receiptStatus).toBe("accepted");
    expect(d2Row?.attempts).toBe(2);
    await waitFor("d2 events in inbox", async () => {
      const rows = await psql(
        h.dbUrl,
        `SELECT state FROM facilitator.inbox WHERE payload->>'subject' = '${d2.subject as string}' AND state = 'processed'`,
      );
      return rows.length === 2 ? rows : null;
    });

    // d1's row is untouched since the pre-kill ack.
    const after = await linkRow(h.dbUrl, d1Id);
    expect(after?.state).toBe("acked");
    expect(after?.attempts).toBe(1);

    await second.kill("SIGINT");
  }, 90_000);
});
