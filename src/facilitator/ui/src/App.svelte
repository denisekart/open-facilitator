<script lang="ts">
  import ChatView from "./components/ChatView.svelte";
  import ConfigView from "./components/ConfigView.svelte";
  import DeliveriesView from "./components/DeliveriesView.svelte";
  import HealthStrip from "./components/HealthStrip.svelte";
  import LinksView from "./components/LinksView.svelte";
  import WorkInspector from "./components/WorkInspector.svelte";
  import { ApiError, getJson } from "./lib/api.js";
  import { apiFetch, session } from "./lib/auth.svelte.js";
  import { health } from "./lib/health.svelte.js";
  import { TONE_CLASS, stateLabel, stateTone } from "./lib/status.js";
  import { theme, toggleTheme } from "./lib/theme.svelte.js";
  import type { AdminSearchResults, SearchSection } from "./lib/types.js";

  type Panel = "none" | "links" | "deliveries" | "config" | "chat";

  let booted = $state(false);
  let bootError = $state<string | null>(null);
  let panel = $state<Panel>("none");
  let query = $state("");
  let activeSections = $state<Set<SearchSection>>(new Set());
  let results = $state<AdminSearchResults | null>(null);
  let searchError = $state<ApiError | null>(null);
  let searching = $state(false);
  let selectedWork = $state<string | null>(null);
  let searchBox = $state<HTMLInputElement | null>(null);

  const SECTIONS: { key: SearchSection; label: string }[] = [
    { key: "works", label: "works" },
    { key: "projects", label: "projects" },
    { key: "assignments", label: "assignments" },
    { key: "artifacts", label: "artifacts" },
    { key: "memories", label: "memories" },
    { key: "messages", label: "messages" },
  ];
  const VIEW_BUTTONS: { key: Panel; label: string; hint: string; needsChatRole?: boolean }[] = [
    { key: "links", label: "Links", hint: "gateways connected to this kernel" },
    {
      key: "deliveries",
      label: "Deliveries",
      hint: "every message the kernel sent, and its fate",
    },
    { key: "config", label: "Config", hint: "the live routing policy" },
    { key: "chat", label: "AiDeck", hint: "talk to the kernel's assistant", needsChatRole: true },
  ];

  let debounce: ReturnType<typeof setTimeout> | undefined;
  let searchSeq = 0;

  $effect(() => {
    void session.init().then(
      () => {
        booted = true;
        if (session.user) {
          health.start(apiFetch);
        }
      },
      (err: unknown) => {
        bootError = String(err);
      },
    );
  });

  // '/' focuses search from anywhere (except while typing); Esc leaves it.
  function onKeydown(ev: KeyboardEvent): void {
    const typing =
      ev.target instanceof HTMLElement &&
      ["INPUT", "TEXTAREA", "SELECT"].includes(ev.target.tagName);
    if (ev.key === "/" && !typing && !ev.metaKey && !ev.ctrlKey && !ev.altKey) {
      ev.preventDefault();
      searchBox?.focus();
    } else if (ev.key === "Escape" && document.activeElement === searchBox) {
      searchBox?.blur();
    }
  }

  function onQuery(): void {
    clearTimeout(debounce);
    debounce = setTimeout(() => void runSearch(), 300);
  }

  async function runSearch(): Promise<void> {
    const q = query.trim();
    if (!q) {
      results = null;
      return;
    }
    searching = true;
    const seq = ++searchSeq;
    try {
      const type = activeSections.size === 1 ? [...activeSections][0] : null;
      const path = `/api/admin/search?q=${encodeURIComponent(q)}${type ? `&type=${type}` : ""}&limit=20`;
      const fetched = await getJson<AdminSearchResults>(apiFetch, path);
      if (seq === searchSeq) {
        results = fetched;
        searchError = null;
      }
    } catch (err) {
      if (seq === searchSeq) {
        searchError = err instanceof ApiError ? err : new ApiError(0, "fetch-failed", String(err), null);
        results = null;
      }
    } finally {
      if (seq === searchSeq) {
        searching = false;
      }
    }
  }

  function toggleSection(key: SearchSection): void {
    const next = new Set(activeSections);
    if (next.has(key)) {
      next.delete(key);
    } else {
      next.add(key);
    }
    activeSections = next;
    void runSearch();
  }

  function openHit(section: SearchSection, id: string): void {
    if (section === "works") {
      selectedWork = id;
    }
  }

  const authenticated = $derived(!!session.user);
  const visibleSections = $derived.by(() => {
    const found = results;
    return found ? SECTIONS.filter((s) => (found[s.key] as unknown[]).length > 0) : [];
  });
  const hitTotal = $derived(
    results ? visibleSections.reduce((n, s) => n + (results![s.key] as unknown[]).length, 0) : 0,
  );
  const operatorRoles = $derived(
    session.roles.filter((role) => role.startsWith("framework-") || role.startsWith("kernel-")),
  );
  const viewButtons = $derived(VIEW_BUTTONS.filter((b) => !b.needsChatRole || session.canChat));
</script>

<svelte:window onkeydown={onKeydown} />

{#if bootError}
  <main class="mx-auto max-w-lg p-8">
    <p class="badge bg-danger-veil text-danger ring-danger/25" data-testid="boot-error">
      The console could not start: {bootError}
    </p>
  </main>
{:else if !booted}
  <main class="p-8 text-ink-3" data-testid="booting">loading…</main>
{:else if !authenticated}
  <main class="flex min-h-screen flex-col items-center justify-center gap-4" data-testid="login-gate">
    <svg class="size-12 text-ink" viewBox="0 0 32 32" aria-hidden="true"
      ><g stroke="currentColor" stroke-width="2.6" stroke-linecap="round"
        ><path d="M11.8 11.8 L6.4 6.4" /><path d="M20.2 11.8 L25.6 6.4" /><path
          d="M11.8 20.2 L6.4 25.6" /><path d="M20.2 20.2 L25.6 25.6" /></g
      ><rect x="9.6" y="9.6" width="12.8" height="12.8" rx="3.4" fill="currentColor" /><circle
        cx="4.6" cy="4.6" r="2.7" fill="currentColor" opacity="0.55" /><circle
        cx="27.4" cy="4.6" r="2.7" fill="currentColor" opacity="0.55" /><circle
        cx="4.6" cy="27.4" r="2.7" fill="currentColor" opacity="0.55" /><circle
        cx="27.4" cy="27.4" r="2.7" fill="#D97706" /></svg
    >
    <h1 class="text-xl font-medium">Open Facilitator — operator console</h1>
    <p class="max-w-sm text-center text-ink-3">
      The live view of one facilitator kernel: works, decisions, links, evidence.
    </p>
    <p class="text-xs text-ink-3">sign in with a kernel-operator or framework-admin identity</p>
    <button class="btn btn-primary" data-testid="login" onclick={() => void session.login()}>
      sign in
    </button>
  </main>
{:else}
  <div class="mx-auto max-w-6xl p-4">
    <header class="mb-4 flex flex-wrap items-center justify-between gap-4">
      <span class="flex items-center gap-2">
        <svg class="size-6 text-ink" viewBox="0 0 32 32" aria-hidden="true"
          ><g stroke="currentColor" stroke-width="2.6" stroke-linecap="round"
            ><path d="M11.8 11.8 L6.4 6.4" /><path d="M20.2 11.8 L25.6 6.4" /><path
              d="M11.8 20.2 L6.4 25.6" /><path d="M20.2 20.2 L25.6 25.6" /></g
          ><rect x="9.6" y="9.6" width="12.8" height="12.8" rx="3.4" fill="currentColor" /><circle
            cx="4.6" cy="4.6" r="2.7" fill="currentColor" opacity="0.55" /><circle
            cx="27.4" cy="4.6" r="2.7" fill="currentColor" opacity="0.55" /><circle
            cx="4.6" cy="27.4" r="2.7" fill="currentColor" opacity="0.55" /><circle
            cx="27.4" cy="27.4" r="2.7" fill="#D97706" /></svg
        >
        <h1 class="font-medium">Facilitator</h1>
        <span class="rounded-full bg-sunken px-2 py-0.5 text-xs text-ink-2">operator console</span>
      </span>
      <div class="flex items-center gap-2">
        <button
          class="btn"
          data-testid="theme-toggle"
          title={theme.value === "dark" ? "Switch to light theme" : "Switch to dark theme"}
          aria-pressed={theme.value === "dark"}
          onclick={toggleTheme}>
          {theme.value === "dark" ? "light" : "dark"}
        </button>
        <span
          class="badge bg-sunken text-ink-2 ring-edge"
          title="your identity, as the kernel sees it"
          data-testid="identity-chip">
          {[session.displayName, ...operatorRoles].join(" · ")}
        </span>
        <button class="btn" data-testid="logout" onclick={() => void session.logout()}>
          sign out
        </button>
      </div>
    </header>

    <div class="mb-2 flex gap-2">
      <div class="relative min-w-0 grow">
        <input
          class="input pr-16"
          placeholder="search works, projects, artifacts, memories…"
          aria-label="search"
          bind:this={searchBox}
          bind:value={query}
          oninput={onQuery}
          data-testid="search-input" />
        {#if !query}
          <kbd
            class="pointer-events-none absolute top-1/2 right-2 -translate-y-1/2 rounded border border-edge bg-sunken px-1.5 py-0.5 text-[10px] text-ink-3"
            >/ to focus</kbd
          >
        {/if}
      </div>
      <button class="btn" data-testid="search-go" onclick={() => void runSearch()}>search</button>
    </div>
    <div class="mb-4 flex flex-wrap items-center gap-2">
      <span class="sr-only" role="status" aria-live="polite">
        {#if searching}searching…{:else if results}{hitTotal} hits{/if}
      </span>
      {#each SECTIONS as { key, label }}
        <button
          class="chip {activeSections.has(key) ? 'chip-active' : ''}"
          data-testid={`section-${key}`}
          aria-pressed={activeSections.has(key)}
          onclick={() => toggleSection(key)}>{label}</button
        >
      {/each}
      {#if results && !searching}
        <span class="ml-auto text-xs text-ink-3"
          >{hitTotal} {hitTotal === 1 ? "hit" : "hits"}</span
        >
      {/if}
    </div>

    {#if searchError}
      <p class="badge mb-4 bg-danger-veil text-danger ring-danger/25" data-testid="search-error">
        {searchError.code === "search-query-required"
          ? "type at least one character"
          : `${searchError.title} (${searchError.code})`}
      </p>
    {/if}

    {#if results && visibleSections.length === 0 && !searching}
      <p class="mb-4 text-ink-3" data-testid="search-empty">
        Nothing matches “{results.query}”. Try a work id prefix, an objective word, or an artifact
        name.
      </p>
    {/if}

    {#if results}
      {#each visibleSections as { key, label }}
        {@const hits = results[key]}
        <section class="card mb-4" data-testid={`hits-${key}`}>
          <h2 class="mb-2 text-xs font-medium uppercase text-ink-3">{label} · {hits.length}</h2>
          <ul class="grid gap-1">
            {#each hits as hit}
              <li>
                <button
                  class="w-full rounded px-2 py-1 text-left hover:bg-sunken {key === 'works'
                    ? 'cursor-pointer'
                    : 'cursor-default'}"
                  data-testid={`hit-${key}-${hit.id.slice(0, 8)}`}
                  onclick={() => openHit(key, hit.id)}>
                  <span data-testid="hit-display">{hit.display}</span>
                  <span class="badge ml-2 {TONE_CLASS[stateTone(hit.state)]}"
                    ><span class="dot" aria-hidden="true"></span>{stateLabel(hit.state)}</span
                  >
                  <span class="mono ml-2 text-xs text-ink-3">{hit.id.slice(0, 8)}</span>
                </button>
              </li>
            {/each}
          </ul>
        </section>
      {/each}
    {/if}

    {#if selectedWork}
      <div class="mb-4">
        <WorkInspector workId={selectedWork} onclose={() => (selectedWork = null)} />
      </div>
    {/if}

    <nav class="mb-4 flex flex-wrap gap-2" aria-label="views">
      {#each viewButtons as { key, label, hint }}
        <button
          class="btn {panel === key ? 'btn-primary' : ''}"
          title={hint}
          data-testid={`view-${key}`}
          aria-pressed={panel === key}
          onclick={() => (panel = panel === key ? "none" : key)}>{label}</button
        >
      {/each}
    </nav>

    {#if panel === "links"}
      <LinksView />
    {:else if panel === "deliveries"}
      <DeliveriesView />
    {:else if panel === "config"}
      <ConfigView />
    {:else if panel === "chat"}
      <ChatView />
    {/if}

    <div class="mt-4">
      <HealthStrip />
    </div>
  </div>
{/if}
