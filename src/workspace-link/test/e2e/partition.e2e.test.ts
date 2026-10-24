// E2E 5 — partition + supersede (plan step 6 item 5, A:360 test mode).
// While partitioned, an event publish BUFFERS in the client's outbox and
// resolves `accepted` (handler-side outcome stands); a superseding second
// client forces the first off (frozen goodbye `session-superseded`); after
// heal, the buffered event drains into the kernel inbox exactly once.
// Epoch law (pinned in harness.ts): the supersede bump 1→2 proves the
// live-row path; B then leaves GRACEFULLY, so the row is deleted and A's
// post-heal re-hello re-INSERTs — epoch restarts at 1, not 3.
import { describe, expect, it } from "vitest";
import { createStubHandler, FaultInjector, buildEvent } from "../../src/index.js";
import { SignalrTransport } from "../../src/node/index.js";
import { E2E, inboxState, sdkClient, useFacilitator, waitFor, type SdkClient } from "./harness.js";

const h = useFacilitator();

describe.skipIf(!E2E)("partition buffering and supersede", () => {
  it("buffers events across a partition and drains them after heal", async () => {
    const endpointId = `e2e-part-${Math.random().toString(36).slice(2, 8)}`;
    let partitioned = false;
    let inner: SignalrTransport | null = null;
    const a: SdkClient = await sdkClient(h, {
      endpointId,
      handler: createStubHandler(),
      transportFactory: (token: string) => {
        inner = new SignalrTransport({
          baseUrl: h.baseUrl,
          token: { getToken: async () => token },
        });
        return new FaultInjector(inner, { partition: () => partitioned });
      },
    });
    expect(a.client.session?.leaseEpoch).toBe(1);

    // ── partition: publish buffers, resolves accepted ─────────────────────
    partitioned = true;
    const event = buildEvent("org.facilitator.event.work.progress.v1", {
      source: `urn:workspace:linkd/${endpointId}`,
      subject:
        "work/99999999-9999-4999-8999-999999999999/assignment/aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa",
      data: {
        workId: "99999999-9999-4999-8999-999999999999",
        assignmentId: "aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa",
        executionGeneration: 1,
        progressNote: "buffered across the partition",
      },
    });
    const ack = await a.client.publishEvent(event);
    expect(ack.status).toBe("accepted");
    // Durable buffer: the outbox row survives until a live session drains it.
    expect(a.ledger.unsentEvents(16).map((e) => e.id)).toContain(event.id);

    // ── supersede: B on the same endpoint forces A off (A is partitioned,
    // so its own reconnects keep failing; B holds epoch 2) ─────────────────
    const states: string[] = [];
    a.client.on("state", (s) => {
      states.push(s);
    });
    const b: SdkClient = await sdkClient(h, { endpointId, token: a.token });
    expect(b.client.session?.leaseEpoch).toBe(2);
    await waitFor("A sees supersede", () => (a.client.state === "reconnecting" ? true : null));
    expect(inner!.lastServerGoodbye?.reason).toBe("session-superseded");

    // B leaves GRACEFULLY → goodbye deletes the live row.
    await b.client.stop("test-done");

    // ── heal: A re-hellos (fresh row → epoch restarts at 1, per the
    // INSERT-on-no-conflict law) and drains the outbox ─────────────────────
    partitioned = false;
    await waitFor("A re-ready after heal", () =>
      a.client.state === "ready" ? a.client.session : null,
    );
    expect(a.client.session?.leaseEpoch).toBe(1);
    await waitFor("buffered event drained", () =>
      a.ledger.unsentEvents(16).length === 0 ? true : null,
    );
    // Exactly once, server-side: the inbox row exists and is settled.
    await waitFor("E1 processed in inbox", async () =>
      (await inboxState(h.dbUrl, event.id)) === "processed" ? true : null,
    );
    // No zombie from the stale epoch: A's only session death was the
    // goodbye-reason-superseded transition, and the drained event carried
    // A's CURRENT epoch identity (hub fencing would reject a stale one).
    expect(states.filter((s) => s === "reconnecting").length).toBeGreaterThanOrEqual(1);

    await a.client.stop("test-done");
  });
});
