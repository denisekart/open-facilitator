// Webhook mode's WebhookNormalizer (the provider-core fan-out seam): the
// kernel sink (POST /api/ingress/webhooks/telegram) delivers the verbatim
// Update as command.provider.webhook.v1; this normalizer owns the two laws:
//   1. signature law — header `x-telegram-bot-api-secret-token` MUST equal
//      TELEGRAM_WEBHOOK_SECRET, compared constant-time via verifyTokenToken
//      (@ofac/provider-core); a mismatch is `bad-signature` ⇒ the receiver
//      answers the receipt `rejected` and publishes nothing.
//   2. delivery dedupe — an update_id already checkpointed in the ledger
//      (poll offset or a previous fan-out) answers `normalized` with ZERO
//      events: accepted, zero published, no double effect.
// Media runs BEFORE normalization returns (the pipeline is inside the async
// normalizer, so the event rides its refs).
import {
  verifyTokenToken,
  type WebhookDelivery,
  type WebhookNormalizer,
  type WebhookVerdict,
} from "@ofac/provider-core/node";
import type { ChatLedger } from "./chat-ledger.js";
import {
  normalizeUpdate,
  type NormalizeContext,
  type NormalizeResult,
  type TgUpdate,
} from "./normalize.js";
import type { MediaPipeline } from "./media.js";
import type { LinkLogger } from "@ofac/workspace-link";

export const SECRET_HEADER = "x-telegram-bot-api-secret-token";

export interface WebhookNormalizerDeps {
  secret: string;
  state: ChatLedger;
  context: NormalizeContext;
  media: MediaPipeline;
  /** The unconfigured-chat onboarding signal (same law as poll mode). */
  onUnconfigured(result: { chatId: string; text: string }): Promise<void>;
  logger: LinkLogger;
}

/** Decode a webhook body (base64 on the fan-out envelope) into an Update. */
export function decodeUpdate(delivery: WebhookDelivery): TgUpdate | undefined {
  let json: unknown;
  try {
    json = JSON.parse(delivery.rawBody.toString("utf8"));
  } catch {
    return undefined;
  }
  if (typeof json !== "object" || json === null) return undefined;
  const update = json as TgUpdate;
  return typeof update.update_id === "number" ? update : undefined;
}

export function createWebhookNormalizer(deps: WebhookNormalizerDeps): WebhookNormalizer {
  return async (delivery): Promise<WebhookVerdict> => {
    if (!verifyTokenToken(delivery.headers[SECRET_HEADER], deps.secret)) {
      return { kind: "bad-signature" };
    }
    const update = decodeUpdate(delivery);
    if (update === undefined) {
      deps.logger.warn("telegram webhook body is not an Update", {
        contentType: delivery.contentType,
      });
      return { kind: "normalized", events: [] };
    }
    // Dedupe law: the FIRST delivery of an update_id consumes the once-flag;
    // a kernel-side redelivery publishes zero events (accepted).
    if (!deps.state.setKvOnce(`seen:${update.update_id}`, "1")) {
      return { kind: "normalized", events: [] };
    }
    const result = await ingest(update, deps);
    if (result.kind === "events") return { kind: "normalized", events: result.events };
    return { kind: "normalized", events: [] };
  };
}

/** The shared ingest step (media first, then classify): the poll listener
 *  and the webhook normalizer run the SAME pipeline through this function. */
export async function ingest(
  update: TgUpdate,
  deps: Pick<WebhookNormalizerDeps, "state" | "context" | "media" | "onUnconfigured">,
): Promise<NormalizeResult> {
  const message = update.message ?? update.edited_message;
  if (message !== undefined) {
    deps.state.cacheChatType(String(message.chat.id), message.chat.type);
    // The never-fail-the-event law: media errors are contained in the
    // pipeline (logged skips); a total failure still publishes the text.
    await deps.media.processMessage(message);
    const topic = message.message_thread_id;
    if (topic !== undefined) {
      deps.state.markTopic(String(message.chat.id), String(topic), Date.now());
    }
  }
  const result = normalizeUpdate(update, deps.context);
  if (result.kind === "unconfigured-mention") await deps.onUnconfigured(result);
  return result;
}
