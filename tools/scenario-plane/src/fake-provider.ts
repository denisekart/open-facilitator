// The scenario plane's fake provider with a personality: ONE HTTP host that
// speaks the shapes the comms adapters talk to (GitHub REST + webhooks,
// GitLab REST + system hooks, Mattermost REST, Telegram Bot API), plus a
// control surface that lets a scenario drive it:
//
//   POST /__plane/deliver   {family, event, data}   -> replays the family's
//       webhook/hook format onto the registered sink URL (the adapter's
//       receiving leg), counting deliveries so a scenario can re-deliver one.
//   POST /__plane/mode      {family, mode}          -> "ok" | "403" | "429" |
//       "429-now" | "vanished": every subsequent API call of that family
//       answers the injected failure (403 is a wall, 429 carries
//       Retry-After: 2, 429-now the same body with Retry-After: 0 so a retry
//       path is provable without a wall-clock wait, vanished
//       answers the conversation-not-found shape of that family).
//   GET  /__plane/state                             -> everything received
//       and sent, for the scenario's assertions.
//
// The API legs answer minimal-but-real shapes: enough for the adapters'
// happy paths (post a message, add a comment/request-changes, register a
// webhook) to succeed against, no more. Adapters point here via their
// existing base-URL config; nothing on the adapter side knows it is faked.
import { createServer, type Server, type IncomingMessage, type ServerResponse } from "node:http";

export type Family = "github" | "gitlab" | "mattermost" | "telegram";
export type Mode = "ok" | "403" | "429" | "429-now" | "vanished";

export interface PlaneState {
  sinks: Partial<Record<Family, string>>;
  modes: Partial<Record<Family, Mode>>;
  /** API calls received, per family, in order (method, path, body). */
  received: {
    family: Family;
    method: string;
    path: string;
    body: unknown;
    headers: Record<string, string>;
  }[];
  /** Webhooks delivered to sinks (family, event, body, deliveryId). */
  delivered: { family: Family; event: string; body: unknown; deliveryId: string }[];
  /** Monotonic counter behind delivery ids so re-delivery is expressible. */
  nextDelivery: number;
}

const FAMILIES: Family[] = ["github", "gitlab", "mattermost", "telegram"];

async function readBody(req: IncomingMessage): Promise<string> {
  const chunks: Buffer[] = [];
  for await (const c of req) chunks.push(c as Buffer);
  return Buffer.concat(chunks).toString("utf8");
}

function json(res: ServerResponse, status: number, doc: unknown): void {
  const text = JSON.stringify(doc);
  res.writeHead(status, {
    "content-type": "application/json",
    "content-length": Buffer.byteLength(text),
  });
  res.end(text);
}

/** The conversation-not-found shapes each family actually answers. */
function vanished(family: Family): { status: number; body: unknown } {
  switch (family) {
    case "github":
      return { status: 404, body: { message: "Not Found" } };
    case "gitlab":
      return { status: 404, body: { message: "404 Project Not Found" } };
    case "mattermost":
      return {
        status: 404,
        body: {
          id: "api.channel.save_post.post.channel.app_error",
          message: "Unable to save post.",
        },
      };
    case "telegram":
      return {
        status: 400,
        body: { ok: false, error_code: 400, description: "Bad Request: chat not found" },
      };
  }
}

export interface FakePlane {
  server: Server;
  url: string;
  state: PlaneState;
  close(): Promise<void>;
}

export async function startFakeProvider(port = 0): Promise<FakePlane> {
  const state: PlaneState = { sinks: {}, modes: {}, received: [], delivered: [], nextDelivery: 1 };

  const server = createServer((req, res) => {
    void (async () => {
      const raw = await readBody(req);
      const path = new URL(req.url ?? "/", "http://plane").pathname;
      const seg = path.split("/").filter(Boolean);
      const headers: Record<string, string> = {};
      for (const [k, v] of Object.entries(req.headers)) headers[k.toLowerCase()] = String(v);

      // ── control surface ────────────────────────────────────────────────
      if (seg[0] === "__plane") {
        if (path === "/__plane/state") {
          return json(res, 200, state);
        }
        if (path === "/__plane/sink" && req.method === "POST") {
          const c = control(raw);
          if (!isFamily(c["family"]) || typeof c["url"] !== "string")
            return json(res, 400, { error: "family and url required" });
          state.sinks[c["family"]] = c["url"];
          return json(res, 200, { ok: true });
        }
        if (path === "/__plane/mode" && req.method === "POST") {
          const c = control(raw);
          if (!isFamily(c["family"]))
            return json(res, 400, { error: `unknown family ${String(c["family"])}` });
          state.modes[c["family"]] = isMode(c["mode"]) ? c["mode"] : "ok";
          return json(res, 200, { ok: true });
        }
        if (path === "/__plane/deliver" && req.method === "POST") {
          const c = control(raw);
          if (!isFamily(c["family"]) || typeof c["event"] !== "string")
            return json(res, 400, { error: "family and event required" });
          const family = c["family"];
          const event = c["event"];
          const data = "data" in c ? c["data"] : {};
          const sink = state.sinks[family];
          if (!sink) return json(res, 409, { error: `no sink registered for ${family}` });
          const repeat = typeof c["repeat"] === "number" ? c["repeat"] : 1;
          const count = Math.max(1, repeat);
          for (let i = 0; i < count; i++) {
            const deliveryId = `plane-${state.nextDelivery++}`;
            const body = wrapForFamily(family, event, data, deliveryId);
            const headers: Record<string, string> = { "content-type": "application/json" };
            if (family === "github") {
              headers["x-github-event"] = event;
              headers["x-hub-signature-256"] = "sha256=tossed-into-the-plane";
              headers["x-github-delivery"] = deliveryId;
            } else if (family === "gitlab") {
              headers["x-gitlab-event"] = event;
              headers["x-gitlab-webhook-uuid"] = deliveryId;
            } else if (family === "telegram") {
              headers["x-telegram-bot-api-secret-token"] = "plane";
            }
            state.delivered.push({ family, event, body, deliveryId });
            await fetch(sink, { method: "POST", body: JSON.stringify(body), headers }).catch(
              () => undefined,
            );
          }
          return json(res, 200, { ok: true, delivered: count });
        }
        return json(res, 404, { error: `no control route ${path}` });
      }

      // ── API legs ───────────────────────────────────────────────────────
      const family = familyOf(path);
      const mode = state.modes[family] ?? "ok";
      state.received.push({
        family,
        method: req.method ?? "?",
        path,
        body: safeParse(raw),
        headers,
      });
      if (mode === "403") {
        return json(res, 403, {
          message: "403 Forbidden: the plane says so",
          documentation_url: "https://docs.openfacilitator.org/plane",
        });
      }
      if (mode === "429" || mode === "429-now") {
        // "429-now" is the deterministic retry proof: a zero wait means the
        // adapter's retry path must re-issue within the same test window.
        res.setHeader("retry-after", mode === "429" ? "2" : "0");
        return json(res, 429, { message: "429: the plane says so" });
      }
      if (mode === "vanished") {
        const v = vanished(family);
        return json(res, v.status, v.body);
      }
      return answer(family, req.method ?? "?", path, safeParse(raw), res);
    })();
  });

  await new Promise<void>((resolve) => server.listen(port, "127.0.0.1", resolve));
  const address = server.address();
  const url = `http://127.0.0.1:${typeof address === "object" && address !== null ? address.port : port}`;
  return {
    server,
    url,
    state,
    close: () =>
      new Promise<void>((resolve, reject) => server.close((e) => (e ? reject(e) : resolve()))),
  };
}

/** One small guard for the control surface: parsed JSON object or {}. */
function control(raw: string): Record<string, unknown> {
  const parsed = safeParse(raw);
  return parsed !== null && typeof parsed === "object" && !Array.isArray(parsed)
    ? (parsed as Record<string, unknown>)
    : {};
}
function isFamily(v: unknown): v is Family {
  return typeof v === "string" && (FAMILIES as string[]).includes(v);
}
function isMode(v: unknown): v is Mode {
  return v === "ok" || v === "403" || v === "429" || v === "429-now" || v === "vanished";
}

function safeParse(raw: string): unknown {
  try {
    return raw.length ? JSON.parse(raw) : {};
  } catch {
    return { raw };
  }
}

/** Discriminate the four families by URL shape. /api/v4 belongs to BOTH
 * GitLab and Mattermost, so projects/ is the GitLab leg and channels|posts
 * the Mattermost leg; anything unrecognised is a GitHub REST path. */
function familyOf(path: string): Family {
  if (path.startsWith("/bot")) return "telegram";
  if (path.startsWith("/api/v4/projects") || path.startsWith("/api/v3")) return "gitlab";
  if (
    path.startsWith("/api/v4/channels") ||
    path.startsWith("/api/v4/posts") ||
    path.startsWith("/api/v4/users")
  )
    return "mattermost";
  return "github";
}

/** The per-family webhook envelope the receiving leg expects. */
function wrapForFamily(family: Family, event: string, data: unknown, deliveryId: string): unknown {
  const d = control(typeof data === "object" && data !== null ? JSON.stringify(data) : "{}");
  switch (family) {
    case "github":
      return { action: d["action"] ?? "created", ...d, delivery_guid: deliveryId };
    case "gitlab":
      return { object_kind: event.toLowerCase(), ...d };
    case "telegram":
      // data IS the update object; wrap as the getUpdates/getWebhook body.
      return { update_id: Number(deliveryId.replace(/\D/g, "") || 1), ...d };
    case "mattermost":
      // The mattermost adapter polls; a "deliver" answers the posting leg,
      // so the event data is passed through for state recording.
      return { event, data: d };
  }
}

/** The happy-path shapes: enough for adapter ops to succeed and be assertable. */
function answer(
  family: Family,
  method: string,
  path: string,
  body: unknown,
  res: ServerResponse,
): void {
  const id = `plane-${Math.random().toString(36).slice(2, 8)}`;
  if (family === "telegram" && path.startsWith("/bot")) {
    // Bot API: every call answers ok:true with an id-shaped result.
    const text = isBody(body) && "text" in body ? { text: body["text"] } : {};
    return json(res, 200, {
      ok: true,
      result: { message_id: stateCounter(), chat: { id: -1001234567890 }, ...text },
    });
  }
  if (family === "mattermost" && method === "POST" && path.includes("/posts")) {
    const posted = isBody(body) && "post" in body ? body["post"] : body;
    return json(res, 201, { id, create_at: Date.now(), post: JSON.stringify(posted) });
  }
  if (
    family === "gitlab" &&
    method === "POST" &&
    (path.includes("/notes") || path.includes("/reviews"))
  ) {
    return json(res, 201, {
      id: stateCounter(),
      id_iid: stateCounter(),
      web_url: `${"http://plane"}/note/${id}`,
    });
  }
  if (family === "gitlab" && method === "POST" && path.endsWith("/hooks")) {
    return json(res, 201, { id: stateCounter(), web_url: isBody(body) ? body["url"] : undefined });
  }
  if (
    family === "github" &&
    method === "POST" &&
    (path.includes("/comments") || path.includes("/review") || path.includes("/labels"))
  ) {
    return json(res, 201, { id: stateCounter(), html_url: `http://plane/thing/${id}` });
  }
  if (family === "github" && method === "POST" && path.endsWith("/hooks")) {
    const config = isBody(body) && isBody(body["config"]) ? body["config"]["url"] : undefined;
    return json(res, 201, { id: stateCounter(), url: config });
  }
  if (method === "GET") {
    return json(res, 200, family === "github" ? [] : []);
  }
  json(res, 200, { id });
}

let counter = 1000;
function stateCounter(): number {
  return counter++;
}

function isBody(b: unknown): b is Record<string, unknown> {
  return typeof b === "object" && b !== null;
}
