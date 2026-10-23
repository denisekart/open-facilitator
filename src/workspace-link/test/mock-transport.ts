// Shared mock transport for the client unit tier: scripted welcome, buffered
// sends, push emitters — mirrors FakeLinkClient's buffered-push pattern from
// the .NET harness (LinkTestHarness.cs).
import type { EnvelopeObject, LinkDeliveryV1, ReceiptV1 } from "@ofac/protocol";
import type {
  EnvelopeAck,
  FlowControlFrame,
  GoodbyeFrame,
  HelloFrame,
  LinkTransport,
  WelcomeFrame,
} from "../src/index.js";

export class MockTransport implements LinkTransport {
  connectCount = 0;
  hellos: HelloFrame[] = [];
  receipts: ReceiptV1[] = [];
  envelopes: EnvelopeObject[] = [];
  heartbeats: HelloFrame[] = [];
  goodbyes: GoodbyeFrame[] = [];
  closed = 0;
  welcome: WelcomeFrame | null = {
    kind: "welcome",
    sessionId: "sess-1",
    leaseEpoch: 1,
    maxInFlight: 16,
    heartbeatSeconds: 30,
    protocolVersion: "v1",
  };
  envelopeAck: EnvelopeAck = { status: "accepted", deliveryId: "inbox-1" };
  heartbeatAnswer = true;
  failNextConnectTimes = 0;

  private deliverCbs: ((d: LinkDeliveryV1) => void)[] = [];
  private flowCbs: ((f: FlowControlFrame) => void)[] = [];
  private disconnectCbs: ((g: GoodbyeFrame) => void)[] = [];

  async connect(): Promise<void> {
    if (this.failNextConnectTimes > 0) {
      this.failNextConnectTimes -= 1;
      throw new Error("simulated connect failure");
    }
    this.connectCount += 1;
  }

  async hello(frame: HelloFrame): Promise<WelcomeFrame | null> {
    this.hellos.push(frame);
    return this.welcome;
  }

  async envelope(env: EnvelopeObject): Promise<EnvelopeAck> {
    this.envelopes.push(env);
    return this.envelopeAck;
  }

  async receipt(r: ReceiptV1): Promise<void> {
    this.receipts.push(r);
  }

  async heartbeat(): Promise<boolean> {
    return this.heartbeatAnswer;
  }

  async goodbye(gb: GoodbyeFrame): Promise<void> {
    this.goodbyes.push(gb);
  }

  async close(): Promise<void> {
    this.closed += 1;
  }

  onDeliver(cb: (d: LinkDeliveryV1) => void): void {
    this.deliverCbs.push(cb);
  }

  onFlow(cb: (f: FlowControlFrame) => void): void {
    this.flowCbs.push(cb);
  }

  onDisconnect(cb: (g: GoodbyeFrame) => void): void {
    this.disconnectCbs.push(cb);
  }

  // ── test-side push emitters ──────────────────────────────────────────────

  pushDeliver(delivery: LinkDeliveryV1): void {
    for (const cb of this.deliverCbs) cb(delivery);
  }

  pushFlow(frame: FlowControlFrame): void {
    for (const cb of this.flowCbs) cb(frame);
  }

  pushDisconnect(frame: GoodbyeFrame): void {
    for (const cb of this.disconnectCbs) cb(frame);
  }
}

/** Microtask drain so fire-and-forget pipeline work lands deterministically. */
export async function flush(times = 4): Promise<void> {
  for (let i = 0; i < times; i += 1) await Promise.resolve();
}
