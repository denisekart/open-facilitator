<script lang="ts">
  import { Play, Square } from "lucide-svelte";
  import { ApiError, getJson } from "../lib/api.js";
  import { apiFetch } from "../lib/auth.svelte.js";
  import { deckChanged } from "../lib/deck.svelte.js";
  import { go, href, parse } from "../lib/route.svelte.js";
  import { ago, flowTone, stamp } from "../lib/status.js";
  import type { WorkflowInstanceRow } from "../lib/types.js";
  import EmptyState from "./EmptyState.svelte";
  import ProblemNote from "./ProblemNote.svelte";

  // The observational half of the workflow plane: every instance, newest
  // first, optionally filtered to one definition (#/monitor/runs?def=…).
  // One global "live" toggle owns the poll — when it is on AND anything
  // runs or waits, the list re-polls every 3 s so an operator (or the e2e)
  // watches a walk without hammering refresh.

  const POLL_MS = 3000;

  let { defFilter = null }: { defFilter?: string | null } = $props();

  let instances = $state<WorkflowInstanceRow[]>([]);
  let error = $state<ApiError | null>(null);
  let loaded = $state(false);
  let live = $state(true);

  async function load(): Promise<void> {
    try {
      const query = defFilter ? `?def=${encodeURIComponent(defFilter)}` : "";
      const fetched = await getJson<{ items: WorkflowInstanceRow[] }>(
        apiFetch,
        `/api/admin/workflows/instances${query}`,
      );
      instances = fetched.items;
      error = null;
      loaded = true;
    } catch (err) {
      error = err instanceof ApiError ? err : new ApiError(0, "fetch-failed", String(err), null);
    }
  }

  const somethingMoving = $derived(
    instances.some((i) => i.status === "running" || i.status === "waiting"),
  );

  // The live law: poll ONLY while live is on and something actually runs or
  // waits — a quiet kernel costs zero requests, and a turned-off toggle is
  // provably silent (the vitest fake-timer proof lives in MonitorView.test).
  // The chain is manual, not effect-tracked: each tick re-checks the gate,
  // the toggle starts/stops it, and a defFilter change restarts it.
  let poll: ReturnType<typeof setTimeout> | undefined;

  function schedule(): void {
    clearTimeout(poll);
    if (live && somethingMoving) {
      poll = setTimeout(() => void tick(), POLL_MS);
    }
  }

  async function tick(): Promise<void> {
    await load();
    schedule();
  }

  function setLive(next: boolean): void {
    live = next;
    schedule();
  }

  $effect(() => {
    // Deck law: an executed deck change reloads the run table at once.
    void deckChanged();
    void tick();
    return () => clearTimeout(poll);
  });

  const open = (instanceId: string): string => href(parse(`#/monitor/runs/${instanceId}`));
</script>

<section class="card" data-testid="monitor-view" aria-label="monitor">
  <div class="mb-4 flex flex-wrap items-start justify-between gap-4">
    <div>
      <h2 class="mb-1 text-xl font-semibold">Monitor</h2>
      <p class="text-sm text-ink-2">
        Every workflow instance, newest first.
        {#if defFilter}
          filtered to <span class="mono">{defFilter}</span>
          <a class="text-accent underline" href="#/monitor/runs">clear</a>
        {/if}
      </p>
    </div>
    <div class="flex items-center gap-2">
      <button
        class="btn {live ? 'btn-primary' : ''}"
        data-testid="live-toggle"
        aria-pressed={live}
        title={live
          ? "live: re-polls every 3 s while anything runs or waits"
          : "paused: no polling until you refresh or turn live back on"}
        onclick={() => setLive(!live)}>
        {#if live}
          <Play class="size-3.5" strokeWidth={1.5} aria-hidden="true" />live
        {:else}
          <Square class="size-3.5" strokeWidth={1.5} aria-hidden="true" />paused
        {/if}
      </button>
      <button class="btn" data-testid="monitor-refresh" onclick={() => void load()}>
        refresh
      </button>
    </div>
  </div>

  {#if error}
    <ProblemNote {error} />
  {:else if !loaded}
    <p class="text-sm text-ink-2">loading…</p>
  {/if}

  <div class="overflow-x-auto" data-testid="instance-list">
    <table class="table">
      <caption class="caption-bottom pt-2 text-xs text-ink-2">
        Instances · open one for its step-by-step checklist
      </caption>
      <thead>
        <tr>
          <th>started</th>
          <th>definition</th>
          <th>status</th>
          <th>current step</th>
          <th>depth</th>
          <th>work</th>
        </tr>
      </thead>
      <tbody>
        {#each instances as row (row.instanceId)}
          <tr
            class="cursor-pointer"
            role="button"
            tabindex="0"
            onclick={() => go(open(row.instanceId))}
            onkeydown={(ev) => ev.key === "Enter" && go(open(row.instanceId))}
          >
            <td title={stamp(row.startedAt)}>{ago(row.startedAt).text}</td>
            <td class="font-mono">{row.definitionId}@{row.definitionVersion}</td>
            <td>
              <span class="badge {flowTone(row.status)}">
                {row.status}
                {#if row.status === "running" || row.status === "waiting"}
                  <span class="ml-1 opacity-70">· {row.currentStep ?? "start"}</span>
                {:else if row.endReason}
                  <span class="ml-1 opacity-70">· {row.endReason}</span>
                {/if}
              </span>
            </td>
            <td class="font-mono">{row.currentStep ?? "—"}</td>
            <td>{row.depth}</td>
            <td>
              {#if row.primaryWorkId}
                <a
                  class="mono text-accent underline"
                  href={`#/work/${row.primaryWorkId}`}
                  onclick={(ev) => ev.stopPropagation()}
                >{row.primaryWorkId.slice(0, 8)}</a
                >
              {:else}
                —
              {/if}
            </td>
          </tr>
        {:else}
          <tr>
            <td colspan="6">
              <EmptyState
                title="no instances yet"
                hint="instances appear when a trigger event starts a flow."
                testid="monitor-empty" />
            </td>
          </tr>
        {/each}
      </tbody>
    </table>
  </div>
</section>
