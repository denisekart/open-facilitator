<script lang="ts">
  import { ApiError, getJson, toApiError } from "../lib/api.js";
  import { apiFetch, session } from "../lib/auth.svelte.js";
  import type { AdminWorkDetail, WorkEventRow } from "../lib/types.js";
  import ArtifactRef from "./ArtifactRef.svelte";
  import ProblemNote from "./ProblemNote.svelte";

  let { workId, onclose }: { workId: string; onclose: () => void } = $props();

  let detail = $state<AdminWorkDetail | null>(null);
  let timeline = $state<WorkEventRow[] | null>(null);
  let error = $state<ApiError | null>(null);
  let notice = $state<string | null>(null);
  let comment = $state("");
  let busy = $state(false);

  const canMutate = $derived(session.canMutate);
  const RESULT_STATES = ["available", "validated", "rejected"] as const;

  async function load(): Promise<void> {
    detail = null;
    timeline = null;
    error = null;
    try {
      detail = await getJson<AdminWorkDetail>(apiFetch, `/api/admin/work/${workId}`);
      timeline = await getJson<WorkEventRow[]>(apiFetch, `/api/admin/work/${workId}/timeline`);
    } catch (err) {
      error = err instanceof ApiError ? err : new ApiError(0, "fetch-failed", String(err), null);
    }
  }

  $effect(() => {
    void load();
  });

  async function post(path: string, body: string, ok: string): Promise<void> {
    busy = true;
    notice = null;
    try {
      const response = await apiFetch(path, {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body,
      });
      if (!response.ok) {
        throw await toApiError(response);
      }
      notice = ok;
      await load();
    } catch (err) {
      error = err instanceof ApiError ? err : new ApiError(0, "fetch-failed", String(err), null);
    } finally {
      busy = false;
    }
  }

  function respond(approvalId: string, decision: "approved" | "denied"): void {
    const body = comment.trim()
      ? JSON.stringify({ decision, comment: comment.trim() })
      : JSON.stringify({ decision });
    void post(
      `/api/works/${workId}/approvals/${approvalId}/respond`,
      body,
      `approval ${decision}`,
    );
  }

  function remark(manifestId: string, resultState: string): void {
    void post(
      `/api/admin/manifests/${manifestId}/re-mark`,
      JSON.stringify({ resultState }),
      `manifest re-marked ${resultState}`,
    );
  }

  function stamp(when: string): string {
    return when.replace("T", " ").replace(/\.\d+Z$/, "Z");
  }
</script>

<section class="card" data-testid="work-inspector">
  <div class="mb-3 flex items-center justify-between">
    <h2 class="text-sm font-medium">Work inspector</h2>
    <button class="btn" data-testid="inspector-close" onclick={onclose}>close</button>
  </div>

  <ProblemNote {error} />
  {#if notice}
    <p class="badge bg-ok/15 text-ok" data-testid="inspector-notice">{notice}</p>
  {/if}

  {#if detail}
    {@const w = detail.work}
    <div class="mb-4 grid grid-cols-[auto_1fr] gap-x-4 gap-y-1 text-sm">
      <span class="text-ink-3">Objective</span><span data-testid="work-objective">{w.objective}</span>
      <span class="text-ink-3">State</span><span class="badge bg-accent/15 text-accent" data-testid="work-state">{w.state}</span>
      <span class="text-ink-3">Version</span><span class="mono">v{w.version}</span>
      <span class="text-ink-3">Created</span><span class="mono text-xs text-ink-2">{stamp(w.createdAt)}</span>
    </div>

    {#if detail.assignments.length > 0}
      <h3 class="mb-1 text-xs font-medium uppercase text-ink-3">Assignments</h3>
      <table class="table mb-4">
        <thead><tr><th>Assignment</th><th>Status</th><th>Gen</th><th>Attempts</th></tr></thead>
        <tbody>
          {#each detail.assignments as a}
            <tr>
              <td class="mono text-xs">{a.assignmentId.slice(0, 8)}</td>
              <td>{a.status}</td>
              <td class="mono">{a.currentExecutionGeneration}</td>
              <td class="mono text-xs">
                {a.attempts.map((at) => `${at.status}@g${at.generation}`).join(", ") || "—"}
              </td>
            </tr>
          {/each}
        </tbody>
      </table>
    {/if}

    <h3 class="mb-1 text-xs font-medium uppercase text-ink-3">Approvals</h3>
    {#if detail.approvals.length === 0}
      <p class="mb-4 text-ink-3">none</p>
    {:else}
      <table class="table mb-2" data-testid="approvals">
        <thead><tr><th>Approval</th><th>State</th><th>Action</th>{#if canMutate}<th>Decide</th>{/if}</tr></thead>
        <tbody>
          {#each detail.approvals as ap}
            {@const short = ap.approvalId.slice(0, 8)}
            <tr>
              <td class="mono text-xs">{short}</td>
              <td data-testid={`approval-state-${short}`}>{ap.state}</td>
              <td class="mono text-xs">{ap.actionDigest.slice(0, 18)}…</td>
              {#if canMutate}
                <td>
                  {#if ap.state === "pending"}
                    <div class="flex gap-1">
                      <button
                        class="btn btn-primary"
                        disabled={busy}
                        data-testid={`approve-${short}`}
                        onclick={() => respond(ap.approvalId, "approved")}>approve</button>
                      <button
                        class="btn btn-danger"
                        disabled={busy}
                        data-testid={`deny-${short}`}
                        onclick={() => respond(ap.approvalId, "denied")}>deny</button>
                    </div>
                  {:else}
                    <span class="text-ink-3">{ap.decidedAt ? stamp(ap.decidedAt) : "—"}</span>
                  {/if}
                </td>
              {/if}
            </tr>
          {/each}
        </tbody>
      </table>
      {#if canMutate}
        <input class="input mb-4 max-w-md" placeholder="optional comment" bind:value={comment} aria-label="approval comment" />
      {/if}
    {/if}

    {#if detail.manifests.length > 0}
      <h3 class="mb-1 text-xs font-medium uppercase text-ink-3">Manifests</h3>
      <table class="table mb-4">
        <thead>
          <tr><th>Manifest</th><th>Rev</th><th>Result</th><th>Artifact</th>{#if canMutate}<th>Re-mark</th>{/if}</tr>
        </thead>
        <tbody>
          {#each detail.manifests as m}
            {@const short = m.manifestId.slice(0, 12)}
            <tr>
              <td class="mono text-xs">{short}</td>
              <td class="mono">{m.revision}</td>
              <td data-testid={`manifest-state-${short}`}>{m.resultState}</td>
              <td><ArtifactRef artifactId={m.artifactId} /></td>
              {#if canMutate}
                <td>
                  <div class="flex gap-1" role="group" aria-label="re-mark result state">
                    {#each RESULT_STATES as state}
                      <button
                        class="btn {m.resultState === state ? 'btn-primary' : ''}"
                        disabled={busy || m.resultState === state}
                        data-testid={`remark-${short}-${state}`}
                        onclick={() => remark(m.manifestId, state)}>{state}</button>
                    {/each}
                  </div>
                </td>
              {/if}
            </tr>
          {/each}
        </tbody>
      </table>
    {/if}
  {:else if !error}
    <p class="text-ink-3">loading…</p>
  {/if}

  {#if timeline}
    <h3 class="mb-1 text-xs font-medium uppercase text-ink-3">Activity</h3>
    <table class="table" data-testid="timeline">
      <thead><tr><th>At</th><th>Kind</th><th>Actor</th><th>Detail</th><th>Trace</th></tr></thead>
      <tbody>
        {#each timeline as row}
          <tr data-testid={`timeline-${row.kind}`}>
            <td class="mono text-xs whitespace-nowrap">{stamp(row.createdAt)}</td>
            <td>{row.kind}</td>
            <td class="text-xs">{row.actor}</td>
            <td class="text-xs text-ink-2">{row.detail}</td>
            <td>
              {#if row.correlationId}
                <button
                  class="mono badge bg-raised text-ink-2"
                  title="copy correlation id (the trace handle)"
                  data-testid="trace-chip"
                  onclick={() => void navigator.clipboard.writeText(row.correlationId ?? "")}
                  >{row.correlationId.slice(0, 8)}…</button>
              {:else}
                <span class="text-ink-3">—</span>
              {/if}
            </td>
          </tr>
        {/each}
      </tbody>
    </table>
  {/if}
</section>
