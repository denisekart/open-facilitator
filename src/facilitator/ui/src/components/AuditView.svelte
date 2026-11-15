<script lang="ts">
  import { ApiError, getJson } from "../lib/api.js";
  import { apiFetch } from "../lib/auth.svelte.js";
  import { deckChanged } from "../lib/deck.svelte.js";
  import { ago, stateTone, stamp, TONE_CLASS } from "../lib/status.js";
  import { go, href, parse, route } from "../lib/route.svelte.js";
  import type { AdminAuditRow } from "../lib/types.js";
  import ProblemNote from "./ProblemNote.svelte";

  // The audit trail: every decision the gate made, newest first, filterable
  // down to one correlation id. All filters ride in the hash — every filter
  // state is a link you can paste into a ticket.

  const DECISIONS = [
    { key: "all", label: "all", decision: "" },
    { key: "allow", label: "allow", decision: "allow" },
    { key: "deny", label: "deny", decision: "deny" },
  ] as const;

  let items = $state<AdminAuditRow[]>([]);
  let error = $state<ApiError | null>(null);

  const wanted = $derived(route.route.params);
  let actor = $state("");
  let action = $state("");
  let correlation = $state("");
  let trace = $state("");

  async function load(): Promise<void> {
    try {
      const params = new URLSearchParams();
      for (const key of ["decision", "actor", "action", "correlation", "traceId", "since"]) {
        const value = wanted.get(key);
        if (value) {
          params.set(key, value);
        }
      }
      params.set("limit", "100");
      const query = params.toString().replace(/%2C/gi, ",").replace(/%3A/gi, ":");
      items = await getJson<AdminAuditRow[]>(apiFetch, `/api/admin/audit?${query}`);
      error = null;
    } catch (err) {
      error = err instanceof ApiError ? err : new ApiError(0, "fetch-failed", String(err), null);
    }
  }

  // Re-read whenever the hash changes; re-seed the free-text boxes from it.
  $effect(() => {
    void wanted.toString();
    actor = wanted.get("actor") ?? "";
    action = wanted.get("action") ?? "";
    correlation = wanted.get("correlation") ?? "";
    trace = wanted.get("traceId") ?? "";
    // Deck law: after the AiDeck executes a change, re-read the trail.
    void deckChanged();
    void load();
  });

  function apply(extra: { decision?: string } = {}): void {
    const params = new URLSearchParams();
    const decision = extra.decision ?? wanted.get("decision") ?? "";
    if (decision) {
      params.set("decision", decision);
    }
    if (actor.trim()) {
      params.set("actor", actor.trim());
    }
    if (action.trim()) {
      params.set("action", action.trim());
    }
    if (correlation.trim()) {
      params.set("correlation", correlation.trim());
    }
    if (trace.trim()) {
      params.set("traceId", trace.trim());
    }
    const since = wanted.get("since");
    if (since) {
      params.set("since", since);
    }
    go(href(parse(`#/monitor/audit?${params.toString()}`)));
  }
</script>

<section class="card" data-testid="audit-view">
  <div class="mb-3 flex items-center justify-between">
    <h2 class="text-sm font-medium">
      Audit
      <span class="ml-1 text-xs font-normal text-ink-3"
        >— every allow and deny the kernel recorded</span
      >
    </h2>
    <div class="flex items-center gap-2">
      <a
        class="btn"
        data-testid="audit-export"
        href="/api/admin/audit/export?format=csv">export CSV</a
      >
      <button class="btn" onclick={() => void load()}>refresh</button>
    </div>
  </div>
  <div class="mb-3 flex flex-wrap items-center gap-2">
    <div class="flex flex-wrap gap-2" role="group" aria-label="decision filter">
      {#each DECISIONS as { key, label, decision }}
        <button
          class="chip {(wanted.get("decision") || "all") === key ? 'chip-active' : ''}"
          data-testid={`audit-filter-${key}`}
          aria-pressed={(wanted.get("decision") || "all") === key}
          onclick={() => apply({ decision })}>{label}</button
        >
      {/each}
    </div>
    <form
      class="flex flex-wrap items-center gap-2"
      onsubmit={(e) => {
        e.preventDefault();
        apply();
      }}>
      <input
        class="input max-w-40"
        placeholder="actor"
        aria-label="audit actor filter"
        data-testid="audit-actor"
        bind:value={actor} />
      <input
        class="input max-w-52"
        placeholder="action"
        aria-label="audit action filter"
        data-testid="audit-action"
        bind:value={action} />
      <input
        class="input max-w-52"
        placeholder="correlation id"
        aria-label="audit correlation filter"
        data-testid="audit-correlation"
        bind:value={correlation} />
      <input
        class="input max-w-52"
        placeholder="trace id (32 hex)"
        aria-label="audit trace id filter"
        data-testid="audit-trace"
        bind:value={trace} />
      <button class="btn btn-primary" type="submit" data-testid="audit-apply">filter</button>
    </form>
  </div>
  <ProblemNote {error} />
  <table class="table">
    <thead>
      <tr>
        <th>When</th><th>Decision</th><th>Actor</th><th>Action</th><th>Resource</th
        ><th>Correlation</th><th>Trace</th>
      </tr>
    </thead>
    <tbody>
      {#each items as row}
        {@const short = row.auditId.slice(0, 8)}
        <tr data-testid={`audit-row-${short}`}>
          <td class="mono text-xs text-ink-2" title={stamp(row.occurredAt)}>
            {ago(row.occurredAt).text}
          </td>
          <td data-testid={`audit-decision-${short}`}>
            <!-- the admin-e2e badge law: this cell's text is the wire word -->
            <span class="badge {TONE_CLASS[stateTone(row.decision)]}">
              <span class="dot" aria-hidden="true"></span>{row.decision}
            </span>
          </td>
          <td>{row.actor ?? "—"}</td>
          <td class="mono text-xs">{row.action}</td>
          <td class="mono text-xs">{row.resource ?? "—"}</td>
          <td class="mono text-xs">
            {#if row.correlationId}
              <a
                class="text-accent underline"
                title="every row carrying this correlation"
                data-testid={`audit-correlation-${short}`}
                href={`#/monitor/audit?correlation=${row.correlationId}`}
                >{row.correlationId.slice(0, 8)}</a
              >
            {:else}
              —
            {/if}
          </td>
          <td class="mono text-xs">
            {#if row.traceId}
              <button
                class="mono badge bg-sunken text-ink-2 ring-edge"
                title="copy trace id — paste it into the OTLP viewer"
                data-testid={`audit-copy-trace-${short}`}
                onclick={() => void navigator.clipboard.writeText(row.traceId ?? "")}
                >{row.traceId.slice(0, 8)}…</button
              >
              <a
                class="ml-1 text-accent underline"
                title="filter to this trace"
                data-testid={`audit-trace-${short}`}
                href={`#/monitor/audit?traceId=${row.traceId}`}
                >trace</a
              >
            {:else}
              —
            {/if}
          </td>
        </tr>
      {/each}
      {#if items.length === 0}
        <tr>
          <td colspan="7" class="text-ink-3">Nothing here — nothing matched.</td>
        </tr>
      {/if}
    </tbody>
  </table>
</section>
