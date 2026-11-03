// The webhook edge, fan-out form: the kernel sink (`POST
// /api/ingress/webhooks/github`) rode the verbatim bytes + headers to this
// adapter inside a `command.provider.webhook.v1`; the seam verifies the HMAC
// over the RAW bytes BEFORE any parsing — GitHub's scheme is
// `X-Hub-Signature-256: sha256=<hex>` (the exact form provider-core's
// verifier tolerates). Bad/missing signature ⇒ `bad-signature` (the receiver
// answers a `rejected` receipt). A well-signed delivery that is unmappable ⇒
// zero events (audit line). `X-GitHub-Delivery` (stable UUID per delivery,
// docs-verified) is the providerDeliveryId — passed through, never
// synthesized.
import {
  verifyHmacSha256,
  type WebhookDelivery,
  type WebhookNormalizer,
} from "@ofac/provider-core/node";
import type { LinkLogger } from "@ofac/workspace-link";
import type { GitHubApi } from "./github-client.js";
import {
  normalizeCheckSuite,
  normalizeIssueComment,
  normalizeIssues,
  normalizePullRequest,
  normalizeReview,
  normalizeReviewComment,
  normalizeStatus,
  type NormalizeContext,
  type NormalizedEvent,
  type Payload,
} from "./normalize.js";

export interface GitHubNormalizerDeps {
  ctx: NormalizeContext;
  /** HMAC secret — the same `.env` key the webhook registration sends. */
  webhookSecret: string;
  /** Undefined when the adapter has no bot token: commit-status deliveries
   *  (the only I/O arm) then drop, everything else normalizes. */
  api: GitHubApi | undefined;
  logger: LinkLogger;
}

export function createGitHubNormalizer(deps: GitHubNormalizerDeps): WebhookNormalizer {
  const { ctx, webhookSecret, api, logger } = deps;

  return async (delivery: WebhookDelivery) => {
    const signature = delivery.headers["x-hub-signature-256"];
    if (!verifyHmacSha256(delivery.rawBody, webhookSecret, signature)) {
      return { kind: "bad-signature" };
    }

    let parsed: unknown;
    try {
      parsed = JSON.parse(delivery.rawBody.toString("utf8"));
    } catch {
      return { kind: "normalized", events: [] };
    }

    if (typeof parsed !== "object" || parsed === null || Array.isArray(parsed)) {
      return { kind: "normalized", events: [] };
    }

    const payload = parsed as Payload;
    const deliveryId = delivery.headers["x-github-delivery"];
    const eventKind = delivery.headers["x-github-event"];
    if (typeof deliveryId !== "string" || deliveryId === "" || typeof eventKind !== "string") {
      return { kind: "normalized", events: [] };
    }

    const events = await dispatch(payload, deliveryId, eventKind, ctx, api, logger);
    return { kind: "normalized", events };
  };
}

async function dispatch(
  payload: Payload,
  deliveryId: string,
  eventKind: string,
  ctx: NormalizeContext,
  api: GitHubApi | undefined,
  logger: LinkLogger,
): Promise<NormalizedEvent[]> {
  if (eventKind === "issues") return normalizeIssues(payload, deliveryId, ctx);
  if (eventKind === "issue_comment") return normalizeIssueComment(payload, deliveryId, ctx);
  if (eventKind === "pull_request_review_comment") {
    return normalizeReviewComment(payload, deliveryId, ctx);
  }

  if (eventKind === "pull_request") return normalizePullRequest(payload, deliveryId, ctx);
  if (eventKind === "pull_request_review") return normalizeReview(payload, deliveryId, ctx);
  if (eventKind === "status" || eventKind === "check_suite") {
    // The sha→number resolution is I/O, so it lives HERE, not in normalize.
    const repo =
      typeof payload["repository"] === "object" && payload["repository"] !== null
        ? (payload["repository"] as Record<string, unknown>)["full_name"]
        : undefined;
    const sha =
      typeof payload["sha"] === "string"
        ? payload["sha"]
        : typeof payload["check_suite"] === "object" && payload["check_suite"] !== null
          ? (payload["check_suite"] as Record<string, unknown>)["head_sha"]
          : undefined;
    let number: string | undefined;
    if (typeof repo === "string" && typeof sha === "string" && api !== undefined) {
      try {
        const prs = await api.prsForHeadSha(repo, sha);
        number = prs[0] === undefined ? undefined : String(prs[0]);
      } catch (error) {
        logger.warn("status sha resolution failed", { error: String(error) });
      }
    }

    return eventKind === "status"
      ? normalizeStatus(payload, deliveryId, number, ctx)
      : normalizeCheckSuite(payload, deliveryId, number, ctx);
  }

  logger.info("webhook dropped", { eventKind, deliveryId });
  return [];
}
