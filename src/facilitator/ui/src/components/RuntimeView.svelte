<script lang="ts">
  import { ApiError, getJson, toApiError } from "../lib/api.js";
  import { apiFetch, session } from "../lib/auth.svelte.js";
  import { deckChanged } from "../lib/deck.svelte.js";
  import { ago, stateLabel, stateTone, stamp, TONE_CLASS } from "../lib/status.js";
  import type { AdminProvisioningRow, AdminReplicasView } from "../lib/types.js";
  import ProblemNote from "./ProblemNote.svelte";

  // Who is answering, who holds the leases, what provisioning has done, and
  // the manual sweeps an operator can fire right now — each with a reason.

  const SWEEPS = ["nudge", "approvals", "schedules", "session-loss", "audit-retention"] as const;

  let replicas = $state<AdminReplicasView | null>(null);
  let provisioning = $state<AdminProvisioningRow[]>([]);
  let error = $state<ApiError | null>(null);
  let notice = $state<string | null>(null);
  let sweeping = $state<string | null>(null);
  let reason = $state("");

  const canMutate = $derived(session.canMutate);

  async function load(): Promise<void> {
    try {
      const [replicaView, ledger] = await Promise.all([
        getJson<AdminReplicasView>(apiFetch, "/api/admin/replicas"),
        getJson<{ items: AdminProvisioningRow[] }>(apiFetch, "/api/admin/provisioning"),
      ]);
      replicas = replicaView;
      provisioning = ledger.items;
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

  async function sweep(name: string): Promise<void> {
    if (!reason.trim()) {
      return;
    }
    notice = null;
    try {
      const response = await apiFetch(`/api/admin/sweeps/${name}`, {
        method: "POST",
        headers: { "X-Change-Reason": reason.trim() },
      });
      if (!response.ok) {
        const problem = await toApiError(response);
        notice = `${problem.title} (${problem.code})`;
      } else {
        const fired = (await response.json()) as { sweep: string; effects: number };
        notice = `ran ${fired.sweep} — ${fired.effects} effects`;
      }
      sweeping = null;
      await load();
    } catch (err) {
      error = err instanceof ApiError ? err : new ApiError(0, "fetch-failed", String(err), null);
    }
  }
</script>

<section class="card" data-testid="runtime-view">
  <div class="mb-3 flex items-center justify-between">
    <h2 class="text-sm font-medium">
      Runtime
      <span class="ml-1 text-xs font-normal text-ink-3"
        >— which replica answers, and the sweeps you can fire</span
      >
    </h2>
    <button class="btn" onclick={() => void load()}>refresh</button>
  </div>
  <ProblemNote {error} />
  {#if notice}
    <p class="badge mb-2 bg-accent-veil text-accent ring-accent/25" data-testid="sweep-notice" role="status">
      {notice}
    </p>
  {/if}

  <h3 class="mb-1 text-xs font-medium uppercase text-ink-3">Replicas</h3>
  <table class="table mb-4">
    <thead>
      <tr>
        <th>Replica</th><th>Live sessions</th><th>Sockets here</th>
      </tr>
    </thead>
    <tbody>
      {#each replicas?.replicas ?? [] as row (row.replicaId)}
        <tr data-testid={`replica-${row.replicaId}`}>
          <td class="mono text-xs">
            {row.replicaId}
            {#if row.replicaId === replicas?.answeringReplicaId}
              <span class="badge ml-1 bg-ok-veil text-ok ring-ok/25"
                ><span class="dot" aria-hidden="true"></span>answering</span
              >
            {/if}
          </td>
          <td class="mono">{row.liveSessions}</td>
          <td class="mono">{row.socketsHere}</td>
        </tr>
      {:else}
        <tr>
          <td colspan="3" class="text-ink-3">No replica has taken a lease yet.</td>
        </tr>
      {/each}
      {#if replicas}
        <tr>
          <td class="text-ink-3">outbox pending (fleet)</td>
          <td class="mono" colspan="2">{replicas.outboxPending}</td>
        </tr>
      {/if}
    </tbody>
  </table>

  {#if canMutate}
    <h3 class="mb-1 text-xs font-medium uppercase text-ink-3">Sweeps</h3>
    <div class="mb-4 flex flex-wrap items-center gap-2">
      {#each SWEEPS as name}
        {#if sweeping === name}
          <div class="flex gap-1" data-testid={`sweep-form-${name}`}>
            <input
              class="input max-w-40"
              placeholder="change reason"
              aria-label={`${name} sweep reason`}
              bind:value={reason}
              data-testid="sweep-reason" />
            <button
              class="btn btn-primary"
              disabled={!reason.trim()}
              data-testid={`sweep-confirm-${name}`}
              onclick={() => void sweep(name)}>confirm</button
            >
            <button class="btn" onclick={() => (sweeping = null)}>cancel</button>
          </div>
        {:else}
          <button
            class="btn"
            title="run this sweeper pass now; the reason is recorded on the audit trail"
            data-testid={`sweep-${name}`}
            onclick={() => {
              reason = "";
              notice = null;
              sweeping = name;
            }}>{name}</button
          >
        {/if}
      {/each}
    </div>
  {/if}

  <h3 class="mb-1 text-xs font-medium uppercase text-ink-3">Provisioning ledger</h3>
  <table class="table">
    <thead>
      <tr>
        <th>Team</th><th>Workload</th><th>Operation</th><th>State</th><th>Reason</th><th>Created</th>
      </tr>
    </thead>
    <tbody>
      {#each provisioning as row (row.operationId)}
        <tr data-testid={`provisioning-${row.operationId}`}>
          <td class="mono text-xs">{row.team}</td>
          <td class="mono text-xs">{row.workloadId}</td>
          <td>{row.operation}</td>
          <td>
            <span class="badge {TONE_CLASS[stateTone(row.state)]}">
              <span class="dot" aria-hidden="true"></span>{stateLabel(row.state)}
            </span>
          </td>
          <td class="text-xs text-ink-2">{row.reason ?? "—"}</td>
          <td class="mono text-xs text-ink-2" title={stamp(row.createdAt)}>
            {ago(row.createdAt).text}
          </td>
        </tr>
      {:else}
        <tr>
          <td colspan="6" class="text-ink-3">Nothing has been provisioned yet.</td>
        </tr>
      {/each}
    </tbody>
  </table>
</section>
