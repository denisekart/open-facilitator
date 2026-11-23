<script lang="ts">
  import {
    Activity,
    ChevronDown,
    GitBranch,
    LayoutDashboard,
    LogOut,
    Menu,
    Moon,
    ScrollText,
    Search,
    Send,
    Server,
    Settings,
    Sparkles,
    Sun,
    Users,
    X,
  } from "lucide-svelte";
  import AiDeck from "./components/AiDeck.svelte";
  import AuditView from "./components/AuditView.svelte";
  import Breadcrumbs from "./components/Breadcrumbs.svelte";
  import ConfigView from "./components/ConfigView.svelte";
  import DefinitionDetailView from "./components/DefinitionDetailView.svelte";
  import DeliveriesView from "./components/DeliveriesView.svelte";
  import FlowDetail from "./components/FlowDetail.svelte";
  import FlowEditor from "./components/FlowEditor.svelte";
  import FlowsView from "./components/FlowsView.svelte";
  import HealthStrip from "./components/HealthStrip.svelte";
  import HitDetail from "./components/HitDetail.svelte";
  import LinksView from "./components/LinksView.svelte";
  import Logo from "./components/Logo.svelte";
  import MonitorView from "./components/MonitorView.svelte";
  import RuntimeView from "./components/RuntimeView.svelte";
  import WorkInspector from "./components/WorkInspector.svelte";
  import WorksView from "./components/WorksView.svelte";
  import { ApiError, getJson } from "./lib/api.js";
  import { apiFetch, session } from "./lib/auth.svelte.js";
  import { deck } from "./lib/deck.svelte.js";
  import { health } from "./lib/health.svelte.js";
  import { go, href, route, type RouteView } from "./lib/route.svelte.js";
  import { TONE_CLASS, stateLabel, stateTone } from "./lib/status.js";
  import { theme, toggleTheme } from "./lib/theme.svelte.js";
  import type { AdminSearchResults, SearchSection } from "./lib/types.js";

  let booted = $state(false);
  let bootError = $state<string | null>(null);
  let query = $state("");
  let activeSections = $state<Set<SearchSection>>(new Set());
  let results = $state<AdminSearchResults | null>(null);
  let searchError = $state<ApiError | null>(null);
  let searching = $state(false);
  let searchBox = $state<HTMLInputElement | null>(null);
  let userMenuOpen = $state(false);

  const SECTIONS: { key: SearchSection; label: string }[] = [
    { key: "works", label: "activities" },
    { key: "projects", label: "projects" },
    { key: "assignments", label: "assignments" },
    { key: "artifacts", label: "artifacts" },
    { key: "memories", label: "memories" },
    { key: "messages", label: "messages" },
  ];

  // The category rail. Every entry keeps its historical testid; monitor
  // sub-entries point at the new #/monitor/* routes, and Assistant is a drawer
  // toggle, not a screen (the assistant law: available from every screen).
  type RailEntry = {
    key: string;
    label: string;
    hint: string;
    icon: typeof Activity;
    hash?: string;
    drawer?: boolean;
    active?: RouteView[];
    needsChatRole?: boolean;
  };
  const RAIL: RailEntry[] = [
    {
      key: "works",
      label: "Activities",
      hint: "Active and completed activity items, ordered by creation date",
      icon: LayoutDashboard,
      hash: "#/works",
      active: ["works", "work"],
    },
    {
      key: "flows",
      label: "Flows",
      hint: "Workflow definitions: author, version, and enable trigger rules",
      icon: GitBranch,
      hash: "#/flows",
      active: ["flows"],
    },
    {
      key: "monitor",
      label: "Monitor",
      hint: "Runs, deliveries, audit logs, and runtime metrics",
      icon: Activity,
      hash: "#/monitor/runs",
      active: ["monitor-runs", "monitor-run"],
    },
    {
      key: "deliveries",
      label: "Deliveries",
      hint: "Outbox deliveries, destinations, and receipt statuses",
      icon: Send,
      hash: "#/monitor/deliveries",
      active: ["monitor-deliveries"],
    },
    {
      key: "audit",
      label: "Audit",
      hint: "Audit trail of authorization decisions, allows, and denies",
      icon: ScrollText,
      hash: "#/monitor/audit",
      active: ["monitor-audit"],
    },
    {
      key: "runtime",
      label: "Runtime",
      hint: "Kernel replicas, dead-letter queue, metrics, and sweeper tasks",
      icon: Server,
      hash: "#/monitor/runtime",
      active: ["monitor-runtime"],
    },
    {
      key: "team",
      label: "Team",
      hint: "Teams and workspaces connected over outbound links",
      icon: Users,
      hash: "#/team",
      active: ["team"],
    },
    {
      key: "config",
      label: "Config",
      hint: "Runtime configuration resources and change history",
      icon: Settings,
      hash: "#/config",
      active: ["config"],
    },
    {
      key: "chat",
      label: "Assistant",
      hint: "Ask the kernel for activity status, dead-letter inspections, or diagnostics",
      icon: Sparkles,
      drawer: true,
      needsChatRole: true,
    },
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

  function onKeydown(ev: KeyboardEvent): void {
    if (ev.key === "Escape") {
      if (userMenuOpen) {
        userMenuOpen = false;
        return;
      }
      if (results) {
        results = null;
        query = "";
        return;
      }
      if (document.activeElement === searchBox) {
        searchBox?.blur();
      }
      return;
    }
    const typing =
      ev.target instanceof HTMLElement &&
      ["INPUT", "TEXTAREA", "SELECT"].includes(ev.target.tagName);
    const focusCombo =
      (ev.key === "/" && !ev.metaKey && !ev.ctrlKey && !ev.altKey) ||
      ((ev.metaKey || ev.ctrlKey) && ev.key.toLowerCase() === "k");
    if (focusCombo && !(typing && ev.key === "/")) {
      ev.preventDefault();
      searchBox?.focus();
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
    go(section === "works" ? `#/work/${id}` : `#/hit/${section}/${id}`);
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
    session.roles.filter((role) => role.startsWith("framework-") || role.startsWith("kernel-") || role.startsWith("ai-") || role.startsWith("team-")),
  );
  const userInitials = $derived(
    (session.displayName || "OP").slice(0, 2).toUpperCase(),
  );
  const railEntries = $derived(RAIL.filter((b) => !b.needsChatRole || session.canChat));
  const isMonitor = $derived(route.route.view.startsWith("monitor"));

  function railClick(entry: RailEntry): void {
    if (entry.drawer) {
      deck.toggle();
      return;
    }
    if (entry.hash) {
      go(entry.hash);
    }
  }

  function railActive(entry: RailEntry): boolean {
    return entry.active?.includes(route.route.view) ?? false;
  }

  // The drawer's screen context: what the assistant should know the
  // operator is looking at. Route-derived (the route IS the state), ids
  // included when the screen is about one object.
  const screenLabel = $derived.by(() => {
    const r = route.route;
    switch (r.view) {
      case "works":
        return "the works queue";
      case "work":
        return `work ${r.id}`;
      case "flows":
        return r.id ? `flow definition ${r.id} under edit` : "the flow definitions list";
      case "monitor-runs":
        return r.params.get("def") ? `workflow runs of ${r.params.get("def")}` : "workflow runs";
      case "monitor-run":
        return `workflow run ${r.id}`;
      case "monitor-deliveries":
        return "message deliveries";
      case "monitor-audit":
        return "the audit trail";
      case "monitor-runtime":
        return "runtime state";
      case "monitor-definition":
        return `definition ${r.id} history`;
      case "team":
        return "team environments and links";
      case "config":
        return "the live routing policy";
      default:
        return r.view;
    }
  });
  $effect(() => {
    const r = route.route;
    // #/chat is not a screen: it opens the drawer over whatever precedes it.
    if (r.view === "chat") {
      deck.setOpen(true);
      deck.setSession(r.id);
    }
    deck.setContext(
      r.view === "none" || r.view === "chat"
        ? null
        : { route: href(r), label: screenLabel, ids: r.id ? [r.id] : [] },
    );
  });

  // The health bar is shared chrome: when the deck executes a change, the
  // numbers on screen must move with it, without a refresh.
  $effect(() => {
    void deck.executed;
    if (booted && authenticated && deck.executed) {
      health.refresh();
    }
  });

  // Screen actions the deck can fire HERE: reloadScreen re-mounts whatever
  // view is open (the universal "show me the current truth" primitive).
  let screenKey = $state(0);
  $effect(() => deck.onAction("reloadScreen", () => (screenKey += 1)));

  function closeDeck(): void {
    deck.setOpen(false);
    // A #/chat deep link that only opened the drawer should heal the bar.
    if (route.route.view === "chat") {
      go("#/works");
    }
  }

  const view = $derived(route.route.view);

  // §22 compact chrome: past 720px the nav lives in a horizontal strip
  // under the header; at or under it, the same nav becomes a left drawer
  // behind a hamburger. One nav element (one set of testids), restyled.
  let menuOpen = $state(false);
  $effect(() => {
    // Navigating dismisses the drawer (the destination is what matters).
    void route.route.view;
    menuOpen = false;
  });
</script>

<svelte:window onkeydown={onKeydown} />

{#if bootError}
  <main class="mx-auto max-w-lg p-4">
    <p class="badge bg-danger-veil text-danger" data-testid="boot-error">
      The console could not start: {bootError}
    </p>
  </main>
{:else if !booted}
  <main class="p-4 text-ink-2" data-testid="booting">loading…</main>
{:else if !authenticated}
  <main class="flex min-h-screen flex-col items-center justify-center gap-3 p-4 text-center" data-testid="login-gate">
    <Logo class="size-12 text-ink mx-auto" />
    <h1 class="text-xl font-semibold text-center">Open Facilitator — operator console</h1>
    <p class="max-w-sm text-center text-ink-2 mx-auto">
      The live view of one facilitator kernel: activities, decisions, links, evidence.
    </p>
    <p class="text-xs text-ink-2 text-center">sign in with a kernel-operator or framework-admin identity</p>
    <button class="btn btn-primary mx-auto" data-testid="login" onclick={() => void session.login()}>
      sign in
    </button>
  </main>
{:else}
  <header class="chrome sticky top-0 z-20 flex h-11 items-center justify-between gap-2.5 px-3 bg-surface border-b border-edge">
    <div class="flex items-center gap-2 min-w-0 shrink-0">
      <button
        class="nav-item hamburger p-1 text-ink"
        data-testid="menu-toggle"
        aria-label="open the view menu"
        aria-expanded={menuOpen}
        onclick={() => (menuOpen = !menuOpen)}>
        <Menu class="size-5" strokeWidth={1.5} aria-hidden="true" />
      </button>
      <a href="#/works" class="flex items-center gap-2 min-w-0" aria-label="Open Facilitator home">
        <Logo class="size-6 shrink-0 text-ink" />
        <h1 class="truncate text-sm font-semibold tracking-tight text-ink max-sm:hidden">Open Facilitator</h1>
      </a>

      <!-- Desktop Nav Rail inlined into the single header row -->
      <nav class="nav-desktop flex items-center gap-0.5 ml-2" aria-label="desktop views">
        {#each railEntries as entry (entry.key)}
          {@const Icon = entry.icon}
          <button
            class="nav-item"
            title={entry.hint}
            data-testid={`view-${entry.key}`}
            aria-current={railActive(entry) ? "page" : entry.drawer && deck.state.open ? "true" : undefined}
            onclick={() => railClick(entry)}>
            <Icon class="size-4 shrink-0" strokeWidth={1.5} aria-hidden="true" />
            <span>{entry.label}</span>
          </button>
        {/each}
      </nav>
    </div>

    <!-- Centered Search Box -->
    <div class="chrome-search relative mx-auto max-w-sm w-full min-w-0">
      <Search class="pointer-events-none absolute left-2.5 top-1/2 -translate-y-1/2 size-4 text-ink-3" strokeWidth={1.5} />
      <input
        class="input pl-8 pr-14 text-xs h-8"
        type="search"
        placeholder="search activities, projects, artifacts…"
        aria-label="search"
        bind:this={searchBox}
        bind:value={query}
        oninput={onQuery}
        onkeydown={(ev) => ev.key === "Enter" && void runSearch()}
        data-testid="search-input" />
      <button class="sr-only" data-testid="search-go" onclick={() => void runSearch()}>
        search
      </button>
      {#if !query}
        <kbd
          class="mono pointer-events-none absolute top-1/2 right-2 -translate-y-1/2 border border-edge rounded px-1 text-[10px] text-ink-2 max-md:hidden"
          >/ or ⌘K</kbd
        >
      {/if}
    </div>

    <!-- Right Controls: Theme Toggle & User Profile Popover -->
    <div class="flex items-center gap-1.5 shrink-0">
      <button
        class="btn p-1 size-8 rounded border border-edge hover:bg-hover flex items-center justify-center cursor-pointer"
        data-testid="theme-toggle"
        title={theme.value === "dark" ? "Switch to light theme" : "Switch to dark theme"}
        aria-pressed={theme.value === "dark"}
        aria-label="Toggle theme"
        onclick={toggleTheme}>
        {#if theme.value === "dark"}
          <Sun class="size-4 text-ink" strokeWidth={1.5} aria-hidden="true" />
          <span class="sr-only">light</span>
        {:else}
          <Moon class="size-4 text-ink" strokeWidth={1.5} aria-hidden="true" />
          <span class="sr-only">dark</span>
        {/if}
      </button>

      <!-- User Profile Avatar & Popover -->
      <div
        class="relative"
        onfocusout={(ev) => {
          if (!ev.currentTarget.contains(ev.relatedTarget as Node | null)) {
            userMenuOpen = false;
          }
        }}>
        <button
          class="flex items-center gap-1.5 h-8 pl-1 pr-2 rounded-full border border-edge bg-surface-alt hover:bg-surface-raise hover:border-line text-ink transition-colors cursor-pointer"
          data-testid="user-menu-toggle"
          title="Account menu"
          aria-label="Account menu"
          aria-expanded={userMenuOpen}
          onclick={() => (userMenuOpen = !userMenuOpen)}>
          <span class="size-6 rounded-full bg-accent-veil border border-border-primary text-accent flex items-center justify-center font-bold text-xs shrink-0">
            {userInitials}
          </span>
          <span class="text-xs font-semibold text-ink truncate max-w-24 max-md:hidden" data-testid="identity-chip">
            {session.displayName || "Operator"}
          </span>
          <ChevronDown class="size-3.5 text-ink-2 shrink-0 transition-transform {userMenuOpen ? 'rotate-180' : ''}" strokeWidth={1.5} />
        </button>

        {#if userMenuOpen}
          <button
            class="fixed inset-0 z-40 bg-transparent cursor-default"
            aria-label="close account menu"
            onclick={() => (userMenuOpen = false)}></button>
          <div
            class="absolute right-0 top-full mt-1.5 z-50 w-64 rounded-lg border border-edge bg-surface-raise p-3 shadow-overlay"
            style="background-color: var(--surface-raise);"
            role="menu"
            aria-label="User account details">
            <div class="mb-3 border-b border-edge pb-2.5 flex items-center gap-2.5">
              <div class="size-10 rounded-full bg-accent-veil border border-border-primary text-accent flex items-center justify-center font-bold text-sm shrink-0">
                {userInitials}
              </div>
              <div class="min-w-0 grow">
                <p class="text-sm font-semibold text-ink truncate">{session.displayName || "Kernel Operator"}</p>
                <div class="mt-1 flex flex-wrap gap-1">
                  {#if operatorRoles.length > 0}
                    {#each operatorRoles as r}
                      <span class="badge text-[11px] font-mono text-ink-2 bg-surface border border-edge">{r}</span>
                    {/each}
                  {:else}
                    <span class="badge text-[11px] font-mono text-ink-2 bg-surface border border-edge">operator</span>
                  {/if}
                </div>
              </div>
            </div>
            <button
              class="btn btn-danger w-full justify-start text-xs font-semibold h-8"
              data-testid="logout"
              onclick={() => {
                userMenuOpen = false;
                void session.logout();
              }}>
              <LogOut class="size-4 shrink-0 mr-1.5" strokeWidth={1.5} />
              sign out
            </button>
          </div>
        {/if}
      </div>
    </div>
  </header>

  {#if menuOpen}
    <button
      class="scrim fixed inset-0 z-30"
      aria-label="close the view menu"
      onclick={() => (menuOpen = false)}></button>
  {/if}

  <nav
    class="viewnav"
    class:open={menuOpen}
    aria-label="views"
    data-testid="rail">
    <div class="mb-2 flex items-center justify-between border-b border-edge pb-2 md:hidden">
      <span class="text-xs font-semibold text-ink">Navigation</span>
      <button class="btn p-1 size-7" aria-label="close navigation" onclick={() => (menuOpen = false)}>
        <X class="size-4" strokeWidth={1.5} />
      </button>
    </div>
    {#each railEntries as entry (entry.key)}
      {@const Icon = entry.icon}
      <button
        class="nav-item"
        title={entry.hint}
        data-testid={`menu-view-${entry.key}`}
        aria-current={railActive(entry) ? "page" : entry.drawer && deck.state.open ? "true" : undefined}
        onclick={() => railClick(entry)}>
        <Icon class="size-4 shrink-0" strokeWidth={1.5} aria-hidden="true" />
        {entry.label}
      </button>
    {/each}
  </nav>

  <div
    class="mx-auto w-full max-w-7xl p-3 transition-[margin-right] duration-180 ease-out"
    style={deck.state.open && !deck.state.expanded ? "margin-right: min(420px, 32vw);" : ""}>
    {#if query}
      <div class="mb-2 flex flex-wrap items-center gap-2">
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
          <span class="mono ml-auto text-xs text-ink-2"
            >{hitTotal} {hitTotal === 1 ? "hit" : "hits"}</span
          >
        {/if}
      </div>

      {#if searchError}
        <p class="badge mb-3 bg-danger-veil text-danger" data-testid="search-error">
          {searchError.code === "search-query-required"
            ? "type at least one character"
            : `${searchError.title} (${searchError.code})`}
        </p>
      {/if}

      {#if results && visibleSections.length === 0 && !searching}
        <p class="mb-3 text-ink-2" data-testid="search-empty">
          Nothing matches “{results.query}”. Try a work id prefix, an objective word, or an
          artifact name.
        </p>
      {/if}

      {#if results}
        {#each visibleSections as { key, label }}
          {@const hits = results[key]}
          <section class="card mb-3" data-testid={`hits-${key}`}>
            <h2 class="micro-label mb-2">{label} · {hits.length}</h2>
            <ul class="grid gap-1">
              {#each hits as hit}
                <li>
                  <button
                    class="w-full cursor-pointer px-2 py-1 text-left hover:bg-surface"
                    style="border-radius: var(--radius-control)"
                    data-testid={`hit-${key}-${hit.id.slice(0, 8)}`}
                    onclick={() => openHit(key, hit.id)}>
                    <span data-testid="hit-display">{hit.display}</span>
                    <span class="badge ml-2 {TONE_CLASS[stateTone(hit.state)]}"
                      ><span class="dot" aria-hidden="true"></span>{stateLabel(hit.state)}</span
                    >
                    <span class="mono ml-2 text-xs text-ink-2">{hit.id.slice(0, 8)}</span>
                  </button>
                </li>
              {/each}
            </ul>
          </section>
        {/each}
      {/if}
    {/if}

    {#if isMonitor}
      <section class="mb-3" aria-label="kernel health bar" data-testid="monitor-bar">
        <HealthStrip />
      </section>
    {/if}

    {#if route.route.view === "work" && route.route.id}
      <div class="mb-3">
        <WorkInspector workId={route.route.id} onclose={() => go("#/")} />
      </div>
    {/if}

    {#if route.route.view === "hit" && route.route.id && route.route.kind}
      <div class="mb-3">
        <HitDetail kind={route.route.kind} id={route.route.id} onclose={() => go("#/")} />
      </div>
    {/if}

    <Breadcrumbs />

    {#key screenKey}
      {#if view === "works" || view === "chat"}
        <WorksView />
      {:else if view === "team"}
        <LinksView />
      {:else if view === "monitor-deliveries"}
        <DeliveriesView />
      {:else if view === "monitor-audit"}
        <AuditView />
      {:else if view === "config"}
        <ConfigView />
      {:else if view === "monitor-runtime"}
        <RuntimeView />
      {:else if view === "flows" && !route.route.id}
        <FlowsView />
      {:else if view === "flows" && route.route.id}
        <FlowEditor
          defId={route.route.id}
          clone={route.route.params.get("clone") === "1"}
          onclose={() => go("#/flows")}
        />
      {:else if view === "monitor-runs"}
        {#key route.route.params.get("def") ?? ""}
          <MonitorView defFilter={route.route.params.get("def")} />
        {/key}
      {:else if view === "monitor-run" && route.route.id}
        <FlowDetail instanceId={route.route.id} onclose={() => go("#/monitor/runs")} />
      {:else if view === "monitor-definition" && route.route.id}
        {#key route.route.id}
          <DefinitionDetailView defId={route.route.id} />
        {/key}
      {/if}
    {/key}
  </div>

  {#if !deck.state.open}
    <button
      class="fixed bottom-4 right-4 z-30 flex items-center gap-1.5 rounded-full border border-border-primary bg-surface px-3 py-2 text-accent shadow-overlay hover:border-accent hover:bg-surface-raise transition-all cursor-pointer"
      data-testid="floating-ai-deck-toggle"
      title="Open Assistant"
      aria-label="Open Assistant"
      onclick={() => deck.setOpen(true)}>
      <Sparkles class="size-4 shrink-0 text-accent" strokeWidth={1.5} aria-hidden="true" />
      <span class="text-xs font-semibold text-ink max-sm:hidden">Assistant</span>
    </button>
  {/if}

  <AiDeck onclose={closeDeck} />
{/if}
