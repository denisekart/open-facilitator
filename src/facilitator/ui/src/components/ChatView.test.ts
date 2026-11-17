import { flushSync, mount, unmount } from "svelte";
import { afterEach, describe, expect, it, vi } from "vitest";

import type { Fetcher } from "../lib/api.js";
import type { ChatAction, ChatCatalogView, ChatMessage, ChatSession } from "../lib/types.js";
import ChatView from "./ChatView.svelte";

// ChatView mounts against a fake kernel: the catalog, the session list and
// the message history are all stubbed, so the card/basis/palette rendering is
// checked without a gateway, a database or a clock.

interface Call {
  path: string;
  method: string;
  body: string | null;
}

function json(body: unknown, status = 200): Response {
  return new Response(JSON.stringify(body), {
    status,
    headers: { "Content-Type": "application/json" },
  });
}

const SESSION_ID = "0194f0a0-0000-7000-8000-0000000000aa";
const MESSAGE_ID = "0194f0a0-0000-7000-8000-0000000000bb";
const NOT_FOUND = "0194f0a0-0000-7000-8000-0000000000ff";

function catalog(over: Partial<ChatCatalogView> = {}): ChatCatalogView {
  return {
    catalog: [
      {
        method: "GET",
        path: "/api/admin/summary",
        summary: "Kernel roll-up dashboard.",
        bodyHint: "-",
        notes: null,
        effect: "read",
      },
      {
        method: "POST",
        path: "/api/admin/dead-letters/{id}/replay",
        summary: "Replay one dead letter.",
        bodyHint: "{}",
        notes: null,
        effect: "write",
      },
    ],
    verbs: [
      { verb: "help", description: "list the commands", args: [] },
      {
        verb: "pause",
        description: "pause a team",
        args: [{ name: "team", required: true }],
      },
    ],
    ...over,
  };
}

function session(over: Partial<ChatSession> = {}): ChatSession {
  return {
    sessionId: SESSION_ID,
    actor: "oidc:iss|sub",
    title: "deck session",
    lastMessageAt: "2026-11-04T11:05:00Z",
    ...over,
  };
}

function action(over: Partial<ChatAction> = {}): ChatAction {
  return {
    actionId: "0194f0a0-0000-7000-8000-000000000001",
    method: "GET",
    path: "/api/admin/summary",
    description: "Read the roll-up.",
    status: "pending",
    summary: "Kernel roll-up dashboard.",
    ...over,
  };
}

function message(over: Partial<ChatMessage> = {}): ChatMessage {
  return {
    messageId: MESSAGE_ID,
    role: "assistant",
    text: "I could not map that to an action.",
    basis: "fallback",
    createdAt: "2026-11-04T11:05:00Z",
    actions: [],
    ...over,
  };
}

let container: HTMLDivElement | null = null;
let app: Record<string, unknown> | null = null;

function mountComponent(component: object, props: Record<string, unknown> = {}): HTMLDivElement {
  container = document.createElement("div");
  document.body.append(container);
  app = mount(component as never, { target: container, props }) as unknown as Record<
    string,
    unknown
  >;
  flushSync();
  return container;
}

function cleanup(): void {
  if (app !== null) {
    unmount(app as never);
    app = null;
  }
  container?.remove();
  container = null;
  vi.unstubAllGlobals();
  vi.restoreAllMocks();
}

afterEach(cleanup);

function byTestId<T extends HTMLElement>(id: string): T {
  const el = container?.querySelector(`[data-testid="${id}"]`) as T | undefined;
  if (!el) {
    throw new Error(`no element with data-testid="${id}"`);
  }
  return el;
}

function hasTestId(id: string): boolean {
  return container?.querySelector(`[data-testid="${id}"]`) !== null;
}

function setField(input: HTMLInputElement, value: string): void {
  input.value = value;
  input.dispatchEvent(new Event("input", { bubbles: true }));
  flushSync();
}

/** Stub the three reads the panel performs on mount and on session open. */
function stub(opts: {
  messages?: ChatMessage[];
  sessions?: ChatSession[];
  catalogView?: ChatCatalogView | null;
  calls?: Call[];
}): void {
  const fetcher: Fetcher = async (path, init) => {
    const method = init?.method ?? "GET";
    (opts.calls ?? []).push({
      path,
      method,
      body: typeof init?.body === "string" ? init.body : null,
    });
    if (path === "/api/chat/catalog") {
      return opts.catalogView === null ? json({}, 404) : json(opts.catalogView ?? catalog());
    }
    if (path === "/api/chat/sessions") {
      return json(opts.sessions ?? [session()]);
    }
    if (path === `/api/chat/sessions/${SESSION_ID}/messages`) {
      return json(opts.messages ?? [message()]);
    }
    return json({ title: "not stubbed", status: 404 }, 404);
  };
  vi.stubGlobal("fetch", fetcher);
  mountComponent(ChatView, { fetcher });
}

async function opened(): Promise<void> {
  await vi.waitFor(() => expect(hasTestId(`chat-session-${SESSION_ID}`)).toBe(true));
  byTestId<HTMLButtonElement>(`chat-session-${SESSION_ID}`).click();
  await vi.waitFor(() => expect(hasTestId("chat-md")).toBe(true));
}

describe("ChatView", () => {
  it("renders a canned (fallback) reply with the basis chip, not the wire word", async () => {
    stub({
      messages: [
        message({
          basis: "fallback",
          reads: { reads: [{ method: "GET", path: "/api/admin/summary", status: 200 }] },
        }),
      ],
    });
    await opened();

    expect(byTestId("chat-basis-0").textContent).toContain("canned");
    // the wire word never shows for a fallback turn
    expect(byTestId("chat-basis-0").textContent).not.toContain("fallback");
    // the read-phase trail renders collapsed, one line per read
    const trail = byTestId("chat-reads-0");
    expect(trail.getAttribute("open")).toBeNull();
    expect(trail.textContent).toContain("looked at: GET /api/admin/summary (200)");
  });

  it("maps the other bases through the chip labels", async () => {
    stub({
      messages: [
        message({ role: "user", basis: "slash", text: "/pause team=home" }),
        message({ basis: "model" }),
      ],
    });
    await opened();

    expect(byTestId("chat-basis-0").textContent).toContain("command");
    expect(byTestId("chat-basis-1").textContent).toContain("model");
  });

  it("a proposal group with one write offers NO run-all, only per-action confirm", async () => {
    stub({
      messages: [
        message({
          basis: "model",
          actions: [
            action(),
            action({
              actionId: "0194f0a0-0000-7000-8000-000000000002",
              method: "POST",
              path: "/api/admin/dead-letters/0194f0a0/replay",
              body: { reason: "drain" },
              summary: "Replay one dead letter.",
              expiresAt: "2026-11-04T11:15:00Z",
            }),
          ],
        }),
      ],
    });
    await opened();

    expect(hasTestId("chat-option-card")).toBe(true);
    expect(hasTestId("chat-option-confirm-all")).toBe(false);
    expect(hasTestId("chat-option-cancel-all")).toBe(true);
    expect(hasTestId("chat-action-confirm-0194f0a0-0000-7000-8000-000000000002")).toBe(true);
    // the write card headlines the kernel summary and shows the effect word
    expect(byTestId("chat-action-post").textContent).toContain("Replay one dead letter.");
    expect(byTestId("chat-action-effect-0194f0a0-0000-7000-8000-000000000002").textContent).toBe(
      "write",
    );
    // the body is folded behind a disclosure and the expiry line rides along
    expect(byTestId("chat-action-body").getAttribute("open")).toBeNull();
    expect(byTestId("chat-action-expires").textContent).toContain("expires");
  });

  it("an all-read proposal group offers run-all and sends the note on confirm", async () => {
    const calls: Call[] = [];
    stub({
      calls,
      messages: [message({ basis: "model", actions: [action()] })],
    });
    await opened();

    expect(hasTestId("chat-option-confirm-all")).toBe(true);
    setField(byTestId("chat-action-note-0194f0a0-0000-7000-8000-000000000001"), "ops asked");
    byTestId<HTMLButtonElement>("chat-action-confirm-0194f0a0-0000-7000-8000-000000000001").click();

    await vi.waitFor(() =>
      expect(
        calls.find(
          (c) => c.path === "/api/chat/actions/0194f0a0-0000-7000-8000-000000000001/confirm",
        ),
      ).toBeTruthy(),
    );
    const confirm = calls.find((c) => c.path.endsWith("/confirm"));
    expect(JSON.parse(confirm?.body ?? "{}")).toEqual({ note: "ops asked" });
  });

  it("typing a slash opens the verb palette off the catalog", async () => {
    stub({});
    await vi.waitFor(() => expect(byTestId("chat-sessions").textContent).toContain("deck session"));

    expect(hasTestId("chat-verb-palette")).toBe(false);
    setField(byTestId("chat-input"), "/");
    expect(byTestId("chat-verb-palette").textContent).toContain("list the commands");
    expect(byTestId("chat-verb-palette").textContent).toContain("<team>");

    setField(byTestId("chat-input"), "/pa");
    expect(hasTestId("chat-verb-pause")).toBe(true);
    expect(container?.querySelector('[data-testid="chat-verb-help"]')).toBeNull();
  });

  it("a canned reply offers the verbs as a trigger instead of prose", async () => {
    stub({
      messages: [
        message({ text: "Nothing runs until you confirm. Available commands:\n- /help — list" }),
      ],
    });
    await opened();

    // the prose verb list is cut, the palette trigger takes over
    expect(byTestId("chat-md").textContent).not.toContain("Available commands:");
    expect(hasTestId("chat-fallback-verbs-0")).toBe(true);
    byTestId<HTMLButtonElement>("chat-fallback-verb-pause").click();
    await vi.waitFor(() => expect(byTestId<HTMLInputElement>("chat-input").value).toBe("/pause "));
  });

  it("session rows rename through PATCH and delete through DELETE", async () => {
    const calls: Call[] = [];
    stub({
      calls,
      sessions: [session(), session({ sessionId: NOT_FOUND, title: "other" })],
    });
    await vi.waitFor(() => expect(hasTestId(`chat-session-${SESSION_ID}`)).toBe(true));

    byTestId<HTMLButtonElement>(`chat-session-rename-open-${SESSION_ID}`).click();
    await vi.waitFor(() => expect(hasTestId(`chat-session-rename-${SESSION_ID}`)).toBe(true));
    setField(byTestId(`chat-session-rename-${SESSION_ID}`), "renamed deck");
    byTestId<HTMLButtonElement>(`chat-session-rename-save-${SESSION_ID}`).click();
    await vi.waitFor(() =>
      expect(
        calls.some((c) => c.method === "PATCH" && c.path === `/api/chat/sessions/${SESSION_ID}`),
      ).toBe(true),
    );
    const patch = calls.find((c) => c.method === "PATCH");
    expect(JSON.parse(patch?.body ?? "{}")).toEqual({ title: "renamed deck" });

    byTestId<HTMLButtonElement>(`chat-session-delete-${NOT_FOUND}`).click();
    await vi.waitFor(() =>
      expect(
        calls.some((c) => c.method === "DELETE" && c.path === `/api/chat/sessions/${NOT_FOUND}`),
      ).toBe(true),
    );
  });

  it("a session that moved since it was last opened is marked", async () => {
    stub({
      sessions: [
        session(),
        session({
          sessionId: NOT_FOUND,
          title: "moved",
          lastMessageAt: new Date(Date.now() + 60_000).toISOString(),
        }),
      ],
    });
    await vi.waitFor(() => expect(byTestId("chat-sessions").textContent).toContain("moved"));
    // never opened at all → the newer row advertises itself
    expect(container?.querySelectorAll('[data-testid="chat-session-updated"]').length).toBe(1);
    await opened();
    // the opened session itself never shows the marker
    expect(
      byTestId(`chat-session-${SESSION_ID}`).querySelectorAll(
        '[data-testid="chat-session-updated"]',
      ).length,
    ).toBe(0);
  });

  it("toggles conversation history drawer", async () => {
    stub({});
    await vi.waitFor(() => expect(hasTestId("chat-toggle-history")).toBe(true));

    const toggle = byTestId<HTMLButtonElement>("chat-toggle-history");
    expect(toggle.getAttribute("aria-expanded")).toBe("false");

    toggle.click();
    flushSync();
    expect(toggle.getAttribute("aria-expanded")).toBe("true");

    toggle.click();
    flushSync();
    expect(toggle.getAttribute("aria-expanded")).toBe("false");
  });

  it("renders contextual prompt starters on welcome screen and populates input", async () => {
    const fetcher: Fetcher = async (path) => {
      if (path === "/api/chat/catalog") return json(catalog());
      if (path === "/api/chat/sessions") return json([]);
      return json({}, 404);
    };
    mountComponent(ChatView, {
      fetcher,
      screenContext: { route: "#/works", label: "works queue", ids: ["0194f0a0"] },
    });

    await vi.waitFor(() => expect(hasTestId("chat-input")).toBe(true));
    expect(container?.textContent).toContain("Open Facilitator Assistant");
    expect(container?.textContent).toContain("Briefed on: works queue");

    const starterBtn = Array.from(container?.querySelectorAll("button.card") ?? []).find((b) =>
      b.textContent?.includes("Summarize Works"),
    ) as HTMLButtonElement | undefined;
    expect(starterBtn).toBeTruthy();

    starterBtn?.click();
    flushSync();
    expect(byTestId<HTMLInputElement>("chat-input").value).toContain("Summarize active work items");
  });

  it("new chat button resets active session", async () => {
    stub({ messages: [message()] });
    await opened();

    expect(hasTestId("chat-message-0")).toBe(true);
    byTestId<HTMLButtonElement>("chat-new-session").click();
    flushSync();

    expect(hasTestId("chat-message-0")).toBe(false);
    expect(container?.textContent).toContain("Open Facilitator Assistant");
  });
});
