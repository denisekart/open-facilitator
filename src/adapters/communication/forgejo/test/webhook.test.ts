// The webhook edge law (decision 6, fan-out form): the kernel sink rode the
// verbatim delivery here inside a command.provider.webhook.v1; the seam
// verifies the HMAC over the RAW bytes BEFORE any parsing — both header forms
// (bare hex X-Forgejo-Signature / X-Gitea-Signature and the GitHub-compatible
// sha256=-prefixed copy) — and a bad or missing signature answers
// `bad-signature` with zero processing. X-Forgejo-Delivery + X-Forgejo-Event
// pass through untouched as the dedupe identity. Status deliveries resolve
// the sha→PR number through the injected API (the I/O arm).
import { createHmac } from "node:crypto";
import { describe, expect, it } from "vitest";
import type { LinkLogger } from "@ofac/workspace-link";
import type { WebhookDelivery } from "@ofac/provider-core/node";
import { createForgejoNormalizer } from "../src/webhook.js";
import type { ForgejoApi } from "../src/forgejo-client.js";
import type { NormalizeContext } from "../src/normalize.js";
import { issueOpened, statusFailure } from "./fixtures/payloads.js";

const SECRET = "s3cret-hook";
const CTX: NormalizeContext = {
  providerInstanceId: "local-git",
  tenantOrOrganizationId: "git.localhost",
  workLabels: new Set(["ofac:work"]),
  botUser: "ofac-bot",
};

const silent: LinkLogger = {
  debug: () => undefined,
  info: () => undefined,
  warn: () => undefined,
  error: () => undefined,
};

class FakeApi implements ForgejoApi {
  constructor(private readonly prs: number[] = [7]) {}
  shaLookups: { repo: string; sha: string }[] = [];

  async postComment(): Promise<{ id: number; html_url: string }> {
    throw new Error("unused");
  }
  async openPr(): Promise<never> {
    throw new Error("unused");
  }
  async prsForHeadSha(repo: string, sha: string): Promise<number[]> {
    this.shaLookups.push({ repo, sha });
    return this.prs;
  }
  async commitStatus(): Promise<void> {
    /* unused */
  }
  async openPrsForHead(): Promise<number[]> {
    throw new Error("unused");
  }
  async findCommentByMarker(): Promise<never> {
    throw new Error("unused");
  }
  async getIssue(): Promise<never> {
    throw new Error("unused");
  }
  async createBranch(): Promise<void> {
    throw new Error("unused");
  }
  async addLabels(): Promise<void> {
    throw new Error("unused");
  }
  async getPullRequest(): Promise<never> {
    throw new Error("unused");
  }
  async reviewPullRequest(): Promise<never> {
    throw new Error("unused");
  }
}

function hex(body: string): string {
  return createHmac("sha256", SECRET).update(body, "utf8").digest("hex");
}

function delivery(body: string, headers: Record<string, string>): WebhookDelivery {
  return {
    provider: "forgejo",
    contentType: "application/json",
    rawBody: Buffer.from(body, "utf8"),
    headers,
  };
}

function normalizer(api: ForgejoApi | undefined) {
  return createForgejoNormalizer({ ctx: CTX, webhookSecret: SECRET, api, logger: silent });
}

describe("createForgejoNormalizer signature law", () => {
  const body = JSON.stringify(issueOpened);

  it("accepts the bare-hex form (the Gitea-family gotcha: NO sha256= prefix)", async () => {
    const verdict = await normalizer(new FakeApi())(
      delivery(body, {
        "x-forgejo-signature": hex(body),
        "x-forgejo-delivery": "dlv-1",
        "x-forgejo-event": "issues",
      }),
    );
    expect(verdict.kind).toBe("normalized");
    expect(verdict.kind === "normalized" && verdict.events).toHaveLength(1);
    expect(verdict.kind === "normalized" && verdict.events[0]?.data.providerDeliveryId).toBe(
      "dlv-1",
    );
  });

  it("the gitea-mirror headers work too", async () => {
    const verdict = await normalizer(new FakeApi())(
      delivery(body, {
        "x-gitea-signature": hex(body),
        "x-gitea-delivery": "dlv-2",
        "x-gitea-event": "issues",
      }),
    );
    expect(verdict.kind === "normalized" && verdict.events[0]?.data.providerDeliveryId).toBe(
      "dlv-2",
    );
  });

  it("accepts the GitHub-compatible prefixed copy", async () => {
    const verdict = await normalizer(new FakeApi())(
      delivery(body, {
        "x-hub-signature-256": `sha256=${hex(body)}`,
        "x-forgejo-delivery": "dlv-3",
        "x-forgejo-event": "issues",
      }),
    );
    expect(verdict.kind).toBe("normalized");
  });

  it("a tampered body, a wrong secret, and a missing header all answer bad-signature", async () => {
    const n = normalizer(undefined);
    expect(
      (
        await n(
          delivery(body + "x", {
            "x-forgejo-signature": hex(body),
            "x-forgejo-delivery": "d",
            "x-forgejo-event": "issues",
          }),
        )
      ).kind,
    ).toBe("bad-signature");
    expect(
      (
        await n(
          delivery(body, {
            "x-forgejo-signature": "f".repeat(64),
            "x-forgejo-delivery": "d",
            "x-forgejo-event": "issues",
          }),
        )
      ).kind,
    ).toBe("bad-signature");
    expect(
      (await n(delivery(body, { "x-forgejo-delivery": "d", "x-forgejo-event": "issues" }))).kind,
    ).toBe("bad-signature");
  });

  it("a verified delivery without the passthrough identity drops (zero events)", async () => {
    const verdict = await normalizer(new FakeApi())(
      delivery(body, { "x-forgejo-signature": hex(body), "x-forgejo-event": "issues" }),
    );
    expect(verdict).toEqual({ kind: "normalized", events: [] });
  });

  it("an unmapped event kind drops with zero events", async () => {
    const verdict = await normalizer(new FakeApi())(
      delivery(body, {
        "x-forgejo-signature": hex(body),
        "x-forgejo-delivery": "dlv-4",
        "x-forgejo-event": "create",
      }),
    );
    expect(verdict).toEqual({ kind: "normalized", events: [] });
  });
});

describe("status arm (I/O sha resolution)", () => {
  it("resolves the PR number through the API and normalizes pipeline.v1", async () => {
    const api = new FakeApi([7]);
    const body = JSON.stringify(statusFailure);
    const verdict = await normalizer(api)(
      delivery(body, {
        "x-forgejo-signature": hex(body),
        "x-forgejo-delivery": "dlv-9",
        "x-forgejo-event": "status",
      }),
    );
    expect(api.shaLookups).toEqual([{ repo: "demo/widgets", sha: "abc123" }]);
    expect(verdict.kind === "normalized" && verdict.events).toHaveLength(1);
    expect(verdict.kind === "normalized" && verdict.events[0]?.data.reference.rootThreadId).toBe(
      "7",
    );
  });

  it("an unresolvable sha drops the delivery", async () => {
    const api = new FakeApi([]);
    const body = JSON.stringify(statusFailure);
    const verdict = await normalizer(api)(
      delivery(body, {
        "x-forgejo-signature": hex(body),
        "x-forgejo-delivery": "dlv-10",
        "x-forgejo-event": "status",
      }),
    );
    expect(verdict).toEqual({ kind: "normalized", events: [] });
  });

  it("no bot token (api undefined): status drops, everything else normalizes", async () => {
    const statusBody = JSON.stringify(statusFailure);
    const drop = await normalizer(undefined)(
      delivery(statusBody, {
        "x-forgejo-signature": hex(statusBody),
        "x-forgejo-delivery": "dlv-11",
        "x-forgejo-event": "status",
      }),
    );
    expect(drop).toEqual({ kind: "normalized", events: [] });

    const issueBody = JSON.stringify(issueOpened);
    const keep = await normalizer(undefined)(
      delivery(issueBody, {
        "x-forgejo-signature": hex(issueBody),
        "x-forgejo-delivery": "dlv-12",
        "x-forgejo-event": "issues",
      }),
    );
    expect(keep.kind === "normalized" && keep.events).toHaveLength(1);
  });
});
