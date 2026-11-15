import { flushSync, mount, unmount } from "svelte";
import { afterEach, describe, expect, it, vi } from "vitest";

import { deck } from "../lib/deck.svelte.js";
import AiDeck from "./AiDeck.svelte";

// The drawer proof: opening (from the rail's view-chat toggle or a #/chat
// deep link) mounts the full ChatView inside it — chat-input reachable —
// and Esc / the close button tear it down. The chat wire is stubbed with
// the same shape ChatView.test.ts uses, minimally.

function jwt(roles: string[]): string {
  const b64 = (input: string) =>
    btoa(input).replace(/\+/g, "-").replace(/\//g, "_").replace(/=+$/, "");
  return `${b64('{"alg":"RS256"}')}.${b64(JSON.stringify({ realm_access: { roles } }))}.sig`;
}

let container: HTMLDivElement | null = null;
let app: Record<string, unknown> | null = null;

function stubChat(): void {
  vi.stubGlobal(
    "fetch",
    vi.fn(async (path: string) => {
      const url = String(path);
      if (url.includes("/catalog")) {
        return new Response(JSON.stringify({ catalog: [], verbs: [] }), {
          status: 200,
          headers: { "Content-Type": "application/json" },
        });
      }
      if (url.includes("/preferences")) {
        return new Response(JSON.stringify({ verbosity: "balanced" }), {
          status: 200,
          headers: { "Content-Type": "application/json" },
        });
      }
      if (url.includes("/sessions")) {
        return new Response(JSON.stringify([]), {
          status: 200,
          headers: { "Content-Type": "application/json" },
        });
      }
      return new Response("{}", { status: 404 });
    }),
  );
}

afterEach(() => {
  if (app !== null) {
    unmount(app as never);
    app = null;
  }
  container?.remove();
  container = null;
  deck.setOpen(false);
  deck.setSession(null);
  deck.setContext(null);
  vi.unstubAllGlobals();
});

function mountDeck(): HTMLDivElement {
  container = document.createElement("div");
  document.body.append(container);
  app = mount(AiDeck, {
    target: container,
    props: { onclose: () => deck.setOpen(false) },
  }) as unknown as Record<string, unknown>;
  flushSync();
  return container;
}

function byTestId<T extends HTMLElement>(id: string): T {
  const el = container?.querySelector(`[data-testid="${id}"]`) as T | undefined;
  if (!el) {
    throw new Error(`no element with data-testid="${id}"`);
  }
  return el;
}

describe("AiDeck drawer", () => {
  it("closed by default: no chat surface in the DOM", () => {
    stubChat();
    mountDeck();
    expect(container?.querySelector("[data-testid='ai-deck']")).toBeNull();
    expect(container?.querySelector("[data-testid='chat-input']")).toBeNull();
  });

  it("deck.open mounts the ChatView with its input", async () => {
    void jwt(["ai-operator"]);
    stubChat();
    mountDeck();

    deck.setOpen(true);
    flushSync();

    expect(byTestId("ai-deck")).toBeTruthy();
    await vi.waitFor(() => expect(byTestId("chat-input")).toBeTruthy());
    expect(byTestId("chat-send")).toBeTruthy();
  });

  it("desktop is non-modal: no scrim, backdrop layer click-through", async () => {
    // jsdom reports no matchMedia → desktop. The deck is a companion panel:
    // the backdrop layer must not intercept anything underneath it.
    stubChat();
    mountDeck();
    deck.setOpen(true);
    flushSync();
    await vi.waitFor(() => expect(byTestId("chat-input")).toBeTruthy());

    const deckEl = byTestId("ai-deck");
    expect(deckEl.querySelector("[data-testid='ai-deck-scrim']")).toBeNull();
    expect(deckEl.className).toContain("pointer-events-none");
    // aria-modal only in the mobile dialog mode.
    expect(deckEl.getAttribute("aria-modal")).toBe("false");
    // The panel itself stays interactive inside a pass-through layer.
    const panel = deckEl.querySelector(".pointer-events-auto");
    expect(panel).toBeTruthy();
    expect(panel?.contains(byTestId("chat-input"))).toBe(true);
  });

  it("mobile (matchMedia match) is modal: scrim renders and dismisses", async () => {
    const listeners: ((ev: MediaQueryListEvent) => void)[] = [];
    const mql = {
      matches: true,
      addEventListener: (_: string, cb: (ev: MediaQueryListEvent) => void) => listeners.push(cb),
      removeEventListener: () => undefined,
    };
    vi.stubGlobal("matchMedia", () => mql);
    stubChat();
    mountDeck();
    deck.setOpen(true);
    flushSync();
    await vi.waitFor(() => expect(byTestId("chat-input")).toBeTruthy());

    expect(byTestId("ai-deck").getAttribute("aria-modal")).toBe("true");
    byTestId<HTMLButtonElement>("ai-deck-scrim").click();
    flushSync();
    expect(container?.querySelector("[data-testid='ai-deck']")).toBeNull();
  });

  it("close button and Esc both shut the drawer", async () => {
    stubChat();
    mountDeck();
    deck.setOpen(true);
    flushSync();
    await vi.waitFor(() => expect(byTestId("chat-input")).toBeTruthy());

    byTestId<HTMLButtonElement>("ai-deck-close").click();
    flushSync();
    expect(container?.querySelector("[data-testid='ai-deck']")).toBeNull();

    deck.setOpen(true);
    flushSync();
    window.dispatchEvent(new KeyboardEvent("keydown", { key: "Escape", bubbles: true }));
    flushSync();
    expect(container?.querySelector("[data-testid='ai-deck']")).toBeNull();
  });

  it("screen actions: a registered handler shows a chip and fires", async () => {
    stubChat();
    let fired = 0;
    const teardown = deck.onAction("reloadScreen", () => (fired += 1));
    try {
      mountDeck();
      deck.setOpen(true);
      flushSync();
      await vi.waitFor(() => expect(byTestId("chat-input")).toBeTruthy());

      const chip = byTestId<HTMLButtonElement>("deck-action-reloadScreen");
      expect(chip).toBeTruthy();
      chip.click();
      expect(fired).toBe(1);
    } finally {
      teardown();
    }
    // After teardown the chip is gone on the next open.
    deck.setOpen(false);
    flushSync();
    deck.setOpen(true);
    flushSync();
    expect(container?.querySelector("[data-testid='deck-action-reloadScreen']")).toBeNull();
  });

  it("desktop Esc inside a text field belongs to the field, not the deck", async () => {
    stubChat();
    mountDeck();
    deck.setOpen(true);
    flushSync();
    await vi.waitFor(() => expect(byTestId("chat-input")).toBeTruthy());

    byTestId("chat-input").dispatchEvent(
      new KeyboardEvent("keydown", { key: "Escape", bubbles: true }),
    );
    flushSync();
    expect(byTestId("ai-deck")).toBeTruthy();
  });
});
