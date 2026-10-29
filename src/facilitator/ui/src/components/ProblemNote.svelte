<script lang="ts">
  import { ApiError } from "../lib/api.js";

  let { error }: { error: ApiError | string | null } = $props();

  const mapped = $derived(typeof error === "string" ? null : error);
</script>

{#if mapped?.status === 403}
  <p class="badge bg-warn/15 text-warn" data-testid="forbidden">insufficient role ({mapped.code})</p>
{:else if mapped}
  <p class="badge bg-danger/15 text-danger" data-testid="problem">
    {mapped.title} <span class="mono">({mapped.code})</span>
    {#if mapped.detail}<span class="ml-1 text-ink-2">{mapped.detail}</span>{/if}
  </p>
{:else if error}
  <p class="badge bg-danger/15 text-danger" data-testid="problem">{error}</p>
{/if}
