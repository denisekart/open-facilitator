// @ofac/forgejo-adapter — the inbound forge provider module (Plan 23 step 3):
// a link-plane endpoint (enroll → capabilities → SignalR) fronted by an HMAC
// webhook receiver. Inbound forge webhooks normalize to the central forge
// domain events (data.provider = "forgejo"); outbound
// command.communication.reply.v1 executes as a bot-identity comment under the
// comment-ledger idempotency law (decision 4). Stateless apart from the
// SQLite comment ledger — deployable next to the kernel or anywhere the forge
// can reach and the kernel link plane is dialable.
import { createServer, type Server } from "node:http";
import { randomBytes } from "node:crypto";
import type { EnvelopeEvent } from "@ofac/protocol";
import {
  buildEvent,
  createLinkClient,
  enroll,
  type LinkClient,
  type LinkLogger,
  type LinkTokenProvider,
} from "@ofac/workspace-link";
import { SignalrTransport, SqliteLedger, postCapabilities } from "@ofac/workspace-link/node";
import { deriveTenant, type AdapterConfig } from "./config.js";
import { AdapterState } from "./comment-ledger.js";
import { ForgejoClient, type ForgejoApi } from "./forgejo-client.js";
import {
  normalizeIssueComment,
  normalizeIssues,
  normalizePullRequest,
  normalizeReview,
  normalizeStatus,
  type NormalizeContext,
  type NormalizedEvent,
} from "./normalize.js";
import { readWebhookRequest, type WebhookVerdict } from "./webhook.js";
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
  PULL_REQUEST_EVENT,
  normalizeIssueComment,
  normalizeIssues,
  normalizePullRequest,
  normalizeReview,
  normalizeStatus,
  type NormalizeContext,
  type NormalizedEvent,
} from "./normalize.js";
export { readWebhookRequest, verifySignature, type WebhookVerdict } from "./webhook.js";
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
  const source = `urn:ofac:provider:forgejo:${tenant}`;
  const api =
    deps.api ??
    (config.botToken === undefined
      ? undefined
      : new ForgejoClient(config.forgejoUrl, config.botToken, deps.fetchImpl));
  const state = new AdapterState(config.statePath);
  const normalizeCtx: NormalizeContext = {
    providerInstanceId: config.providerInstanceId,
    tenantOrOrganizationId: tenant,
    workLabels: new Set(config.workLabels.map((label) => label.toLowerCase())),
    botUser: config.botUser,
  };

  // ── link plane: enroll (secret mode) → capabilities → client ───────────
  const activeToken = { value: "" };
  const enrollSelf = async (): Promise<void> => {
    const response = await enroll({
      baseUrl: config.kernelUrl,
      token: config.enrollSecret,
      endpointId: config.endpointId,
      instanceId: `lnk-${randomBytes(8).toString("hex")}`,
      trustMode: "isolated-executor",
      role: "child",
      team: config.team,
    });
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

  const { digest } = await postCapabilities(forgejoAdapterManifest(config.team), {
    baseUrl: config.kernelUrl,
    getToken: token.getToken,
    ...(deps.fetchImpl === undefined ? {} : { fetchImpl: deps.fetchImpl }),
  });

  const client = createLinkClient({
    transport: new SignalrTransport({ baseUrl: config.kernelUrl, token }),
    ledger: new SqliteLedger(config.statePath),
    endpointId: config.endpointId,
    capabilityManifestDigest: digest,
    token,
    handler: createForgejoHandler({ api, state, logger }),
    logger,
  });

  const publish = async (event: NormalizedEvent, causationid?: string): Promise<void> => {
    const env: EnvelopeEvent = buildEvent(event.type, {
      source,
      subject: String(event.data.reference.subjectRef ?? event.data.providerDeliveryId),
      data: event.data as unknown as Record<string, unknown>,
      ...(causationid === undefined ? {} : { causationid }),
    });
    await client.publishEvent(env);
  };

  const dispatch = async (delivery: WebhookVerdict & { kind: "delivery" }): Promise<void> => {
    const { deliveryId, eventKind, payload } = delivery;
    let normalized: NormalizedEvent | null = null;
    if (eventKind === "issues") {
      normalized = normalizeIssues(payload, deliveryId, normalizeCtx);
    } else if (eventKind === "issue_comment") {
      normalized = normalizeIssueComment(payload, deliveryId, normalizeCtx);
    } else if (eventKind === "pull_request") {
      normalized = normalizePullRequest(payload, deliveryId, normalizeCtx);
    } else if (
      eventKind === "pull_request_review" ||
      eventKind === "review" ||
      (eventKind.startsWith("pull_request_") && payload["action"] === "reviewed")
    ) {
      // 16.x fires review submissions under per-action kind names
      // (`pull_request_approved` with body action `reviewed`, verified
      // live); review requests share the prefix but not the action, so
      // they stay dropped. The review body shape is the same — all
      // normalize onto `reviewed`.
      normalized = normalizeReview(payload, deliveryId, normalizeCtx);
    } else if (eventKind === "status") {
      // The sha→number resolution is I/O, so it lives HERE, not in normalize.
      const repo =
        typeof payload["repository"] === "object" && payload["repository"] !== null
          ? (payload["repository"] as Record<string, unknown>)["full_name"]
          : undefined;
      const sha = typeof payload["sha"] === "string" ? payload["sha"] : undefined;
      let number: string | undefined;
      if (typeof repo === "string" && sha !== undefined && api !== undefined) {
        try {
          const prs = await api.prsForHeadSha(repo, sha);
          number = prs[0] === undefined ? undefined : String(prs[0]);
        } catch (error) {
          logger.warn("status sha resolution failed", { error: String(error) });
        }
      }

      normalized = normalizeStatus(payload, deliveryId, number, normalizeCtx);
    }

    if (normalized === null) {
      logger.info("webhook dropped", { eventKind, deliveryId });
      return;
    }

    await publish(normalized);
    logger.info("webhook normalized", { type: normalized.type, deliveryId });
  };

  let health: Server | undefined;
  const ready = (async () => {
    await client.start();
    health = createServer((req, res) => {
      if (req.url === "/healthz") {
        res.writeHead(200, { "content-type": "application/json" });
        res.end(JSON.stringify({ ok: true, endpointId: config.endpointId }));
        return;
      }

      if (req.method === "POST" && req.url === config.hookPath) {
        void readWebhookRequest(req, res, config.webhookSecret).then(async (verdict) => {
          try {
            if (verdict.kind === "delivery") await dispatch(verdict);
          } catch (error) {
            logger.error("webhook dispatch failed", { error: String(error) });
          }
        });
        return;
      }

      res.writeHead(404);
      res.end();
    });
    await new Promise<void>((resolve) => health?.listen(config.port, resolve));
    logger.info("forgejo adapter ready", {
      endpointId: config.endpointId,
      port: config.port,
      kernel: config.kernelUrl,
    });
  })();

  return {
    client,
    ready,
    async shutdown() {
      if (health !== undefined) {
        await new Promise<void>((resolve) => health?.close(() => resolve()));
      }

      await client.stop("adapter-shutdown");
      state.close();
    },
  };
}
