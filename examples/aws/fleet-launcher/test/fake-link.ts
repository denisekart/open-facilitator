// The fake link plane: a MockTransport implementing the SDK's LinkTransport
// seam (the gateway fleet test's precedent) plus a fetch stub serving
// POST /enroll → 201 {linkToken} and POST /capabilities → 204, recording
// every wire body so the enroll-shape and $-name laws are pinned ON THE WIRE.
import type { EnvelopeObject, LinkDeliveryV1, ReceiptV1 } from "@ofac/protocol";
import type {
  EnvelopeAck,
  WelcomeFrame,
  GoodbyeFrame,
  HelloFrame,
  LinkTransport,
} from "@ofac/workspace-link";

export class MockTransport implements LinkTransport {
  hellos: HelloFrame[] = [];
  receipts: ReceiptV1[] = [];
  goodbyes: GoodbyeFrame[] = [];
  welcome: WelcomeFrame = {
    kind: "welcome",
    sessionId: "sess-fleet-1",
    leaseEpoch: 1,
    maxInFlight: 8,
    heartbeatSeconds: 3600,
    protocolVersion: "v1",
  };
  private deliverCbs: ((d: LinkDeliveryV1) => void)[] = [];

  async connect(): Promise<void> {}
  async hello(frame: HelloFrame): Promise<WelcomeFrame | null> {
    this.hellos.push(frame);
    return this.welcome;
  }
  async envelope(): Promise<EnvelopeAck> {
    return { status: "accepted", deliveryId: "inbox-1" };
  }
  async receipt(r: ReceiptV1): Promise<void> {
    this.receipts.push(r);
  }
  async heartbeat(): Promise<boolean> {
    return true;
  }
  async goodbye(g: GoodbyeFrame): Promise<void> {
    this.goodbyes.push(g);
  }
  async close(): Promise<void> {}
  onDeliver(cb: (d: LinkDeliveryV1) => void): void {
    this.deliverCbs.push(cb);
  }
  onFlow(): void {}
  onDisconnect(): void {}

  pushDeliver(delivery: LinkDeliveryV1): void {
    for (const cb of this.deliverCbs) cb(delivery);
  }
}

export interface FakeKernel {
  calls: { path: string; body?: unknown; authorization?: string | undefined }[];
  fetchImpl: typeof fetch;
}

/** enroll → 201 with a linkToken; capabilities → 204. Every body is recorded. */
export function fakeKernel(linkToken = "tok-fleet-1"): FakeKernel {
  const calls: FakeKernel["calls"] = [];
  const fetchImpl = (async (input: string | URL | Request, init?: RequestInit) => {
    const path = String(input);
    const body = typeof init?.body === "string" ? JSON.parse(init.body) : undefined;
    const headers = new Headers(init?.headers);
    calls.push({
      path,
      ...(body === undefined ? {} : { body }),
      ...(headers.get("authorization") === null
        ? {}
        : { authorization: headers.get("authorization") ?? undefined }),
    });
    if (path.endsWith("/api/link/v1/enroll")) {
      return new Response(
        JSON.stringify({ enrollmentId: "enr-fleet-1", trustMode: "isolated-executor", linkToken }),
        { status: 201, headers: { "content-type": "application/json" } },
      );
    }
    if (path.endsWith("/api/link/v1/capabilities")) {
      return new Response(null, { status: 204 });
    }
    return new Response("{}", { status: 404 });
  }) as unknown as typeof fetch;
  return { calls, fetchImpl };
}

/** A schema-valid provision delivery (the kernel's wire shape, pinned by the
 *  @ofac/protocol envelope+data validators the client pipeline enforces). */
export function provisionDelivery(
  kind: "start" | "stop",
  deliveryId: string,
  data: Record<string, unknown>,
  type?: string,
): LinkDeliveryV1 {
  const commandType =
    type ??
    (kind === "start"
      ? "org.facilitator.command.provision.start.v1"
      : "org.facilitator.command.provision.stop.v1");
  const envelope: EnvelopeObject = {
    specversion: "1.0",
    id: `env-${deliveryId}`,
    source: "urn:facilitator:kernel:unit",
    type: commandType,
    subject: `provision/${deliveryId}`,
    time: new Date().toISOString(),
    dataschema: `https://schemas.openfacilitator.org/v1/${commandType.replace(
      /^org\.facilitator\./,
      "",
    )}.json`,
    datacontenttype: "application/json",
    data: data as NonNullable<EnvelopeObject["data"]>,
  };
  return { deliveryId, deliveredAt: new Date().toISOString(), envelope };
}
