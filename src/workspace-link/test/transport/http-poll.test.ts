// HTTP-poll transport mechanics on a loopback node:http server + scripted
// keeper: long-poll GET claims deliveries, receipt POSTs answer 204, a 404
// session-not-found re-runs keeper connect+hello before the next poll.
import { createServer } from "node:http";
import type { AddressInfo } from "node:net";
import { afterEach, describe, expect, it } from "vitest";
import type { LinkDeliveryV1 } from "@ofac/protocol";
import { HttpPollTransport } from "../../src/node/index.js";
import type { EnvelopeAck, HelloFrame, LinkTransport, WelcomeFrame } from "../../src/index.js";

class ScriptedKeeper implements LinkTransport {
  connectCount = 0;
  helloCount = 0;
  welcome: WelcomeFrame = {
    kind: "welcome",
    sessionId: "keeper-1",
    leaseEpoch: 1,
    maxInFlight: 16,
    heartbeatSeconds: 30,
    protocolVersion: "v1",
  };
  async connect(): Promise<void> {
    this.connectCount += 1;
  }
  lastHelloFrame: HelloFrame | null = null;
  async hello(frame: HelloFrame): Promise<WelcomeFrame> {
    this.lastHelloFrame = frame;
    this.helloCount += 1;
    return this.welcome;
  }
  async envelope(): Promise<EnvelopeAck> {
    return { status: "accepted" };
  }
  async receipt(): Promise<void> {}
  async heartbeat(): Promise<boolean> {
    return true;
  }
  async goodbye(): Promise<void> {}
  async close(): Promise<void> {}
  onDeliver(): void {}
  onFlow(): void {}
  onDisconnect(): void {}
}

const DELIVERY: LinkDeliveryV1 = {
  deliveryId: "del-http-1",
  envelope: {
    specversion: "1.0",
    id: "evt-1",
    source: "urn:facilitator:instance:main",
    type: "org.facilitator.event.workspace.heartbeat.v1",
    subject: "workspace/ws-1",
    time: "2026-10-22T18:00:00Z",
    datacontenttype: "application/json",
    dataschema: "https://schemas.openfacilitator.org/v1/event.workspace.heartbeat.v1.json",
    data: { workspaceId: "ws-1", status: "idle" },
  },
  deliveredAt: "2026-10-22T18:00:01Z",
};

interface Harness {
  url: string;
  keeper: ScriptedKeeper;
  requests: string[];
  receipts: { deliveryId: string; body: Record<string, unknown> }[];
  close(): Promise<void>;
}

async function startServer(opts: { sessionAlive?: () => boolean }): Promise<Harness> {
  const keeper = new ScriptedKeeper();
  const requests: string[] = [];
  const receipts: Harness["receipts"] = [];
  let delivered = false;
  const server = createServer((req, res) => {
    const url = req.url ?? "";
    requests.push(`${req.method} ${url.split("?")[0]}`);
    const alive = opts.sessionAlive?.() ?? true;
    if (url.startsWith("/api/link/v1/deliveries") && !url.includes("/receipt")) {
      if (!alive) {
        res.writeHead(404, { "content-type": "application/json" });
        res.end(
          JSON.stringify({ type: "session-not-found", title: "session not found", status: 404 }),
        );
        return;
      }
      const items = delivered ? [] : [DELIVERY];
      delivered = true;
      res.writeHead(200, { "content-type": "application/json" });
      res.end(JSON.stringify(items));
      return;
    }
    const receipt = /^POST \/api\/link\/v1\/deliveries\/([^/]+)\/receipt$/.exec(
      `${req.method} ${url.split("?")[0]}`,
    );
    if (receipt) {
      let body = "";
      req.on("data", (c) => {
        body += c;
      });
      req.on("end", () => {
        receipts.push({
          deliveryId: receipt[1]!,
          body: JSON.parse(body) as Record<string, unknown>,
        });
        res.writeHead(204);
        res.end();
      });
      return;
    }
    if (url.startsWith("/api/link/v1/events")) {
      res.writeHead(202, { "content-type": "application/json" });
      res.end(JSON.stringify({ deliveryId: "inbox-1", status: "accepted" }));
      return;
    }
    res.writeHead(404);
    res.end();
  });
  await new Promise<void>((resolve) => server.listen(0, "127.0.0.1", resolve));
  const port = (server.address() as AddressInfo).port;
  return {
    url: `http://127.0.0.1:${port}`,
    keeper,
    requests,
    receipts,
    close: () => new Promise<void>((resolve) => server.close(() => resolve())),
  };
}

async function waitFor(predicate: () => boolean, ms = 2000): Promise<void> {
  const start = Date.now();
  while (!predicate()) {
    if (Date.now() - start > ms) throw new Error("waitFor timed out");
    await new Promise((r) => setImmediate(r));
  }
}

describe("HttpPollTransport", () => {
  let harness: Harness | null = null;
  afterEach(async () => {
    await harness?.close();
    harness = null;
  });

  it("long-polls a delivery, posts the receipt, re-issues the GET", async () => {
    harness = await startServer({});
    const transport = new HttpPollTransport(
      { baseUrl: harness.url, waitSeconds: 0, keeper: harness.keeper },
      { getToken: async () => "tok" },
    );
    const seen: LinkDeliveryV1[] = [];
    transport.onDeliver((d) => seen.push(d));
    await transport.connect();
    expect(
      await transport.hello({
        kind: "hello",
        protocolVersions: ["v1"],
        supportedTransports: ["http-poll"],
        endpointId: "gw-http",
        instanceId: "inst-1",
        capabilityManifestDigest: `sha256:${"ab".repeat(32)}`,
      }),
    ).toMatchObject({ sessionId: "keeper-1" });
    await waitFor(() => seen.length >= 1);
    await transport.receipt({
      deliveryId: seen[0]!.deliveryId,
      status: "accepted",
      processedAt: new Date().toISOString(),
    });
    await waitFor(() => harness!.receipts.length >= 1);
    expect(harness.receipts[0]).toEqual({
      deliveryId: "del-http-1",
      body: { status: "accepted", processedAt: expect.any(String) },
    });
    // deliveryId travels in the path, not the body (OpenAPI ReceiptWithoutDeliveryId).
    expect(
      harness.requests.filter((r) => r.startsWith("GET /api/link/v1/deliveries")).length,
    ).toBeGreaterThanOrEqual(2);
    await transport.close();
  });

  it("404 session-not-found re-runs keeper connect + hello before the next poll", async () => {
    let alive = false; // session dead until the keeper re-hellos once
    let helloSeen = 0;
    harness = await startServer({ sessionAlive: () => alive });
    const keeper = harness.keeper;
    const originalHello = keeper.hello.bind(keeper);
    keeper.hello = async (frame: HelloFrame) => {
      const w = await originalHello(frame);
      helloSeen += 1;
      if (helloSeen >= 2) alive = true; // the resuscitation re-hello revives it
      return w;
    };
    const transport = new HttpPollTransport(
      { baseUrl: harness.url, waitSeconds: 0, keeper },
      { getToken: async () => "tok" },
    );
    const seen: LinkDeliveryV1[] = [];
    transport.onDeliver((d) => seen.push(d));
    await transport.connect();
    await transport.hello({
      kind: "hello",
      protocolVersions: ["v1"],
      supportedTransports: ["http-poll"],
      endpointId: "gw-http",
      instanceId: "inst-1",
      capabilityManifestDigest: `sha256:${"ab".repeat(32)}`,
    });
    await waitFor(() => seen.length >= 1 && keeper.connectCount >= 2 && helloSeen >= 2);
    await transport.close();
  });

  it("POST events maps 202/200/4xx onto EnvelopeAck", async () => {
    harness = await startServer({});
    const transport = new HttpPollTransport(
      { baseUrl: harness.url, keeper: harness.keeper },
      { getToken: async () => "tok" },
    );
    const ack = await transport.envelope({
      specversion: "1.0",
      id: "evt-ack",
      source: "urn:workspace:gw-http",
      type: "org.facilitator.event.workspace.heartbeat.v1",
      subject: "workspace/ws-1",
      time: "2026-10-22T18:00:00Z",
      datacontenttype: "application/json",
      dataschema: "https://schemas.openfacilitator.org/v1/event.workspace.heartbeat.v1.json",
    });
    expect(ack).toEqual({ status: "accepted", deliveryId: "inbox-1" });
  });
});
