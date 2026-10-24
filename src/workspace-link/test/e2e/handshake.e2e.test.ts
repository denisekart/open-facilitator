// E2E 1 — handshake (plan step 6 item 1). enroll → capabilities POST →
// hello ⇒ welcome; capabilities GET returns the stored manifest; a
// superseding hello over a LIVE session row bumps the epoch (law pinned in
// harness.ts). Divergence from the plan wording ("reconnect ⇒ leaseEpoch:2"):
// the bump happens on ANY hello over a live row — proven here by client B —
// while the first hello after a graceful goodbye re-INSERTs and restarts at 1.
import { describe, expect, it } from "vitest";
import { enroll } from "../../src/index.js";
import { ENROLL_SECRET, E2E, linkGet, sdkClient, useFacilitator, waitFor } from "./harness.js";

const h = useFacilitator();

describe.skipIf(!E2E)("link handshake", () => {
  it("welcomes a fresh enrollment, stores capabilities, bumps on supersede", async () => {
    const endpointId = `e2e-handshake-${Math.random().toString(36).slice(2, 8)}`;
    const a = await sdkClient(h, { endpointId });

    // Welcome frame: reference-server defaults (LinkOptions).
    expect(a.client.session).toMatchObject({
      protocolVersion: "v1",
      leaseEpoch: 1,
      maxInFlight: 16,
      heartbeatSeconds: 30,
    });

    // An endpoint that enrolled but never POSTed gets capabilities-not-found.
    const bare = await enroll({
      baseUrl: h.baseUrl,
      token: ENROLL_SECRET,
      endpointId: `${endpointId}-bare`,
      instanceId: "e2e-bare",
      trustMode: "shared-runtime",
    });
    const missing = await linkGet(h.baseUrl, "/api/link/v1/capabilities", bare.linkToken ?? "");
    expect(missing.status).toBe(404);
    expect((missing.body as { type: string }).type).toContain("capabilities-not-found");

    // GET /capabilities returns the stored manifest, structurally identical
    // to the POSTed bytes. (Stored in Postgres jsonb, which normalizes key
    // order — hence canonical re-serialization, not raw byte equality.)
    const got = await linkGet(h.baseUrl, "/api/link/v1/capabilities", a.token);
    expect(got.status).toBe(200);
    const canonical = (v: unknown) => JSON.stringify(JSON.parse(JSON.stringify(v)), stableReplacer);
    expect(canonical(got.body)).toBe(canonical(JSON.parse(a.manifestText)));

    // Supersede: a second client on the SAME endpoint + token forces the
    // first off, and the still-live row's epoch bumps 1 → 2.
    const states: string[] = [];
    a.client.on("state", (s) => {
      states.push(s);
    });
    const b = await sdkClient(h, { endpointId, token: a.token });
    expect(b.client.session?.leaseEpoch).toBe(2);
    await waitFor("A superseded", () => a.client.state === "reconnecting" || null);
    // A's supervisor parks in a ≥1 s backoff (client.ts supervisorLoop); the
    // goodbye it received is the frozen session-superseded one.
    const aLost = JSON.stringify(states);
    expect(aLost).toContain("reconnecting");

    // B leaves gracefully: goodbye deletes the row (epoch-matched). A's
    // re-hello therefore re-INSERTs — fresh row, epoch restarts at 1.
    await b.client.stop("test-done");
    await waitFor("A re-ready", () => (a.client.state === "ready" ? a.client.session : null));
    expect(a.client.session?.leaseEpoch).toBe(1);

    await a.client.stop("test-done");
  });
});

function stableReplacer(_key: string, value: unknown): unknown {
  if (value !== null && typeof value === "object" && !Array.isArray(value)) {
    const out: Record<string, unknown> = {};
    for (const k of Object.keys(value as Record<string, unknown>).sort()) {
      out[k] = (value as Record<string, unknown>)[k];
    }
    return out;
  }
  return value;
}
