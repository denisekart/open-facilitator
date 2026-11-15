<script lang="ts">
  import { ApiError, getJson, toApiError } from "../lib/api.js";
  import { apiFetch, session } from "../lib/auth.svelte.js";
  import { deckChanged } from "../lib/deck.svelte.js";
  import { stateLabel, stateTone, TONE_CLASS } from "../lib/status.js";
  import type { AdminDeliveryRow } from "../lib/types.js";
  import ProblemNote from "./ProblemNote.svelte";

  const STATES = ["", "pending", "leased", "delivered", "dead_letter"] as const;

  let filter = $state<string>("");
  let items = $state<AdminDeliveryRow[]>([]);
  let error = $state<ApiError | null>(null);
  let notice = $state<string | null>(null);
  let replaying = $state<string | null>(null);
  let reason = $state("");

  const canMutate = $derived(session.canMutate);

  async function load(): Promise<void> {
    try {
      const query = filter ? `?state=${filter}&limit=50` : "?limit=50";
      const page = await getJson<{ items: AdminDeliveryRow[] }>(apiFetch, `/api/admin/deliveries${query}`);
      items = page.items;
      error = null;
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

  async function replay(row: AdminDeliveryRow): Promise<void> {
    if (!reason.trim()) {
      return;
    }
    notice = null;
    try {
      const response = await apiFetch(`/api/admin/dead-letters/${row.id}/replay`, {
        method: "POST",
        headers: { "X-Change-Reason": reason.trim() },
      });
      if (!response.ok) {
        const problem = await toApiError(response);
        notice =
          problem.status === 409
            ? "already replayed — ledger holds one replay"
            : `${problem.title} (${problem.code})`;
      } else {
        notice = `replayed ${row.id.slice(0, 8)}`;
      }
      replaying = null;
      await load();
    } catch (err) {
      error = err instanceof ApiError ? err : new ApiError(0, "fetch-failed", String(err), null);
    }
  }
</script>

<section class="card" data-testid="deliveries-view">
  <div class="mb-3 flex items-center justify-between">
    <h2 class="text-sm font-medium">
      Deliveries
      <span class="ml-1 text-xs font-normal text-ink-3"
        >— every message the kernel sent out, and what became of it</span
      >
    </h2>
    <button class="btn" onclick={() => void load()}>refresh</button>
  </div>
  <div class="mb-3 flex flex-wrap gap-2" role="group" aria-label="state filter">
    {#each STATES as s}
      <button
        class="chip {filter === s ? 'chip-active' : ''}"
        data-testid={`filter-${s || "all"}`}
        aria-pressed={filter === s}
        onclick={() => {
          filter = s;
        }}>{s ? stateLabel(s) : "all"}</button
      >
    {/each}
  </div>
  <ProblemNote {error} />
  {#if notice}
    <p class="badge mb-2 bg-accent-veil text-accent ring-accent/25" data-testid="replay-notice" role="status">
      {notice}
    </p>
  {/if}
  <table class="table">
    <thead>
      <tr>
        <th>Plane</th><th>Id</th><th>State</th><th>Attempts</th><th>Endpoint</th
        ><th>Reason / error</th>{#if canMutate}<th>Actions</th>{/if}
      </tr>
    </thead>
    <tbody>
      {#each items as row}
        {@const short = row.id.slice(0, 8)}
        <tr data-testid={`delivery-${short}`}>
          <td>{row.plane}</td>
          <td class="mono text-xs" title={row.id}>{short}</td>
          <td data-testid={`delivery-state-${short}`}>
            <span class="badge {TONE_CLASS[stateTone(row.state)]}">
              <span class="dot" aria-hidden="true"></span>{stateLabel(row.state)}
            </span>
          </td>
          <td class="mono">{row.attempts}</td>
          <td class="mono text-xs">{row.endpointId ?? "—"}</td>
          <td class="text-xs text-ink-2">{row.lastError ?? row.reason ?? "—"}</td>
          {#if canMutate}
            <td>
              {#if row.state === "dead_letter"}
                {#if replaying === row.id}
                  <div class="flex gap-1">
                    <input class="input max-w-40" placeholder="change reason" bind:value={reason} aria-label="replay reason" data-testid="replay-reason" />
                    <button class="btn btn-primary" disabled={!reason.trim()} data-testid={`replay-confirm-${short}`} onclick={() => void replay(row)}>confirm</button>
                    <button class="btn" onclick={() => (replaying = null)}>cancel</button>
                  </div>
                {:else}
                  <button
                    class="btn btn-danger"
                    title="send it again; the reason is recorded on the audit trail"
                    data-testid={`replay-${short}`}
                    onclick={() => {
                      reason = "";
                      replaying = row.id;
                    }}>replay</button
                  >
                {/if}
              {:else}
                <span class="text-ink-3">—</span>
              {/if}
            </td>
          {/if}
        </tr>
      {/each}
      {#if items.length === 0}
        <tr>
          <td colspan="7" class="text-ink-3">
            {filter
              ? `Nothing ${stateLabel(filter)} — the closest thing to a good sign.`
              : "Nothing has been delivered yet."}
          </td>
        </tr>
      {/if}
    </tbody>
  </table>
</section>
