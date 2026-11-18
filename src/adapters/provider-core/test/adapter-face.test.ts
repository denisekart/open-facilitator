// The adapter face, wired against a stub transport: no socket, no kernel
// process. Covers the credential boot (first-boot enroll → session) through a
// scripted fetch, the capabilities POST, the envelope shaping in `publish`
// (subject precedence: reference.subjectRef → providerDeliveryId → the
// decorate seam), the webhook fan-out riding the receiver INSIDE the client's
// delivery pipeline, and a clean shutdown.
import { mkdtempSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { beforeEach, describe, expect, it, vi } from "vitest";
import type {
  CapabilityManifestV1,
  ConversationReference,
  EnvelopeObject,
  LinkDeliveryV1,
  ProviderIssueEventV1Data,
  ReceiptV1,
  SupportedTransport,
} from "@ofac/protocol";
import type { EnvelopeAck, GoodbyeFrame, LinkTransport, WelcomeFrame } from "@ofac/workspace-link";
import type { NormalizedEvent, ProviderAdapterOptions } from "../src/node.js";
import type { ProviderAdapterHandle } from "../src/node.js";

// ── the stub hub: the ONLY wire ─────────────────────────────────────────────

class StubTransport implements LinkTransport {
  readonly supportedTransports: [SupportedTransport, ...SupportedTransport[]] = ["signalr"];
  hubUrl = "https://kernel.test/hubs/link";
  envelopes: EnvelopeObject[] = [];
  receipts: ReceiptV1[] = [];
  goodbyes: unknown[] = [];
  closed = 0;
  welcome: WelcomeFrame = {
    kind: "welcome",
    sessionId: "sess-1",
    leaseEpoch: 1,
    maxInFlight: 16,
    heartbeatSeconds: 30,
    protocolVersion: "v1",
  };
  private deliverCbs: ((d: LinkDeliveryV1) => void)[] = [];

  async connect(): Promise<void> {}
  async hello(): Promise<WelcomeFrame | null> {
    return this.welcome;
  }
  async envelope(env: EnvelopeObject): Promise<EnvelopeAck> {
    this.envelopes.push(env);
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
  async close(): Promise<void> {
    this.closed += 1;
  }
  onDeliver(cb: (d: LinkDeliveryV1) => void): void {
    this.deliverCbs.push(cb);
  }
  onFlow(): void {}
  onDisconnect(): void {}
  pushDeliver(delivery: LinkDeliveryV1): void {
    for (const cb of this.deliverCbs) cb(delivery);
  }
}

const stub = { transport: new StubTransport() };
const capabilityPosts: unknown[] = [];

vi.mock("@ofac/workspace-link/node", async () => {
  const core = await import("@ofac/workspace-link");
  return {
    // Hand-rolled stub hub (mock seam, not a dynamic-specifier import).
    SignalrTransport: class {
      constructor() {
        return stub.transport;
      }
    },
    // The link ledger, in-memory: the file face is already covered upstream.
    SqliteLedger: class extends core.MemoryLedger {},
    postCapabilities: async (manifest: unknown) => {
      capabilityPosts.push(manifest);
      return { digest: `sha256:${"ab".repeat(32)}`, body: "{}" };
    },
  };
});

// Imported dynamically on purpose: `vi.mock` must be registered (and the
// fixtures above initialized) before the face module pulls in
// @ofac/workspace-link/node.
const { runProviderAdapter } = await import("../src/node.js");

// ── the stub kernel: challenge → (enroll) → proof ───────────────────────────

const calls: string[] = [];

function kernelFetch(script: "first-boot" | "enrolled"): typeof fetch {
  let enrolled = script === "enrolled";
  const json = (status: number, body: unknown): Response =>
    new Response(JSON.stringify(body), {
      status,
      headers: { "content-type": "application/json" },
    });
  return (async (url: unknown) => {
    const route = String(url).replace("https://kernel.test/api/link/v1/", "");
    calls.push(route);
    if (route === "challenge") {
      if (!enrolled) return json(401, { detail: "No live enrollment for this endpoint." });
      return json(200, {
        nonce: "1.1.r1",
        publicKeyRequired: true,
        algorithm: "ed25519",
        expiresAt: new Date(Date.now() + 30_000).toISOString(),
      });
    }
    if (route === "enroll") {
      enrolled = true;
      return json(201, {
        enrollmentId: "enr-1",
        trustMode: "isolated-executor",
        endpointKeyId: "key-1",
        seq: 1,
      });
    }
    if (route === "proof") {
      return json(200, {
        sessionToken: "jwt-1",
        expiresAt: new Date(Date.now() + 300_000).toISOString(),
        seq: 1,
      });
    }
    return json(404, { detail: `unrouted ${String(url)}` });
  }) as unknown as typeof fetch;
}

const manifest: CapabilityManifestV1 = {
  apiVersion: "facilitator.framework/v1",
  kind: "CapabilityManifest",
  ownerTeam: "team-alpha",
  capabilities: [
    {
      id: "vcs.issue.comment",
      version: 1,
      execution: "asynchronous",
      supportsSteering: false,
      supportsCancellation: false,
      supportsNudge: false,
    },
  ],
};

const logger = { debug() {}, info() {}, warn() {}, error() {} };

const issueData: ProviderIssueEventV1Data = {
  reference: {
    provider: "forgejo",
    providerInstanceId: "i",
    tenantOrOrganizationId: "t",
    channelOrResourceId: "repo/1",
    conversationKind: "comment",
    authorProviderAccountId: "alice",
    subjectRef: "forgejo:repo/1",
  },
  provider: "forgejo",
  providerDeliveryId: "dl-1",
  actorProviderAccountId: "alice",
  action: "opened",
  workTrigger: false,
};

const issueEvent: NormalizedEvent = {
  type: "org.facilitator.event.provider.issue.v1",
  data: issueData,
};

async function adapter(
  overrides: Partial<ProviderAdapterOptions> = {},
): Promise<ProviderAdapterHandle> {
  return runProviderAdapter(
    {
      kernelUrl: "https://kernel.test",
      enrollSecret: "enroll-code",
      endpointId: "gw.team-a/forgejo",
      team: "team-alpha",
      statePath: join(mkdtempSync(join(tmpdir(), "adapter-face-")), "state.sqlite3"),
      manifest,
      normalizeWebhook: () => ({ kind: "normalized", events: [issueEvent] }),
      logger,
      ...overrides,
    },
    "urn:ofac:provider:forgejo:tenant-1",
  );
}

/** Microtask drain so the fire-and-forget delivery pipeline lands. */
async function flush(times = 8): Promise<void> {
  for (let i = 0; i < times; i += 1) await Promise.resolve();
}

beforeEach(() => {
  calls.length = 0;
  capabilityPosts.length = 0;
  stub.transport = new StubTransport();
  vi.unstubAllGlobals();
});

describe("adapter face", () => {
  it("boots: enroll once, post the manifest, reach ready, shut down cleanly", async () => {
    vi.stubGlobal("fetch", kernelFetch("first-boot"));
    const handle = await adapter();
    await handle.ready;
    expect(handle.telemetry.emitting).toBe(false); // no OTEL endpoint ⇒ dark
    expect(capabilityPosts).toEqual([manifest]);
    // The boot law: prove refused (challenge 401) → enroll → prove.
    expect(calls.slice(0, 3)).toEqual(["challenge", "enroll", "challenge"]);
    expect(handle.client.state).toBe("ready");
    await handle.shutdown();
    expect(stub.transport.goodbyes).toHaveLength(1);
    expect(stub.transport.closed).toBe(1);
  });

  it("an already-enrolled endpoint re-presents the key: no enroll call", async () => {
    vi.stubGlobal("fetch", kernelFetch("enrolled"));
    const handle = await adapter();
    await handle.ready;
    // prove (challenge+proof), then the hello-carried WS-channel proof.
    expect(calls).toEqual(["challenge", "proof", "challenge"]);
    await handle.shutdown();
  });

  it("publish shapes the envelope: subject from reference.subjectRef, source verbatim", async () => {
    vi.stubGlobal("fetch", kernelFetch("enrolled"));
    const handle = await adapter();
    await handle.ready;
    await handle.publish(issueEvent);
    expect(stub.transport.envelopes).toHaveLength(1);
    const env = stub.transport.envelopes[0] as { type: string; subject: string; source: string };
    expect(env.type).toBe("org.facilitator.event.provider.issue.v1");
    expect(env.subject).toBe("forgejo:repo/1");
    expect(env.source).toBe("urn:ofac:provider:forgejo:tenant-1");
    await handle.shutdown();
  });

  it("without a subjectRef the providerDeliveryId answers", async () => {
    vi.stubGlobal("fetch", kernelFetch("enrolled"));
    const handle = await adapter();
    await handle.ready;
    const reference: ConversationReference = { ...issueData.reference };
    delete reference.subjectRef;
    await handle.publish({ ...issueEvent, data: { ...issueData, reference } });
    const env = stub.transport.envelopes.at(-1) as { subject: string };
    expect(env.subject).toBe("dl-1");
    await handle.shutdown();
  });

  it("decorate adds envelope attributes per event", async () => {
    vi.stubGlobal("fetch", kernelFetch("enrolled"));
    const handle = await adapter({ decorate: () => ({ causationid: "cause-1" }) });
    await handle.ready;
    await handle.publish(issueEvent);
    const env = stub.transport.envelopes.at(-1) as { causationid?: string };
    expect(env.causationid).toBe("cause-1");
    await handle.shutdown();
  });

  it("a webhook fan-out delivery rides the receiver and publishes over the hub", async () => {
    vi.stubGlobal("fetch", kernelFetch("enrolled"));
    const handle = await adapter();
    await handle.ready;
    stub.transport.pushDeliver({
      deliveryId: "del-1",
      deliveredAt: new Date().toISOString(),
      envelope: {
        specversion: "1.0",
        id: "cmd-evt-1",
        source: "urn:facilitator:kernel",
        type: "org.facilitator.command.provider.webhook.v1",
        subject: "provider/forgejo/webhook",
        time: new Date().toISOString(),
        datacontenttype: "application/json",
        dataschema: "https://schemas.openfacilitator.org/v1/command.provider.webhook.v1.json",
        data: {
          commandId: "cmd-1",
          provider: "forgejo",
          contentType: "application/json",
          body: Buffer.from(`{"a":1}`).toString("base64"),
          headers: { "X-Gitea-Event": "issues" },
        },
      },
    });
    await flush();
    expect(stub.transport.envelopes).toHaveLength(1);
    const published = stub.transport.envelopes[0] as { type: string; subject: string };
    expect(published.type).toBe("org.facilitator.event.provider.issue.v1");
    expect(published.subject).toBe("forgejo:repo/1");
    expect(stub.transport.receipts.at(-1)).toMatchObject({
      deliveryId: "del-1",
      status: "accepted",
    });
    await handle.shutdown();
  });
});
