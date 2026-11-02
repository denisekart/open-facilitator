<script lang="ts">
  import { ApiError, getJson } from "../lib/api.js";
  import { apiFetch } from "../lib/auth.svelte.js";

  let { artifactId }: { artifactId: string } = $props();

  /** Reads GET /api/artifacts/{id} metadata (never the storage URI). */
  let artifact = $state<{ artifactId: string; mediaType: string; size: number; state: string } | null>(null);
  let error = $state<string | null>(null);
  let loading = $state(false);

  async function show(): Promise<void> {
    loading = true;
    error = null;
    artifact = null;
    try {
      artifact = await getJson(apiFetch, `/api/artifacts/${artifactId}`);
    } catch (err) {
      error = err instanceof ApiError ? err.code : "fetch-failed";
    } finally {
      loading = false;
    }
  }
</script>

<button
  class="mono text-xs text-accent underline"
  data-testid="artifact-link"
  onclick={() => void show()}>{artifactId.slice(0, 8)}</button>
{#if loading}<span class="text-ink-3 text-xs">…</span>{/if}
{#if artifact}
  <span class="badge bg-raised text-ink-2" data-testid="artifact-meta">
    {artifact.state} · {artifact.mediaType} · {artifact.size} B
  </span>
{/if}
{#if error}<span class="badge bg-danger/15 text-danger" data-testid="artifact-error">{error}</span>{/if}
