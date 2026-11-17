<script lang="ts">
  import {
    ArrowDown,
    Compass,
    History,
    MessageSquare,
    Plus,
    Send,
    Sparkles,
    User,
    X,
  } from "lucide-svelte";
  import { ApiError, getJson, toApiError, type Fetcher } from "../lib/api.js";
  import { apiFetch } from "../lib/auth.svelte.js";
  import { deck } from "../lib/deck.svelte.js";
  import type { ScreenContext } from "../lib/deck.svelte.js";
  import { renderChatMarkdown } from "../lib/markdown.js";
  import { go } from "../lib/route.svelte.js";
  import { ago, stamp, stateTone, TONE_CLASS, type Tone } from "../lib/status.js";
  import type {
    ChatAction,
    ChatCatalogView,
    ChatMessage,
    ChatPreferences,
    ChatReadEntry,
    ChatReply,
    ChatSession,
    ChatVerbosity,
  } from "../lib/types.js";
  import ProblemNote from "./ProblemNote.svelte";

  // Tests inject a mock fetcher; production rides the shared bearer fetch.
  // screenContext is what the AiDeck drawer knows the operator is looking
  // at (see lib/deck.svelte.ts); absent in tests and standalone mounts.
  let {
    fetcher: api = apiFetch,
    screenContext = null,
    initialSession = null,
    expanded = false,
  }: {
    fetcher?: Fetcher;
    screenContext?: ScreenContext | null;
    initialSession?: string | null;
    expanded?: boolean;
  } = $props();

  const VERBOSITIES: ChatVerbosity[] = ["terse", "balanced", "proactive"];

  // Basis chip: the wire word stays the key, the operator sees what the
  // sentence actually was — model, canned, or a slash command.
  const BASIS_LABEL: Record<string, string> = {
    model: "model",
    fallback: "canned",
    slash: "command",
  };
  const BASIS_TONE: Record<string, Tone> = {
    model: "progress",
    fallback: "wait",
    slash: "neutral",
  };

  let sessions = $state<ChatSession[]>([]);
  let sessionId = $state<string | null>(null);
  let messages = $state<ChatMessage[]>([]);
  let draft = $state("");
  let pending = $state(false);
  let error = $state<ApiError | string | null>(null);
  let verbosity = $state<ChatVerbosity>("balanced");
  // Principal-filtered catalog + verb registry: the palette's single source.
  let catalog = $state<ChatCatalogView | null>(null);
  // One PROPOSAL OPTION per assistant message: a card grouping that turn's
  // discrete actions; confirm runs inline in the chat, per action or all.
  // Live status lands in actionStatus from confirm + polling. History reloads
  // carry each message's actions, so a reopened session replays the same
  // option cards (the kernel rows are the source of truth).
  let actionsByMessage = $state<Record<string, ChatAction[]>>({});
  let actionStatus = $state<Record<string, ChatAction>>({});
  // Optional per-action decision note (sent as {"note": "..."} on the POST).
  let notes = $state<Record<string, string>>({});
  // Session hygiene: which row shows the inline rename input.
  let editingId = $state<string | null>(null);
  let editTitle = $state("");
  // History panel visibility toggle: keeps conversation thread as primary focus.
  let historyOpen = $state(false);
  // Per-session "I have seen it this recently" stamps, for the updated chip;
  // a session never opened in this panel is measured against the mount.
  let openedAt = $state<Record<string, string>>({});
  const panelOpenedAt = new Date().toISOString();
  // Scroll pin: stick to the bottom only while the operator is at the bottom.
  let thread = $state<HTMLDivElement | null>(null);
  let atBottom = $state(true);
  let input = $state<HTMLInputElement | null>(null);

  async function postJson<T>(path: string, body?: unknown): Promise<T> {
    const response = await api(path, {
      method: "POST",
      headers: body === undefined ? {} : { "Content-Type": "application/json" },
      body: body === undefined ? null : JSON.stringify(body),
    });
    if (!response.ok) {
      throw await toApiError(response);
    }
    return (await response.json()) as T;
  }

  async function loadSessions(): Promise<void> {
    try {
      sessions = await getJson<ChatSession[]>(api, "/api/chat/sessions");
    } catch (err) {
      error = err instanceof ApiError ? err : "sessions-failed";
    }
  }

  async function loadCatalog(): Promise<void> {
    try {
      catalog = await getJson<ChatCatalogView>(api, "/api/chat/catalog");
    } catch {
      // no catalog: the palette stays closed, the chat still works
    }
  }

  async function loadPreferences(): Promise<void> {
    try {
      const prefs = await getJson<ChatPreferences>(api, "/api/chat/preferences");
      if (VERBOSITIES.includes(prefs.verbosity)) {
        verbosity = prefs.verbosity;
      }
    } catch {
      // absent preference row or gate miss: the select keeps the default
    }
  }

  $effect(() => {
    void loadSessions();
    void loadCatalog();
    void loadPreferences();
    if (initialSession) {
      void openSession(initialSession);
    }
    // The list also rides the pollAction cadence below; this interval keeps
    // it honest while the panel is merely open.
    const timer = setInterval(() => void loadSessions(), 15_000);
    return () => clearInterval(timer);
  });

  function newSession(): void {
    sessionId = null;
    messages = [];
    actionsByMessage = {};
    actionStatus = {};
    editingId = null;
    error = null;
  }

  async function openSession(id: string): Promise<void> {
    sessionId = id;
    messages = [];
    actionsByMessage = {};
    actionStatus = {};
    editingId = null;
    error = null;
    try {
      messages = await getJson<ChatMessage[]>(
        api,
        `/api/chat/sessions/${id}/messages`,
      );
      const byMessage: Record<string, ChatAction[]> = {};
      for (const m of messages) {
        if (m.actions?.length) {
          byMessage[m.messageId] = m.actions;
        }
      }
      actionsByMessage = byMessage;
      openedAt = { ...openedAt, [id]: new Date().toISOString() };
      atBottom = true;
    } catch (err) {
      error = err instanceof ApiError ? err : "messages-failed";
    }
  }

  // ---- session hygiene ----------------------------------------------------

  const renameValid = $derived(
    editTitle.trim().length > 0 && editTitle.trim().length <= 120,
  );

  function startRename(s: ChatSession): void {
    editingId = s.sessionId;
    editTitle = s.title;
  }

  async function renameSession(id: string): Promise<void> {
    if (!renameValid) {
      return;
    }
    try {
      const response = await api(`/api/chat/sessions/${id}`, {
        method: "PATCH",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ title: editTitle.trim() }),
      });
      if (!response.ok) {
        throw await toApiError(response);
      }
      editingId = null;
      await loadSessions();
    } catch (err) {
      error = err instanceof ApiError ? err : "rename-failed";
    }
  }

  async function deleteSession(id: string): Promise<void> {
    try {
      const response = await api(`/api/chat/sessions/${id}`, { method: "DELETE" });
      if (!response.ok) {
        throw await toApiError(response);
      }
      sessions = sessions.filter((s) => s.sessionId !== id);
      if (sessionId === id) {
        newSession();
      }
    } catch (err) {
      error = err instanceof ApiError ? err : "delete-failed";
    }
  }

  /** "updated" marker: a session that moved after I last looked inside. */
  function updatedSinceOpen(s: ChatSession): boolean {
    if (s.sessionId === sessionId || !s.lastMessageAt) {
      return false;
    }
    const seen = openedAt[s.sessionId] ?? panelOpenedAt;
    return Date.parse(s.lastMessageAt) > Date.parse(seen);
  }

  // ---- scroll pin ----------------------------------------------------------

  function onThreadScroll(): void {
    if (!thread) {
      return;
    }
    atBottom = thread.scrollHeight - thread.scrollTop - thread.clientHeight < 24;
  }

  function jumpLatest(): void {
    atBottom = true;
    if (thread) {
      thread.scrollTop = thread.scrollHeight;
    }
  }

  $effect(() => {
    // Re-pin after every render while the operator is at the bottom.
    messages.length;
    if (atBottom && thread) {
      thread.scrollTop = thread.scrollHeight;
    }
  });

  // ---- verb palette --------------------------------------------------------

  const paletteOpen = $derived(draft.startsWith("/"));
  const paletteVerbs = $derived.by(() => {
    if (!catalog) {
      return [];
    }
    const prefix = draft.split(" ")[0].toLowerCase();
    return catalog.verbs.filter((v) => `/${v.verb}`.startsWith(prefix));
  });

  function useVerb(verb: string): void {
    draft = `/${verb} `;
    input?.focus();
  }

  /** A canned reply's prose verb list is replaced by the palette trigger. */
  function fallbackProse(m: ChatMessage): string {
    const cut = m.text.indexOf("Available commands:");
    return m.basis === "fallback" && cut >= 0 ? m.text.slice(0, cut).trimEnd() : m.text;
  }

  function readTrail(m: ChatMessage): ChatReadEntry[] {
    const reads = m.reads?.reads;
    return Array.isArray(reads) ? reads : [];
  }

  // ---- turns ---------------------------------------------------------------

  async function send(): Promise<void> {
    const text = draft.trim();
    if (!text || pending) {
      return;
    }
    pending = true;
    error = null;
    try {
      let target = sessionId;
      if (!target) {
        const created = await postJson<{ sessionId: string }>("/api/chat/sessions");
        target = created.sessionId;
      }
      // Screen context travels inside the message text: the wire request
      // (ChatMessageRequest) has only `text`, so no protocol change — the
      // assistant reads the preamble line, the operator's own echo stays
      // exactly what they typed.
      const wire = screenContext
        ? `${text}\n[screen: ${screenContext.label} @ ${screenContext.route}${screenContext.ids.length > 0 ? ` ids: ${screenContext.ids.join(", ")}` : ""}]`
        : text;
      const reply = await postJson<ChatReply>(`/api/chat/sessions/${target}/messages`, {
        text: wire,
      });
      sessionId = target;
      draft = "";
      const now = new Date().toISOString();
      openedAt = { ...openedAt, [target]: now };
      messages = [
        ...messages,
        {
          messageId: `local-user-${Date.now()}`,
          role: "user",
          text,
          basis: "fallback",
          createdAt: now,
        },
        {
          messageId: reply.messageId,
          role: "assistant",
          text: reply.reply,
          basis: "model",
          createdAt: now,
          reads: reply.reads ?? null,
        },
      ];
      actionsByMessage = { ...actionsByMessage, [reply.messageId]: reply.actions };
      atBottom = true;
      void loadSessions();
    } catch (err) {
      error = err instanceof ApiError ? err : "send-failed";
    } finally {
      pending = false;
    }
  }

  function sleep(ms: number): Promise<void> {
    return new Promise((resolve) => setTimeout(resolve, ms));
  }

  /** After a confirm, poll the action row until it leaves `pending` (≤10×1s). */
  async function pollAction(id: string): Promise<void> {
    for (let attempt = 0; attempt < 10; attempt += 1) {
      await sleep(1000);
      // the session list rides the same cadence: titles/updates stay honest
      void loadSessions();
      try {
        const fresh = await getJson<ChatAction>(api, `/api/chat/actions/${id}`);
        actionStatus = { ...actionStatus, [id]: fresh };
        if (fresh.status !== "pending") {
          return;
        }
      } catch {
        return;
      }
    }
  }

  /** {"note": "..."} for confirm/cancel; blank notes are omitted, never sent. */
  function noteBody(id: string): { note: string } | undefined {
    const note = (notes[id] ?? "").trim();
    return note ? { note } : undefined;
  }

  async function confirmAction(id: string): Promise<void> {
    try {
      const updated = await postJson<ChatAction>(`/api/chat/actions/${id}/confirm`, noteBody(id));
      actionStatus = { ...actionStatus, [id]: updated };
      if (updated.status === "pending") {
        await pollAction(id);
      }
      const settled = actionStatus[id] ?? updated;
      if (settled.status === "executed") {
        // The change is law now: every live screen re-loads its slice, so
        // the operator sees the effect without touching refresh.
        deck.broadcastExecuted(settled.method, settled.path);
      }
    } catch (err) {
      error = err instanceof ApiError ? err : "confirm-failed";
    }
  }

  /** One click runs the whole proposal: discrete actions confirm in order,
   * each under its own kernel fence + audit row. Offered only when EVERY
   * pending action reads (no blind bulk writes). */
  async function confirmAll(actionList: ChatAction[]): Promise<void> {
    for (const action of actionList) {
      if (current(action).status === "pending") {
        await confirmAction(action.actionId);
      }
    }
  }

  async function cancelAction(id: string): Promise<void> {
    try {
      const updated = await postJson<ChatAction>(`/api/chat/actions/${id}/cancel`, noteBody(id));
      actionStatus = { ...actionStatus, [id]: updated };
    } catch (err) {
      error = err instanceof ApiError ? err : "cancel-failed";
    }
  }

  async function setVerbosity(next: ChatVerbosity): Promise<void> {
    try {
      const response = await api("/api/chat/preferences", {
        method: "PUT",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ verbosity: next }),
      });
      if (!response.ok) {
        throw await toApiError(response);
      }
    } catch (err) {
      error = err instanceof ApiError ? err : "preferences-failed";
    }
  }

  function current(action: ChatAction): ChatAction {
    return actionStatus[action.actionId] ?? action;
  }

  function pendingActions(actionList: ChatAction[]): ChatAction[] {
    return actionList.filter((a) => current(a).status === "pending");
  }

  /** Effect of a row: the catalog says read for GET, write for the rest —
   * the action row carries the method, so the word is derived, never trusted. */
  function effectFor(action: ChatAction): "read" | "write" {
    return action.method.toUpperCase() === "GET" ? "read" : "write";
  }

  function canRunAll(actionList: ChatAction[]): boolean {
    const open = pendingActions(actionList);
    return open.length > 0 && open.every((a) => effectFor(a) === "read");
  }

  function badgeClass(status: ChatAction["status"]): string {
    return `badge ${TONE_CLASS[stateTone(status)]}`;
  }

  function bodyLines(body: unknown): string[] {
    if (body === undefined || body === null) {
      return [];
    }
    try {
      return JSON.stringify(body, null, 2).split("\n");
    } catch {
      return [String(body)];
    }
  }

  const currentSession = $derived(sessions.find((s) => s.sessionId === sessionId));
  const currentSessionTitle = $derived(
    currentSession?.title || (sessionId ? "Active Session" : "New Conversation"),
  );

  const contextualPrompts = $derived.by(() => {
    const route = screenContext?.route ?? "";
    if (route.includes("/works") || route.includes("/work")) {
      return [
        { title: "Summarize Works", query: "Summarize active work items and stalled steps" },
        { title: "Dead-Letter Queue", query: "Check dead-letter queue status and recent failures" },
        { title: "Pending Policy Gates", query: "List all works waiting for policy gate approval" },
        { title: "Available Commands", query: "/help" },
      ];
    }
    if (route.includes("/flows") || route.includes("/flow")) {
      return [
        { title: "Explain Workflow", query: "Explain active workflow definitions and their steps" },
        { title: "Shadow Mode Runs", query: "Show workflows currently running in shadow mode" },
        { title: "Step Failure Audit", query: "Check if any workflow steps have recent errors" },
        { title: "Available Commands", query: "/help" },
      ];
    }
    if (route.includes("/monitor") || route.includes("/audit") || route.includes("/deliveries")) {
      return [
        { title: "Kernel Health", query: "Check kernel health, metrics roll-up, and replicas" },
        { title: "Recent Audit Events", query: "Summarize recent audit log entries and changes" },
        { title: "Delivery Backlog", query: "Inspect delivery queue backlog and link statuses" },
        { title: "Available Commands", query: "/help" },
      ];
    }
    return [
      { title: "Kernel Roll-Up", query: "Summarize current facilitator kernel state and active works" },
      { title: "Connected Links", query: "Show connected workspace links and gateways" },
      { title: "Dead-Letters", query: "Check dead-letter queue for failed deliveries" },
      { title: "Available Commands", query: "/help" },
    ];
  });

  function applyPrompt(query: string): void {
    draft = query;
    input?.focus();
  }
</script>

<section class="relative flex h-full min-h-0 grow flex-col overflow-hidden" data-testid="chat-view">
  <!-- Top Control Bar: Chat title, History drawer toggle, verbosity, and New Chat -->
  <header class="flex shrink-0 items-center justify-between gap-2 border-b border-edge bg-surface pb-2 pt-1 px-1">
    <div class="flex items-center gap-1.5 min-w-0">
      <button
        class="chip flex items-center gap-1 text-xs"
        class:chip-active={historyOpen}
        data-testid="chat-toggle-history"
        aria-expanded={historyOpen}
        title="Toggle conversation history"
        onclick={() => (historyOpen = !historyOpen)}>
        <History class="size-3.5 text-ink-2" strokeWidth={1.5} aria-hidden="true" />
        <span class="font-medium">History</span>
        {#if sessions.length > 0}
          <span class="mono rounded bg-surface-alt px-1 text-[11px] text-ink-2">{sessions.length}</span>
        {/if}
      </button>

      <div class="flex items-center gap-1 truncate text-xs text-ink-2 max-w-44 sm:max-w-72">
        <span class="text-ink-3">/</span>
        <span class="font-medium text-ink truncate" title={currentSessionTitle}>{currentSessionTitle}</span>
      </div>
    </div>

    <div class="flex items-center gap-1.5 shrink-0">
      <div class="flex items-center gap-1">
        <label class="sr-only" for="chat-verbosity">verbosity</label>
        <select
          id="chat-verbosity"
          class="input py-0.5 px-2 text-xs font-medium cursor-pointer"
          data-testid="chat-verbosity"
          value={verbosity}
          title="Response detail verbosity"
          onchange={(e) => void setVerbosity(e.currentTarget.value as ChatVerbosity)}>
          {#each VERBOSITIES as level}
            <option value={level}>{level}</option>
          {/each}
        </select>
      </div>

      <button
        class="chip chip-active flex items-center gap-1 text-xs font-semibold"
        data-testid="chat-new-session"
        title="Start a new conversation"
        onclick={() => {
          newSession();
          historyOpen = false;
        }}>
        <Plus class="size-3.5" strokeWidth={2} aria-hidden="true" />
        <span>New Chat</span>
      </button>
    </div>
  </header>

  <div class="relative flex min-h-0 grow overflow-hidden">
    <!-- Collapsible History Drawer / Flyout: Stays accessible, hidden when focused on chat -->
    <aside
      class="history-drawer absolute inset-y-0 left-0 z-20 flex w-72 flex-col border-r border-edge bg-surface shadow-overlay transition-transform duration-180 ease-out {historyOpen
        ? 'translate-x-0'
        : '-translate-x-full pointer-events-none opacity-0 md:opacity-100'}"
      class:hidden={!historyOpen}
      aria-label="chat history"
      data-testid="chat-history-sidebar">
      <div class="flex items-center justify-between border-b border-edge px-3 py-2 bg-surface-alt">
        <div class="flex items-center gap-1.5 text-xs font-semibold text-ink">
          <History class="size-3.5 text-ink-2" strokeWidth={1.5} aria-hidden="true" />
          <span>Conversations</span>
          <span class="mono text-[11px] text-ink-2">({sessions.length})</span>
        </div>
        <button
          class="btn px-1.5 py-0.5 text-xs"
          aria-label="close history"
          onclick={() => (historyOpen = false)}>
          <X class="size-3.5" strokeWidth={1.5} />
        </button>
      </div>

      <ul class="grid gap-1 overflow-y-auto grow p-2" data-testid="chat-sessions">
        {#each sessions as s (s.sessionId)}
          {@const seen = ago(s.lastMessageAt)}
          <li class="relative">
            {#if editingId === s.sessionId}
              <div class="flex items-center gap-1 p-1 rounded border border-accent bg-surface-alt">
                <input
                  class="input min-w-0 grow py-0.5 text-xs"
                  aria-label="session title"
                  data-testid={`chat-session-rename-${s.sessionId}`}
                  maxlength={120}
                  bind:value={editTitle}
                  onkeydown={(e) => {
                    if (e.key === "Enter") {
                      e.preventDefault();
                      void renameSession(s.sessionId);
                    }
                    if (e.key === "Escape") {
                      editingId = null;
                    }
                  }} />
                <button
                  class="chip chip-active shrink-0 text-xs"
                  data-testid={`chat-session-rename-save-${s.sessionId}`}
                  disabled={!renameValid}
                  onclick={() => void renameSession(s.sessionId)}>save</button>
                <button
                  class="chip shrink-0 text-xs"
                  onclick={() => (editingId = null)}>cancel</button>
              </div>
            {:else}
              <div class="group flex items-center justify-between gap-1 rounded px-2 py-1.5 hover:bg-surface-alt transition-colors {sessionId === s.sessionId ? 'bg-sunken border-l-2 border-accent' : ''}">
                <button
                  class="min-w-0 grow text-left cursor-pointer"
                  data-testid={`chat-session-${s.sessionId}`}
                  onclick={() => {
                    void openSession(s.sessionId);
                    historyOpen = false;
                  }}>
                  <span class="flex items-center gap-1">
                    <span class="block truncate text-xs font-medium text-ink">{s.title || "(untitled)"}</span>
                    {#if updatedSinceOpen(s)}
                      <span
                        class="badge bg-accent-veil text-accent shrink-0 px-1 py-0 text-[10px]"
                        data-testid="chat-session-updated">new</span>
                    {/if}
                  </span>
                  <span class="mono text-[11px] text-ink-2" title={seen.title}>{seen.text}</span>
                </button>
                <div class="flex items-center gap-0.5 opacity-80 group-hover:opacity-100">
                  <button
                    class="chip shrink-0 px-1.5 py-0.5 text-[11px]"
                    aria-label="rename session"
                    title="rename"
                    data-testid={`chat-session-rename-open-${s.sessionId}`}
                    onclick={() => startRename(s)}>✎</button>
                  <button
                    class="chip shrink-0 px-1.5 py-0.5 text-[11px]"
                    aria-label="delete session"
                    title="delete"
                    data-testid={`chat-session-delete-${s.sessionId}`}
                    onclick={() => void deleteSession(s.sessionId)}>×</button>
                </div>
              </div>
            {/if}
          </li>
        {:else}
          <li class="px-2 py-4 text-xs text-ink-2 text-center">No conversations yet — start one below.</li>
        {/each}
      </ul>
    </aside>

    <!-- Main Conversational Thread Area: Pure ChatGPT-like Focus -->
    <div class="flex min-w-0 grow flex-col justify-between overflow-hidden {expanded ? 'max-w-4xl mx-auto w-full' : ''}">
      <ProblemNote {error} />

      <!-- Scrollable Message Stream -->
      <div
        class="flex grow flex-col gap-3 overflow-y-auto px-1 py-3"
        bind:this={thread}
        onscroll={onThreadScroll}>
        {#if messages.length === 0}
          <!-- Empty State: ChatGPT-style Welcome Hero & Contextual Starter Cards -->
          <div class="my-auto flex flex-col items-center justify-center p-4 text-center max-w-lg mx-auto">
            <div class="mb-2.5 flex size-10 items-center justify-center rounded-full bg-accent-veil border border-border-primary text-accent">
              <Sparkles class="size-5" strokeWidth={1.5} />
            </div>
            <h2 class="text-sm font-semibold text-ink">Open Facilitator Assistant</h2>
            <p class="mt-0.5 text-xs text-ink-2 max-w-sm">
              Ask about active works, workflow execution, dead-letters, or kernel health.
            </p>

            {#if screenContext}
              <div class="mt-3 flex items-center gap-1.5 rounded border border-border-primary bg-accent-veil px-2.5 py-1 text-xs text-accent">
                <Compass class="size-3.5 shrink-0" strokeWidth={1.5} />
                <span class="font-medium truncate">Briefed on: {screenContext.label}</span>
              </div>
            {/if}

            <div class="mt-4 grid w-full grid-cols-1 sm:grid-cols-2 gap-1.5 text-left">
              {#each contextualPrompts as prompt}
                <button
                  class="card p-2 hover:border-accent hover:bg-surface-alt transition-colors text-left cursor-pointer group"
                  onclick={() => applyPrompt(prompt.query)}>
                  <div class="flex items-center gap-1.5 text-xs font-semibold text-ink group-hover:text-accent">
                    <MessageSquare class="size-3.5 text-accent shrink-0" strokeWidth={1.5} />
                    <span class="truncate">{prompt.title}</span>
                  </div>
                  <p class="mt-0.5 text-[11px] text-ink-2 truncate">{prompt.query}</p>
                </button>
              {/each}
            </div>
          </div>
        {/if}

        {#each messages as m, i}
          {#if m.role === "user"}
            <!-- User Message: High contrast elevated card -->
            <div
              class="ml-auto max-w-[88%] rounded-lg border border-border-primary bg-accent-veil p-3 text-ink shadow-sm"
              data-testid={`chat-message-${i}`}>
              <div class="mb-1 flex items-center justify-end gap-1.5 text-[11px] uppercase text-ink-2 font-medium">
                <User class="size-3" strokeWidth={1.5} aria-hidden="true" />
                <span>you</span>
                <span
                  class="badge px-1.5 py-0 text-[10px] {TONE_CLASS[BASIS_TONE[m.basis] ?? 'neutral']}"
                  data-testid={`chat-basis-${i}`}>{BASIS_LABEL[m.basis] ?? m.basis}</span>
              </div>
              <p class="break-words whitespace-pre-wrap text-sm leading-relaxed">{m.text}</p>
            </div>
          {:else}
            <!-- Assistant Message: Modern prose card with tools and proposals -->
            <div
              class="mr-auto w-full max-w-full rounded-lg border border-edge bg-surface p-3.5 text-ink shadow-sm"
              data-testid={`chat-message-${i}`}>
              <div class="mb-1.5 flex items-center gap-1.5 text-[11px] uppercase text-ink-2 font-medium">
                <Sparkles class="size-3.5 text-accent" strokeWidth={1.5} aria-hidden="true" />
                <span class="font-semibold text-ink">assistant</span>
                <span
                  class="badge px-1.5 py-0 text-[10px] {TONE_CLASS[BASIS_TONE[m.basis] ?? 'neutral']}"
                  data-testid={`chat-basis-${i}`}>{BASIS_LABEL[m.basis] ?? m.basis}</span>
              </div>

              <div class="chat-prose break-words whitespace-pre-wrap text-sm leading-relaxed" data-testid="chat-md">{@html renderChatMarkdown(fallbackProse(m))}</div>

              {#if m.basis === "fallback" && catalog && catalog.verbs.length > 0}
                <!-- Canned verb list: the palette trigger -->
                <details class="mt-2 rounded border border-edge bg-surface-alt p-2 text-xs" data-testid={`chat-fallback-verbs-${i}`}>
                  <summary class="cursor-pointer font-medium text-ink-2 hover:text-ink">Available commands ({catalog.verbs.length})</summary>
                  <div class="mt-1.5 flex flex-wrap gap-1">
                    {#each catalog.verbs as v}
                      <button class="chip text-xs" data-testid={`chat-fallback-verb-${v.verb}`} onclick={() => useVerb(v.verb)}>
                        <span class="mono font-semibold text-accent">/{v.verb}</span>
                        {#if v.args.length}
                          <span class="text-ink-2">{v.args.map((a) => (a.required ? `<${a.name}>` : `[${a.name}]`)).join(" ")}</span>
                        {/if}
                        — {v.description}
                      </button>
                    {/each}
                  </div>
                </details>
              {/if}

              {#if readTrail(m).length}
                <details class="mt-2 rounded border border-edge bg-surface-alt p-2 text-xs" data-testid={`chat-reads-${i}`}>
                  <summary class="cursor-pointer font-medium text-ink-2 hover:text-ink">Looked at {readTrail(m).length} endpoint{readTrail(m).length === 1 ? "" : "s"}</summary>
                  <ul class="mt-1 space-y-0.5">
                    {#each readTrail(m) as r}
                      <li class="mono text-[11px] text-ink-2">looked at: {r.method} {r.path} ({r.status})</li>
                    {/each}
                  </ul>
                </details>
              {/if}

              {#if actionsByMessage[m.messageId]?.length}
                {@const proposal = actionsByMessage[m.messageId]}
                <div
                  class="mt-3 rounded-lg border border-edge bg-surface-alt p-3"
                  data-testid="chat-option-card">
                  <div class="mb-2 flex items-center justify-between gap-2 border-b border-edge pb-1.5">
                    <h3 class="text-xs font-semibold uppercase tracking-wider text-ink-2">
                      proposal · {proposal.length} action{proposal.length === 1 ? "" : "s"}
                    </h3>
                    {#if pendingActions(proposal).length}
                      <div class="flex gap-1">
                        {#if canRunAll(proposal)}
                          <button
                            class="chip chip-active text-xs font-semibold"
                            data-testid="chat-option-confirm-all"
                            onclick={() => void confirmAll(proposal)}>
                            run all
                          </button>
                        {/if}
                        <button
                          class="chip text-xs"
                          data-testid="chat-option-cancel-all"
                          onclick={() => void Promise.all(
                            pendingActions(proposal).map((a) => cancelAction(a.actionId)))}>
                          dismiss
                        </button>
                      </div>
                    {/if}
                  </div>
                  {#each proposal as action}
                    {@const live = current(action)}
                    {@const lines = bodyLines(action.body)}
                    <div
                      class="border-t border-edge/60 py-2 first:border-t-0"
                      data-testid={`chat-action-${action.method.toLowerCase()}`}>
                      <div class="flex items-baseline justify-between gap-2">
                        <p class="text-xs font-semibold text-ink">{live.summary || action.description}</p>
                        <span class="text-[11px] font-semibold uppercase text-ink-2" data-testid={`chat-action-effect-${action.actionId}`}>
                          {effectFor(action)}
                        </span>
                      </div>
                      <p class="mono mt-1 break-all text-xs text-ink-2">
                        <span class="font-semibold text-ink">{action.method}</span> {action.path}
                        <span class={badgeClass(live.status)} data-testid={`chat-action-status-${action.actionId}`}>
                          {live.status}{#if live.statusCode !== null && live.statusCode !== undefined}
                            {" "}{live.statusCode}{/if}
                        </span>
                        {#if live.expiresAt}
                          {@const exp = ago(live.expiresAt)}
                          <span
                            class="text-[11px] text-ink-2"
                            title={stamp(live.expiresAt)}
                            data-testid="chat-action-expires">expires {exp.text}</span>
                        {/if}
                      </p>
                      {#if lines.length}
                        <details class="mt-1" data-testid="chat-action-body">
                          <summary class="mono cursor-pointer whitespace-pre text-[11px] text-ink-2">body · {lines.length} line{lines.length === 1 ? "" : "s"}
{lines.length > 5 ? `${lines.slice(0, 5).join("\n")}\n…` : lines.join("\n")}</summary>
                          <pre class="mono mt-1 overflow-x-auto rounded bg-bg p-2 text-xs">{lines.join("\n")}</pre>
                        </details>
                      {/if}
                      {#if live.status === "failed"}
                        <p class="mono mt-1 break-all text-xs text-danger" data-testid={`chat-action-result-${action.actionId}`}>
                          {live.statusCode ?? "no status"}
                          {#if live.result !== null && live.result !== undefined}
                            — {typeof live.result === "string" ? live.result : JSON.stringify(live.result)}
                          {/if}
                        </p>
                      {/if}
                      {#if live.status === "executed"}
                        <a
                          class="mt-1 inline-block text-xs text-accent underline font-medium"
                          href={`#/monitor/audit?correlation=${action.actionId}`}
                          data-testid="chat-action-audit-link"
                          onclick={(e) => {
                            e.preventDefault();
                            go(`#/monitor/audit?correlation=${action.actionId}`);
                          }}>audit trail</a>
                      {/if}
                      {#if live.status === "pending"}
                        <div class="mt-1.5 flex flex-wrap items-center gap-1.5">
                          <input
                            class="input min-w-0 grow basis-32 py-1 text-xs"
                            placeholder="note (optional)"
                            aria-label="decision note"
                            maxlength={1024}
                            bind:value={notes[action.actionId]}
                            data-testid={`chat-action-note-${action.actionId}`} />
                          <button
                            class="chip chip-active text-xs font-semibold"
                            data-testid={`chat-action-confirm-${action.actionId}`}
                            onclick={() => void confirmAction(action.actionId)}>confirm</button>
                          <button
                            class="chip text-xs"
                            data-testid={`chat-action-cancel-${action.actionId}`}
                            onclick={() => void cancelAction(action.actionId)}>cancel</button>
                        </div>
                      {/if}
                    </div>
                  {/each}
                </div>
              {/if}
            </div>
          {/if}
        {/each}

        {#if pending}
          <div class="flex items-center gap-2 text-xs text-accent px-3 py-2 rounded-md bg-accent-veil border border-border-primary w-fit shadow-sm" data-testid="chat-pending">
            <Sparkles class="size-3.5 animate-spin" strokeWidth={1.5} />
            <span class="font-medium">Thinking & querying kernel state…</span>
          </div>
        {/if}
      </div>

      {#if !atBottom}
        <button
          class="chip chip-active self-center my-1 shadow-overlay flex items-center gap-1 text-xs font-semibold"
          data-testid="chat-jump-latest"
          onclick={jumpLatest}>
          <ArrowDown class="size-3" /> jump to latest
        </button>
      {/if}

      {#if paletteOpen && catalog && catalog.verbs.length > 0}
        <div class="mt-1 flex flex-wrap gap-1 border-t border-edge bg-surface-alt p-2 rounded-t" data-testid="chat-verb-palette">
          {#each paletteVerbs as v}
            <button class="chip text-xs" data-testid={`chat-verb-${v.verb}`} onclick={() => useVerb(v.verb)}>
              <span class="mono font-semibold text-accent">/{v.verb}</span>
              {#if v.args.length}
                <span class="text-ink-2">{v.args.map((a) => (a.required ? `<${a.name}>` : `[${a.name}]`)).join(" ")}</span>
              {/if}
              — {v.description}
            </button>
          {/each}
          {#if paletteVerbs.length === 0}
            <p class="text-xs text-ink-2">no matching command</p>
          {/if}
        </div>
      {/if}

      <!-- Composer Input Box -->
      <div class="mt-1.5 relative flex items-center gap-2 rounded-lg border border-edge bg-surface p-1.5 focus-within:border-accent shadow-sm">
        <input
          class="input min-w-0 grow border-0 bg-transparent px-2.5 py-1 text-sm focus:outline-none"
          placeholder="Ask the AiDeck or type / for commands…"
          bind:this={input}
          bind:value={draft}
          data-testid="chat-input"
          onkeydown={(e) => {
            if (e.key === "Enter" && !e.shiftKey) {
              e.preventDefault();
              void send();
            }
          }} />
        <button
          class="btn btn-primary shrink-0 py-1.5 px-3 flex items-center gap-1.5 text-xs font-semibold"
          disabled={pending || !draft.trim()}
          data-testid="chat-send"
          onclick={() => void send()}>
          <Send class="size-3.5" strokeWidth={1.5} />
          <span>send</span>
        </button>
      </div>
    </div>
  </div>
</section>

<style>
  .history-drawer {
    transition: transform 180ms cubic-bezier(0.2, 0, 0, 1), opacity 180ms ease;
  }
</style>
