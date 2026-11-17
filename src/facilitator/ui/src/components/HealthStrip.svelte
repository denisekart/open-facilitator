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

  function lineGraph(bucket: AdminHourBucket[], width = 140, height = 36): { line: string; area: string; points: { x: number; y: number }[] } {
    if (!bucket || bucket.length === 0) {
      return { line: `M 0 ${height} L ${width} ${height}`, area: `M 0 ${height} L ${width} ${height} Z`, points: [] };
    }
    const max = Math.max(1, ...bucket.map((b) => b.count));
    const step = bucket.length > 1 ? width / (bucket.length - 1) : width;
    const points = bucket.map((b, i) => {
      const x = Math.round(i * step * 10) / 10;
      const y = Math.round((height - 4 - ((b.count / max) * (height - 8))) * 10) / 10;
      return { x, y };
    });
    const line = points.map((p, i) => `${i === 0 ? "M" : "L"} ${p.x} ${p.y}`).join(" ");
    const area = `${line} L ${width} ${height} L 0 ${height} Z`;
    return { line, area, points };
  }
</script>

<section class="grid gap-4 md:grid-cols-2" aria-label="kernel health" data-testid="health-strip">
  <div class="card">
    <h2 class="mb-2 text-xs font-medium uppercase text-ink-2">Right now</h2>
    {#if health.summary}
      {@const s = health.summary}
      <dl class="grid grid-cols-[auto_1fr] gap-x-4 gap-y-1.5 text-sm">
        <dt class="text-ink-2">Activities</dt>
        <dd class="flex flex-wrap gap-1">
          {#each Object.entries(s.workByState) as [state, n]}
            <span class="badge {TONE_CLASS[stateTone(state)]}"
              ><span class="dot" aria-hidden="true"></span>{stateLabel(state)}
              <span class="font-semibold">{n}</span></span
            >
          {:else}
            <span class="text-ink-2">none yet</span>
          {/each}
        </dd>
        <dt class="text-ink-2">Assignments</dt>
        <dd class="flex flex-wrap gap-1">
          {#each Object.entries(s.assignmentsByState) as [state, n]}
            <span class="badge {TONE_CLASS[stateTone(state)]}"
              ><span class="dot" aria-hidden="true"></span>{stateLabel(state)}
              <span class="font-semibold">{n}</span></span
            >
          {:else}
            <span class="text-ink-2">none yet</span>
          {/each}
        </dd>
        <dt class="text-ink-2">Gateways</dt>
        <dd>
          <span class="badge bg-ok-veil text-ok"
            ><span class="dot" aria-hidden="true"></span>{s.gatewaysOnline} online</span
          >
          {#if s.gatewaysOffline > 0}
            <span class="badge ml-1 bg-sunken text-ink-2"
              >{s.gatewaysOffline} offline</span
            >
          {/if}
        </dd>
        <dt class="text-ink-2">Awaiting a human</dt>
        <dd>
          {#if (s.approvalsByState["pending"] ?? 0) > 0}
            <a
              class="badge bg-amber-veil text-amber"
              title="the works a decision is holding up"
              href={href(parse("#/works?state=blocked,review"))}
              ><span class="dot" aria-hidden="true"></span>{s.approvalsByState["pending"]}
              pending</a
            >
          {:else}
            <span class="text-ink-2">nothing is blocked on a decision</span>
          {/if}
        </dd>
        <dt class="text-ink-2">Dead letters</dt>
        <dd>
          {#if s.deadLettersTotal > 0}
            <span
              class="badge bg-danger-veil text-danger"
              data-testid="deadletters"
              title="undeliverable kernel messages — replay from Deliveries"
              ><span class="dot" aria-hidden="true"></span>{s.deadLettersTotal}</span
            >
          {:else}
            <span class="text-ink-2" data-testid="deadletters">0</span>
          {/if}
        </dd>
        <dt class="text-ink-2">Artifacts</dt>
        <dd class="text-ink-2">
          {s.artifactsTotal} stored · {Object.values(s.manifestsByResultState).reduce(
            (a, b) => a + b,
            0,
          )} result manifests
        </dd>
        <dt class="text-ink-2">Link sessions</dt>
        <dd class="text-ink-2">{s.linkSessionsLive} live</dd>
      </dl>
    {:else}
      <ProblemNote error={health.error ?? "loading…"} />
    {/if}
  </div>

  <div class="card">
    <h2 class="mb-2 text-xs font-medium uppercase text-ink-2">Last hours (UTC)</h2>
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
          {@const graph = lineGraph(bucket)}
          {@const total = bucket.reduce((a, b) => a + b.count, 0)}
          <div>
            <p class="mb-1 flex justify-between text-xs">
              {#if link}
                <a class="text-ink-2 underline decoration-dotted" title={hint} href={link}
                  >{label}</a
                >
              {:else}
                <span class="text-ink-2" title={hint}>{label}</span>
              {/if}
              <span class="mono {total > 0 && bad ? 'text-danger' : 'text-ink-2'}">{total}</span>
            </p>
            <div
              class="h-9 w-full rounded bg-surface-raise border border-edge/60 p-1 flex items-center"
              role="img"
              aria-label={`${label}: ${total} in the window`}>
              <svg viewBox="0 0 140 36" class="w-full h-full overflow-visible" preserveAspectRatio="none">
                <defs>
                  <linearGradient id={`grad-${label.replace(/\s+/g, "-")}`} x1="0" y1="0" x2="0" y2="1">
                    <stop offset="0%" stop-color={bad ? "var(--danger)" : "var(--accent)"} stop-opacity="0.25" />
                    <stop offset="100%" stop-color={bad ? "var(--danger)" : "var(--accent)"} stop-opacity="0.0" />
                  </linearGradient>
                </defs>
                <path d={graph.area} fill={`url(#grad-${label.replace(/\s+/g, "-")})`} />
                <path d={graph.line} fill="none" stroke={bad ? "var(--danger)" : "var(--accent)"} stroke-width="1.75" stroke-linecap="round" stroke-linejoin="round" />
                {#each graph.points as pt}
                  <circle cx={pt.x} cy={pt.y} r="1.5" fill={bad ? "var(--danger)" : "var(--accent)"} />
                {/each}
              </svg>
            </div>
          </div>
        {/each}
      </div>
    {:else}
      <p class="text-ink-2">loading…</p>
    {/if}
  </div>
</section>
