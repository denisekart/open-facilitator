<script lang="ts">
  import { ApiError, getJson, toApiError } from "../lib/api.js";
  import { apiFetch } from "../lib/auth.svelte.js";
  import { renderChatMarkdown } from "../lib/markdown.js";
  import type {
    ChatAction,
    ChatMessage,
    ChatPreferences,
    ChatReply,
    ChatSession,
    ChatVerbosity,
  } from "../lib/types.js";
  import ProblemNote from "./ProblemNote.svelte";

  const VERBOSITIES: ChatVerbosity[] = ["terse", "balanced", "proactive"];

  let sessions = $state<ChatSession[]>([]);
  let sessionId = $state<string | null>(null);
  let messages = $state<ChatMessage[]>([]);
  let draft = $state("");
  let pending = $state(false);
  let error = $state<ApiError | string | null>(null);
  let verbosity = $state<ChatVerbosity>("balanced");
  // One PROPOSAL OPTION per assistant message: a card grouping that turn's
  // discrete actions; confirm runs inline in the chat, per action or all.
  // Live status lands in actionStatus from confirm + polling. History reloads
  // carry each message's actions, so a reopened session replays the same
  // option cards (the kernel rows are the source of truth).
  let actionsByMessage = $state<Record<string, ChatAction[]>>({});
  let actionStatus = $state<Record<string, ChatAction>>({});

  async function postJson<T>(path: string, body?: unknown): Promise<T> {
    const response = await apiFetch(path, {
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
      sessions = await getJson<ChatSession[]>(apiFetch, "/api/chat/sessions");
    } catch (err) {
      error = err instanceof ApiError ? err : "sessions-failed";
    }
  }

  async function loadPreferences(): Promise<void> {
    try {
      const prefs = await getJson<ChatPreferences>(apiFetch, "/api/chat/preferences");
      if (VERBOSITIES.includes(prefs.verbosity)) {
        verbosity = prefs.verbosity;
      }
    } catch {
      // absent preference row or gate miss: the select keeps the default
    }
  }

  $effect(() => {
    void loadSessions();
    void loadPreferences();
  });

  function newSession(): void {
    sessionId = null;
    messages = [];
    actionsByMessage = {};
    actionStatus = {};
    error = null;
  }

  async function openSession(id: string): Promise<void> {
    sessionId = id;
    messages = [];
    actionsByMessage = {};
    actionStatus = {};
    error = null;
    try {
      messages = await getJson<ChatMessage[]>(
        apiFetch,
        `/api/chat/sessions/${id}/messages`,
      );
      const byMessage: Record<string, ChatAction[]> = {};
      for (const m of messages) {
        if (m.actions?.length) {
          byMessage[m.messageId] = m.actions;
        }
      }
      actionsByMessage = byMessage;
    } catch (err) {
      error = err instanceof ApiError ? err : "messages-failed";
    }
  }

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
        },
      ];
      actionsByMessage = { ...actionsByMessage, [reply.messageId]: reply.actions };
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
      try {
        const fresh = await getJson<ChatAction>(apiFetch, `/api/chat/actions/${id}`);
        actionStatus = { ...actionStatus, [id]: fresh };
        if (fresh.status !== "pending") {
          return;
        }
      } catch {
        return;
      }
    }
  }

  async function confirmAction(id: string): Promise<void> {
    try {
      const updated = await postJson<ChatAction>(`/api/chat/actions/${id}/confirm`);
      actionStatus = { ...actionStatus, [id]: updated };
      if (updated.status === "pending") {
        await pollAction(id);
      }
    } catch (err) {
      error = err instanceof ApiError ? err : "confirm-failed";
    }
  }

  /** One click runs the whole proposal: discrete actions confirm in order,
   * each under its own kernel fence + audit row. */
  async function confirmAll(actionList: ChatAction[]): Promise<void> {
    for (const action of actionList) {
      if (current(action).status === "pending") {
        await confirmAction(action.actionId);
      }
    }
  }

  async function cancelAction(id: string): Promise<void> {
    try {
      const updated = await postJson<ChatAction>(`/api/chat/actions/${id}/cancel`);
      actionStatus = { ...actionStatus, [id]: updated };
    } catch (err) {
      error = err instanceof ApiError ? err : "cancel-failed";
    }
  }

  async function setVerbosity(next: ChatVerbosity): Promise<void> {
    try {
      const response = await apiFetch("/api/chat/preferences", {
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

  function pendingCount(actionList: ChatAction[]): number {
    return actionList.filter((a) => current(a).status === "pending").length;
  }

  function badgeClass(status: ChatAction["status"]): string {
    if (status === "executed") {
      return "badge bg-ok/15 text-ok";
    }
    if (status === "failed") {
      return "badge bg-danger/15 text-danger";
    }
    return "badge bg-warn/15 text-warn";
  }

  function bodyPreview(body: unknown): string | null {
    return body === undefined || body === null ? null : JSON.stringify(body, null, 2);
  }

  function stamp(when: string | null): string {
    return when ? when.replace("T", " ").replace(/\.\d+Z$/, "Z") : "never";
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
        <li class="min-w-40 shrink-0 md:min-w-0">
          <button
            class="w-full rounded px-2 py-1 text-left hover:bg-raised {sessionId === s.sessionId
              ? 'bg-raised'
              : ''}"
            data-testid={`chat-session-${s.sessionId}`}
            onclick={() => void openSession(s.sessionId)}>
            <span class="block truncate text-xs">{s.title || "(untitled)"}</span>
            <span class="mono text-[10px] text-ink-3">{stamp(s.lastMessageAt)}</span>
          </button>
        </li>
      {:else}
        <li class="text-xs text-ink-3">no sessions yet</li>
      {/each}
    </ul>
  </aside>

  <div class="flex min-h-96 grow flex-col">
    <ProblemNote {error} />
    <div class="flex grow flex-col gap-2 overflow-y-auto py-2">
      {#each messages as m, i}
        <div
          class="max-w-full rounded-lg border border-edge p-3 {m.role === 'user' ? 'bg-raised' : ''}"
          data-testid={`chat-message-${i}`}>
          <p class="mb-1 text-[10px] uppercase text-ink-3">{m.role} · {m.basis}</p>
          {#if m.role === "assistant"}
            <div class="chat-prose break-words whitespace-pre-wrap text-sm leading-relaxed" data-testid="chat-md">{@html renderChatMarkdown(m.text)}</div>
          {:else}
            <p class="break-words whitespace-pre-wrap text-sm">{m.text}</p>
          {/if}
          {#if actionsByMessage[m.messageId]?.length}
            {@const proposal = actionsByMessage[m.messageId]}
            <div
              class="mt-2 rounded-lg border border-edge bg-raised p-2"
              data-testid="chat-option-card">
              <div class="mb-1 flex items-center justify-between gap-2">
                <h3 class="text-xs font-medium text-ink-2">
                  proposal · {proposal.length} action{proposal.length === 1 ? "" : "s"}
                </h3>
                {#if pendingCount(proposal)}
                  <div class="flex gap-1">
                    <button
                      class="chip chip-active"
                      data-testid="chat-option-confirm-all"
                      onclick={() => void confirmAll(proposal)}>
                      run all
                    </button>
                    <button
                      class="chip"
                      data-testid="chat-option-cancel-all"
                      onclick={() => void Promise.all(
                        proposal.filter((a) => current(a).status === "pending").map((a) => cancelAction(a.actionId)))}>
                      dismiss
                    </button>
                  </div>
                {/if}
              </div>
              {#each proposal as action}
                {@const live = current(action)}
                <div
                  class="border-t border-edge/50 py-1 first:border-t-0"
                  data-testid={`chat-action-${action.method.toLowerCase()}`}>
                  <p class="text-xs">{action.description}</p>
                  <p class="mono mt-1 break-all text-xs text-ink-2">
                    {action.method} {action.path}
                    <span class={badgeClass(live.status)} data-testid={`chat-action-status-${action.actionId}`}>
                      {live.status}{#if live.statusCode !== null && live.statusCode !== undefined}
                        {" "}{live.statusCode}{/if}
                    </span>
                  </p>
                  {#if bodyPreview(action.body)}
                    <pre class="mono mt-1 overflow-x-auto rounded bg-bg p-2 text-[10px]">{bodyPreview(action.body)}</pre>
                  {/if}
                  {#if live.status === "pending"}
                    <div class="mt-1 flex gap-1">
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
        <p class="badge w-fit bg-warn/15 text-warn" data-testid="chat-pending">thinking…</p>
      {/if}
    </div>
    <div class="mt-2 flex gap-2">
      <input
        class="input min-w-0 grow"
        placeholder="ask the AiDeck…"
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
