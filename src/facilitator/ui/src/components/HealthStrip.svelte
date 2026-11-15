<script lang="ts">
  import { health } from "../lib/health.svelte.js";
  import { href, parse } from "../lib/route.svelte.js";
  import { stateLabel, stateTone, TONE_CLASS } from "../lib/status.js";
  import type { AdminHourBucket } from "../lib/types.js";
  import ProblemNote from "./ProblemNote.svelte";

  const sparks = $derived<{ label: string; hint: string; bad: boolean; bucket: AdminHourBucket[]; link: string | null }[]>(
    health.metrics
      ? [
          {
            label: "dead letters",
            hint: "sent and gave up — see Deliveries to replay",
            bad: true,
            bucket: health.metrics.deadLettersPerHour,
            link: null,
          },
          {
            label: "audit denies",
            hint: "requests refused by policy",
            bad: true,
            bucket: health.metrics.auditDeniesPerHour,
            link: href(parse("#/monitor/audit?decision=deny")),
          },
          {
            label: "artifacts stored",
            hint: "evidence accepted into the store",
            bad: false,
            bucket: health.metrics.artifactsStoredPerHour,
            link: null,
          },
          {
            label: "artifacts rejected",
            hint: "bytes refused at the gate",
            bad: true,
            bucket: health.metrics.artifactsRejectedPerHour,
            link: null,
          },
        ]
      : [],
  );

  function bars(bucket: AdminHourBucket[]): string[] {
    const max = Math.max(1, ...bucket.map((b) => b.count));
    return bucket.map((b) => `height:${Math.max(2, Math.round((b.count / max) * 100))}%`);
  }
</script>

<section class="grid gap-4 md:grid-cols-2" aria-label="kernel health" data-testid="health-strip">
  <div class="card">
    <h2 class="mb-2 text-xs font-medium uppercase text-ink-3">Right now</h2>
    {#if health.summary}
      {@const s = health.summary}
      <dl class="grid grid-cols-[auto_1fr] gap-x-4 gap-y-1.5 text-sm">
        <dt class="text-ink-3">Works</dt>
        <dd class="flex flex-wrap gap-1">
          {#each Object.entries(s.workByState) as [state, n]}
            <span class="badge {TONE_CLASS[stateTone(state)]}"
              ><span class="dot" aria-hidden="true"></span>{stateLabel(state)}
              <span class="font-semibold">{n}</span></span
            >
          {:else}
            <span class="text-ink-3">none yet</span>
          {/each}
        </dd>
        <dt class="text-ink-3">Assignments</dt>
        <dd class="flex flex-wrap gap-1">
          {#each Object.entries(s.assignmentsByState) as [state, n]}
            <span class="badge {TONE_CLASS[stateTone(state)]}"
              ><span class="dot" aria-hidden="true"></span>{stateLabel(state)}
              <span class="font-semibold">{n}</span></span
            >
          {:else}
            <span class="text-ink-3">none yet</span>
          {/each}
        </dd>
        <dt class="text-ink-3">Gateways</dt>
        <dd>
          <span class="badge bg-ok-veil text-ok ring-ok/25"
            ><span class="dot" aria-hidden="true"></span>{s.gatewaysOnline} online</span
          >
          {#if s.gatewaysOffline > 0}
            <span class="badge ml-1 bg-sunken text-ink-2 ring-edge"
              >{s.gatewaysOffline} offline</span
            >
          {/if}
        </dd>
        <dt class="text-ink-3">Awaiting a human</dt>
        <dd>
          {#if (s.approvalsByState["pending"] ?? 0) > 0}
            <a
              class="badge bg-amber-veil text-amber ring-amber/25"
              title="the works a decision is holding up"
              href={href(parse("#/works?state=blocked,review"))}
              ><span class="dot" aria-hidden="true"></span>{s.approvalsByState["pending"]}
              pending</a
            >
          {:else}
            <span class="text-ink-3">nothing is blocked on a decision</span>
          {/if}
        </dd>
        <dt class="text-ink-3">Dead letters</dt>
        <dd>
          {#if s.deadLettersTotal > 0}
            <span
              class="badge bg-danger-veil text-danger ring-danger/25"
              data-testid="deadletters"
              title="undeliverable kernel messages — replay from Deliveries"
              ><span class="dot" aria-hidden="true"></span>{s.deadLettersTotal}</span
            >
          {:else}
            <span class="text-ink-3" data-testid="deadletters">0</span>
          {/if}
        </dd>
        <dt class="text-ink-3">Artifacts</dt>
        <dd class="text-ink-2">
          {s.artifactsTotal} stored · {Object.values(s.manifestsByResultState).reduce(
            (a, b) => a + b,
            0,
          )} result manifests
        </dd>
        <dt class="text-ink-3">Link sessions</dt>
        <dd class="text-ink-2">{s.linkSessionsLive} live</dd>
      </dl>
    {:else}
      <ProblemNote error={health.error ?? "loading…"} />
    {/if}
  </div>

  <div class="card">
    <h2 class="mb-2 text-xs font-medium uppercase text-ink-3">Last hours (UTC)</h2>
    {#if health.metrics}
      {@const m = health.metrics}
      <div class="grid grid-cols-2 gap-x-4 gap-y-1 text-sm text-ink-2">
        <div>outbox pending <span class="mono">{m.outboxPending}</span></div>
        <div>inbox pending <span class="mono">{m.inboxPending}</span></div>
        <div>dead letters <span class="mono">{m.deadLetters}</span></div>
        <div>link sessions <span class="mono">{m.linkSessions}</span></div>
      </div>
      <div class="mt-3 grid grid-cols-2 gap-4">
        {#each sparks as { label, hint, bad, bucket, link }}
          {@const heights = bars(bucket)}
          {@const total = bucket.reduce((a, b) => a + b.count, 0)}
          <div>
            <p class="mb-1 flex justify-between text-xs">
              {#if link}
                <a class="text-ink-3 underline decoration-dotted" title={hint} href={link}
                  >{label}</a
                >
              {:else}
                <span class="text-ink-3" title={hint}>{label}</span>
              {/if}
              <span class="mono {total > 0 && bad ? 'text-danger' : 'text-ink-3'}">{total}</span>
            </p>
            <div
              class="flex h-10 items-end gap-0.5"
              role="img"
              aria-label={`${label}: ${total} in the window`}>
              {#each heights as style}
                <div class="w-2 rounded-sm {bad ? 'bg-danger/60' : 'bg-accent/60'}" {style}></div>
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
