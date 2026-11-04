<script lang="ts">
  import { ApiError, getJson } from "../lib/api.js";
  import { apiFetch, session } from "../lib/auth.svelte.js";
  import type { AdminLinksView } from "../lib/types.js";
  import EnrollmentTokensCard from "./EnrollmentTokensCard.svelte";
  import ProblemNote from "./ProblemNote.svelte";

  // Strictly framework-admin: the enrollment-tokens.manage gate, not the
  // broader operator surface.
  const canManageTokens = $derived(session.canManageEnrollmentTokens);

  let links = $state<AdminLinksView | null>(null);
  let error = $state<ApiError | null>(null);

  async function load(): Promise<void> {
    try {
      links = await getJson<AdminLinksView>(apiFetch, "/api/admin/links");
      error = null;
    } catch (err) {
      error = err instanceof ApiError ? err : new ApiError(0, "fetch-failed", String(err), null);
    }
  }

  $effect(() => {
    void load();
  });

  function stamp(when: string | null): string {
    return when ? when.replace("T", " ").replace(/\.\d+Z$/, "Z") : "never";
  }
</script>

<section class="card" data-testid="links-view">
  <div class="mb-3 flex items-center justify-between">
    <h2 class="text-sm font-medium">Links (gateways)</h2>
    <button class="btn" onclick={() => void load()}>refresh</button>
  </div>
  <ProblemNote {error} />
  {#if links}
    <table class="table mb-4">
      <thead>
        <tr><th>Endpoint</th><th>Team</th><th>Trust</th><th>Status</th><th>Epoch</th><th>Lease until</th><th>Sockets</th></tr>
      </thead>
      <tbody>
        {#each links.gateways as g}
          <tr data-testid={`gateway-${g.endpointId}`}>
            <td class="mono text-xs">{g.endpointId}</td>
            <td>{g.team}</td>
            <td><span class="badge bg-raised text-ink-2" data-testid={`trust-${g.endpointId}`}>{g.trustMode}</span></td>
            <td>
              {#if g.online}
                <span class="badge bg-ok/15 text-ok" data-testid={`online-${g.endpointId}`}>online</span>
              {:else}
                <span class="badge bg-warn/15 text-warn" data-testid={`offline-${g.endpointId}`}>
                  offline (last seen {stamp(g.lastHeartbeat)})
                </span>
              {/if}
            </td>
            <td class="mono">{g.sessionEpoch ?? "—"}</td>
            <td class="mono text-xs">{g.leaseUntil ? stamp(g.leaseUntil) : "—"}</td>
            <td class="mono">{g.liveSockets}</td>
          </tr>
        {/each}
      </tbody>
    </table>
    {#if links.affectedAssignments.length > 0}
      <h3 class="mb-1 text-xs font-medium uppercase text-ink-3">Assignments on offline gateways</h3>
      <table class="table" data-testid="affected-assignments">
        <thead><tr><th>Assignment</th><th>Work</th><th>Status</th></tr></thead>
        <tbody>
          {#each links.affectedAssignments as a}
            <tr>
              <td class="mono text-xs">{a.assignmentId.slice(0, 8)}</td>
              <td class="mono text-xs">{a.workId.slice(0, 8)}</td>
              <td>{a.status}</td>
            </tr>
          {/each}
        </tbody>
      </table>
    {/if}
  {:else if !error}
    <p class="text-ink-3">loading…</p>
  {/if}
</section>

{#if canManageTokens}
  <div class="mt-4">
    <EnrollmentTokensCard />
  </div>
{/if}
