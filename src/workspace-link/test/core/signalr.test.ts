// The SignalR transport against a fake HubConnection: no live hub, no socket.
// Covers the hub URL + advertised transports, the connect/rebuild rules, the
// hello→serverTimeout narrowing, the null-welcome terminal answer, every
// server-method error mapped onto TransportError (with 401 flagged for the
// re-prove path), and the push handlers registered BEFORE start.
import { beforeEach, describe, expect, it, vi } from "vitest";
import type { EnvelopeObject, LinkDeliveryV1, ReceiptV1 } from "@ofac/protocol";
import { SignalrTransport, mapHubError } from "../../src/node/signalr.js";
import {
  TransportError,
  type EnvelopeAck,
  type FlowControlFrame,
  type GoodbyeFrame,
  type HelloFrame,
  type WelcomeFrame,
} from "../../src/index.js";
import { builderUrls, fakes, hubs, liveHub, resetFakes, type FakeHub } from "./fake-signalr.js";

// The module namespace the transport imports, replaced by the hand-rolled one.
vi.mock("@microsoft/signalr", () => import("./fake-signalr.js").then((m) => m.fakeSignalr));

function hub(): FakeHub {
  return liveHub();
}

// ── fixtures ───────────────────────────────────────────────────────────────

const HELLO: HelloFrame = {
  kind: "hello",
  endpointId: "gw-1",
  instanceId: "lnk-test-1",
  protocolVersions: ["v1"],
  capabilityManifestDigest: `sha256:${"ab".repeat(32)}`,
  supportedTransports: ["signalr", "http-poll"],
};

const WELCOME: WelcomeFrame = {
  kind: "welcome",
  sessionId: "sess-1",
  leaseEpoch: 3,
  maxInFlight: 16,
  heartbeatSeconds: 20,
  protocolVersion: "v1",
};

const GOODBYE: GoodbyeFrame = { kind: "goodbye", sessionId: "sess-1", reason: "client-shutdown" };

const HEARTBEAT = { kind: "heartbeat", sessionId: "sess-1", leaseEpoch: 3 } as const;

const ENVELOPE = { specversion: "1.0", id: "e1", type: "t", source: "s" } as EnvelopeObject;

function transportUnderTest(): SignalrTransport {
  return new SignalrTransport({
    baseUrl: "https://kernel.test/",
    token: { getToken: async () => "jwt" },
  });
}

beforeEach(() => {
  resetFakes();
});

describe("SignalrTransport construction", () => {
  it("dials /hubs/link with the bearer seam and advertises both transports", () => {
    const t = transportUnderTest();
    expect(t.hubUrl).toBe("https://kernel.test/hubs/link");
    expect(t.supportedTransports).toEqual(["signalr", "http-poll"]);
    expect(builderUrls).toEqual(["https://kernel.test/hubs/link"]);
    // The pre-negotiation floor: an idle link must not die inside the first
    // heartbeat window.
    expect(hubs).toHaveLength(1); // the constructor builds exactly one
    expect(hub().serverTimeoutInMilliseconds).toBe(900_000);
  });

  it("registers the push handlers before any connect (a Deliver can ride the hello)", () => {
    const t = transportUnderTest();
    const deliveries: LinkDeliveryV1[] = [];
    const goodbyes: GoodbyeFrame[] = [];
    const flows: FlowControlFrame[] = [];
    t.onDeliver((d) => deliveries.push(d));
    t.onFlow((f) => flows.push(f));
    t.onDisconnect((g) => goodbyes.push(g));

    const delivery: LinkDeliveryV1 = {
      deliveryId: "d1",
      envelope: ENVELOPE,
      deliveredAt: "2026-10-22T18:00:00Z",
    };
    hub().handlers.get("Deliver")?.(delivery);
    hub().handlers.get("FlowControl")?.({
      kind: "flow_control",
      sessionId: "sess-1",
      maxInFlight: 4,
    });
    hub().handlers.get("Disconnect")?.(GOODBYE);

    expect(deliveries).toEqual([delivery]);
    expect(flows).toEqual([{ kind: "flow_control", sessionId: "sess-1", maxInFlight: 4 }]);
    expect(goodbyes).toEqual([GOODBYE]);
    // A server-pushed goodbye is remembered: policy drop vs network loss.
    expect(t.lastServerGoodbye).toEqual(GOODBYE);
  });
});

describe("connect", () => {
  it("starts a disconnected connection and is a no-op when already connected", async () => {
    const t = transportUnderTest();
    await t.connect();
    expect(hub().starts).toBe(1);
    await t.connect(); // state === Connected → no second start, no rebuild
    expect(hub().starts).toBe(1);
    expect(hubs).toHaveLength(2); // constructor's hub + the one connect rebuilt
  });

  it("rebuilds the socket after a stop (a stopped SignalR connection cannot restart)", async () => {
    const t = transportUnderTest();
    await t.connect();
    const live = hub();
    await t.close();
    expect(live.stops).toBe(1);
    await t.connect();
    expect(hubs).toHaveLength(3); // a fresh connection, handlers re-registered
    expect(hub()).not.toBe(live);
    expect(hub().starts).toBe(1);
    expect(hub().handlers.has("Deliver")).toBe(true);
  });

  it("maps a failed connect onto a TransportError, flagging an upgrade 401", async () => {
    const t = transportUnderTest();
    fakes.nextStartError = new Error("WebSocket failed with 401");
    const error = await t.connect().catch((e: unknown) => e);
    expect(error).toBeInstanceOf(TransportError);
    expect((error as TransportError).message).toContain("WebSocket failed with 401");
    expect((error as TransportError).unauthorized).toBe(true);
  });
});

describe("hello", () => {
  it("narrows the server timeout to 3× the negotiated cadence", async () => {
    const t = transportUnderTest();
    await t.connect();
    const h = hub();
    h.invokeResults.set("Hello", WELCOME);
    expect(await t.hello(HELLO)).toEqual(WELCOME);
    expect(h.invokes[0]).toEqual({ method: "Hello", arg: HELLO });
    expect(h.serverTimeoutInMilliseconds).toBe(20 * 3_000);
  });

  it("passes a null welcome through (the terminal policy answer)", async () => {
    const t = transportUnderTest();
    await t.connect();
    const h = hub();
    h.invokeResults.set("Hello", null);
    expect(await t.hello(HELLO)).toBeNull();
    expect(h.serverTimeoutInMilliseconds).toBe(900_000); // untouched by a null welcome
  });

  it("maps a hello failure onto TransportError", async () => {
    const t = transportUnderTest();
    await t.connect();
    const h = hub();
    h.invokeErrors.set("Hello", new Error("hub offline"));
    await expect(t.hello(HELLO)).rejects.toThrow("hub offline");
  });
});

describe("the outbound server methods", () => {
  it("envelope returns the ack and maps a 401 refusal", async () => {
    const t = transportUnderTest();
    await t.connect();
    const h = hub();
    const ack: EnvelopeAck = { status: "accepted", deliveryId: "inbox-9" };
    h.invokeResults.set("Envelope", ack);
    expect(await t.envelope(ENVELOPE)).toEqual(ack);
    h.invokeResults.delete("Envelope");
    h.invokeErrors.set("Envelope", Object.assign(new Error("refused"), { statusCode: 401 }));
    const error = await t.envelope(ENVELOPE).catch((e: unknown) => e);
    expect(error).toBeInstanceOf(TransportError);
    expect((error as TransportError).unauthorized).toBe(true);
    expect((error as TransportError).problem).toEqual({ status: 401 });
  });

  it("receipt rides the Receipt method", async () => {
    const t = transportUnderTest();
    await t.connect();
    const h = hub();
    const receipt: ReceiptV1 = {
      deliveryId: "del-1",
      status: "accepted",
      processedAt: "2026-10-22T18:00:02Z",
    };
    await t.receipt(receipt);
    expect(h.invokes.at(-1)).toEqual({ method: "Receipt", arg: receipt });
    h.invokeErrors.set("Receipt", new Error("nope"));
    await expect(t.receipt(receipt)).rejects.toThrow("nope");
  });

  it("heartbeat answers the hub's boolean and maps failures", async () => {
    const t = transportUnderTest();
    await t.connect();
    const h = hub();
    h.invokeResults.set("Heartbeat", false);
    expect(await t.heartbeat(HEARTBEAT)).toBe(false);
    h.invokeErrors.set("Heartbeat", new Error("socket gone"));
    await expect(t.heartbeat(HEARTBEAT)).rejects.toThrow("socket gone");
  });

  it("goodbye ships the frame and close stops the connection", async () => {
    const t = transportUnderTest();
    await t.connect();
    const h = hub();
    await t.goodbye(GOODBYE);
    expect(h.invokes.at(-1)).toEqual({ method: "Goodbye", arg: GOODBYE });
    await t.close();
    expect(h.stops).toBe(1);
  });

  it("close swallows a stop failure (the socket is already gone)", async () => {
    const t = transportUnderTest();
    await t.connect();
    const h = hub();
    h.stop = async () => {
      throw new Error("already stopped");
    };
    await expect(t.close()).resolves.toBeUndefined();
  });
});

describe("mapHubError", () => {
  it("flags 401 from a statusCode or from the message", () => {
    const withStatus = mapHubError(
      Object.assign(new Error("gateway said no"), { statusCode: 401 }),
    );
    expect(withStatus.unauthorized).toBe(true);
    expect(withStatus.problem).toEqual({ status: 401 });
    expect(mapHubError(new Error("Unexpected status code '401'")).unauthorized).toBe(true);
    expect(mapHubError("Error: Unauthorized").unauthorized).toBe(true);
    const other = mapHubError(Object.assign(new Error("nope"), { statusCode: 503 }));
    expect(other.unauthorized).toBe(false);
    expect(other.problem).toEqual({ status: 503 });
  });

  it("wraps a non-Error failure as a plain transport error", () => {
    const error = mapHubError({ toString: () => "weird" });
    expect(error).toBeInstanceOf(TransportError);
    expect(error.message).toBe("weird");
    expect(error.unauthorized).toBe(false);
    expect(error.problem).toBeUndefined();
  });
});
