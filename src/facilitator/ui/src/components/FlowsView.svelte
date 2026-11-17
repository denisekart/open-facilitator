<script lang="ts">
  import { ApiError, getJson } from "../lib/api.js";
  import { apiFetch, session } from "../lib/auth.svelte.js";
  import { deckChanged } from "../lib/deck.svelte.js";
  import type { WorkflowDefinitionRow } from "../lib/types.js";
  import EmptyState from "./EmptyState.svelte";
  import ProblemNote from "./ProblemNote.svelte";

  // Flows is the EDITORIAL screen: what definitions exist and whether they
  // are live, shadow or off. Watching runs happen belongs to the Monitor
  // column (#/monitor/runs); each row points at both halves.

  let definitions = $state<WorkflowDefinitionRow[]>([]);
  let error = $state<ApiError | null>(null);
  let loaded = $state(false);
  let postureFilter = $state<"all" | "live" | "shadow" | "off">("all");

  const canMutate = $derived(session.canMutate);

  const totalFlows = $derived(definitions.length);
  const liveCount = $derived(definitions.filter((d) => d.enabled && !d.shadow).length);
  const shadowCount = $derived(definitions.filter((d) => d.enabled && d.shadow).length);
  const offCount = $derived(definitions.filter((d) => !d.enabled).length);
  const runningCount = $derived(definitions.reduce((sum, d) => sum + (d.instancesRunning ?? 0), 0));

  const filteredDefinitions = $derived(
    definitions.filter((d) => {
      if (postureFilter === "live") return d.enabled && !d.shadow;
      if (postureFilter === "shadow") return d.enabled && d.shadow;
      if (postureFilter === "off") return !d.enabled;
      return true;
    }),
  );

  async function load(): Promise<void> {
    try {
      definitions = await getJson<WorkflowDefinitionRow[]>(
        apiFetch,
        "/api/admin/workflows/definitions",
      );
      error = null;
      loaded = true;
    } catch (err) {
      error = err instanceof ApiError ? err : new ApiError(0, "fetch-failed", String(err), null);
    }
  }

  $effect(() => {
    // Deck law: after the AiDeck executes a change, this screen re-loads
    // its slice — the operator sees the effect without touching refresh.
    void deckChanged();
    void load();
  });
</script>

<section class="card" data-testid="flows-view" aria-label="flows">
  <div class="mb-4 flex flex-wrap items-start justify-between gap-4">
    <div>
      <h2 class="mb-1 text-xl font-semibold">Flows</h2>
      <p class="text-sm text-ink-2">
        Active and stored workflow definitions. Execution runs appear under
        <a class="text-accent underline" href="#/monitor/runs">Monitor</a>.
        {#if canMutate}
          <a class="mono text-accent underline ml-1" href="#/flows/new" data-testid="new-flow">author a flow</a>
        {/if}
      </p>
    </div>
    <div class="flex items-center gap-2">
      {#if canMutate}
        <a class="btn btn-primary" href="#/flows/new">new flow</a>
      {/if}
      <button class="btn" data-testid="flows-definitions-refresh" onclick={() => void load()}>refresh</button>
    </div>
  </div>

  <!-- KPI Strip matching the theme showcase -->
  <div class="mb-4 grid grid-cols-2 gap-3 sm:grid-cols-3 md:grid-cols-5">
    <div class="card bg-sunken p-2.5">
      <div class="micro-label">Total Flows</div>
      <div class="mono text-lg font-semibold text-ink mt-0.5">{totalFlows}</div>
    </div>
    <div class="card bg-ok-veil/40 p-2.5 border-border-success">
      <div class="micro-label text-ok">Live Rules</div>
      <div class="mono text-lg font-semibold text-ok mt-0.5">{liveCount}</div>
    </div>
    <div class="card bg-amber-veil/40 p-2.5 border-border-warning">
      <div class="micro-label text-warn">Shadow</div>
      <div class="mono text-lg font-semibold text-warn mt-0.5">{shadowCount}</div>
    </div>
    <div class="card bg-sunken p-2.5">
      <div class="micro-label">Inactive</div>
      <div class="mono text-lg font-semibold text-ink-2 mt-0.5">{offCount}</div>
    </div>
    <div class="card bg-accent-veil/40 p-2.5 border-border-primary">
      <div class="micro-label text-accent-text">Running Instances</div>
      <div class="mono text-lg font-semibold text-accent mt-0.5">{runningCount}</div>
    </div>
  </div>

  <div class="mb-3 flex items-center gap-2">
    <span class="micro-label">Filter:</span>
    <button
      class="chip {postureFilter === 'all' ? 'chip-active' : ''}"
      onclick={() => (postureFilter = "all")}>all</button>
    <button
      class="chip {postureFilter === 'live' ? 'chip-active' : ''}"
      onclick={() => (postureFilter = "live")}>live</button>
    <button
      class="chip {postureFilter === 'shadow' ? 'chip-active' : ''}"
      onclick={() => (postureFilter = "shadow")}>shadow</button>
    <button
      class="chip {postureFilter === 'off' ? 'chip-active' : ''}"
      onclick={() => (postureFilter = "off")}>off</button>
  </div>

  {#if error}
    <ProblemNote {error} />
  {/if}

  <div class="overflow-x-auto">
    <table class="table">
      <caption class="caption-bottom pt-2 text-xs text-ink-2">
        Definitions · edit opens the authoring face, runs opens the filtered monitor
      </caption>
      <thead>
        <tr>
          <th>id</th>
          <th>v</th>
          <th>title</th>
          <th>state</th>
          <th>team</th>
          <th>running</th>
          <th><span class="sr-only">actions</span></th>
        </tr>
      </thead>
      <tbody>
        {#each filteredDefinitions as d (d.id)}
          <tr>
            <td class="font-mono font-medium">{d.id}</td>
            <td class="mono text-ink-2">v{d.version}</td>
            <td>{d.title}</td>
            <td>
              {#if !d.enabled}
                <span class="badge bg-sunken text-ink-2">off</span>
              {:else if d.shadow}
                <span class="badge bg-amber-veil text-amber">shadow</span>
              {:else}
                <span class="badge bg-ok-veil text-ok">live</span>
              {/if}
            </td>
            <td class="mono text-xs">{d.team ?? "—"}</td>
            <td>
              <a class="mono text-accent underline" href={`#/monitor/runs?def=${encodeURIComponent(d.id)}`}>
                {d.instancesRunning}
              </a>
            </td>
            <td class="whitespace-nowrap text-right">
              <a class="mono text-accent underline" href={`#/monitor/runs?def=${encodeURIComponent(d.id)}`}
                >runs</a
              >
              {#if canMutate}
                <a class="mono ml-2 text-accent underline" href={`#/flows/${d.id}`}>edit</a>
                <a class="mono ml-2 text-accent underline" href={`#/flows/${d.id}?clone=1`}>clone</a>
              {/if}
            </td>
          </tr>
        {:else}
          {#if loaded && !error}
          <tr>
            <td colspan="7" class="text-ink-2">
              {#if canMutate}
                <EmptyState
                  title="no definitions yet"
                  hint="author a flow, or store one through the admin API."
                  actionHref="#/flows/new"
                  actionLabel="author a flow"
                  testid="flows-empty" />
              {:else}
                no definitions yet
              {/if}
            </td>
          </tr>
          {/if}
        {/each}
      </tbody>
    </table>
  </div>
</section>
