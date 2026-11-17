<script lang="ts">
  import { SlidersHorizontal } from "lucide-svelte";
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
  let advancedOpen = $state(false);

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
    if (actor || action || correlation || trace) {
      advancedOpen = true;
    }
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

  function clearFilters(): void {
    actor = "";
    action = "";
    correlation = "";
    trace = "";
    const decision = wanted.get("decision") ?? "";
    const params = new URLSearchParams();
    if (decision) {
      params.set("decision", decision);
    }
    go(href(parse(`#/monitor/audit?${params.toString()}`)));
  }
</script>

<section class="card" data-testid="audit-view">
  <div class="mb-3 flex items-center justify-between">
    <div>
      <h2 class="text-sm font-semibold text-ink">
        Audit Trail
        <span class="ml-1 text-xs font-normal text-ink-2"
          >— authorization decisions and policy evaluations recorded by the kernel</span
        >
      </h2>
    </div>
    <div class="flex items-center gap-2">
      <a
        class="btn text-xs h-7 min-h-7"
        data-testid="audit-export"
        href="/api/admin/audit/export?format=csv">export CSV</a
      >
      <button class="btn text-xs h-7 min-h-7" onclick={() => void load()}>refresh</button>
    </div>
  </div>

  <div class="mb-3 flex flex-col gap-2 rounded border border-edge bg-surface-alt p-2.5">
    <div class="flex flex-wrap items-center justify-between gap-2">
      <div class="flex flex-wrap items-center gap-1.5" role="group" aria-label="decision filter">
        <span class="text-xs font-semibold text-ink-2 mr-1">Decision:</span>
        {#each DECISIONS as { key, label, decision }}
          <button
            class="chip h-7 min-h-7 text-xs font-semibold {(wanted.get("decision") || "all") === key ? 'chip-active' : ''}"
            data-testid={`audit-filter-${key}`}
            aria-pressed={(wanted.get("decision") || "all") === key}
            onclick={() => apply({ decision })}>{label}</button
          >
        {/each}
      </div>

      <button
        class="chip text-xs h-7 min-h-7 flex items-center gap-1.5"
        class:chip-active={advancedOpen}
        onclick={() => (advancedOpen = !advancedOpen)}>
        <SlidersHorizontal class="size-4" strokeWidth={1.5} />
        <span>{advancedOpen ? "Hide filters" : "Field filters"}</span>
        {#if actor || action || correlation || trace}
          <span class="size-1.5 rounded-full bg-accent"></span>
        {/if}
      </button>
    </div>

    <form
      class="flex flex-wrap items-center gap-2 pt-1 border-t border-edge/50 {advancedOpen ? '' : 'hidden'}"
      onsubmit={(e) => {
        e.preventDefault();
        apply();
      }}>
      <div class="relative min-w-0 flex-1 basis-32">
        <input
          class="input text-xs h-8"
          placeholder="actor slug"
          aria-label="audit actor filter"
          data-testid="audit-actor"
          bind:value={actor} />
      </div>
      <div class="relative min-w-0 flex-1 basis-36">
        <input
          class="input text-xs h-8"
          placeholder="action (e.g. work.create)"
          aria-label="audit action filter"
          data-testid="audit-action"
          bind:value={action} />
      </div>
      <div class="relative min-w-0 flex-1 basis-36">
        <input
          class="input text-xs h-8"
          placeholder="correlation ID"
          aria-label="audit correlation filter"
          data-testid="audit-correlation"
          bind:value={correlation} />
      </div>
      <div class="relative min-w-0 flex-1 basis-44">
        <input
          class="input text-xs h-8"
          placeholder="trace ID (32 hex)"
          aria-label="audit trace id filter"
          data-testid="audit-trace"
          bind:value={trace} />
      </div>
      <div class="flex items-center gap-1.5 shrink-0">
        <button class="btn btn-primary text-xs h-8 min-h-8 px-3" type="submit" data-testid="audit-apply">filter</button>
        {#if actor || action || correlation || trace}
          <button class="btn text-xs h-8 min-h-8" type="button" onclick={clearFilters}>clear</button>
        {/if}
      </div>
    </form>
  </div>

  <ProblemNote {error} />

  <div class="overflow-x-auto">
    <table class="table table-fixed w-full">
      <thead>
        <tr>
          <th class="w-24">When</th>
          <th class="w-24">Decision</th>
          <th class="w-28">Actor</th>
          <th class="w-36">Action</th>
          <th class="w-40">Resource</th>
          <th class="w-28">Correlation</th>
          <th class="w-28">Trace</th>
        </tr>
      </thead>
      <tbody>
        {#each items as row}
          {@const short = row.auditId.slice(0, 8)}
          <tr data-testid={`audit-row-${short}`}>
            <td class="mono text-xs text-ink-2 whitespace-nowrap" title={stamp(row.occurredAt)}>
              {ago(row.occurredAt).text}
            </td>
            <td data-testid={`audit-decision-${short}`}>
              <!-- the admin-e2e badge law: this cell's text is the wire word -->
              <span class="badge {TONE_CLASS[stateTone(row.decision)]}">
                <span class="dot" aria-hidden="true"></span>{row.decision}
              </span>
            </td>
            <td class="truncate" title={row.actor ?? "—"}>{row.actor ?? "—"}</td>
            <td class="mono text-xs truncate" title={row.action}>{row.action}</td>
            <td class="mono text-xs truncate" title={row.resource ?? "—"}>{row.resource ?? "—"}</td>
            <td class="mono text-xs truncate">
              {#if row.correlationId}
                <a
                  class="text-accent underline font-medium"
                  title={`Correlation: ${row.correlationId} — click to filter`}
                  data-testid={`audit-correlation-${short}`}
                  href={`#/monitor/audit?correlation=${row.correlationId}`}
                  >{row.correlationId.slice(0, 8)}</a
                >
              {:else}
                —
              {/if}
            </td>
            <td class="mono text-xs truncate">
              {#if row.traceId}
                <button
                  class="mono badge bg-sunken text-ink-2 hover:bg-hover cursor-pointer"
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
            <td colspan="7" class="text-ink-2 py-4 text-center">Nothing here — no audit events matched your filter.</td>
          </tr>
        {/if}
      </tbody>
    </table>
  </div>
</section>
