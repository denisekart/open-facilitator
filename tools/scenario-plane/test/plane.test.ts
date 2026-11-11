import { afterEach, describe, expect, it } from "vitest";
import { createServer } from "node:http";
import { startFakeProvider, type FakePlane } from "../src/fake-provider.js";

// The plane's own contract: a scenario must be able to (1) register a sink
// and get a family-shaped webhook delivered, (2) re-deliver one delivery,
// (3) inject 403/429/vanished per family, (4) see everything in state.
interface SinkBox {
  url: string;
  hits: { headers: Record<string, string>; body: Record<string, unknown> }[];
  close(): Promise<void>;
}

async function sinkBox(): Promise<SinkBox> {
  const hits: { headers: Record<string, string>; body: Record<string, unknown> }[] = [];
  const server = createServer((req, res) => {
    const chunks: Buffer[] = [];
    req.on("data", (c) => chunks.push(c as Buffer));
    req.on("end", () => {
      hits.push({
        headers: Object.fromEntries(Object.entries(req.headers).map(([k, v]) => [k, String(v)])),
        body: JSON.parse(Buffer.concat(chunks).toString("utf8")) as Record<string, unknown>,
      });
      res.writeHead(200).end("{}");
    });
  });
  await new Promise<void>((r) => server.listen(0, "127.0.0.1", r));
  const addr = server.address();
  return {
    url: `http://127.0.0.1:${typeof addr === "object" && addr !== null ? addr.port : 0}`,
    hits,
    close: () => new Promise<void>((resolve) => server.close(() => resolve())),
  };
}

async function post(url: string, body: unknown): Promise<Response> {
  return fetch(url, { method: "POST", body: JSON.stringify(body) });
}

describe("fake provider plane", () => {
  let plane: FakePlane | null = null;
  afterEach(async () => {
    await plane?.close();
    plane = null;
  });

  it("delivers a github-shaped webhook to the registered sink and records it", async () => {
    plane = await startFakeProvider();
    const box = await sinkBox();
    await post(`${plane.url}/__plane/sink`, { family: "github", url: `${box.url}/hook` });
    const r = await post(`${plane.url}/__plane/deliver`, {
      family: "github",
      event: "issues",
      data: {
        action: "opened",
        issue: { number: 7, labels: [{ name: "ofac" }], user: { login: "octo" } },
        repository: { full_name: "o/r" },
      },
    });
    expect(r.status).toBe(200);
    expect(box.hits).toHaveLength(1);
    expect(box.hits[0].headers["x-github-event"]).toBe("issues");
    expect(box.hits[0].body["action"]).toBe("opened");
    const st = (await (await fetch(`${plane.url}/__plane/state`)).json()) as {
      delivered: unknown[];
    };
    expect(st.delivered).toHaveLength(1);
    await box.close();
  });

  it("repeat: 2 delivers the SAME event twice with distinct delivery ids", async () => {
    plane = await startFakeProvider();
    const box = await sinkBox();
    await post(`${plane.url}/__plane/sink`, { family: "github", url: `${box.url}/h` });
    await post(`${plane.url}/__plane/deliver`, {
      family: "github",
      event: "issue_comment",
      data: { action: "created" },
      repeat: 2,
    });
    expect(box.hits).toHaveLength(2);
    expect([...new Set(box.hits.map((h) => h.headers["x-github-delivery"]))]).toHaveLength(2);
    await box.close();
  });

  it("per-family modes: 403 wall, 429 with Retry-After, vanished answers the family shape", async () => {
    plane = await startFakeProvider();
    await post(`${plane.url}/__plane/mode`, { family: "github", mode: "403" });
    expect(
      (await fetch(`${plane.url}/repos/o/r/issues/1/comments`, { method: "POST", body: "{}" }))
        .status,
    ).toBe(403);
    await post(`${plane.url}/__plane/mode`, { family: "github", mode: "429" });
    const limited = await fetch(`${plane.url}/repos/o/r/issues/1/comments`, {
      method: "POST",
      body: "{}",
    });
    expect(limited.status).toBe(429);
    expect(limited.headers.get("retry-after")).toBe("2");
    await post(`${plane.url}/__plane/mode`, { family: "telegram", mode: "vanished" });
    const tg = await fetch(`${plane.url}/bot123:abc/sendMessage`, { method: "POST", body: "{}" });
    expect(tg.status).toBe(400);
    const body = (await tg.json()) as { ok: boolean; description?: string };
    expect(body.ok).toBe(false);
    expect(body.description).toContain("chat not found");
  });

  it("happy API legs answer the shapes the adapters consume", async () => {
    plane = await startFakeProvider();
    const tg = (await (
      await post(`${plane.url}/bot1:token/sendMessage`, { chat_id: -1, text: "hi" })
    ).json()) as { ok: boolean; result: { message_id: number } };
    expect(tg.ok).toBe(true);
    expect(tg.result.message_id).toBeGreaterThan(0);
    const mm = (await (
      await post(`${plane.url}/api/v4/posts`, { post: { message: "hi", channel_id: "c" } })
    ).json()) as { id: string };
    expect(mm.id).toBeTruthy();
    const gh = (await (await post(`${plane.url}/repos/o/r/issues/1/reviews`, {})).json()) as {
      html_url: string;
    };
    expect(gh.html_url).toContain("http://plane");
    const st = (await (await fetch(`${plane.url}/__plane/state`)).json()) as {
      received: { family: string }[];
    };
    expect(st.received.map((r) => r.family).sort()).toEqual(["github", "mattermost", "telegram"]);
  });
});
