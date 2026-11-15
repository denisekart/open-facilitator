// @ofac/forgejo-adapter — the inbound forge provider module: a link-plane
// endpoint on the @ofac/provider-core adapter face (enroll → capabilities →
// SignalR + webhook fan-out receiver). The kernel sink
// (`POST /api/ingress/webhooks/forgejo`, the only web-exposed point) rides
// verbatim forge deliveries here as `command.provider.webhook.v1`; the
// provider-core receiver seam verifies the HMAC and runs the forge→domain
// normalizer, publishing the central forge domain events
// (data.provider = "forgejo"). Outbound command.communication.reply.v1
// executes as a bot-identity comment under the comment-ledger idempotency
// law. Stateless apart from the SQLite comment ledger — deployable anywhere
// the kernel link plane is dialable.
import { createServer, type Server } from "node:http";
import { randomBytes } from "node:crypto";
import { enroll, type LinkClient, type LinkLogger } from "@ofac/workspace-link";
import { runProviderAdapter } from "@ofac/provider-core/node";
import { deriveTenant, type AdapterConfig } from "./config.js";
import { AdapterState } from "./comment-ledger.js";
import { ForgejoClient, type ForgejoApi } from "./forgejo-client.js";
import { MediaPipeline, fetchArtifactContent } from "./media.js";
import type { NormalizeContext } from "./normalize.js";
import { createForgejoNormalizer } from "./webhook.js";
import { createForgejoHandler, forgejoAdapterManifest } from "./handler.js";

export {
  AdapterConfigError,
  deriveTenant,
  parseEnvConfig,
  resolveBotToken,
  type AdapterConfig,
} from "./config.js";
export {
  ForgejoError,
  ForgejoClient,
  type ForgejoApi,
  type ForgejoComment,
  type ForgejoPullRequest,
} from "./forgejo-client.js";
export { AdapterState, type CommentLedgerEntry } from "./comment-ledger.js";
export {
  MediaPipeline,
  fetchArtifactContent,
  collectAttachments,
  type MediaPipelineDeps,
  type ForgejoFileRef,
} from "./media.js";
export {
  CI_EVENT,
  ISSUE_EVENT,
  MENTION_EVENT,
  PULL_REQUEST_EVENT,
  normalizeIssueComment,
  normalizeIssues,
  normalizePullRequest,
  normalizeReview,
  normalizeStatus,
  type NormalizeContext,
  type NormalizedEvent,
} from "./normalize.js";
export { createForgejoNormalizer, type ForgejoNormalizerDeps } from "./webhook.js";
export {
  CMD_REPLY,
  createForgejoHandler,
  forgejoAdapterManifest,
  type HandlerDeps,
} from "./handler.js";
export { runInit, type InitConfig, type InitDeps, type InitReport } from "./init.js";

export interface RunHandle {
  client: LinkClient;
  ready: Promise<void>;
  shutdown(): Promise<void>;
}

export interface RunDeps {
  /** Injected provider client (tests run the whole loop against a fake). */
  api?: ForgejoApi;
  fetchImpl?: typeof fetch;
}

export async function run(
  config: AdapterConfig,
  logger: LinkLogger,
  deps: RunDeps = {},
): Promise<RunHandle> {
  const tenant = deriveTenant(config.forgejoUrl);
  const api =
    deps.api ??
    (config.botToken === undefined
      ? undefined
      : new ForgejoClient(config.forgejoUrl, config.botToken, deps.fetchImpl));
  const state = new AdapterState(config.statePath);

  // Our own link credential for the artifact client + content GETs (the
  // telegram law): the adapter-face owns the CLIENT's token, so media rides
  // an enrollment of its own (same secret/endpoint — the kernel issues
  // per-enrollment tokens; 401 re-enrolls).
  const linkToken = { value: "" };
  const enrollSelf = async (): Promise<string> => {
    const response = await enroll(
      {
        baseUrl: config.kernelUrl,
        token: config.enrollSecret,
        endpointId: config.endpointId,
        instanceId: `fjm-${randomBytes(6).toString("hex")}`,
        trustMode: "isolated-executor",
        role: "child",
        team: config.team,
      },
      ...(deps.fetchImpl === undefined ? [] : [deps.fetchImpl]),
    );
    linkToken.value = response.linkToken ?? "";
    return linkToken.value;
  };
  const token = async (): Promise<string> =>
    linkToken.value === "" ? await enrollSelf() : linkToken.value;

  // The media leg rides the bot token (the only forge identity) and the
  // facet's own caps: no token ⇒ no media, text-only events as before.
  const media =
    api === undefined
      ? undefined
      : new MediaPipeline({
          api,
          state,
          kernelUrl: config.kernelUrl,
          token,
          endpointId: config.endpointId,
          maxBytes: 8388608,
          enabled: true,
          logger,
          ...(deps.fetchImpl === undefined ? {} : { fetchImpl: deps.fetchImpl }),
        });

  const ctx: NormalizeContext = {
    providerInstanceId: config.providerInstanceId,
    tenantOrOrganizationId: tenant,
    workLabels: new Set(config.workLabels.map((label) => label.toLowerCase())),
    botUser: config.botUser,
    mediaArtifactsFor: (objectId) => media?.artifactsFor(objectId) ?? [],
  };

  const handle = await runProviderAdapter(
    {
      kernelUrl: config.kernelUrl,
      enrollSecret: config.enrollSecret,
      endpointId: config.endpointId,
      team: config.team,
      statePath: config.statePath,
      manifest: forgejoAdapterManifest(config.team),
      normalizeWebhook: createForgejoNormalizer({
        ctx,
        webhookSecret: config.webhookSecret,
        api,
        ...(media === undefined ? {} : { media }),
        logger,
      }),
      handler: createForgejoHandler({
        api,
        state,
        // The reply-with-artifact path: the link-plane content GET rides our
        // own enrollment token.
        media: {
          download: (artifactId: string) =>
            fetchArtifactContent(config.kernelUrl, artifactId, token, deps.fetchImpl ?? fetch),
        },
        // The context leg's event authorship: the same provider instance the
        // normalizer signs its events with.
        providerInstanceId: config.providerInstanceId,
        logger,
      }),
      logger,
      ...(deps.fetchImpl === undefined ? {} : { fetchImpl: deps.fetchImpl }),
    },
    `urn:ofac:provider:forgejo:${tenant}`,
  );

  // GET /healthz only: the webhook HTTP server is GONE (the kernel sink owns
  // the web edge); the port stays published so the e2e boot leg has a probe.
  let health: Server | undefined;
  const ready = handle.ready.then(async () => {
    health = createServer((req, res) => {
      if (req.url === "/healthz") {
        res.writeHead(200, { "content-type": "application/json" });
        res.end(JSON.stringify({ ok: true, endpointId: config.endpointId }));
        return;
      }

      res.writeHead(404);
      res.end();
    });
    const listening = Promise.withResolvers<void>();
    health.once("listening", listening.resolve);
    health.listen(config.port);
    await listening.promise;
    logger.info("forgejo adapter ready", {
      endpointId: config.endpointId,
      port: config.port,
      kernel: config.kernelUrl,
    });
  });

  return {
    client: handle.client,
    ready,
    async shutdown() {
      if (health !== undefined) {
        const { promise, resolve } = Promise.withResolvers<void>();
        health.close(() => resolve());
        await promise;
      }

      await handle.shutdown();
      state.close();
    },
  };
}
