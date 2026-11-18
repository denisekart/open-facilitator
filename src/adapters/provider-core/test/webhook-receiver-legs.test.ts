// The receiver's remaining legs: a chat provider without a normalizer answers
// the fan-out with the provider's handler or `unsupported` (a receipt, never a
// throw); a redelivery of an already-seen providerDeliveryId answers
// `accepted` having published ZERO (at-least-once fan-out); the injected
// durable `seen` seam replaces the in-memory window; the in-memory window is
// bounded (oldest-id eviction); and the telemetry seam counts per outcome.
import { describe, expect, it } from "vitest";
import type { EnvelopeCommand } from "@ofac/protocol";
import {
  createWebhookReceiver,
  type NormalizedEvent,
  type WebhookReceiverDeps,
} from "../src/node.js";
import type { ProviderTelemetry } from "../src/webhook-receiver.js";

const WEBHOOK = "org.facilitator.command.provider.webhook.v1";
const ctx = { deliveryId: "d", onStream: () => undefined } as never;
const logger = { debug() {}, info() {}, warn() {}, error() {} };

function cmd(data: unknown): EnvelopeCommand {
  return {
    specversion: "1.0",
    id: "e1",
    source: "urn:facilitator:kernel",
    type: WEBHOOK,
    subject: "provider/github/webhook",
    time: new Date().toISOString(),
    datacontenttype: "application/json",
    dataschema: "https://schemas.openfacilitator.org/v1/command.provider.webhook.v1.json",
    data,
  } as unknown as EnvelopeCommand;
}

const deliveryData = (body = "x"): Record<string, unknown> => ({
  commandId: "c",
  provider: "github",
  contentType: "application/json",
  body: Buffer.from(body).toString("base64"),
  headers: {},
});

function event(deliveryId: string): NormalizedEvent {
  return {
    type: "org.facilitator.event.provider.issue.v1",
    data: {
      reference: {
        provider: "forgejo",
        providerInstanceId: "i",
        tenantOrOrganizationId: "t",
        channelOrResourceId: "r",
        conversationKind: "comment",
        authorProviderAccountId: "a",
      },
      provider: "forgejo",
      providerDeliveryId: deliveryId,
      actorProviderAccountId: "a",
      action: "opened",
      workTrigger: false,
    },
  } as unknown as NormalizedEvent;
}

function recordingTelemetry(): ProviderTelemetry & {
  counts: Record<string, string>[];
  spans: string[];
} {
  const counts: Record<string, string>[] = [];
  const spans: string[] = [];
  return {
    emitting: true,
    spans,
    counts,
    meter: () => ({
      createCounter: () => ({
        add: (_delta: number, attributes?: Record<string, string>) => {
          if (attributes !== undefined) counts.push(attributes);
        },
      }),
    }),
    instrument: <T>(name: string, _carrier: unknown, run: () => Promise<T>): Promise<T> => {
      spans.push(name);
      return run();
    },
  };
}

function receiver(
  deps: Partial<WebhookReceiverDeps> & { publish?: (e: NormalizedEvent) => Promise<void> } = {},
) {
  const published: NormalizedEvent[] = [];
  const handler = createWebhookReceiver({
    normalize: () => ({ kind: "normalized", events: [] }),
    publish: async (e) => {
      published.push(e);
    },
    logger,
    ...deps,
  });
  return { handler, published };
}

describe("no-normalizer chat provider", () => {
  it("a webhook command delegates to the provider handler when there is one", async () => {
    const { handler } = receiver({
      normalize: undefined,
      handler: {
        async onCommand() {
          return { kind: "accepted" };
        },
      },
    });
    await expect(handler.onCommand?.(cmd(deliveryData()), ctx)).resolves.toEqual({
      kind: "accepted",
    });
  });

  it("without a handler either, the fan-out answers `unsupported`", async () => {
    const { handler, published } = receiver({ normalize: undefined });
    await expect(handler.onCommand?.(cmd(deliveryData()), ctx)).resolves.toEqual({
      kind: "unsupported",
    });
    expect(published).toHaveLength(0);
  });

  it("a non-webhook command still answers `unsupported`, untouched", async () => {
    const { handler } = receiver();
    await expect(
      handler.onCommand?.({ ...cmd({}), type: "org.facilitator.command.work.cancel.v1" }, ctx),
    ).resolves.toEqual({ kind: "unsupported" });
  });
});

describe("duplicate law", () => {
  it("a redelivery of a seen providerDeliveryId accepts with zero publishes", async () => {
    let events = [event("dl-1")];
    const { handler, published } = receiver({
      normalize: () => ({ kind: "normalized", events }),
    });
    await expect(handler.onCommand?.(cmd(deliveryData()), ctx)).resolves.toEqual({
      kind: "accepted",
    });
    events = [event("dl-1")];
    await expect(handler.onCommand?.(cmd(deliveryData()), ctx)).resolves.toEqual({
      kind: "accepted",
    });
    expect(published).toHaveLength(1); // only the fresh delivery published
  });

  it("the durable `seen` seam replaces the in-memory window", async () => {
    const seenCalls: string[] = [];
    const { handler, published } = receiver({
      normalize: () => ({ kind: "normalized", events: [event("dl-9")] }),
      seen: (id) => {
        seenCalls.push(id);
        return seenCalls.length > 1; // first call fresh, later ones duplicates
      },
    });
    await expect(handler.onCommand?.(cmd(deliveryData()), ctx)).resolves.toEqual({
      kind: "accepted",
    });
    await expect(handler.onCommand?.(cmd(deliveryData()), ctx)).resolves.toEqual({
      kind: "accepted",
    });
    expect(seenCalls).toEqual(["dl-9", "dl-9"]);
    expect(published).toHaveLength(1);
  });

  it("events without a providerDeliveryId always publish (nothing to dedupe on)", async () => {
    const { handler, published } = receiver({
      normalize: () => ({
        kind: "normalized",
        events: [{ type: "t", data: {} } as unknown as NormalizedEvent],
      }),
    });
    for (let i = 0; i < 2; i += 1) {
      await expect(handler.onCommand?.(cmd(deliveryData()), ctx)).resolves.toEqual({
        kind: "accepted",
      });
    }
    expect(published).toHaveLength(2);
  });

  it("the in-memory window is bounded: the oldest ids evict", async () => {
    let events = Array.from({ length: 4097 }, (_, i) => event(`dl-${i}`));
    const { handler, published } = receiver({
      normalize: () => ({ kind: "normalized", events }),
    });
    await expect(handler.onCommand?.(cmd(deliveryData()), ctx)).resolves.toEqual({
      kind: "accepted",
    });
    // dl-0 was evicted (the window holds 4096): fresh again, so the
    // redelivery republishes just it.
    events = [event("dl-0")];
    await expect(handler.onCommand?.(cmd(deliveryData()), ctx)).resolves.toEqual({
      kind: "accepted",
    });
    expect(published).toHaveLength(4098);
    // An id still inside the window is a duplicate.
    events = [event("dl-4096")];
    await expect(handler.onCommand?.(cmd(deliveryData()), ctx)).resolves.toEqual({
      kind: "accepted",
    });
    expect(published).toHaveLength(4098);
  });
});

describe("telemetry seam", () => {
  it("one provider.webhook span per delivery + the outcome counter label", async () => {
    const telemetry = recordingTelemetry();
    const { handler } = receiver({
      normalize: () => ({ kind: "normalized", events: [event("dl-1")] }),
      telemetry,
      provider: "forgejo",
    });
    await handler.onCommand?.(cmd(deliveryData()), ctx);
    expect(telemetry.spans).toEqual(["provider.webhook"]);
    expect(telemetry.counts).toEqual([{ provider: "forgejo", outcome: "normalized" }]);
  });

  it("a duplicate delivery counts `duplicate`, a malformed one `rejected`", async () => {
    const telemetry = recordingTelemetry();
    const { handler } = receiver({
      normalize: () => ({ kind: "normalized", events: [event("dl-1")] }),
      telemetry,
    });
    await handler.onCommand?.(cmd(deliveryData()), ctx);
    await handler.onCommand?.(cmd(deliveryData()), ctx);
    await handler.onCommand?.(cmd({ nope: true }), ctx);
    expect(telemetry.counts).toEqual([
      { provider: "unknown", outcome: "normalized" },
      { provider: "unknown", outcome: "duplicate" },
      { provider: "unknown", outcome: "rejected" },
    ]);
  });
});
