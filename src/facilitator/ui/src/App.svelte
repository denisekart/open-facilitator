<script lang="ts">
  import {
    Activity,
    GitBranch,
    LayoutDashboard,
    Menu,
    ScrollText,
    Send,
    Server,
    Settings,
    Sparkles,
    Users,
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

  const SECTIONS: { key: SearchSection; label: string }[] = [
    { key: "works", label: "works" },
    { key: "projects", label: "projects" },
    { key: "assignments", label: "assignments" },
    { key: "artifacts", label: "artifacts" },
    { key: "memories", label: "memories" },
    { key: "messages", label: "messages" },
  ];

  // The category rail. Every entry keeps its historical testid; monitor
  // sub-entries point at the new #/monitor/* routes, and AiDeck is a drawer
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
      label: "Works",
      hint: "Active and completed work items, ordered by creation date",
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
      label: "AiDeck",
      hint: "Ask the kernel for work status, dead-letter inspections, or diagnostics",
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

  // '/' or ⌘K/Ctrl-K focuses search from anywhere (except while typing);
  // Esc leaves it.
  function onKeydown(ev: KeyboardEvent): void {
    const typing =
      ev.target instanceof HTMLElement &&
      ["INPUT", "TEXTAREA", "SELECT"].includes(ev.target.tagName);
    const focusCombo =
      (ev.key === "/" && !ev.metaKey && !ev.ctrlKey && !ev.altKey) ||
      ((ev.metaKey || ev.ctrlKey) && ev.key.toLowerCase() === "k");
    if (focusCombo && !(typing && ev.key === "/")) {
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
    session.roles.filter((role) => role.startsWith("framework-") || role.startsWith("kernel-")),
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
  <main class="flex min-h-screen flex-col items-center justify-center gap-3" data-testid="login-gate">
    <Logo class="size-12 text-ink" />
    <h1 class="text-xl font-semibold">Open Facilitator — operator console</h1>
    <p class="max-w-sm text-center text-ink-2">
      The live view of one facilitator kernel: works, decisions, links, evidence.
    </p>
    <p class="text-xs text-ink-2">sign in with a kernel-operator or framework-admin identity</p>
    <button class="btn btn-primary" data-testid="login" onclick={() => void session.login()}>
      sign in
    </button>
  </main>
{:else}
  <header class="chrome sticky top-0 z-5 flex items-center gap-3">
    <button
      class="nav-item hamburger"
      data-testid="menu-toggle"
      aria-label="open the view menu"
      aria-expanded={menuOpen}
      onclick={() => (menuOpen = !menuOpen)}>
      <Menu class="size-5" strokeWidth={1.5} aria-hidden="true" />
    </button>
    <span class="flex min-w-0 items-center gap-2">
      <Logo class="size-6 shrink-0 text-ink" />
      <h1 class="truncate text-sm font-semibold tracking-[0.01em]">Open Facilitator</h1>
      <span class="chip max-md:hidden">operator console</span>
    </span>
    <div class="chrome-search relative">
      <input
        class="input pr-16"
        type="search"
        placeholder="search works, projects, artifacts, memories…"
        aria-label="search"
        bind:this={searchBox}
        bind:value={query}
        oninput={onQuery}
        data-testid="search-input" />
      {#if !query}
        <kbd
          class="mono pointer-events-none absolute top-1/2 right-2 -translate-y-1/2 border border-edge px-1 text-xs text-ink-2 max-md:hidden"
          >/ or ⌘K</kbd
        >
      {/if}
    </div>
    <button class="btn hide-compact" data-testid="search-go" onclick={() => void runSearch()}>
      search
    </button>
    <span
      class="badge identity-chip"
      title="your identity, as the kernel sees it"
      data-testid="identity-chip">
      {[session.displayName, ...operatorRoles].join(" · ")}
    </span>
    <button
      class="nav-item"
      data-testid="theme-toggle"
      title={theme.value === "dark" ? "Switch to light theme" : "Switch to dark theme"}
      aria-pressed={theme.value === "dark"}
      onclick={toggleTheme}>
      {theme.value === "dark" ? "light" : "dark"}
    </button>
    <button class="btn" data-testid="logout" onclick={() => void session.logout()}>
      sign out
    </button>
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
    {#each railEntries as entry (entry.key)}
      {@const Icon = entry.icon}
      <button
        class="nav-item"
        title={entry.hint}
        data-testid={`view-${entry.key}`}
        aria-current={railActive(entry) ? "page" : entry.drawer && deck.state.open ? "true" : undefined}
        onclick={() => railClick(entry)}>
        <Icon class="size-4 shrink-0" strokeWidth={1.5} aria-hidden="true" />
        {entry.label}
      </button>
    {/each}
  </nav>

  <div class="mx-auto w-full max-w-7xl p-3">
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

  <AiDeck onclose={closeDeck} />
{/if}
