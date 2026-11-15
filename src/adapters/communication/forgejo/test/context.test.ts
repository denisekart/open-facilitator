// The context plane's adapter leg (plan 3.2): a routed thread://forgejo fetch
// is answered asynchronously with event.context.fetched.v1 — the platform
// window (the issue/PR comment thread) rides the URI's limit (capped), an
// unresolvable URI or a platform error is an honest error item, and the
// command ALWAYS settles accepted.
import { mkdtempSync, rmSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { describe, expect, it } from "vitest";

import type { EnvelopeCommand, EnvelopeEvent } from "@ofac/protocol";
import type { CommandContext, LinkLogger } from "@ofac/workspace-link";

import { AdapterState } from "../src/comment-ledger.js";
import { CONTEXT_FETCH_COMMAND, answerContextFetch, parseThreadUri } from "../src/context.js";
import { ForgejoClient, type ForgejoApi } from "../src/forgejo-client.js";
import { createForgejoHandler } from "../src/handler.js";

const silent: LinkLogger = {
  debug: () => undefined,
  info: () => undefined,
  warn: () => undefined,
  error: () => undefined,
};

/** The published item view (one named assertion at the test boundary). */
type ContextItemView = {
  uri: string;
  content: {
    messages?: { sender: string; text: string; timestamp: string }[];
    error?: string;
  } & Record<string, unknown>;
  truncated: boolean;
};

/** The real client over a stubbed fetch: the window leg must ride the
 *  platform route with the URI's limit, so the URL is captured and asserted. */
function stubClient(respond: (url: string) => { status: number; body: unknown }): {
  api: ForgejoApi;
  urls: string[];
} {
  const urls: string[] = [];
  const fetchImpl = (async (input: string | URL | Request) => {
    const url = String(input);
    urls.push(url);
    const { status, body } = respond(url);
    return {
      ok: status >= 200 && status < 300,
      status,
      json: async () => body,
      text: async () => JSON.stringify(body),
    };
  }) as unknown as typeof fetch;
  return { api: new ForgejoClient("http://git.localhost", "tok", fetchImpl), urls };
}

function comment(id: number, author: string, body: string, created: string): unknown {
  return {
    id,
    html_url: `http://git.localhost/repos/demo/widgets/issues/7#${String(id)}`,
    body,
    user: { login: author },
    created_at: created,
  };
}

function fetchCommand(refs: unknown[], deadline?: string): EnvelopeCommand {
  return {
    specversion: "1.0",
    id: "cmd-env-1",
    source: "urn:facilitator:instance:main",
    type: CONTEXT_FETCH_COMMAND,
    subject: "work/work-1",
    time: new Date().toISOString(),
    datacontenttype: "application/json",
    dataschema: "https://schemas.openfacilitator.org/v1/command.context.fetch.v1.json",
    data: {
      commandId: "context-fetch:fetch-1",
      workId: "work-1",
      targetId: "forgejo-1",
      actorEvidenceRef: "kernel:context-fetch:fetch-1",
      deadline: deadline ?? new Date(Date.now() + 60_000).toISOString(),
      contextRefs: refs,
    },
  } as unknown as EnvelopeCommand;
}

function capturingCtx(): { ctx: CommandContext; published: EnvelopeEvent[] } {
  const published: EnvelopeEvent[] = [];
  return {
    published,
    ctx: {
      deliveryId: "del-1",
      envelope: fetchCommand([]),
      signal: new AbortController().signal,
      publishEvent: async (env) => {
        published.push(env);
        return { status: "accepted" };
      },
    },
  };
}

describe("parseThreadUri (forgejo)", () => {
  it("reads the repo, the issue number and a bounded limit", () => {
    expect(parseThreadUri("thread://forgejo/demo/widgets/7")).toEqual({
      repo: "demo/widgets",
      number: 7,
      limit: 20,
    });
    expect(parseThreadUri("thread://forgejo/demo/widgets/7?limit=5")?.limit).toBe(5);
    expect(parseThreadUri("thread://forgejo/demo/widgets/7?limit=999")?.limit).toBe(50);
  });

  it("answers undefined for foreign schemes and malformed URIs", () => {
    expect(parseThreadUri("thread://telegram/123/456")).toBeUndefined();
    expect(parseThreadUri("artifact://w/a/b")).toBeUndefined();
    expect(parseThreadUri("thread://forgejo/demo/widgets")).toBeUndefined();
    expect(parseThreadUri("thread://forgejo/7")).toBeUndefined();
    expect(parseThreadUri("thread://forgejo/demo//7")).toBeUndefined();
    expect(parseThreadUri("thread://forgejo/demo/widgets/zero")).toBeUndefined();
  });
});

describe("answerContextFetch (forgejo)", () => {
  it("answers a resolvable thread from the platform with the URI's limit", async () => {
    const long = "x".repeat(2500);
    const { api, urls } = stubClient(() => ({
      status: 200,
      body: [
        comment(11, "bob", "first word", "2026-11-01T10:00:00Z"),
        comment(12, "alice", long, "2026-11-02T11:00:00Z"),
      ],
    }));
    const { ctx, published } = capturingCtx();
    const outcome = await answerContextFetch(
      api,
      "inst-1",
      fetchCommand(["thread://forgejo/demo/widgets/7?limit=5"]),
      ctx,
    );
    expect(outcome).toEqual({ kind: "accepted" });
    // The window comes from the platform route, with the URI's limit.
    expect(urls).toEqual([
      "http://git.localhost/api/v1/repos/demo/widgets/issues/7/comments?limit=5",
    ]);
    expect(published).toHaveLength(1);
    const env = published[0]!;
    expect(env.type).toBe("org.facilitator.event.context.fetched.v1");
    expect(env.dataschema).toBe(
      "https://schemas.openfacilitator.org/v1/event.context.fetched.v1.json",
    );
    expect(env.causationid).toBe("cmd-env-1");
    const data = env.data as Record<string, unknown>;
    expect(data["fetchId"]).toBe("context-fetch:fetch-1");
    expect(data["workId"]).toBe("work-1");
    expect(data["responder"]).toBe("forgejo-inst-1");
    const items = data["items"] as ContextItemView[];
    expect(items).toHaveLength(1);
    expect(items[0]!.uri).toBe("thread://forgejo/demo/widgets/7?limit=5");
    expect(items[0]!.truncated).toBe(false);
    expect(items[0]!.content.messages).toEqual([
      { sender: "bob", text: "first word", timestamp: "2026-11-01T10:00:00Z" },
      // The clamp law: a 2500-char body answers at 2000 (1999 + ellipsis).
      {
        sender: "alice",
        text: `${"x".repeat(1999)}…`,
        timestamp: "2026-11-02T11:00:00Z",
      },
    ]);
  });

  it("a thread with no limit asks the platform for the default window", async () => {
    const { api, urls } = stubClient(() => ({ status: 200, body: [] }));
    const { ctx } = capturingCtx();
    expect(
      await answerContextFetch(
        api,
        "inst-1",
        fetchCommand(["thread://forgejo/demo/widgets/7"]),
        ctx,
      ),
    ).toEqual({ kind: "accepted" });
    expect(urls[0]).toContain("/comments?limit=20");
  });

  it("an unknown repo (platform 404) is an honest error item, still accepted", async () => {
    const { api } = stubClient(() => ({ status: 404, body: { message: "not found" } }));
    const { ctx, published } = capturingCtx();
    const outcome = await answerContextFetch(
      api,
      "inst-1",
      fetchCommand(["thread://forgejo/no/such-repo/7"]),
      ctx,
    );
    expect(outcome).toEqual({ kind: "accepted" });
    const data = published[0]!.data as Record<string, unknown>;
    const items = data["items"] as ContextItemView[];
    expect(items).toHaveLength(1);
    expect(items[0]!.content.error).toBe("ForgejoError");
    expect(items[0]!.truncated).toBe(false);
  });

  it("a foreign thread:// scheme and a malformed URI are honest error items", async () => {
    const { api } = stubClient(() => ({ status: 200, body: [] }));
    const { ctx, published } = capturingCtx();
    const outcome = await answerContextFetch(
      api,
      "inst-1",
      fetchCommand(["thread://telegram/123/456", "thread://forgejo/demo/widgets"]),
      ctx,
    );
    expect(outcome).toEqual({ kind: "accepted" });
    const data = published[0]!.data as Record<string, unknown>;
    const items = data["items"] as ContextItemView[];
    expect(items).toHaveLength(2);
    expect(items[0]!.content.error).toBe("thread-unresolvable");
    expect(items[1]!.content.error).toBe("thread-unresolvable");
  });

  it("no refs publishes the no-refs error item", async () => {
    const { api } = stubClient(() => ({ status: 200, body: [] }));
    const { ctx, published } = capturingCtx();
    expect(await answerContextFetch(api, "inst-1", fetchCommand([]), ctx)).toEqual({
      kind: "accepted",
    });
    const data = published[0]!.data as Record<string, unknown>;
    expect((data["items"] as ContextItemView[])[0]!.content.error).toBe("no-refs");
  });

  it("rejects only malformed data", async () => {
    const { api } = stubClient(() => ({ status: 200, body: [] }));
    const { ctx } = capturingCtx();
    const cmd = fetchCommand([]);
    const data: Record<string, unknown> = cmd.data as Record<string, unknown>;
    delete data["commandId"];
    expect(await answerContextFetch(api, "inst-1", cmd, ctx)).toEqual({
      kind: "rejected",
      reason: "malformed-context-fetch-data",
    });
  });

  it("the handler arm dispatches a fetch and authors the event under the instance id", async () => {
    const { api } = stubClient(() => ({
      status: 200,
      body: [comment(11, "bob", "window", "2026-11-01T10:00:00Z")],
    }));
    const dir = mkdtempSync(join(tmpdir(), "ofac-fj-ctx-"));
    const state = new AdapterState(join(dir, "state.sqlite3"));
    try {
      const handler = createForgejoHandler({
        api,
        state,
        providerInstanceId: "local-git",
        logger: silent,
      });
      const { ctx, published } = capturingCtx();
      expect(
        await handler.onCommand(fetchCommand(["thread://forgejo/demo/widgets/7?limit=3"]), ctx),
      ).toEqual({ kind: "accepted" });
      const data = published[0]!.data as Record<string, unknown>;
      expect(data["fetchId"]).toBe("context-fetch:fetch-1");
      expect(data["responder"]).toBe("forgejo-local-git");
      expect((data["items"] as ContextItemView[])[0]!.content.messages?.[0]?.text).toBe("window");
    } finally {
      state.close();
      rmSync(dir, { recursive: true, force: true });
    }
  });
});
