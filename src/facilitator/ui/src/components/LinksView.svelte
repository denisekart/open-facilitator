<script lang="ts">
  import { ApiError, getJson } from "../lib/api.js";
  import { apiFetch, session } from "../lib/auth.svelte.js";
  import { deckChanged } from "../lib/deck.svelte.js";
  import { ago, stateLabel, stamp, stateTone, TONE_CLASS } from "../lib/status.js";
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
    // Deck law: after the AiDeck executes a change, this screen re-loads
    // its slice — the operator sees the effect without touching refresh.
    void deckChanged();
    void load();
  });
</script>

<section class="card" data-testid="links-view">
  <div class="mb-3 flex items-center justify-between">
    <h2 class="text-sm font-medium">
      Team environments
      <span class="ml-1 text-xs font-normal text-ink-3"
        >— gateways dial out to this kernel; nothing dials in</span
      >
    </h2>
    <button class="btn" onclick={() => void load()}>refresh</button>
  </div>
  <ProblemNote {error} />
  {#if links}
    {#if links.gateways.length === 0}
      <p class="text-ink-3">
        No team has linked yet. Mint an enrollment token below, hand it to a team, and their
        gateway will appear here the moment it dials out.
      </p>
    {:else}
      <table class="table mb-4">
        <thead>
          <tr>
            <th>Endpoint</th><th>Team</th><th>Trust</th><th>Status</th><th>Epoch</th
            ><th>Lease until</th><th>Sockets</th>
          </tr>
        </thead>
        <tbody>
          {#each links.gateways as g}
            <tr data-testid={`gateway-${g.endpointId}`}>
              <td class="mono text-xs" title={g.endpointId}>{g.endpointId}</td>
              <td>{g.team}</td>
              <td>
                <span class="badge bg-sunken text-ink-2 ring-edge" data-testid={`trust-${g.endpointId}`}
                  >{g.trustMode}</span
                >
              </td>
              <td>
                {#if g.online}
                  <span class="badge bg-ok-veil text-ok ring-ok/25" data-testid={`online-${g.endpointId}`}>
                    <span class="dot" aria-hidden="true"></span>online
                  </span>
                {:else}
                  <span
                    class="badge bg-sunken text-ink-2 ring-edge"
                    data-testid={`offline-${g.endpointId}`}
                    title={g.lastHeartbeat ? stamp(g.lastHeartbeat) : null}>
                    <span class="dot" aria-hidden="true"></span>offline · last seen
                    {ago(g.lastHeartbeat).text}
                  </span>
                {/if}
              </td>
              <td class="mono">{g.sessionEpoch ?? "—"}</td>
              <td class="mono text-xs" title={g.leaseUntil ? stamp(g.leaseUntil) : null}>
                {g.leaseUntil ? ago(g.leaseUntil).text : "—"}
              </td>
              <td class="mono">{g.liveSockets}</td>
            </tr>
          {/each}
        </tbody>
      </table>
    {/if}
    {#if links.affectedAssignments.length > 0}
      <h3 class="mb-1 text-xs font-medium uppercase text-ink-3">
        Work waiting on an offline environment
      </h3>
      <p class="mb-1 text-xs text-ink-3">
        Durable: these resume on their own when the team's gateway returns.
      </p>
      <table class="table" data-testid="affected-assignments">
        <thead><tr><th>Assignment</th><th>Work</th><th>Status</th></tr></thead>
        <tbody>
          {#each links.affectedAssignments as a}
            <tr>
              <td class="mono text-xs" title={a.assignmentId}>{a.assignmentId.slice(0, 8)}</td>
              <td class="mono text-xs" title={a.workId}>{a.workId.slice(0, 8)}</td>
              <td>
                <span class="badge {TONE_CLASS[stateTone(a.status)]}">
                  <span class="dot" aria-hidden="true"></span>{stateLabel(a.status)}
                </span>
              </td>
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
