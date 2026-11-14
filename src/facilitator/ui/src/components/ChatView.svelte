<script lang="ts">
  import { ApiError, getJson, toApiError, type Fetcher } from "../lib/api.js";
  import { apiFetch } from "../lib/auth.svelte.js";
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
  let { fetcher: api = apiFetch }: { fetcher?: Fetcher } = $props();

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
      const reply = await postJson<ChatReply>(`/api/chat/sessions/${target}/messages`, {
        text,
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
</script>

<section class="card flex flex-col gap-4 md:flex-row" data-testid="chat-view">
  <aside class="w-full shrink-0 border-b border-edge pb-3 md:w-60 md:border-b-0 md:border-r md:pb-0 md:pr-3">
    <div class="mb-2 flex items-center justify-between">
      <h2 class="text-sm font-medium">Sessions</h2>
      <button class="btn" data-testid="chat-new-session" onclick={newSession}>new</button>
    </div>
    <div class="mb-3 md:max-w-40">
      <label class="mb-1 block text-xs uppercase text-ink-3" for="chat-verbosity">verbosity</label>
      <select
        id="chat-verbosity"
        class="input"
        data-testid="chat-verbosity"
        value={verbosity}
        onchange={(e) => void setVerbosity(e.currentTarget.value as ChatVerbosity)}>
        {#each VERBOSITIES as level}
          <option value={level}>{level}</option>
        {/each}
      </select>
    </div>
    <ul class="flex gap-1 overflow-x-auto md:grid" data-testid="chat-sessions">
      {#each sessions as s}
        {@const seen = ago(s.lastMessageAt)}
        <li class="min-w-40 shrink-0 md:min-w-0">
          {#if editingId === s.sessionId}
            <div class="flex gap-1">
              <input
                class="input min-w-0 grow"
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
                class="chip chip-active"
                data-testid={`chat-session-rename-save-${s.sessionId}`}
                disabled={!renameValid}
                onclick={() => void renameSession(s.sessionId)}>save</button>
            </div>
          {:else}
            <div class="flex items-start gap-1">
              <button
                class="w-full min-w-0 rounded px-2 py-1 text-left hover:bg-sunken {sessionId === s.sessionId
                  ? 'bg-sunken'
                  : ''}"
                data-testid={`chat-session-${s.sessionId}`}
                onclick={() => void openSession(s.sessionId)}>
                <span class="flex items-center gap-1">
                  <span class="block truncate text-xs">{s.title || "(untitled)"}</span>
                  {#if updatedSinceOpen(s)}
                    <span
                      class="badge bg-accent-veil text-accent ring-accent/25 shrink-0 px-1 py-0 text-xs"
                      data-testid="chat-session-updated">new</span>
                  {/if}
                </span>
                <span class="mono text-xs text-ink-3" title={seen.title}>{seen.text}</span>
              </button>
              <button
                class="chip shrink-0"
                aria-label="rename session"
                title="rename"
                data-testid={`chat-session-rename-open-${s.sessionId}`}
                onclick={() => startRename(s)}>✎</button>
              <button
                class="chip shrink-0"
                aria-label="delete session"
                title="delete"
                data-testid={`chat-session-delete-${s.sessionId}`}
                onclick={() => void deleteSession(s.sessionId)}>×</button>
            </div>
          {/if}
        </li>
      {:else}
        <li class="text-xs text-ink-3">No conversations yet — start one below.</li>
      {/each}
    </ul>
  </aside>

  <div class="flex min-h-96 grow flex-col">
    <ProblemNote {error} />
    <div
      class="flex grow flex-col gap-2 overflow-y-auto py-2"
      bind:this={thread}
      onscroll={onThreadScroll}>
      {#each messages as m, i}
        <div
          class="max-w-full rounded-lg border border-edge p-3 {m.role === 'user' ? 'bg-sunken' : ''}"
          data-testid={`chat-message-${i}`}>
          <p class="mb-1 flex items-center gap-2 text-xs uppercase text-ink-3">
            {m.role}
            <span
              class="badge px-1.5 py-0 text-xs {TONE_CLASS[BASIS_TONE[m.basis] ?? 'neutral']}"
              data-testid={`chat-basis-${i}`}>{BASIS_LABEL[m.basis] ?? m.basis}</span>
          </p>
          {#if m.role === "assistant"}
            <div class="chat-prose break-words whitespace-pre-wrap text-sm leading-relaxed" data-testid="chat-md">{@html renderChatMarkdown(fallbackProse(m))}</div>
            {#if m.basis === "fallback" && catalog && catalog.verbs.length > 0}
              <!-- the canned verb list: the palette's trigger, not prose -->
              <details class="mt-1" data-testid={`chat-fallback-verbs-${i}`}>
                <summary class="cursor-pointer text-xs text-ink-2">available commands</summary>
                <div class="mt-1 flex flex-wrap gap-1">
                  {#each catalog.verbs as v}
                    <button class="chip" data-testid={`chat-fallback-verb-${v.verb}`} onclick={() => useVerb(v.verb)}>
                      <span class="mono">/{v.verb}</span>
                      {#if v.args.length}
                        <span class="text-ink-3">{v.args.map((a) => (a.required ? `<${a.name}>` : `[${a.name}]`)).join(" ")}</span>
                      {/if}
                      — {v.description}
                    </button>
                  {/each}
                </div>
              </details>
            {/if}
            {#if readTrail(m).length}
              <details class="mt-1" data-testid={`chat-reads-${i}`}>
                <summary class="cursor-pointer text-xs text-ink-3">looked at {readTrail(m).length} endpoint{readTrail(m).length === 1 ? "" : "s"}</summary>
                <ul class="mt-1 space-y-0.5">
                  {#each readTrail(m) as r}
                    <li class="mono text-xs text-ink-2">looked at: {r.method} {r.path} ({r.status})</li>
                  {/each}
                </ul>
              </details>
            {/if}
          {:else}
            <p class="break-words whitespace-pre-wrap text-sm">{m.text}</p>
          {/if}
          {#if actionsByMessage[m.messageId]?.length}
            {@const proposal = actionsByMessage[m.messageId]}
            <div
              class="mt-2 rounded-lg border border-edge bg-sunken p-2"
              data-testid="chat-option-card">
              <div class="mb-1 flex items-center justify-between gap-2">
                <h3 class="text-xs font-medium text-ink-2">
                  proposal · {proposal.length} action{proposal.length === 1 ? "" : "s"}
                </h3>
                {#if pendingActions(proposal).length}
                  <div class="flex gap-1">
                    {#if canRunAll(proposal)}
                      <button
                        class="chip chip-active"
                        data-testid="chat-option-confirm-all"
                        onclick={() => void confirmAll(proposal)}>
                        run all
                      </button>
                    {/if}
                    <button
                      class="chip"
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
                  class="border-t border-edge/50 py-1 first:border-t-0"
                  data-testid={`chat-action-${action.method.toLowerCase()}`}>
                  <div class="flex items-baseline justify-between gap-2">
                    <p class="text-xs font-medium">{live.summary || action.description}</p>
                    <span class="text-xs uppercase text-ink-3" data-testid={`chat-action-effect-${action.actionId}`}>
                      {effectFor(action)}
                    </span>
                  </div>
                  <p class="mono mt-1 break-all text-xs text-ink-2">
                    {action.method} {action.path}
                    <span class={badgeClass(live.status)} data-testid={`chat-action-status-${action.actionId}`}>
                      {live.status}{#if live.statusCode !== null && live.statusCode !== undefined}
                        {" "}{live.statusCode}{/if}
                    </span>
                    {#if live.expiresAt}
                      {@const exp = ago(live.expiresAt)}
                      <span
                        class="text-xs text-ink-3"
                        title={stamp(live.expiresAt)}
                        data-testid="chat-action-expires">expires {exp.text}</span>
                    {/if}
                  </p>
                  {#if lines.length}
                    <details class="mt-1" data-testid="chat-action-body">
                      <summary class="mono cursor-pointer whitespace-pre text-xs text-ink-3">body · {lines.length} line{lines.length === 1 ? "" : "s"}
{lines.length > 5 ? `${lines.slice(0, 5).join("\n")}\n…` : lines.join("\n")}</summary>
                      <pre class="mono overflow-x-auto rounded bg-bg p-2 text-xs">{lines.join("\n")}</pre>
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
                      class="text-xs text-accent underline"
                      href={`#/audit?correlation=${action.actionId}`}
                      data-testid="chat-action-audit-link"
                      onclick={(e) => {
                        e.preventDefault();
                        go(`#/audit?correlation=${action.actionId}`);
                      }}>audit trail</a>
                  {/if}
                  {#if live.status === "pending"}
                    <div class="mt-1 flex flex-wrap items-center gap-1">
                      <input
                        class="input min-w-0 grow basis-32 py-0.5 text-xs"
                        placeholder="note (optional)"
                        aria-label="decision note"
                        maxlength={1024}
                        bind:value={notes[action.actionId]}
                        data-testid={`chat-action-note-${action.actionId}`} />
                      <button
                        class="chip chip-active"
                        data-testid={`chat-action-confirm-${action.actionId}`}
                        onclick={() => void confirmAction(action.actionId)}>confirm</button>
                      <button
                        class="chip"
                        data-testid={`chat-action-cancel-${action.actionId}`}
                        onclick={() => void cancelAction(action.actionId)}>cancel</button>
                    </div>
                  {/if}
                </div>
              {/each}
            </div>
          {/if}
        </div>
      {/each}
      {#if pending}
        <p class="badge w-fit bg-amber-veil text-amber ring-amber/25" data-testid="chat-pending">thinking…</p>
      {/if}
    </div>
    {#if !atBottom}
      <button
        class="chip chip-active mt-1 self-center"
        data-testid="chat-jump-latest"
        onclick={jumpLatest}>jump to latest</button>
    {/if}
    {#if paletteOpen && catalog && catalog.verbs.length > 0}
      <div class="mt-1 flex flex-wrap gap-1" data-testid="chat-verb-palette">
        {#each paletteVerbs as v}
          <button class="chip" data-testid={`chat-verb-${v.verb}`} onclick={() => useVerb(v.verb)}>
            <span class="mono">/{v.verb}</span>
            {#if v.args.length}
              <span class="text-ink-3">{v.args.map((a) => (a.required ? `<${a.name}>` : `[${a.name}]`)).join(" ")}</span>
            {/if}
            — {v.description}
          </button>
        {/each}
        {#if paletteVerbs.length === 0}
          <p class="text-xs text-ink-3">no matching command</p>
        {/if}
      </div>
    {/if}
    <div class="mt-2 flex gap-2">
      <input
        class="input min-w-0 grow"
        placeholder="ask the AiDeck…"
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
        class="btn btn-primary shrink-0"
        data-testid="chat-send"
        disabled={pending || !draft.trim()}
        onclick={() => void send()}>send</button>
    </div>
  </div>
</section>
