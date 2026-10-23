// Test mode (appendix §12.3 / A:360): a transport wrapper with explicit
// policy callbacks — dropped receipts, network partitions, duplicate
// deliveries. No env-var magic: the consumer (or a test) composes the faults.
import type { EnvelopeObject, LinkDeliveryV1, ReceiptV1 } from "@ofac/protocol";
import { TransportError } from "./errors.js";
import type {
  EnvelopeAck,
  FlowControlFrame,
  GoodbyeFrame,
  HeartbeatFrame,
  HelloFrame,
  LinkTransport,
  WelcomeFrame,
} from "./types.js";

export interface FaultPolicy {
  /** Return true to swallow the receipt send (server will redeliver). */
  dropReceipt?: (deliveryId: string) => boolean;
  /** Return true while the network is "down": every send fails. */
  partition?: () => boolean;
  /** Return true to replay a Deliver push a second time (server redelivery sim). */
  duplicateDeliver?: (deliveryId: string) => boolean;
}

export class FaultInjector implements LinkTransport {
  private deliverCbs: ((d: LinkDeliveryV1) => void)[] = [];
  private flowCbs: ((f: FlowControlFrame) => void)[] = [];
  private disconnectCbs: ((g: GoodbyeFrame) => void)[] = [];

  constructor(
    private readonly inner: LinkTransport,
    private readonly policy: FaultPolicy,
  ) {}

  private checkPartition(): void {
    if (this.policy.partition?.()) {
      throw new TransportError("simulated network partition");
    }
  }

  async connect(): Promise<void> {
    this.checkPartition();
    await this.inner.connect();
  }

  async hello(frame: HelloFrame): Promise<WelcomeFrame | null> {
    this.checkPartition();
    return this.inner.hello(frame);
  }

  async envelope(env: EnvelopeObject): Promise<EnvelopeAck> {
    this.checkPartition();
    return this.inner.envelope(env);
  }

  async receipt(r: ReceiptV1): Promise<void> {
    this.checkPartition();
    if (this.policy.dropReceipt?.(r.deliveryId)) return;
    return this.inner.receipt(r);
  }

  async heartbeat(hb: HeartbeatFrame): Promise<boolean> {
    // A partitioned heartbeat ANSWERS true (no observable death) but simply
    // stops refreshing — the server's lease expiry is what ends the session.
    if (this.policy.partition?.()) return true;
    return this.inner.heartbeat(hb);
  }

  async goodbye(gb: GoodbyeFrame): Promise<void> {
    if (this.policy.partition?.()) return;
    return this.inner.goodbye(gb);
  }

  async close(): Promise<void> {
    await this.inner.close();
  }

  onDeliver(cb: (d: LinkDeliveryV1) => void): void {
    this.deliverCbs.push(cb);
    this.inner.onDeliver((d) => {
      cb(d);
      if (this.policy.duplicateDeliver?.(d.deliveryId)) cb(d);
    });
  }

  onFlow(cb: (f: FlowControlFrame) => void): void {
    this.flowCbs.push(cb);
    this.inner.onFlow(cb);
  }

  onDisconnect(cb: (g: GoodbyeFrame) => void): void {
    this.disconnectCbs.push(cb);
    this.inner.onDisconnect(cb);
  }
}
