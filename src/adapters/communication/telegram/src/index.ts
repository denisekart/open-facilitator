// @ofac/telegram-adapter — a communication adapter: a link-plane endpoint
// (enroll → capabilities → SignalR) with a Telegram Bot API listener in
// front (long polling by default — Telegram's webhook/poll exclusivity is
// resolved by TELEGRAM_MODE). Inbound updates normalize to the chat-domain
// events (data.provider = "telegram"); inbound media lands in the kernel
// artifact store BEFORE publish so the event carries sourceRefs; outbound
// command.communication.* execute over the Bot API under the chat-ledger
// idempotency law.
import { createServer, type Server } from "node:http";
import { randomBytes } from "node:crypto";
import type { LinkClient, LinkLogger } from "@ofac/workspace-link";
import { enroll } from "@ofac/workspace-link";
import type { NormalizedEvent as ProviderCoreEvent } from "@ofac/provider-core";
import { runProviderAdapter } from "@ofac/provider-core/node";
import type { ArtifactRef } from "@ofac/protocol";
import { admissionForChat, type AdapterConfig } from "./config.js";
import {
  type NormalizeContext,
  type NormalizeResult,
  type NormalizedEvent,
  type TgUpdate,
} from "./normalize.js";
import { ChatLedger } from "./chat-ledger.js";
import { MediaPipeline, fetchArtifactContent } from "./media.js";
import { TelegramClient, type TelegramApi } from "./tg-client.js";
import { UpdatesListener } from "./updates-listener.js";
import { communicationManifest, createAdapterHandler, type AdapterIdentity } from "./handler.js";
import { SECRET_HEADER, createWebhookNormalizer, decodeUpdate, ingest } from "./webhook.js";
import { verifyTokenToken } from "@ofac/provider-core/node";

export {
  AdapterConfigError,
  admissionForChat,
  parseChats,
  parseEnvConfig,
  resolveBotToken,
  deriveTenant,
  type AdapterConfig,
  type ChatEntry,
} from "./config.js";

export {
  MESSAGE_EVENT,
  MENTION_EVENT,
  REACTION_EVENT,
  mentionsToken,
  normalizeUpdate,
  collectAttachments,
  rootIdFor,
  type NormalizeContext,
  type NormalizeResult,
  type NormalizedEvent,
  type TgUpdate,
  type TgMessage,
} from "./normalize.js";
export {
  TgError,
  TelegramClient,
  type TelegramApi,
  type TgFile,
  type TgUser,
} from "./tg-client.js";
export { ChatLedger, parseObjectIds, type CommandLedgerEntry } from "./chat-ledger.js";
export {
  ACK_EMOJI,
  CMD_ACK,
  CMD_REACT,
  CMD_REPLY,
  CMD_UPDATE,
  COMMUNICATION_COMMANDS,
  chunkText,
  communicationManifest,
  createAdapterHandler,
  type AdapterIdentity,
  type ArtifactSource,
  type HandlerDeps,
} from "./handler.js";
export { MediaPipeline, fetchArtifactContent, type MediaPipelineDeps } from "./media.js";
export { UpdatesListener, type UpdatesListenerOptions } from "./updates-listener.js";
export {
  SECRET_HEADER,
  createWebhookNormalizer,
  decodeUpdate,
  ingest,
  type WebhookNormalizerDeps,
} from "./webhook.js";

export interface RunHandle {
  client: LinkClient;
  ready: Promise<void>;
  publish(update: TgUpdate): Promise<void>;
  shutdown(): Promise<void>;
}

export interface RunDeps {
  /** Injected provider client (tests run the whole loop against a fake). */
  api?: TelegramApi;
  fetchImpl?: typeof fetch;
}

const ONBOARDING = (chatId: string): string =>
  `Chat id ${chatId} is not configured for Facilitator. Add it to TELEGRAM_CHATS as ${chatId}:<label> to route this conversation.`;

export async function run(
  config: AdapterConfig,
  logger: LinkLogger,
  deps: RunDeps = {},
): Promise<RunHandle> {
  const state = new ChatLedger(config.statePath);
  const api =
    deps.api ??
    new TelegramClient(
      config.apiBase,
      config.botToken,
      ...(deps.fetchImpl === undefined ? [] : [deps.fetchImpl]),
    );

  // Bot identity: getMe (health payload + echo authorship). An
  // OFAC_BOT_USER_ID override is honored; a mismatch against getMe is
  // logged, not fatal (the kernel resolves Providers:Telegram:BotUserId
  // separately).
  const me = await api.getMe();
  if (config.botUserId !== undefined && config.botUserId !== String(me.id)) {
    logger.warn("OFAC_BOT_USER_ID disagrees with getMe", {
      configured: config.botUserId,
      getMe: String(me.id),
    });
  }
  const botUserId = config.botUserId ?? String(me.id);
  const identity: AdapterIdentity = {
    botUserId,
    providerInstanceId: config.providerInstanceId,
    tenantOrOrganizationId: config.tenant,
  };
  const source = `urn:ofac:provider:telegram:${config.tenant}`;

  // Our own link credential for the artifact client + content GETs: the
  // adapter-face owns the CLIENT's token, so media rides an enrollment of
  // its own (same secret/endpoint — the kernel issues per-enrollment
  // tokens; 401 re-enrolls).
  const linkToken = { value: "" };
  const enrollSelf = async (): Promise<string> => {
    const response = await enroll(
      {
        baseUrl: config.kernelUrl,
        token: config.enrollSecret,
        endpointId: config.endpointId,
        instanceId: `tgm-${randomBytes(6).toString("hex")}`,
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

  const media = new MediaPipeline({
    api,
    state,
    kernelUrl: config.kernelUrl,
    token,
    endpointId: config.endpointId,
    maxBytes: config.mediaMaxBytes,
    enabled: config.mediaEnabled,
    logger,
    ...(deps.fetchImpl === undefined ? {} : { fetchImpl: deps.fetchImpl }),
  });

  const normalizeCtx: NormalizeContext = {
    botUserId,
    botUsername: me.username,
    providerInstanceId: config.providerInstanceId,
    tenantOrOrganizationId: config.tenant,
    chatTypeFor: (chatId) => state.chatType(chatId),
    admissionFor: (chatId) => admissionForChat(config.chats, chatId),
    boundRootIds: () => state.boundRootIds(),
    mediaArtifactsFor: (messageId) => media.artifactsFor(messageId),
  };

  // The one-time onboarding reply for an unconfigured chat (never an event):
  // the kv guard `notice:<chatId>` makes it exactly-once per chat, forever.
  const onUnconfigured = async (result: { chatId: string; text: string }): Promise<void> => {
    void result;
    if (!state.setKvOnce(`notice:${result.chatId}`, new Date().toISOString())) return;
    try {
      await api.sendMessage({ chat_id: result.chatId, text: ONBOARDING(result.chatId) });
      logger.info("onboarding notice sent", { chatId: result.chatId });
    } catch (error) {
      // The chat may be unreachable (bot not added / DM never opened):
      // clear the guard so a later mention retries.
      state.setKv(`notice:${result.chatId}`, "");
      logger.warn("onboarding notice failed", { chatId: result.chatId, error: String(error) });
    }
  };

  // causationid rides a mutable closure (mattermost law): decorate reads it
  // synchronously inside publish, before its first await interleaves.
  let pendingCausationid: string | undefined;
  const handle = await runProviderAdapter(
    {
      kernelUrl: config.kernelUrl,
      enrollSecret: config.enrollSecret,
      endpointId: config.endpointId,
      team: config.team,
      statePath: config.statePath,
      manifest: communicationManifest(config.team),
      ...(config.mode === "webhook" && config.webhookSecret !== undefined
        ? {
            normalizeWebhook: createWebhookNormalizer({
              secret: config.webhookSecret,
              state,
              context: normalizeCtx,
              media,
              onUnconfigured,
              logger,
            }),
          }
        : { normalizeWebhook: undefined }),
      handler: createAdapterHandler({
        api,
        state,
        identity,
        media: {
          download: (artifactId: string) =>
            fetchArtifactContent(config.kernelUrl, artifactId, token, deps.fetchImpl ?? fetch),
        },
        logger,
      }),
      logger,
      // Telegram envelope law: the subject is provider/telegram/<messageId>
      // (echo + inbound alike); the core spreads decorate LAST.
      decorate: (event) => {
        const data = event.data as unknown as {
          reference: { messageId?: string };
          providerDeliveryId: string;
        };
        return {
          subject: `provider/telegram/${String(
            data.reference.messageId ?? data.providerDeliveryId,
          )}`,
          ...(pendingCausationid === undefined ? {} : { causationid: pendingCausationid }),
        };
      },
      ...(deps.fetchImpl === undefined ? {} : { fetchImpl: deps.fetchImpl }),
    },
    source,
  );

  const publish = async (event: NormalizedEvent, causationid?: string): Promise<void> => {
    pendingCausationid = causationid;
    try {
      await handle.publish(event as unknown as ProviderCoreEvent);
    } finally {
      pendingCausationid = undefined;
    }
  };

  /** The shared ingest step: media → classify → publish (or onboard). The
   *  private-chat ledger entry (`dm:<userId> → chatId`) is recorded here so
   *  the handler can resolve a DmTarget once the human has said /start. */
  const publishUpdate = async (update: TgUpdate): Promise<NormalizeResult> => {
    const result = await ingest(update, { state, context: normalizeCtx, media, onUnconfigured });
    const message = update.message ?? update.edited_message;
    if (message !== undefined) {
      if (message.chat.type === "private" && message.from !== undefined) {
        state.setKv(`dm:${message.from.id}`, String(message.chat.id));
      }
      const topic = message.message_thread_id;
      if (topic !== undefined) state.markTopic(String(message.chat.id), String(topic), Date.now());
    }
    if (result.kind === "events") {
      for (const event of result.events) await publish(event);
    }
    return result;
  };

  const listener = new UpdatesListener({
    api,
    state,
    onUpdate: async (update) => {
      await publishUpdate(update);
    },
    logger,
  });

  let health: Server | undefined;
  let webhook: Server | undefined;

  const ready = (async (): Promise<void> => {
    await handle.ready;
    if (config.mode === "poll") {
      // Poll/webhook exclusivity: a stale webhook makes getUpdates error,
      // so delete it first (without dropping the pending queue).
      try {
        await api.deleteWebhook({ drop_pending_updates: false });
      } catch (error) {
        logger.warn("deleteWebhook failed (continuing)", { error: String(error) });
      }
      void listener.start();
    } else {
      // The ordering law: register the webhook BEFORE binding the port, so
      // a crash cannot leave Telegram POSTing to a closed listener.
      await api.setWebhook({
        url: config.webhookUrl ?? "",
        secret_token: config.webhookSecret ?? "",
        max_connections: 8,
        allowed_updates: ["message", "edited_message", "message_reaction"],
      });
      webhook = createServer((req, res) => {
        const chunks: Buffer[] = [];
        req.on("data", (chunk: Buffer) => chunks.push(chunk));
        req.on("end", () => {
          if (req.method === "GET" && req.url === "/healthz") {
            res.writeHead(200, { "content-type": "application/json" });
            res.end(JSON.stringify(healthPayload()));
            return;
          }
          if (req.method !== "POST" || req.url !== "/webhook") {
            res.writeHead(404);
            res.end();
            return;
          }
          const headers = req.headers as Record<string, string | string[] | undefined>;
          const header = headers[SECRET_HEADER];
          const secret = config.webhookSecret ?? "";
          if (!verifyTokenToken(typeof header === "string" ? header : undefined, secret)) {
            res.writeHead(401);
            res.end();
            return;
          }
          // 202-immediately law: answer, then process.
          res.writeHead(202);
          res.end();
          const update = decodeUpdate({
            provider: "telegram",
            contentType: "application/json",
            rawBody: Buffer.concat(chunks),
            headers: lowered(headers),
          });
          if (update === undefined) return;
          if (!state.setKvOnce(`seen:${update.update_id}`, "1")) return;
          void publishUpdate(update).catch((error: unknown) => {
            logger.error("webhook ingest failed", { error: String(error) });
          });
        });
      });
      {
        const { promise, resolve } = Promise.withResolvers<void>();
        webhook?.listen(config.webhookListenPort, resolve);
        await promise;
      }
    }
    if (config.healthPort !== undefined) {
      health = createServer((req, res) => {
        if (req.url === "/healthz") {
          res.writeHead(200, { "content-type": "application/json" });
          res.end(JSON.stringify(healthPayload()));
        } else {
          res.writeHead(404);
          res.end();
        }
      });
      {
        const { promise, resolve } = Promise.withResolvers<void>();
        health?.listen(config.healthPort, resolve);
        await promise;
      }
    }
    logger.info("telegram adapter ready", {
      endpointId: config.endpointId,
      botId: botUserId,
      mode: config.mode,
      chats: config.chats.length,
      kernel: config.kernelUrl,
    });
  })();

  const healthPayload = (): { ok: true; botId: string; mode: string; chats: number } & {
    poll?: string;
  } => ({
    ok: true,
    botId: botUserId,
    mode: config.mode,
    chats: config.chats.length,
    ...(config.mode === "poll" ? { poll: listener.state } : {}),
  });

  return {
    client: handle.client,
    ready,
    publish: async (update) => {
      await publishUpdate(update);
    },
    async shutdown() {
      await listener.close();
      if (webhook !== undefined) {
        const { promise, resolve } = Promise.withResolvers<void>();
        webhook?.close(() => resolve());
        await promise;
      }
      if (health !== undefined) {
        const { promise, resolve } = Promise.withResolvers<void>();
        health?.close(() => resolve());
        await promise;
      }
      await handle.shutdown();
      state.close();
    },
  };
}

function lowered(headers: Record<string, string | string[] | undefined>): Record<string, string> {
  const out: Record<string, string> = {};
  for (const [key, value] of Object.entries(headers)) {
    if (typeof value === "string") out[key.toLowerCase()] = value;
    else if (Array.isArray(value) && value[0] !== undefined) out[key.toLowerCase()] = value[0];
  }
  return out;
}

export type { ArtifactRef };
