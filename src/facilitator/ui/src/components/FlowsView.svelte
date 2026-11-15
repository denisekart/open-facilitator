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

  const canMutate = $derived(session.canMutate);

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
  <div class="mb-4 flex items-start justify-between gap-4">
    <div>
      <h2 class="mb-1 text-xl font-semibold">Flows</h2>
      <p class="text-sm text-ink-3">
        Every definition, latest version. Runs and their progress live under
        <a class="text-accent underline" href="#/monitor/runs">Monitor</a>.
        {#if canMutate}
          <a class="mono text-accent underline" href="#/flows/new" data-testid="new-flow">author a flow</a>
        {/if}
      </p>
    </div>
    <button class="btn" data-testid="flows-definitions-refresh" onclick={() => void load()}>refresh</button>
  </div>

  {#if error}
    <ProblemNote {error} />
  {/if}

  <div class="overflow-x-auto">
    <table class="table">
      <caption class="caption-bottom pt-2 text-xs text-ink-3">
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
        {#each definitions as d (d.id)}
          <tr>
            <td class="font-mono">{d.id}</td>
            <td>{d.version}</td>
            <td>{d.title}</td>
            <td>
              {#if !d.enabled}
                <span class="badge bg-sunken text-ink-2 ring-edge">off</span>
              {:else if d.shadow}
                <span class="badge bg-amber-veil text-amber ring-amber/25">shadow</span>
              {:else}
                <span class="badge bg-ok-veil text-ok ring-ok/25">live</span>
              {/if}
            </td>
            <td>{d.team ?? "—"}</td>
            <td>
              <a class="mono text-accent underline" href={`#/monitor/runs?def=${encodeURIComponent(d.id)}`}>
                {d.instancesRunning}
              </a>
            </td>
            <td class="whitespace-nowrap">
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
            <td colspan="7" class="text-ink-3">
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
