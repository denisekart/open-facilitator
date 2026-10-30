// @ofac/mattermost-adapter — the first communication adapter: a link-plane
// endpoint (enroll → capabilities → SignalR) with a Mattermost bot-WebSocket
// listener in front (D1: 11.11 webhooks carry no signing secret, so inbound
// trust rides the bot credential instead). Inbound posts/reactions normalize
// to the central chat-domain events (data.provider = "mattermost"); outbound
// command.communication.* execute over REST v4 under the post-ledger
// idempotency law.
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
import {
  asPost,
  asReaction,
  normalizePost,
  normalizeReaction,
  parseJsonField,
  type NormalizeContext,
  type NormalizedEvent,
} from "./normalize.js";
import { AdapterState } from "./post-ledger.js";
import { MattermostClient, type MattermostApi, type WsEvent } from "./mm-client.js";
import { WsListener } from "./ws-listener.js";
import { communicationManifest, createAdapterHandler, type AdapterIdentity } from "./handler.js";

export { AdapterConfigError, deriveTenant, parseEnvConfig, type AdapterConfig } from "./config.js";
export {
  MmError,
  MattermostClient,
  type MattermostApi,
  type MmChannel,
  type MmUser,
  type WsEvent,
} from "./mm-client.js";
export {
  MESSAGE_EVENT,
  MENTION_EVENT,
  REACTION_EVENT,
  asPost,
  asReaction,
  normalizePost,
  normalizeReaction,
  parseJsonField,
  type MmPost,
  type MmReaction,
  type NormalizeContext,
  type NormalizedEvent,
} from "./normalize.js";
export { AdapterState, type ChannelType, type PostLedgerEntry } from "./post-ledger.js";
export { WsListener, type WsListenerOptions } from "./ws-listener.js";
export {
  CMD_ACK,
  CMD_REACT,
  CMD_REPLY,
  CMD_UPDATE,
  COMMUNICATION_COMMANDS,
  communicationManifest,
  createAdapterHandler,
  type AdapterIdentity,
  type HandlerDeps,
} from "./handler.js";

export interface RunHandle {
  client: LinkClient;
  ready: Promise<void>;
  shutdown(): Promise<void>;
}

/** Startup-replay window when no persisted cursor exists. */
const REPLAY_FALLBACK_MS = 24 * 60 * 60 * 1000;

export interface RunDeps {
  /** Injected provider client (tests run the whole loop against a fake). */
  api?: MattermostApi;
  fetchImpl?: typeof fetch;
  onState?(state: "up" | "down"): void;
}

export async function run(
  config: AdapterConfig,
  logger: LinkLogger,
  deps: RunDeps = {},
): Promise<RunHandle> {
  const tenant = deriveTenant(config.mmUrl);
  const state = new AdapterState(config.statePath);
  const api =
    deps.api ?? new MattermostClient(config.mmUrl, config.botToken, state, deps.fetchImpl);

  const bot = await api.me();
  const identity: AdapterIdentity = {
    botUserId: bot.id,
    providerInstanceId: config.providerInstanceId,
    tenantOrOrganizationId: tenant,
  };
  const source = `urn:ofac:provider:mattermost:${tenant}`;

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
  const { digest } = await postCapabilities(communicationManifest(config.team), {
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
    handler: createAdapterHandler({ api, state, identity, logger }),
    logger,
  });

  const publish = async (event: NormalizedEvent, causationid?: string): Promise<void> => {
    const env: EnvelopeEvent = buildEvent(event.type, {
      source,
      subject: String(event.data.reference.messageId ?? event.data.providerDeliveryId),
      data: event.data as unknown as Record<string, unknown>,
      ...(causationid === undefined ? {} : { causationid }),
    });
    await client.publishEvent(env);
  };

  const normalizeCtx: NormalizeContext = {
    botUserId: bot.id,
    botUsername: bot.username,
    botMention: config.botMention,
    providerInstanceId: config.providerInstanceId,
    tenantOrOrganizationId: tenant,
    channelTypeFor: async (channelId) => {
      const cached = state.channelType(channelId);
      if (cached !== undefined) return cached;
      try {
        const channel = await api.channel(channelId);
        return channel.type;
      } catch {
        return undefined;
      }
    },
    boundRootIds: () => state.boundRootIds(),
  };

  const onWsEvent = async (event: WsEvent): Promise<void> => {
    if (event.event === "posted") {
      const raw = parseJsonField(event.data?.["post"] as string | undefined);
      const post = raw === undefined ? undefined : asPost(raw);
      if (post === undefined) return;
      const normalized = await normalizePost(post, raw as Record<string, unknown>, normalizeCtx);
      if (normalized !== null) await publish(normalized);
      return;
    }
    if (event.event === "reaction_added") {
      const raw = parseJsonField(event.data?.["reaction"] as string | undefined);
      const reaction = raw === undefined ? undefined : asReaction(raw);
      const channelId = event.broadcast?.["channel_id"];
      if (reaction === undefined || typeof channelId !== "string") return;
      const normalized = await normalizeReaction(
        reaction,
        raw as Record<string, unknown>,
        channelId,
        normalizeCtx,
      );
      if (normalized !== null) await publish(normalized);
    }
  };

  const listener = new WsListener({
    mmUrl: config.mmUrl,
    botToken: config.botToken,
    api,
    onEvent: onWsEvent,
    onState: deps.onState ?? (() => undefined),
    logger,
  });

  let health: Server | undefined;

  const ready = (async () => {
    await client.start();
    // Startup replay (mention-only, per plan): posts newer than the persisted
    // cursor (else last 24h) across the bot's channels; the provider-events
    // ledger kernel-side dedupes overlaps with the live WS stream.
    const since = Number(state.getKv("last_seen_create_at") ?? Date.now() - REPLAY_FALLBACK_MS);
    try {
      for (const channelId of await api.botChannels()) {
        for (const post of await api.channelPosts(channelId, since)) {
          const normalized = await normalizePost(
            post,
            post as unknown as Record<string, unknown>,
            normalizeCtx,
            {
              replay: true,
            },
          );
          if (normalized !== null) await publish(normalized);
        }
      }
    } catch (error) {
      logger.warn("startup replay failed", { error: String(error) });
    }
    await listener.start();
    if (config.healthPort !== undefined) {
      health = createServer((req, res) => {
        if (req.url === "/healthz") {
          res.writeHead(200, { "content-type": "application/json" });
          res.end(JSON.stringify({ ok: true, botId: bot.id, ws: listener.state }));
        } else {
          res.writeHead(404);
          res.end();
        }
      });
      await new Promise<void>((resolve) => health?.listen(config.healthPort, resolve));
    }
    logger.info("mattermost adapter ready", {
      endpointId: config.endpointId,
      botId: bot.id,
      kernel: config.kernelUrl,
    });
  })();

  // Persist the cursor continuously: any later boot replays only after this point.
  const cursor = setInterval(() => {
    state.setKv("last_seen_create_at", String(Date.now()));
  }, 30_000);

  return {
    client,
    ready,
    async shutdown() {
      clearInterval(cursor);
      state.setKv("last_seen_create_at", String(Date.now()));
      await listener.close();
      if (health !== undefined)
        await new Promise<void>((resolve) => health?.close(() => resolve()));
      await client.stop("adapter-shutdown");
      state.close();
    },
  };
}
