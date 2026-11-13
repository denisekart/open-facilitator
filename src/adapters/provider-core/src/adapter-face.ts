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
import { startTelemetryFromEnv } from "@ofac/otel";
import { setHttpErrorObserver } from "./http-core.js";
import {
  createWebhookReceiver,
  type ProviderTelemetry,
  type WebhookNormalizer,
} from "./webhook-receiver.js";
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
  /** The face's telemetry handle (dark when the env names no endpoint):
   *  connector binaries pass it to `createConnectorHooks` / `createAdapter`
   *  so HTTP failures and executions ride the SAME resource + meter. */
  telemetry: ProviderTelemetry;
  shutdown(): Promise<void>;
}

export async function runProviderAdapter(
  opts: ProviderAdapterOptions,
  envelopeSource: string,
): Promise<ProviderAdapterHandle> {
  // Telemetry starts here, from env: every provider bin gets the same
  // silent-by-default behavior (no OTEL_EXPORTER_OTLP_ENDPOINT ⇒ no SDK,
  // no module load). The provider label rides the envelope URN
  // (`urn:ofac:provider:<name>:<tenant>`), so no bin has to repeat it.
  const provider = envelopeSource.split(":")[3] ?? "unknown";
  const telemetry = startTelemetryFromEnv({
    serviceName: `ofac-${provider}-adapter`,
    meterName: "ofac.provider",
    instanceId: opts.endpointId,
    resourceAttributes: { "ofac.team": opts.team, "ofac.endpoint_id": opts.endpointId },
  });
  // The taxonomy feeds ofac.provider.http_errors: the label comes from
  // http-core's own classification, never re-parsed here. One provider
  // adapter per process is the deployment law (each bin = one container).
  const httpErrors = telemetry.meter("ofac.provider").createCounter("ofac.provider.http_errors", {
    description: "Provider HTTP failures (labels: provider, status 429|403|5xx)",
  });
  setHttpErrorObserver((status) => httpErrors.add(1, { provider, status }));
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
      telemetry,
      provider,
      ...(opts.handler === undefined ? {} : { handler: opts.handler }),
    }),
    logger: opts.logger,
    // Every delivery gets a link.deliver span; the webhook receiver's
    // provider.webhook span nests inside it, parented on the carrier.
    instrument: (name, env, run) => telemetry.instrument(name, env, run),
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
    telemetry,
    async shutdown() {
      setHttpErrorObserver(undefined);
      await client.stop("adapter-shutdown");
      await telemetry.shutdown();
    },
  };
}
