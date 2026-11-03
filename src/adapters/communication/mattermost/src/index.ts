// @ofac/mattermost-adapter — the first communication adapter: a link-plane
// endpoint (enroll → capabilities → SignalR) with a Mattermost bot-WebSocket
// listener in front (D1: 11.11 webhooks carry no signing secret, so inbound
// trust rides the bot credential instead). Inbound posts/reactions normalize
// to the central chat-domain events (data.provider = "mattermost"); outbound
// command.communication.* execute over REST v4 under the post-ledger
// idempotency law.
import { createServer, type Server } from "node:http";
import type { LinkClient, LinkLogger } from "@ofac/workspace-link";
import type { NormalizedEvent as ProviderCoreEvent } from "@ofac/provider-core";
import { runProviderAdapter } from "@ofac/provider-core/node";
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

  // ── link plane: the @ofac/provider-core adapter face owns enroll (secret
  // mode, 401 re-enroll) → capabilities → SignalrTransport + SqliteLedger
  // client wiring; mattermost has NO webhook edge (normalizeWebhook:
  // undefined — WsListener keeps inbound trust on the bot credential).
  // causationid rides a mutable closure: no WS/replay call site passes one
  // today, but the parameter's behavior is preserved exactly — decorate
  // reads it synchronously inside handle.ready's client.start, before its
  // first await can interleave another publish.
  let pendingCausationid: string | undefined;
  const handle = await runProviderAdapter(
    {
      kernelUrl: config.kernelUrl,
      enrollSecret: config.enrollSecret,
      endpointId: config.endpointId,
      team: config.team,
      statePath: config.statePath,
      manifest: communicationManifest(config.team),
      normalizeWebhook: undefined,
      handler: createAdapterHandler({ api, state, identity, logger }),
      logger,
      // Mattermost envelope law: the subject is the message id (else the
      // delivery id), never the generic subjectRef. The core spreads the
      // decorate result LAST, so this override wins.
      decorate: (event) => {
        const data = event.data as unknown as {
          reference: { messageId?: string };
          providerDeliveryId: string;
        };
        return {
          subject: String(data.reference.messageId ?? data.providerDeliveryId),
          ...(pendingCausationid === undefined ? {} : { causationid: pendingCausationid }),
        };
      },
      ...(deps.fetchImpl === undefined ? {} : { fetchImpl: deps.fetchImpl }),
    },
    source,
  );

  // The chat-domain data shapes (message/mention/reaction) are not members
  // of the core's forge-domain union, so the event rides handle.publish
  // through the widening cast; the envelope bytes are unchanged.
  const publish = async (event: NormalizedEvent, causationid?: string): Promise<void> => {
    pendingCausationid = causationid;
    try {
      await handle.publish(event as unknown as ProviderCoreEvent);
    } finally {
      pendingCausationid = undefined;
    }
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

  const ready = handle.ready.then(async () => {
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
  });

  // Persist the cursor continuously: any later boot replays only after this point.
  const cursor = setInterval(() => {
    state.setKv("last_seen_create_at", String(Date.now()));
  }, 30_000);

  return {
    client: handle.client,
    ready,
    async shutdown() {
      clearInterval(cursor);
      state.setKv("last_seen_create_at", String(Date.now()));
      await listener.close();
      if (health !== undefined)
        await new Promise<void>((resolve) => health?.close(() => resolve()));
      await handle.shutdown();
      state.close();
    },
  };
}
