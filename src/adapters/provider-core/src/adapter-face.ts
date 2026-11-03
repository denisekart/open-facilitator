// The provider adapter face: the link-plane run loop every forge adapter
// repeats — enroll (secret mode, isolated-executor child, 401 re-enroll),
// capability-manifest POST, SignalrTransport + SqliteLedger client wiring —
// plus the webhook fan-out seam (the kernel sink delivers
// `command.provider.webhook.v1`; signature verification stays in the
// provider's normalizer). Provider-local stays outside: REST clients,
// vendor→domain mapping, vendor webhook schemes, init seeding.
import { randomBytes } from "node:crypto";
import type { CapabilityManifestV1, EnvelopeEvent } from "@ofac/protocol";
import {
  buildEvent,
  createLinkClient,
  enroll,
  type LinkClient,
  type LinkHandler,
  type LinkLogger,
  type LinkTokenProvider,
} from "@ofac/workspace-link";
import { SignalrTransport, SqliteLedger, postCapabilities } from "@ofac/workspace-link/node";
import { createWebhookReceiver, type WebhookNormalizer } from "./webhook-receiver.js";
import type { NormalizedEvent } from "./normalize-shared.js";

export interface ProviderAdapterOptions {
  /** Kernel base URL (link plane: enroll / capabilities / hub). */
  kernelUrl: string;
  /** Mode-(a) enrollment secret. */
  enrollSecret: string;
  /** Logical link endpoint id this adapter enrolls as. */
  endpointId: string;
  /** Kernel team roster slug the enrollment binds to. */
  team: string;
  /** SQLite path for the link ledger (+ provider state the adapter opens itself). */
  statePath: string;
  /** The manifest this adapter advertises (POST /api/link/v1/capabilities). */
  manifest: CapabilityManifestV1;
  /** Vendor webhook → normalized provider events (incl. signature law).
   *  Undefined for chat providers with no webhook edge (mattermost). */
  normalizeWebhook: WebhookNormalizer | undefined;
  /** The provider's own command handler (communication.reply etc.). */
  handler?: LinkHandler | undefined;
  logger: LinkLogger;
  fetchImpl?: typeof fetch;
  /** Extra envelope attributes per published event (e.g. causation). */
  decorate?: (event: NormalizedEvent) => Partial<EnvelopeEvent> | undefined;
}

export interface ProviderAdapterHandle {
  client: LinkClient;
  ready: Promise<void>;
  /** Publish one normalized provider event (envelope-shaped by the core). */
  publish(event: NormalizedEvent): Promise<void>;
  shutdown(): Promise<void>;
}

export async function runProviderAdapter(
  opts: ProviderAdapterOptions,
  envelopeSource: string,
): Promise<ProviderAdapterHandle> {
  // ── link plane: enroll (secret mode) → capabilities → client ───────────
  const activeToken = { value: "" };
  const enrollSelf = async (): Promise<void> => {
    const response = await enroll(
      {
        baseUrl: opts.kernelUrl,
        token: opts.enrollSecret,
        endpointId: opts.endpointId,
        instanceId: `lnk-${randomBytes(8).toString("hex")}`,
        trustMode: "isolated-executor",
        role: "child",
        team: opts.team,
      },
      ...(opts.fetchImpl === undefined ? [] : [opts.fetchImpl]),
    );
    if (response.linkToken !== undefined) activeToken.value = response.linkToken;
  };
  const token: LinkTokenProvider = {
    getToken: async () => activeToken.value,
    // 401 ⇒ rotate: re-enrollment mints a fresh token (linkd's precedent).
    refresh: enrollSelf,
  };
  await enrollSelf();
  if (activeToken.value === "") {
    throw new Error("enrollment issued no linkToken");
  }

  const { digest } = await postCapabilities(opts.manifest, {
    baseUrl: opts.kernelUrl,
    getToken: token.getToken,
    ...(opts.fetchImpl === undefined ? {} : { fetchImpl: opts.fetchImpl }),
  });

  const publish = async (event: NormalizedEvent): Promise<void> => {
    const data = event.data as unknown as Record<string, unknown>;
    const reference = data["reference"] as Record<string, unknown> | undefined;
    const env = buildEvent(event.type, {
      source: envelopeSource,
      subject: String(reference?.["subjectRef"] ?? data["providerDeliveryId"] ?? event.type),
      data,
      ...(opts.decorate === undefined ? {} : opts.decorate(event)),
    });
    await client.publishEvent(env);
  };

  const client = createLinkClient({
    transport: new SignalrTransport({ baseUrl: opts.kernelUrl, token }),
    ledger: new SqliteLedger(opts.statePath),
    endpointId: opts.endpointId,
    capabilityManifestDigest: digest,
    token,
    handler: createWebhookReceiver({
      normalize: opts.normalizeWebhook,
      publish,
      logger: opts.logger,
      ...(opts.handler === undefined ? {} : { handler: opts.handler }),
    }),
    logger: opts.logger,
  });

  return {
    client,
    ready: client.start().then(() => {
      opts.logger.info("provider adapter ready", {
        endpointId: opts.endpointId,
        kernel: opts.kernelUrl,
      });
    }),
    publish,
    async shutdown() {
      await client.stop("adapter-shutdown");
    },
  };
}
