<script lang="ts">
  import { ApiError, getJson, toApiError } from "../lib/api.js";
  import { apiFetch, session } from "../lib/auth.svelte.js";
  import { deckChanged } from "../lib/deck.svelte.js";
  import { ago, stateLabel, stamp, stateTone, TONE_CLASS } from "../lib/status.js";
  import type { AdminLinksView } from "../lib/types.js";
  import EnrollmentTokensCard from "./EnrollmentTokensCard.svelte";
  import ProblemNote from "./ProblemNote.svelte";

  // Strictly framework-admin: the enrollment-tokens.manage gate, not the
  // broader operator surface.
  const canManageTokens = $derived(session.canManageEnrollmentTokens);

  // Revoking an endpoint is an admin mutation (the group's law): the operator
  // surface, not the narrower enrollment-tokens gate.
  const canRevoke = $derived(session.canMutate);

  let links = $state<AdminLinksView | null>(null);
  let error = $state<ApiError | null>(null);
  let revoking = $state<string | null>(null);

  async function load(): Promise<void> {
    try {
      links = await getJson<AdminLinksView>(apiFetch, "/api/admin/links");
      error = null;
    } catch (err) {
      error = err instanceof ApiError ? err : new ApiError(0, "fetch-failed", String(err), null);
    }
  }

  async function revoke(endpointId: string): Promise<void> {
    if (!window.confirm(`Revoke ${endpointId}? Its enrollment, key and live session die; the endpoint rejoins only with a fresh enrollment code.`)) {
      return;
    }
    revoking = endpointId;
    try {
      const response = await apiFetch(`/api/admin/links/${encodeURIComponent(endpointId)}`, {
        method: "DELETE",
      });
      if (!response.ok) {
        throw await toApiError(response);
      }
      await load();
    } catch (err) {
      error = err instanceof ApiError ? err : new ApiError(0, "fetch-failed", String(err), null);
    } finally {
      revoking = null;
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
            ><th>Lease until</th><th>Sockets</th><th>Credential</th><th>Key</th><th></th>
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
              <td
                class="mono"
                title={`revocation epoch ${g.epoch ?? "—"} · replay fence seq ${g.seq ?? "—"}`}
                >{g.sessionEpoch ?? "—"}</td
              >
              <td class="mono text-xs" title={g.leaseUntil ? stamp(g.leaseUntil) : null}>
                {g.leaseUntil ? ago(g.leaseUntil).text : "—"}
              </td>
              <td class="mono">{g.liveSockets}</td>
              <td class="text-xs text-ink-2">
                {#if g.replaySuspect}
                  <span class="badge bg-warn-veil text-warn ring-warn/25" data-testid={`replay-${g.endpointId}`}
                    title="the last proof of possession was rejected by the replay fence"
                    >replay?</span
                  >
                {/if}
                {g.credentialClass ?? "—"}
              </td>
              <td class="mono text-xs" title={g.endpointKeyId ?? "no active key"}>
                {g.endpointKeyId ? g.endpointKeyId.slice("sha256:".length, "sha256:".length + 12) : "—"}
              </td>
              <td>
                {#if canRevoke}
                  <button
                    class="btn text-xs"
                    data-testid={`revoke-${g.endpointId}`}
                    disabled={revoking === g.endpointId}
                    onclick={() => void revoke(g.endpointId)}>
                    {revoking === g.endpointId ? "revoking…" : "revoke"}
                  </button>
                {/if}
              </td>
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
