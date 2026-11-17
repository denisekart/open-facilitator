// The AiDeck drawer's state: it is chrome, not a route — every screen can
// open it, so it lives outside route.svelte.ts (which only speaks the URL).
// Views register a ScreenContext (route + the ids on screen); the drawer
// prefixes its next message with it so the assistant knows what the operator
// is looking at, without a protocol change.

import { untrack } from "svelte";

export interface ScreenContext {
  /** Canonical hash of the screen, e.g. "#/monitor/runs/<id>". */
  route: string;
  /** Short human label of the screen ("monitor run 0194…"). */
  label: string;
  /** The ids on screen (work id, def id, instance id, session ids…). */
  ids: string[];
}

interface DeckState {
  open: boolean;
  expanded: boolean;
  sessionId: string | null;
}

/** What a screen offers the deck as a live action ("open this work"). */
export interface ScreenAction {
  id: string;
  label: string;
  run: () => void;
}

/** What the deck just changed, for the screens that must show it. */
export interface ExecutedChange {
  method: string;
  /** Path without the ids' context, e.g. "/api/admin/dead-letters/replay". */
  path: string;
  at: number;
}

class Deck {
  state = $state<DeckState>({ open: false, expanded: false, sessionId: null });
  context = $state<ScreenContext | null>(null);
  // One handler map per action id; the inner map is keyed by the mounting
  // view instance, so two mounts never steal each other's handlers.
  handlers = new Map<string, Map<object, () => void>>();
  // Bumped on register/unregister so available() is a tracked read.
  version = $state(0);
  // Monotonic bump: every deck-executed change lands here, and any view
  // that reads it inside an effect re-loads. No refresh, no dead air.
  executed = $state<ExecutedChange | null>(null);

  toggle(): void {
    this.state.open = !this.state.open;
  }

  setOpen(open: boolean): void {
    this.state.open = open;
  }

  setExpanded(expanded: boolean): void {
    this.state.expanded = expanded;
  }

  toggleExpanded(): void {
    this.state.expanded = !this.state.expanded;
  }

  setSession(sessionId: string | null): void {
    this.state.sessionId = sessionId;
  }

  setContext(context: ScreenContext | null): void {
    this.context = context;
  }

  /** A view mounts a handler (e.g. openWork) for the lifetime of owner. */
  register(id: string, owner: object, handler: () => void): void {
    let map = this.handlers.get(id);
    if (!map) {
      map = new Map();
      this.handlers.set(id, map);
    }
    map.set(owner, handler);
    // Bump untracked: register runs inside a caller's $effect (onAction),
    // and a tracked read of the version here would make that effect depend
    // on its own write — an update loop. available() keeps the tracked read.
    this.version = untrack(() => this.version) + 1;
  }

  unregister(id: string, owner: object): void {
    const map = this.handlers.get(id);
    if (map) {
      map.delete(owner);
      if (map.size === 0) {
        this.handlers.delete(id);
      }
      this.version = untrack(() => this.version) + 1;
    }
  }

  /** Handlers live right now — what the deck may offer as screen actions. */
  available(): ScreenAction[] {
    void this.version; // tracked: chips appear/disappear with the screens
    const out: ScreenAction[] = [];
    for (const [id, map] of this.handlers) {
      const first = map.values().next().value;
      if (first) {
        out.push({ id, label: id, run: () => this.dispatch(id) });
      }
    }
    return out;
  }

  dispatch(id: string): void {
    for (const handler of this.handlers.get(id)?.values() ?? []) {
      handler();
      return;
    }
  }

  /** After a deck action executes: tell every live screen to show it. */
  broadcastExecuted(method: string, path: string): void {
    this.executed = { method, path, at: Date.now() };
  }

  /**
   * View-side mount helper: expose `id` ("openWork") to the deck for this
   * component's lifetime. Call from a $effect body; the returned teardown
   * belongs in the effect's cleanup.
   */
  onAction(id: string, handler: () => void): () => void {
    const owner = {};
    this.register(id, owner, handler);
    return () => this.unregister(id, owner);
  }
}

/**
 * View-side refresh helper: re-runs the calling effect whenever the deck
 * executed a change (optionally only for changes whose path matches).
 * Read it inside an effect: `if (deck.onExecuted()) void load()`.
 */
export function deckChanged(pathIncludes?: string): boolean {
  const e = deck.executed;
  return e !== null && (pathIncludes === undefined || e.path.includes(pathIncludes));
}

export const deck = new Deck();
