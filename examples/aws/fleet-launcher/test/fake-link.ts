// The fake link plane: a MockTransport implementing the SDK's LinkTransport
// seam (the gateway fleet test's precedent) plus a fetch stub serving
// POST /challenge + /proof + /enroll (the credential law) and POST
// /capabilities → 204, recording
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

/**
 * The credential-law fake: POST /challenge → a nonce, POST /proof (detached
 * Ed25519 JWS) → a session JWT, POST /enroll → 201 identity receipt, POST
 * /capabilities → 204. The signature itself is not verified here (the kernel
 * suites own that law); the fake only requires a well-formed proof — a client
 * that could not mint one fails the loop, which is what this file pins. Every
 * wire body is recorded for the assertions.
 */
export function fakeKernel(sessionToken = "tok-fleet-1"): FakeKernel {
  const calls: FakeKernel["calls"] = [];
  let seq = 0;
  let enrolled = false;
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
      enrolled = true;
      return new Response(
        JSON.stringify({
          enrollmentId: "enr-fleet-1",
          trustMode: "isolated-executor",
          endpointKeyId: "sha256:0000000000000000000000000000000000000000000000000000000000000000",
          seq: 0,
        }),
        { status: 201, headers: { "content-type": "application/json" } },
      );
    }
    if (path.endsWith("/api/link/v1/challenge")) {
      if (!enrolled) {
        // The no-existence-oracle answer the first-boot law keys on.
        return new Response(JSON.stringify({ detail: "No live enrollment for this endpoint." }), {
          status: 401,
          headers: { "content-type": "application/problem+json" },
        });
      }
      seq += 1;
      return new Response(JSON.stringify({ nonce: `1.${seq}.fake-nonce-${seq}`, epoch: 1, seq }), {
        status: 200,
        headers: { "content-type": "application/json" },
      });
    }
    if (path.endsWith("/api/link/v1/proof")) {
      if (typeof body?.["proof"] !== "string" || body["proof"].split(".").length !== 3) {
        return new Response(JSON.stringify({ detail: "proof malformed" }), { status: 401 });
      }
      return new Response(
        JSON.stringify({
          sessionToken,
          expiresAt: new Date(Date.now() + 300_000).toISOString(),
          seq,
        }),
        { status: 200, headers: { "content-type": "application/json" } },
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
