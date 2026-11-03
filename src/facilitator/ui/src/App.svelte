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

  const SECTIONS: { key: SearchSection; label: string }[] = [
    { key: "works", label: "works" },
    { key: "projects", label: "projects" },
    { key: "assignments", label: "assignments" },
    { key: "artifacts", label: "artifacts" },
    { key: "memories", label: "memories" },
    { key: "messages", label: "messages" },
  ];
  const VIEW_BUTTONS: { key: Panel; label: string; needsChatRole?: boolean }[] = [
    { key: "links", label: "Links" },
    { key: "deliveries", label: "Deliveries" },
    { key: "config", label: "Config" },
    { key: "chat", label: "AiDeck", needsChatRole: true },
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
  const operatorRoles = $derived(
    session.roles.filter((role) => role.startsWith("framework-") || role.startsWith("kernel-")),
  );
  const viewButtons = $derived(VIEW_BUTTONS.filter((b) => !b.needsChatRole || session.canChat));
</script>

{#if bootError}
  <main class="mx-auto max-w-lg p-8">
    <p class="badge bg-danger/15 text-danger" data-testid="boot-error">bootstrap failed: {bootError}</p>
  </main>
{:else if !booted}
  <main class="p-8 text-ink-3" data-testid="booting">loading…</main>
{:else if !authenticated}
  <main class="flex min-h-screen flex-col items-center justify-center gap-4" data-testid="login-gate">
    <h1 class="text-xl font-medium">Facilitator — operator inspector</h1>
    <p class="text-ink-3">sign in with a kernel-operator or framework-admin identity</p>
    <button class="btn btn-primary" data-testid="login" onclick={() => void session.login()}>
      sign in
    </button>
  </main>
{:else}
  <div class="mx-auto max-w-6xl p-4">
    <header class="mb-4 flex flex-wrap items-center justify-between gap-4">
      <h1 class="text-lg font-medium">Facilitator</h1>
      <div class="flex items-center gap-2">
        <span class="badge bg-raised text-ink-2" data-testid="identity-chip">
          {session.displayName}{#each operatorRoles as role} · {role}{/each}
        </span>
        <button class="btn" data-testid="logout" onclick={() => void session.logout()}>logout</button>
      </div>
    </header>

    <div class="mb-4 flex gap-2">
      <input
        class="input min-w-0 grow"
        placeholder="search works, projects, artifacts, memories…"
        bind:value={query}
        oninput={onQuery}
        data-testid="search-input" />
      <button class="btn" data-testid="search-go" onclick={() => void runSearch()}>search</button>
    </div>
    <div class="mb-4 flex flex-wrap gap-2">
      {#each SECTIONS as { key, label }}
        <button
          class="chip {activeSections.has(key) ? 'chip-active' : ''}"
          data-testid={`section-${key}`}
          aria-pressed={activeSections.has(key)}
          onclick={() => toggleSection(key)}>{label}</button>
      {/each}
    </div>

    {#if searchError}
      <p class="badge mb-4 bg-danger/15 text-danger" data-testid="search-error">
        {searchError.code === "search-query-required"
          ? "type at least one character"
          : `${searchError.title} (${searchError.code})`}
      </p>
    {/if}

    {#if results && visibleSections.length === 0 && !searching}
      <p class="mb-4 text-ink-3" data-testid="search-empty">no hits for “{results.query}”</p>
    {/if}

    {#if results}
      {#each visibleSections as { key, label }}
        <section class="card mb-4" data-testid={`hits-${key}`}>
          <h2 class="mb-2 text-xs font-medium uppercase text-ink-3">{label}</h2>
          <ul class="grid gap-1">
            {#each results[key] as hit}
              <li>
                <button
                  class="w-full rounded px-2 py-1 text-left hover:bg-raised {key === 'works' ? 'cursor-pointer' : 'cursor-default'}"
                  data-testid={`hit-${key}-${hit.id.slice(0, 8)}`}
                  onclick={() => openHit(key, hit.id)}>
                  <span data-testid="hit-display">{hit.display}</span>
                  <span class="badge ml-2 bg-raised text-ink-2">{hit.state}</span>
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
      {#each viewButtons as { key, label }}
        <button
          class="btn {panel === key ? 'btn-primary' : ''}"
          data-testid={`view-${key}`}
          aria-pressed={panel === key}
          onclick={() => (panel = panel === key ? "none" : key)}>{label}</button>
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
