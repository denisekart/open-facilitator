// The webhook fan-out receiver (adapter face): the kernel sink delivers
// `command.provider.webhook.v1` to this endpoint; the seam decodes the
// verbatim bytes/headers, runs the provider's normalizer (signature
// verification is provider-local — the kernel rode the bytes untouched),
// and publishes each normalized event. Outcome law: a bad signature answers
// `rejected` (a receipt, never a throw — the kernel already answered the
// forge 202, the receipt records why nothing happened); a well-signed
// delivery of unmappable events answers `accepted` having published zero.
import { createHmac, timingSafeEqual } from "node:crypto";
import type { EnvelopeCommand } from "@ofac/protocol";
import type { CommandContext, CommandOutcome, LinkHandler, LinkLogger } from "@ofac/workspace-link";
import type { NormalizedEvent } from "./normalize-shared.js";

export const WEBHOOK_COMMAND_TYPE = "org.facilitator.command.provider.webhook.v1";

export interface WebhookDelivery {
  /** Provider token from the fan-out envelope (the sink path segment). */
  provider: string;
  contentType: string;
  /** Verbatim raw bytes (HMAC verifiability). */
  rawBody: Buffer;
  /** Verbatim headers, keys lowercased (HTTP header names are case-insensitive). */
  headers: Record<string, string>;
}

export type WebhookVerdict =
  | { kind: "bad-signature" }
  /** Normalized events to publish; empty ⇒ dropped (audit only). */
  | { kind: "normalized"; events: NormalizedEvent[] };

export type WebhookNormalizer = (
  delivery: WebhookDelivery,
) => WebhookVerdict | Promise<WebhookVerdict>;

/** HMAC-SHA256 verification over the raw bytes, tolerant of both header
 *  forms the Gitea family + GitHub emit: bare hex (`X-Gitea-Signature`) and
 *  the `sha256=<hex>` prefix (`X-Hub-Signature-256`). Constant-time compare. */
export function verifyHmacSha256(
  rawBody: Buffer,
  secret: string,
  signatureHeader: string | undefined,
): boolean {
  if (signatureHeader === undefined || signatureHeader === "") return false;
  const provided = signatureHeader.startsWith("sha256=")
    ? signatureHeader.slice(7)
    : signatureHeader;
  const expected = createHmac("sha256", secret).update(rawBody).digest("hex");
  const a = Buffer.from(provided.toLowerCase(), "hex");
  const b = Buffer.from(expected, "hex");
  return a.length === b.length && timingSafeEqual(a, b);
}

/** Constant-time secret-token compare (GitLab `X-Gitlab-Token` style: no
 *  body HMAC exists, the shared token IS the proof). */
export function verifyTokenToken(tokenHeader: string | undefined, secret: string): boolean {
  if (tokenHeader === undefined || tokenHeader === "") return false;
  const a = Buffer.from(tokenHeader, "utf8");
  const b = Buffer.from(secret, "utf8");
  return a.length === b.length && timingSafeEqual(a, b);
}

export function decodeWebhookCommand(cmd: EnvelopeCommand): WebhookDelivery | undefined {
  const data = cmd.data as unknown as Record<string, unknown> | undefined;
  if (typeof data !== "object" || data === null) return undefined;
  const provider = data["provider"];
  const body = data["body"];
  const contentType = data["contentType"];
  const headers = data["headers"];
  if (
    typeof provider !== "string" ||
    typeof body !== "string" ||
    typeof contentType !== "string" ||
    typeof headers !== "object" ||
    headers === null
  ) {
    return undefined;
  }

  const lowered: Record<string, string> = {};
  for (const [key, value] of Object.entries(headers as Record<string, unknown>)) {
    if (typeof value === "string") lowered[key.toLowerCase()] = value;
  }

  return {
    provider,
    contentType,
    rawBody: Buffer.from(body, "base64"),
    headers: lowered,
  };
}

export interface WebhookReceiverDeps {
  /** Absent ⇒ this adapter takes no webhook fan-out (chat providers); a
   *  delivered webhook command answers `unsupported` as a receipt. */
  normalize: WebhookNormalizer | undefined;
  /** Publish one normalized provider event over the link plane. */
  publish(event: NormalizedEvent): Promise<void>;
  logger: LinkLogger;
  /** The provider's own command handler (reply/react/etc.), when any. */
  handler?: LinkHandler | undefined;
}

/** A LinkHandler that consumes the webhook fan-out and delegates everything
 *  else to the provider's handler. */
export function createWebhookReceiver(deps: WebhookReceiverDeps): LinkHandler {
  return {
    async onCommand(cmd: EnvelopeCommand, ctx: CommandContext): Promise<CommandOutcome> {
      if (cmd.type !== WEBHOOK_COMMAND_TYPE) {
        return deps.handler?.onCommand(cmd, ctx) ?? { kind: "unsupported" };
      }

      if (deps.normalize === undefined) {
        return deps.handler?.onCommand(cmd, ctx) ?? { kind: "unsupported" };
      }

      const delivery = decodeWebhookCommand(cmd);
      if (delivery === undefined) {
        return { kind: "rejected", reason: "malformed-webhook-data" };
      }

      let verdict: WebhookVerdict;
      try {
        verdict = await deps.normalize(delivery);
      } catch (error) {
        deps.logger.error("webhook normalize failed", { error: String(error) });
        return { kind: "rejected", reason: "normalize-failed" };
      }

      if (verdict.kind === "bad-signature") {
        deps.logger.warn("webhook rejected: bad signature", { provider: delivery.provider });
        return { kind: "rejected", reason: "bad-signature" };
      }

      for (const event of verdict.events) {
        await deps.publish(event);
      }

      deps.logger.info("webhook fan-out processed", {
        provider: delivery.provider,
        published: verdict.events.length,
      });
      return { kind: "accepted" };
    },
  };
}
