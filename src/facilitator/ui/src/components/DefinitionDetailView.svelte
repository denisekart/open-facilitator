<script lang="ts">
  import { getJson } from "../lib/api.js";
  import { apiFetch } from "../lib/auth.svelte.js";
  import MonitorView from "./MonitorView.svelte";
  import ProblemNote from "./ProblemNote.svelte";

  // One definition's observational face: posture, the version it answers
  // with now, and every run of it. Reached from the editor header and the
  // flows table. The version HISTORY endpoint is the export surface; the
  // console links to it rather than re-listing every stored payload.

  type Summary = {
    id: string;
    version: number;
    title: string;
    description: string | null;
    enabled: boolean;
    shadow: boolean;
    team: string | null;
    digest: string;
    instancesRunning: number;
  };

  let { defId }: { defId: string } = $props();

  let summary = $state<Summary | null>(null);
  let error = $state<unknown>(null);

  async function load(): Promise<void> {
    try {
      const view = await getJson<{ summary: Summary }>(
        apiFetch,
        `/api/admin/workflows/definitions/${encodeURIComponent(defId)}`,
      );
      summary = view.summary;
      error = null;
    } catch (err) {
      error = err;
    }
  }

  $effect(() => {
    void load();
  });
</script>

<section class="card mb-4" data-testid="definition-detail" aria-label="definition detail">
  <div class="mb-2 flex items-start justify-between gap-4">
    <div>
      <h2 class="mb-1 text-xl font-semibold">
        <span class="mono">{defId}</span>
        {#if summary}
          · v{summary.version}
          {#if !summary.enabled}
            <span class="badge bg-sunken text-ink-2 ring-edge ml-1">off</span>
          {:else if summary.shadow}
            <span class="badge bg-amber-veil text-amber ring-amber/25 ml-1">shadow</span>
          {:else}
            <span class="badge bg-ok-veil text-ok ring-ok/25 ml-1">live</span>
          {/if}
        {/if}
      </h2>
      {#if summary?.description}
        <p class="text-sm text-ink-3">{summary.description}</p>
      {/if}
    </div>
    <div class="flex gap-2">
      <a class="btn" data-testid="definition-to-editor" href={`#/flows/${defId}`}>edit</a>
      <a class="btn" href="#/flows">all definitions</a>
    </div>
  </div>
  {#if error}
    <ProblemNote error={String(error)} />
  {:else if summary === null}
    <p class="text-sm text-ink-3">loading…</p>
  {/if}
</section>

<MonitorView defFilter={defId} />
