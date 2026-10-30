// The webhook edge (Plan 23 decision 6): verify the HMAC over the RAW bytes
// BEFORE any parsing — `X-Forgejo-Signature` / `X-Gitea-Signature` carry the
// bare hex HMAC-SHA256 (step 0: the Gitea-family form, NO `sha256=` prefix),
// and the GitHub-compatible `X-Hub-Signature-256: sha256=<hex>` copy is also
// accepted. Bad/missing signature ⇒ 401, zero processing. A valid delivery of
// an unmapped event kind ⇒ 200 + drop (audit line): the forge retries neither
// class of answer, but only 4xx tells an attacker nothing was accepted.
// `X-Forgejo-Delivery` (stable UUID per delivery, docs-verified) is the
// providerDeliveryId — passed through, never synthesized.
import { createHmac, timingSafeEqual } from "node:crypto";
import type { IncomingMessage, ServerResponse } from "node:http";

export type WebhookVerdict =
  | { kind: "unauthorized"; reason: string }
  | { kind: "delivery"; deliveryId: string; eventKind: string; payload: Record<string, unknown> }
  | { kind: "invalid-payload" };

export function verifySignature(
  rawBody: string,
  secret: string,
  signatureHeader: string | undefined,
): boolean {
  if (signatureHeader === undefined || signatureHeader === "") return false;
  const provided = signatureHeader.startsWith("sha256=")
    ? signatureHeader.slice(7)
    : signatureHeader;
  const expected = createHmac("sha256", secret).update(rawBody, "utf8").digest("hex");
  const a = Buffer.from(provided.toLowerCase(), "hex");
  const b = Buffer.from(expected, "hex");
  return a.length === b.length && timingSafeEqual(a, b);
}

const MAX_BODY_BYTES = 1_048_576;

/** Reads the raw body (capped), verifies, then parses; answers the HTTP edge.
 *  Returns the parsed delivery ONLY on a verified request. */
export async function readWebhookRequest(
  req: IncomingMessage,
  res: ServerResponse,
  secret: string,
): Promise<WebhookVerdict> {
  const chunks: Buffer[] = [];
  let size = 0;
  for await (const chunk of req) {
    size += (chunk as Buffer).length;
    if (size > MAX_BODY_BYTES) {
      res.writeHead(413);
      res.end();
      return { kind: "unauthorized", reason: "body-too-large" };
    }

    chunks.push(chunk as Buffer);
  }

  const rawBody = Buffer.concat(chunks).toString("utf8");
  const signature =
    (req.headers["x-forgejo-signature"] as string | undefined) ??
    (req.headers["x-gitea-signature"] as string | undefined) ??
    (req.headers["x-hub-signature-256"] as string | undefined);
  if (!verifySignature(rawBody, secret, signature)) {
    res.writeHead(401, { "content-type": "application/json" });
    res.end(JSON.stringify({ error: "bad-signature" }));
    return { kind: "unauthorized", reason: "bad-signature" };
  }

  let payload: unknown;
  try {
    payload = JSON.parse(rawBody);
  } catch {
    res.writeHead(400);
    res.end();
    return { kind: "invalid-payload" };
  }

  if (typeof payload !== "object" || payload === null || Array.isArray(payload)) {
    res.writeHead(400);
    res.end();
    return { kind: "invalid-payload" };
  }

  const deliveryId = req.headers["x-forgejo-delivery"] ?? req.headers["x-gitea-delivery"];
  const eventKind = req.headers["x-forgejo-event"] ?? req.headers["x-gitea-event"];
  if (typeof deliveryId !== "string" || deliveryId === "" || typeof eventKind !== "string") {
    res.writeHead(400);
    res.end();
    return { kind: "invalid-payload" };
  }

  res.writeHead(202, { "content-type": "application/json" });
  res.end(JSON.stringify({ status: "received" }));
  return { kind: "delivery", deliveryId, eventKind, payload: payload as Record<string, unknown> };
}
