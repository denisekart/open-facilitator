// The fan-out seam law: HMAC-SHA256 over the RAW bytes (`sha256=<hex>`,
// X-Hub-Signature-256), the delivery id passthrough (X-GitHub-Delivery),
// event-kind dispatch, the status sha→PR I/O arm, and the drop arms.
import { describe, expect, it } from "vitest";
import type { WebhookDelivery } from "@ofac/provider-core/node";
import type { LinkLogger } from "@ofac/workspace-link";
import { createGitHubNormalizer } from "../src/webhook.js";
import type { GitHubApi } from "../src/github-client.js";
import { ctx } from "./helpers.js";
import {
  checkSuiteCompleted,
  issuesLabeled,
  pullRequestOpened,
  reviewCommentCreated,
  statusSuccess,
  WEBHOOK_SECRET,
  sign,
} from "./fixtures/payloads.js";

const logger: LinkLogger = {
  debug: () => undefined,
  info: () => undefined,
  warn: () => undefined,
  error: () => undefined,
};

function delivery(
  event: string,
  payload: unknown,
  deliveryId = "123e4567-e89b-12d3-a456-426614174000",
  extraHeaders: Record<string, string> = {},
): WebhookDelivery {
  const body = JSON.stringify(payload);
  return {
    provider: "github",
    contentType: "application/json",
    rawBody: Buffer.from(body, "utf8"),
    headers: {
      "x-github-event": event,
      "x-github-delivery": deliveryId,
      "x-hub-signature-256": sign(body),
      ...extraHeaders,
    },
  };
}

const silentApi = (prs: number[]): GitHubApi =>
  ({
    prsForHeadSha: async () => prs,
  }) as unknown as GitHubApi;

function normalizer(api: GitHubApi | undefined = silentApi([])) {
  return createGitHubNormalizer({ ctx, webhookSecret: WEBHOOK_SECRET, api, logger });
}

describe("signature law", () => {
  it("a valid sha256=<hex> signature verifies", async () => {
    const verdict = await normalizer()(delivery("issues", issuesLabeled));
    expect(verdict.kind).toBe("normalized");
    if (verdict.kind === "normalized") {
      expect(verdict.events).toHaveLength(1);
    }
  });

  for (const [name, headers] of [
    ["missing", {}],
    ["bad", { "x-hub-signature-256": "sha256=deadbeef" }],
    ["wrong length", { "x-hub-signature-256": "sha256=abcd" }],
  ] as const) {
    it(`a ${name} signature answers bad-signature`, async () => {
      const d = delivery("issues", issuesLabeled);
      delete d.headers["x-hub-signature-256"];
      Object.assign(d.headers, headers);
      expect(await normalizer()(d)).toEqual({ kind: "bad-signature" });
    });
  }

  it("signature is over the raw bytes, not re-serialized JSON", async () => {
    const d = delivery("issues", issuesLabeled);
    // Re-serialize with different key order — the same object, different
    // bytes, so the recorded signature MUST NOT match.
    const reordered = JSON.stringify(
      Object.fromEntries(
        Object.entries(JSON.parse(d.rawBody.toString("utf8")) as Record<string, unknown>).reverse(),
      ),
    );
    d.rawBody = Buffer.from(reordered, "utf8");
    expect(await normalizer()(d)).toEqual({ kind: "bad-signature" });
  });
});

describe("dispatch", () => {
  it("the delivery id passes through as the event's providerDeliveryId", async () => {
    const verdict = await normalizer()(delivery("issues", issuesLabeled, "deliv-42"));
    if (verdict.kind !== "normalized") throw new Error("expected normalized");
    expect(verdict.events[0]?.data.providerDeliveryId).toBe("deliv-42");
  });

  it("pull_request dispatches to the change_request arm", async () => {
    const verdict = await normalizer()(delivery("pull_request", pullRequestOpened));
    if (verdict.kind !== "normalized") throw new Error("expected normalized");
    expect(verdict.events[0]?.type).toBe("org.facilitator.event.provider.change_request.v1");
  });

  it("pull_request_review_comment dispatches with commentId", async () => {
    const verdict = await normalizer()(
      delivery("pull_request_review_comment", reviewCommentCreated),
    );
    if (verdict.kind !== "normalized") throw new Error("expected normalized");
    expect((verdict.events[0]?.data as { commentId?: string }).commentId).toBe("10");
  });

  it("status resolves the sha→PR number through the api", async () => {
    const verdict = await normalizer(silentApi([42]))(delivery("status", statusSuccess));
    if (verdict.kind !== "normalized") throw new Error("expected normalized");
    expect(verdict.events).toHaveLength(1);
    expect(verdict.events[0]?.data.reference.rootThreadId).toBe("42");
  });

  it("check_suite resolves via the suite head_sha", async () => {
    const verdict = await normalizer(silentApi([7]))(delivery("check_suite", checkSuiteCompleted));
    if (verdict.kind !== "normalized") throw new Error("expected normalized");
    expect(verdict.events[0]?.data.reference.rootThreadId).toBe("7");
  });

  it("status with no api (no bot token) drops to zero events", async () => {
    const verdict = await normalizer(undefined)(delivery("status", statusSuccess));
    expect(verdict).toEqual({ kind: "normalized", events: [] });
  });

  it("an unknown event kind is a well-signed zero-event drop", async () => {
    const verdict = await normalizer()(delivery("star", { action: "created" }));
    expect(verdict).toEqual({ kind: "normalized", events: [] });
  });

  it("a malformed body is a zero-event drop, never a throw", async () => {
    const body = "{not json";
    const verdict = await normalizer()({
      provider: "github",
      contentType: "application/json",
      rawBody: Buffer.from(body, "utf8"),
      headers: {
        "x-github-event": "issues",
        "x-github-delivery": "d",
        "x-hub-signature-256": sign(body),
      },
    });
    expect(verdict).toEqual({ kind: "normalized", events: [] });
  });

  it("a missing delivery id header drops", async () => {
    const d = delivery("issues", issuesLabeled);
    delete d.headers["x-github-delivery"];
    expect(await normalizer()(d)).toEqual({ kind: "normalized", events: [] });
  });
});
