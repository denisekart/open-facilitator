// @ofac/github-adapter — the inbound GitHub provider module: a link-plane
// endpoint on the @ofac/provider-core adapter face (enroll → capabilities →
// SignalR + webhook fan-out receiver). The kernel sink
// (`POST /api/ingress/webhooks/github`, the only web-exposed point) rides
// verbatim GitHub deliveries here as `command.provider.webhook.v1`; the
// provider-core receiver seam verifies the HMAC and runs the GitHub→domain
// normalizer, publishing the central forge domain events
// (data.provider = "github"). Outbound command.communication.reply.v1
// executes as a bot-identity comment under the comment-ledger idempotency
// law; artifacts ride that comment as uploaded issue images. Stateless apart
// from the SQLite comment + media ledgers — deployable anywhere the kernel
// link plane is dialable.
import { createServer, type Server } from "node:http";
import {
  createEndpointCredential,
  identityDirFor,
  type LinkClient,
  type LinkLogger,
} from "@ofac/workspace-link";
import { runProviderAdapter } from "@ofac/provider-core/node";
import { deriveTenant, type AdapterConfig } from "./config.js";
import { AdapterState } from "./comment-ledger.js";
import { GitHubClient, type GitHubApi } from "./github-client.js";
import { MediaPipeline, assetHostsFor, fetchArtifactContent } from "./media.js";
import type { NormalizeContext } from "./normalize.js";
import { createGitHubNormalizer } from "./webhook.js";
import { createGitHubHandler, githubAdapterManifest } from "./handler.js";

export {
  AdapterConfigError,
  deriveTenant,
  parseEnvConfig,
  resolveBotToken,
  type AdapterConfig,
} from "./config.js";
export {
  GitHubError,
  GitHubClient,
  deriveUploadsBase,
  type GitHubApi,
  type GitHubAsset,
  type GitHubComment,
  type GitHubIssue,
  type GitHubPullRequest,
  type GitHubPullRequestDetail,
  type GitHubReviewComment,
} from "./github-client.js";
export { AdapterState, type CommentLedgerEntry, type FileLedgerEntry } from "./comment-ledger.js";
export { CONTEXT_FETCH_COMMAND, CONTEXT_FETCHED_EVENT } from "./context.js";
export {
  MediaPipeline,
  assetHostsFor,
  fetchArtifactContent,
  type MediaPipelineDeps,
} from "./media.js";
export {
  CI_EVENT,
  ISSUE_EVENT,
  MENTION_EVENT,
  PULL_REQUEST_EVENT,
  collectAssetUrls,
  normalizeCheckSuite,
  normalizeIssueComment,
  normalizeIssues,
  normalizePullRequest,
  normalizeReview,
  normalizeReviewComment,
  normalizeStatus,
  type AssetUrl,
  type NormalizeContext,
  type NormalizedEvent,
} from "./normalize.js";
export { createGitHubNormalizer, type GitHubNormalizerDeps } from "./webhook.js";
export {
  CMD_POST,
  CMD_REPLY,
  MEDIA_FACET,
  createGitHubHandler,
  githubAdapterManifest,
  type ArtifactSource,
  type HandlerDeps,
} from "./handler.js";

export interface RunHandle {
  client: LinkClient;
  ready: Promise<void>;
  shutdown(): Promise<void>;
}

export interface RunDeps {
  /** Injected provider client (tests run the whole loop against a fake). */
  api?: GitHubApi;
  fetchImpl?: typeof fetch;
}

export async function run(
  config: AdapterConfig,
  logger: LinkLogger,
  deps: RunDeps = {},
): Promise<RunHandle> {
  const tenant = deriveTenant(config.githubUrl);
  const api =
    deps.api ??
    (config.botToken === undefined
      ? undefined
      : new GitHubClient(config.githubUrl, config.botToken, deps.fetchImpl));
  const state = new AdapterState(config.statePath);
  const { provider: session } = await createEndpointCredential({
    stateDir: identityDirFor(config.statePath),
    baseUrl: config.kernelUrl,
    endpointId: config.endpointId,
    team: config.team,
    trustMode: "isolated-executor",
    role: "child",
    enrollSecret: config.enrollSecret,
    logger,
    ...(deps.fetchImpl === undefined ? {} : { fetchImpl: deps.fetchImpl }),
    instancePrefix: "gha",
  });
  const token = () => session.getToken();

  // The media leg rides the bot token (the only GitHub identity) and the
  // facet's own caps: no token ⇒ no media, text-only events as before; the
  // GITHUB_MEDIA_ENABLED switch rides the shared law's own `enabled` gate
  // (a disabled leg notes and skips, never ingests).
  const media =
    api === undefined
      ? undefined
      : new MediaPipeline({
          api,
          state,
          kernelUrl: config.kernelUrl,
          token,
          endpointId: config.endpointId,
          maxBytes: config.mediaMaxBytes,
          enabled: config.mediaEnabled,
          assetHosts: assetHostsFor(config.githubUrl),
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
      manifest: githubAdapterManifest(config.team),
      normalizeWebhook: createGitHubNormalizer({
        ctx,
        webhookSecret: config.webhookSecret,
        api,
        ...(media === undefined ? {} : { media }),
        logger,
      }),
      handler: createGitHubHandler({
        api,
        state,
        // The reply-with-artifact path: the link-plane content GET rides our
        // own enrollment token.
        media: {
          download: (artifactId: string) =>
            fetchArtifactContent(config.kernelUrl, artifactId, token, deps.fetchImpl ?? fetch),
        },
        providerInstanceId: config.providerInstanceId,
        logger,
      }),
      logger,
      ...(deps.fetchImpl === undefined ? {} : { fetchImpl: deps.fetchImpl }),
    },
    `urn:ofac:provider:github:${tenant}`,
  );

  // GET /healthz only: the webhook HTTP server is GONE (the kernel sink owns
  // the web edge); the port stays published so an e2e boot probe has a
  // target.
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
    logger.info("github adapter ready", {
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
