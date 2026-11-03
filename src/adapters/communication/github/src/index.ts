// @ofac/github-adapter — the inbound GitHub provider module: a link-plane
// endpoint on the @ofac/provider-core adapter face (enroll → capabilities →
// SignalR + webhook fan-out receiver). The kernel sink
// (`POST /api/ingress/webhooks/github`, the only web-exposed point) rides
// verbatim GitHub deliveries here as `command.provider.webhook.v1`; the
// provider-core receiver seam verifies the HMAC and runs the GitHub→domain
// normalizer, publishing the central forge domain events
// (data.provider = "github"). Outbound command.communication.reply.v1
// executes as a bot-identity comment under the comment-ledger idempotency
// law. Stateless apart from the SQLite comment ledger — deployable anywhere
// the kernel link plane is dialable.
import { createServer, type Server } from "node:http";
import type { LinkClient, LinkLogger } from "@ofac/workspace-link";
import { runProviderAdapter } from "@ofac/provider-core/node";
import { deriveTenant, type AdapterConfig } from "./config.js";
import { AdapterState } from "./comment-ledger.js";
import { GitHubClient, type GitHubApi } from "./github-client.js";
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
  type GitHubApi,
  type GitHubComment,
  type GitHubIssue,
  type GitHubPullRequest,
  type GitHubPullRequestDetail,
  type GitHubReviewComment,
} from "./github-client.js";
export { AdapterState, type CommentLedgerEntry } from "./comment-ledger.js";
export {
  CI_EVENT,
  ISSUE_EVENT,
  MENTION_EVENT,
  PULL_REQUEST_EVENT,
  normalizeCheckSuite,
  normalizeIssueComment,
  normalizeIssues,
  normalizePullRequest,
  normalizeReview,
  normalizeReviewComment,
  normalizeStatus,
  type NormalizeContext,
  type NormalizedEvent,
} from "./normalize.js";
export { createGitHubNormalizer, type GitHubNormalizerDeps } from "./webhook.js";
export {
  CMD_REPLY,
  createGitHubHandler,
  githubAdapterManifest,
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
      manifest: githubAdapterManifest(config.team),
      normalizeWebhook: createGitHubNormalizer({
        ctx,
        webhookSecret: config.webhookSecret,
        api,
        logger,
      }),
      handler: createGitHubHandler({ api, state, logger }),
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
