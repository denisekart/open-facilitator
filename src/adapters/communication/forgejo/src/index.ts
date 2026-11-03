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
import type { LinkClient, LinkLogger } from "@ofac/workspace-link";
import { runProviderAdapter } from "@ofac/provider-core/node";
import { deriveTenant, type AdapterConfig } from "./config.js";
import { AdapterState } from "./comment-ledger.js";
import { ForgejoClient, type ForgejoApi } from "./forgejo-client.js";
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
  const ctx: NormalizeContext = {
    providerInstanceId: config.providerInstanceId,
    tenantOrOrganizationId: tenant,
    workLabels: new Set(config.workLabels.map((label) => label.toLowerCase())),
    botUser: config.botUser,
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
        logger,
      }),
      handler: createForgejoHandler({ api, state, logger }),
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
