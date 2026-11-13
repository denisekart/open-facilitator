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
  /**
   * Telemetry seam (structural twin of @ofac/otel, dark by default): one
   * `provider.webhook` span per fan-out delivery + the ofac.provider.*
   * counters.
   */
  telemetry?: ProviderTelemetry | undefined;
  /** Provider identity for span tags + counter labels (forgejo, github…). */
  provider?: string | undefined;
  /**
   * Durable seen-delivery check (providerDeliveryId): a redelivery of an
   * already-processed id counts `duplicate`, not `normalized` again.
   * Adapters may persist this; the default keeps the last deliveries
   * in-memory (at-least-once fan-out duplicates are adjacent).
   */
  seen?: (providerDeliveryId: string) => boolean;
}

/** The counter + instrument surface the receiver uses (subset of
 *  @ofac/otel's TelemetryHandle, kept dependency-free like adapter-sdk). */
export interface ProviderTelemetry {
  readonly emitting: boolean;
  meter(name: string): {
    createCounter(
      name: string,
      options?: { description?: string },
    ): {
      add(delta: number, attributes?: Record<string, string>): void;
    };
  };
  instrument<T>(
    name: string,
    carrier: { traceparent?: string | undefined },
    run: () => Promise<T>,
    attributes?: Record<string, string> | (() => Record<string, string>),
  ): Promise<T>;
}

const DARK_PROVIDER_TELEMETRY: ProviderTelemetry = {
  emitting: false,
  meter: () => ({ createCounter: () => ({ add: () => undefined }) }),
  instrument: (_name, _carrier, run) => run(),
};

/** A LinkHandler that consumes the webhook fan-out and delegates everything
 *  else to the provider's handler. */
export function createWebhookReceiver(deps: WebhookReceiverDeps): LinkHandler {
  const telemetry = deps.telemetry ?? DARK_PROVIDER_TELEMETRY;
  const provider = deps.provider ?? "unknown";
  const events = telemetry.meter("ofac.provider").createCounter("ofac.provider.events", {
    description: "Webhook fan-out deliveries per provider (labels: outcome)",
  });
  // Bounded seen-delivery memory: the fan-out is at-least-once, and the
  // kernel may re-enqueue the SAME provider delivery under a new link
  // deliveryId — the providerDeliveryId is the stable identity.
  const seenDefault = new Set<string>();
  const seenLimit = 4096;
  const firstSeen = (ids: readonly string[]): boolean => {
    let fresh = false;
    for (const id of ids) {
      if (deps.seen !== undefined) {
        if (!deps.seen(id)) fresh = true;
        continue;
      }
      if (!seenDefault.has(id)) fresh = true;
      seenDefault.add(id);
      if (seenDefault.size > seenLimit) {
        const oldest = seenDefault.values().next();
        if (!oldest.done) seenDefault.delete(oldest.value);
      }
    }
    return fresh;
  };
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
        events.add(1, { provider, outcome: "rejected" });
        return { kind: "rejected", reason: "malformed-webhook-data" };
      }

      // One span per fan-out delivery, parented on the command's carrier;
      // the tags are evaluated when the span ends, from the verdict.
      const normalize = deps.normalize;
      let reason = "published";
      const tags = (): Record<string, string> => ({
        "ofac.provider": provider,
        "ofac.reason": reason,
      });
      return await telemetry.instrument(
        "provider.webhook",
        cmd,
        async () => {
          let verdict: WebhookVerdict;
          try {
            verdict = await normalize(delivery);
          } catch (error) {
            deps.logger.error("webhook normalize failed", { error: String(error) });
            reason = "normalize-failed";
            events.add(1, { provider, outcome: "rejected" });
            return { kind: "rejected", reason: "normalize-failed" };
          }

          if (verdict.kind === "bad-signature") {
            deps.logger.warn("webhook rejected: bad signature", { provider: delivery.provider });
            reason = "bad-signature";
            events.add(1, { provider, outcome: "rejected" });
            return { kind: "rejected", reason: "bad-signature" };
          }

          const deliveryIds = verdict.events
            .map((event) => event.data["providerDeliveryId"])
            .filter((id): id is string => typeof id === "string" && id !== "");
          const fresh = deliveryIds.length === 0 || firstSeen(deliveryIds);
          if (!fresh) {
            reason = "duplicate";
            events.add(1, { provider, outcome: "duplicate" });
            return { kind: "accepted" };
          }

          for (const event of verdict.events) {
            await deps.publish(event);
          }

          reason = verdict.events.length === 0 ? "unmappable" : "published";
          events.add(1, { provider, outcome: "normalized" });
          deps.logger.info("webhook fan-out processed", {
            provider: delivery.provider,
            published: verdict.events.length,
          });
          return { kind: "accepted" };
        },
        tags,
      );
    },
  };
}
