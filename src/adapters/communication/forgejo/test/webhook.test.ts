// The webhook edge law (decision 6): HMAC over the RAW bytes, verified before
// parsing, both header forms (bare hex X-Forgejo-Signature / X-Gitea-Signature
// and the GitHub-compatible sha256=-prefixed copy); a bad or missing signature
// answers 401 with zero processing; X-Forgejo-Delivery + X-Forgejo-Event pass
// through untouched as the dedupe identity.
import { createHmac } from "node:crypto";
import { createServer, type IncomingMessage, type ServerResponse } from "node:http";
import type { AddressInfo } from "node:net";
import { afterAll, beforeAll, describe, expect, it } from "vitest";
import { readWebhookRequest, verifySignature, type WebhookVerdict } from "../src/webhook.js";

const SECRET = "s3cret-hook";
const BODY = JSON.stringify({
  action: "opened",
  number: 1,
  repository: { full_name: "demo/widgets" },
});

function hex(body: string): string {
  return createHmac("sha256", SECRET).update(body, "utf8").digest("hex");
}

describe("verifySignature", () => {
  it("accepts the bare-hex form (the Gitea-family gotcha: NO sha256= prefix)", () => {
    expect(verifySignature(BODY, SECRET, hex(BODY))).toBe(true);
  });

  it("accepts the GitHub-compatible prefixed copy", () => {
    expect(verifySignature(BODY, SECRET, `sha256=${hex(BODY)}`)).toBe(true);
  });

  it("rejects a tampered body, a wrong secret, and a missing header", () => {
    expect(verifySignature(BODY + " ", SECRET, hex(BODY))).toBe(false);
    expect(verifySignature(BODY, "other", hex(BODY))).toBe(false);
    expect(verifySignature(BODY, SECRET, undefined)).toBe(false);
    expect(verifySignature(BODY, SECRET, "zz")).toBe(false);
  });
});

describe("readWebhookRequest (http edge)", () => {
  let server: ReturnType<typeof createServer>;
  let url: string;
  const box: { verdict: WebhookVerdict | undefined } = { verdict: undefined };
  const peek = (): WebhookVerdict | undefined => box.verdict;
  let settled: Promise<void> = Promise.resolve();
  let settle: () => void = () => undefined;

  beforeAll(async () => {
    server = createServer((req: IncomingMessage, res: ServerResponse) => {
      void readWebhookRequest(req, res, SECRET).then((verdict) => {
        box.verdict = verdict;
        settle();
      });
    });
    await new Promise<void>((resolve) => server.listen(0, "127.0.0.1", resolve));
    url = `http://127.0.0.1:${(server.address() as AddressInfo).port}/hooks/forgejo`;
  });

  afterAll(() => {
    server.close();
  });
  async function post(headers: Record<string, string>): Promise<number> {
    box.verdict = undefined;
    settled = new Promise<void>((resolve) => {
      settle = resolve;
    });
    const response = await fetch(url, { method: "POST", headers, body: BODY });
    const status = response.status;
    // Await the helper's own verdict signal — never a guessed duration; the
    // 4xx paths settle too, so this resolves on every branch.
    await Promise.race([settled, new Promise((resolve) => response.arrayBuffer().then(resolve))]);
    await settled;
    return status;
  }

  it("verified delivery answers 202 and yields the passthrough identity", async () => {
    const status = await post({
      "content-type": "application/json",
      "x-forgejo-signature": hex(BODY),
      "x-forgejo-delivery": "d-42",
      "x-forgejo-event": "issues",
    });
    expect(status).toBe(202);
    expect(peek()).toEqual({
      kind: "delivery",
      deliveryId: "d-42",
      eventKind: "issues",
      payload: JSON.parse(BODY),
    });
  });

  it("the gitea-mirror headers work too", async () => {
    const status = await post({
      "content-type": "application/json",
      "x-gitea-signature": hex(BODY),
      "x-gitea-delivery": "d-43",
      "x-gitea-event": "issue_comment",
    });
    expect(status).toBe(202);
    const delivered = peek();
    expect(delivered?.kind === "delivery" && delivered.deliveryId).toBe("d-43");
  });

  it("bad signature answers 401, no delivery", async () => {
    const status = await post({
      "content-type": "application/json",
      "x-forgejo-signature": "deadbeef",
      "x-forgejo-delivery": "d-44",
      "x-forgejo-event": "issues",
    });
    expect(status).toBe(401);
    expect(peek()?.kind).toBe("unauthorized");
  });

  it("a missing delivery id on a verified body answers 400", async () => {
    const status = await post({
      "content-type": "application/json",
      "x-forgejo-signature": hex(BODY),
    });
    expect(status).toBe(400);
    expect(peek()?.kind).toBe("invalid-payload");
  });
});
