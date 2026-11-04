<script lang="ts">
  import { ApiError, getJson, toApiError, type Fetcher } from "../lib/api.js";
  import { apiFetch } from "../lib/auth.svelte.js";
  import { enrollmentStatusLabel } from "../lib/enroll-tokens.js";
  import { health } from "../lib/health.svelte.js";
  import type { EnrollmentTokenRow, MintedEnrollmentToken } from "../lib/types.js";
  import ProblemNote from "./ProblemNote.svelte";

  // The mint/revoke card for admin-minted enrollment tokens. Rendered only for
  // framework-admin (the enrollment-tokens.manage gate); the token literal is
  // shown exactly once, straight off the 201 response.

  // Tests inject a mock fetcher; production rides the shared bearer fetch.
  let { fetcher: api = apiFetch }: { fetcher?: Fetcher } = $props();

  const ROLES = ["gateway", "child"] as const;

  let rows = $state<EnrollmentTokenRow[]>([]);
  let error = $state<ApiError | null>(null);
  let minted = $state<MintedEnrollmentToken | null>(null);
  let copied = $state(false);
  let busy = $state(false);

  let label = $state("");
  let role = $state<string>("gateway");
  let ttlMinutes = $state(60);
  let customTeam = $state("");

  // Dropdown source: the roster slugs from /api/admin/summary; a free-text
  // field takes over until the summary lands (or when the roster is empty).
  const teamSlugs = $derived(health.summary?.teamSlugs ?? []);
  const team = $derived(customTeam || teamSlugs[0] || "");
  const mintEnabled = $derived(!busy && label.trim().length > 0 && team.length > 0);

  async function load(): Promise<void> {
    try {
      rows = await getJson<EnrollmentTokenRow[]>(api, "/api/admin/enroll-tokens");
      error = null;
    } catch (err) {
      error = err instanceof ApiError ? err : new ApiError(0, "fetch-failed", String(err), null);
    }
  }

  $effect(() => {
    void load();
  });

  async function mint(): Promise<void> {
    if (!mintEnabled) {
      return;
    }
    busy = true;
    try {
      const response = await api("/api/admin/enroll-tokens", {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({
          label: label.trim(),
          team,
          role,
          ttlMinutes: Number.isFinite(ttlMinutes)
            ? Math.min(10080, Math.max(1, Math.trunc(ttlMinutes)))
            : 60,
        }),
      });
      if (!response.ok) {
        throw await toApiError(response);
      }
      minted = (await response.json()) as MintedEnrollmentToken;
      copied = false;
      label = "";
      await load();
    } catch (err) {
      error = err instanceof ApiError ? err : new ApiError(0, "fetch-failed", String(err), null);
    } finally {
      busy = false;
    }
  }

  async function copyToken(): Promise<void> {
    if (minted === null) {
      return;
    }
    try {
      await navigator.clipboard.writeText(minted.token);
      copied = true;
    } catch {
      copied = false;
    }
  }

  async function revoke(row: EnrollmentTokenRow): Promise<void> {
    try {
      const response = await api(`/api/admin/enroll-tokens/${row.id}`, { method: "DELETE" });
      if (!response.ok) {
        throw await toApiError(response);
      }
      await load();
    } catch (err) {
      error = err instanceof ApiError ? err : new ApiError(0, "fetch-failed", String(err), null);
    }
  }

  function stamp(when: string): string {
    return when.replace("T", " ").replace(/\.\d+Z$/, "Z");
  }
</script>

<section class="card" data-testid="enrollment-tokens-card">
  <div class="mb-3 flex items-center justify-between">
    <h2 class="text-sm font-medium">Enrollment tokens</h2>
    <button class="btn" data-testid="tokens-refresh" onclick={() => void load()}>refresh</button>
  </div>
  <ProblemNote {error} />

  <div class="mb-3 grid gap-3 sm:grid-cols-2">
    <div>
      <label class="label" for="token-label">label</label>
      <input id="token-label" class="input" bind:value={label} placeholder="living-room pc" data-testid="token-label" />
    </div>
    <div>
      <span class="label">team</span>
      {#if teamSlugs.length > 0}
        <select class="input" bind:value={customTeam} aria-label="team" data-testid="token-team">
          <option value="">{teamSlugs[0]}</option>
          {#each teamSlugs.slice(1) as slug}
            <option value={slug}>{slug}</option>
          {/each}
        </select>
      {:else}
        <input class="input" bind:value={customTeam} placeholder="team slug" aria-label="team" data-testid="token-team" />
      {/if}
    </div>
    <div>
      <label class="label" for="token-role">role</label>
      <select id="token-role" class="input" bind:value={role} data-testid="token-role">
        {#each ROLES as r}
          <option value={r}>{r}</option>
        {/each}
      </select>
    </div>
    <div>
      <label class="label" for="token-ttl">TTL minutes</label>
      <input id="token-ttl" class="input" type="number" min="1" max="10080" bind:value={ttlMinutes} data-testid="token-ttl" />
    </div>
    <div class="sm:col-span-2">
      <button class="btn btn-primary" disabled={!mintEnabled} data-testid="token-mint" onclick={() => void mint()}>
        mint
      </button>
    </div>
  </div>

  {#if minted}
    <div class="mb-3 rounded-lg border border-accent/60 bg-accent/10 p-3" data-testid="minted-token">
      <p class="mono mb-2 break-all text-xs" data-testid="minted-token-value">{minted.token}</p>
      <div class="flex items-center gap-2">
        <button class="btn" data-testid="token-copy" onclick={() => void copyToken()}>
          {copied ? "copied" : "copy"}
        </button>
        <span class="text-xs text-warn" data-testid="minted-token-warning">
          shown only once — store it now; the kernel keeps only the hash
        </span>
      </div>
    </div>
  {/if}

  <table class="table" data-testid="token-table">
    <thead>
      <tr><th>Label</th><th>Team</th><th>Role</th><th>Status</th><th>Expires</th><th>Actions</th></tr>
    </thead>
    <tbody>
      {#each rows as row (row.id)}
        <tr data-testid={`token-${row.id}`}>
          <td>{row.label}</td>
          <td>{row.team}</td>
          <td>{row.role}</td>
          <td class="mono text-xs" data-testid={`token-status-${row.id}`}>{enrollmentStatusLabel(row)}</td>
          <td class="mono text-xs">{stamp(row.expiresAt)}</td>
          <td>
            {#if row.revokedAt === null}
              <button class="btn btn-danger" data-testid={`token-revoke-${row.id}`} onclick={() => void revoke(row)}>
                revoke
              </button>
            {:else}
              <span class="text-ink-3">—</span>
            {/if}
          </td>
        </tr>
      {/each}
      {#if rows.length === 0}
        <tr><td colspan="6" class="text-ink-3">no tokens</td></tr>
      {/if}
    </tbody>
  </table>
</section>
