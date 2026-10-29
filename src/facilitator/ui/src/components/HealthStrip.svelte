<script lang="ts">
  import { countPairs, health } from "../lib/health.svelte.js";
  import type { AdminHourBucket } from "../lib/types.js";
  import ProblemNote from "./ProblemNote.svelte";

  const sparks = $derived(
    health.metrics
      ? [
          { label: "dead letters", bucket: health.metrics.deadLettersPerHour },
          { label: "audit denies", bucket: health.metrics.auditDeniesPerHour },
          { label: "artifacts stored", bucket: health.metrics.artifactsStoredPerHour },
          { label: "artifacts rejected", bucket: health.metrics.artifactsRejectedPerHour },
        ]
      : [],
  );

  function bars(bucket: AdminHourBucket[]): string[] {
    const max = Math.max(1, ...bucket.map((b) => b.count));
    return bucket.map((b) => `height:${Math.max(2, Math.round((b.count / max) * 100))}%`);
  }
</script>

<section class="grid gap-4 md:grid-cols-2" aria-label="health strip" data-testid="health-strip">
  <div class="card">
    <h2 class="mb-2 text-xs font-medium uppercase text-ink-3">Summary</h2>
    {#if health.summary}
      {@const s = health.summary}
      <dl class="grid grid-cols-2 gap-x-6 gap-y-1 text-sm">
        <dt class="text-ink-3">Works</dt>
        <dd class="mono">{countPairs(s.workByState)}</dd>
        <dt class="text-ink-3">Assignments</dt>
        <dd class="mono">{countPairs(s.assignmentsByState)}</dd>
        <dt class="text-ink-3">Executions</dt>
        <dd class="mono">{countPairs(s.executionsByState)}</dd>
        <dt class="text-ink-3">Gateways</dt>
        <dd class="mono">
          <span class="text-ok">{s.gatewaysOnline} online</span> ·
          <span class="text-ink-2">{s.gatewaysOffline} offline</span>
        </dd>
        <dt class="text-ink-3">Outbox</dt>
        <dd class="mono">{countPairs(s.outboxByState)}</dd>
        <dt class="text-ink-3">Inbox</dt>
        <dd class="mono">{countPairs(s.inboxByState)}</dd>
        <dt class="text-ink-3">Dead letters</dt>
        <dd class="mono" data-testid="deadletters">{s.deadLettersTotal}</dd>
        <dt class="text-ink-3">Approvals</dt>
        <dd class="mono">{countPairs(s.approvalsByState)}</dd>
        <dt class="text-ink-3">Artifacts</dt>
        <dd class="mono">{s.artifactsTotal}</dd>
        <dt class="text-ink-3">Manifests</dt>
        <dd class="mono">{countPairs(s.manifestsByResultState)}</dd>
        <dt class="text-ink-3">Memories</dt>
        <dd class="mono">{countPairs(s.memoryByStatus)}</dd>
        <dt class="text-ink-3">Link sessions</dt>
        <dd class="mono">{s.linkSessionsLive} live</dd>
      </dl>
    {:else}
      <ProblemNote error={health.error ?? "loading…"} />
    {/if}
  </div>

  <div class="card">
    <h2 class="mb-2 text-xs font-medium uppercase text-ink-3">Metrics (per UTC hour, bounded window)</h2>
    {#if health.metrics}
      {@const m = health.metrics}
      <div class="grid grid-cols-2 gap-4 text-sm">
        <div class="mono">outbox pending {m.outboxPending} · inbox pending {m.inboxPending}</div>
        <div class="mono">dead letters {m.deadLetters} · link sessions {m.linkSessions}</div>
      </div>
      <div class="mt-3 grid grid-cols-2 gap-4">
        {#each sparks as { label, bucket }}
          {@const heights = bars(bucket)}
          <div>
            <p class="mb-1 text-xs text-ink-3">{label}</p>
            <div class="flex h-10 items-end gap-0.5" role="img" aria-label={label}>
              {#each heights as style}
                <div class="w-2 rounded-sm bg-accent/70" {style}></div>
              {/each}
            </div>
          </div>
        {/each}
      </div>
    {:else}
      <p class="text-ink-3">loading…</p>
    {/if}
  </div>
</section>
