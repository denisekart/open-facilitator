<script lang="ts">
  import { ApiError, getJson, toApiError } from "../lib/api.js";
  import { apiFetch, session } from "../lib/auth.svelte.js";
  import { deckChanged } from "../lib/deck.svelte.js";
  import { ago, flowTone, stateLabel, stamp, TONE_CLASS, stateTone } from "../lib/status.js";
  import type { AdminWorkDetail, WorkEventRow, WorkflowInstanceRow } from "../lib/types.js";
  import ArtifactRef from "./ArtifactRef.svelte";
  import ProblemNote from "./ProblemNote.svelte";

  let { workId, onclose }: { workId: string; onclose: () => void } = $props();

  let detail = $state<AdminWorkDetail | null>(null);
  let timeline = $state<WorkEventRow[] | null>(null);
  let flow = $state<WorkflowInstanceRow | null>(null);
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
      // The flow strip: when a workflow instance backs this work, point at
      // it. A 403/404 here never hides the work — the strip is optional.
      try {
        const insts = await getJson<{ items: WorkflowInstanceRow[] }>(
          apiFetch,
          `/api/admin/workflows/instances?work=${workId}&limit=1`,
        );
        flow = insts.items[0] ?? null;
      } catch {
        flow = null;
      }
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
    void post(`/api/works/${workId}/approvals/${approvalId}/respond`, body, `approval ${decision}`);
  }

  function remark(manifestId: string, resultState: string): void {
    void post(
      `/api/admin/manifests/${manifestId}/re-mark`,
      JSON.stringify({ resultState }),
      `manifest re-marked ${resultState}`,
    );
  }

  // Close on Esc only when focus isn't in the comment box, so editing a
  // comment and dismissing the inspector don't fight each other.
  function onKeydown(ev: KeyboardEvent): void {
    if (ev.key === "Escape" && !(ev.target instanceof HTMLInputElement)) onclose();
  }
</script>

<svelte:window onkeydown={onKeydown} />

<section class="card" data-testid="work-inspector" aria-label="work inspector">
  <div class="mb-3 flex items-center justify-between gap-2">
    <h2 class="text-sm font-medium">
      Work
      <span class="mono ml-1 text-xs font-normal text-ink-2" title={workId}
        >{workId.slice(0, 8)}…</span
      >
    </h2>
    <div class="flex items-center gap-2">
      <button class="btn" title="reload" onclick={() => void load()}>↻</button>
      <button class="btn" data-testid="inspector-close" onclick={onclose}>close</button>
    </div>
  </div>

  <ProblemNote {error} />
  {#if notice}
    <p class="badge mb-2 bg-ok-veil text-ok" data-testid="inspector-notice" role="status">
      {notice}
    </p>
  {/if}

  {#if detail}
    {@const w = detail.work}
    <div class="mb-4 grid grid-cols-[auto_1fr] gap-x-4 gap-y-1 text-sm">
      <span class="text-ink-2">Objective</span><span data-testid="work-objective">{w.objective}</span>
      <span class="text-ink-2">State</span>
      <span>
        <span class="badge {TONE_CLASS[stateTone(w.state)]}" data-testid="work-state">
          <span class="dot" aria-hidden="true"></span>{stateLabel(w.state)}
        </span>
        {#if w.state === "awaitingapproval"}
          <span class="ml-2 text-xs text-ink-2">a human decision is holding this work</span>
        {/if}
      </span>
      <span class="text-ink-2">Version</span><span class="mono">v{w.version}</span>
      <span class="text-ink-2">Created</span>
      <span class="mono text-xs text-ink-2" title={stamp(w.createdAt)}>{ago(w.createdAt).text}</span>
    </div>

    {#if flow}
      <div class="mb-4 rounded-lg border border-border-primary bg-surface-alt p-3" data-testid="work-flow-strip">
        <div class="flex items-center justify-between gap-2 mb-2">
          <div class="flex items-center gap-2">
            <span class="text-xs font-semibold uppercase tracking-wider text-ink-2">Workflow Instance:</span>
            <a class="mono font-semibold text-accent underline text-xs" href={`#/monitor/runs/${flow.instanceId}`}>
              {flow.definitionId}@{flow.definitionVersion}
            </a>
          </div>
          <div class="flex items-center gap-1.5">
            <span class="badge {TONE_CLASS[flowTone(flow.status)]}">
              <span class="dot" aria-hidden="true"></span>{flow.status}
            </span>
            {#if flow.currentStep}
              <span class="mono text-xs text-ink font-semibold">at {flow.currentStep}</span>
            {/if}
          </div>
        </div>

        <!-- Visual Step Pipeline Stepper -->
        <div class="flex items-center gap-1.5 overflow-x-auto py-1">
          <div class="flex items-center gap-1 text-xs px-2 py-1 rounded bg-surface border border-edge text-ink-2">
            <span class="size-2 rounded-full bg-ok"></span>
            <span>trigger</span>
          </div>
          <span class="text-ink-3 text-xs font-mono">→</span>
          <div class="flex items-center gap-1 text-xs px-2 py-1 rounded bg-surface border border-edge text-ink-2">
            <span class="size-2 rounded-full bg-ok"></span>
            <span>ingress</span>
          </div>
          <span class="text-ink-3 text-xs font-mono">→</span>
          {#if flow.currentStep}
            <div class="flex items-center gap-1.5 text-xs px-2.5 py-1 rounded bg-accent-veil border border-border-primary text-accent font-semibold shadow-sm">
              <span class="size-2 rounded-full bg-accent animate-pulse"></span>
              <span class="mono">{flow.currentStep}</span>
            </div>
            <span class="text-ink-3 text-xs font-mono">→</span>
          {/if}
          <div class="flex items-center gap-1 text-xs px-2 py-1 rounded bg-surface border border-edge text-ink-2 opacity-80">
            <span class="size-2 rounded-full bg-ink-3"></span>
            <span>dispatch</span>
          </div>
          <span class="text-ink-3 text-xs font-mono">→</span>
          <div class="flex items-center gap-1 text-xs px-2 py-1 rounded bg-surface border border-edge text-ink-2 opacity-80">
            <span class="size-2 rounded-full bg-ink-3"></span>
            <span>completion</span>
          </div>
        </div>
      </div>
    {:else}
      <!-- Work Lifecycle Stepper -->
      <div class="mb-4 rounded-lg border border-edge bg-surface-alt p-2.5">
        <div class="flex items-center justify-between mb-1.5">
          <span class="text-[11px] font-semibold uppercase tracking-wider text-ink-2">Activity Lifecycle:</span>
          <span class="badge {TONE_CLASS[stateTone(w.state)]}">
            <span class="dot" aria-hidden="true"></span>{stateLabel(w.state)}
          </span>
        </div>
        <div class="flex items-center gap-1 text-xs overflow-x-auto py-0.5">
          <span class="px-2 py-0.5 rounded bg-surface border border-edge text-ink font-medium">requested</span>
          <span class="text-ink-3 text-xs font-mono">→</span>
          <span class="px-2 py-0.5 rounded {detail.assignments.length > 0 ? 'bg-surface border border-edge text-ink font-medium' : 'text-ink-3'}">assigned</span>
          <span class="text-ink-3 text-xs font-mono">→</span>
          <span class="px-2 py-0.5 rounded {w.state === 'awaitingapproval' ? 'bg-amber-veil border border-border-warning text-amber font-semibold' : 'text-ink-3'}">policy gate</span>
          <span class="text-ink-3 text-xs font-mono">→</span>
          <span class="px-2 py-0.5 rounded {w.state === 'completed' || w.state === 'validated' ? 'bg-ok-veil border border-border-success text-ok font-semibold' : 'text-ink-3'}">resolved</span>
        </div>
      </div>
    {/if}

    {#if detail.assignments.length > 0}
      <h3 class="mb-1 text-xs font-medium uppercase text-ink-2">
        Assignments <span class="normal-case">· {detail.assignments.length}</span>
      </h3>
      <table class="table mb-4">
        <thead><tr><th>Assignment</th><th>Status</th><th>Gen</th><th>Attempts</th></tr></thead>
        <tbody>
          {#each detail.assignments as a}
            <tr>
              <td class="mono text-xs" title={a.assignmentId}>{a.assignmentId.slice(0, 8)}</td>
              <td>
                <span class="badge {TONE_CLASS[stateTone(a.status)]}">
                  <span class="dot" aria-hidden="true"></span>{stateLabel(a.status)}
                </span>
              </td>
              <td class="mono">{a.currentExecutionGeneration}</td>
              <td class="mono text-xs">
                {a.attempts.map((at) => `${at.status}@g${at.generation}`).join(", ") || "—"}
              </td>
            </tr>
          {/each}
        </tbody>
      </table>
    {:else}
      <p class="mb-4 text-xs text-ink-2">
        No team has taken this work yet — it is waiting for a gateway with matching capability to
        pick it up.
      </p>
    {/if}

    <h3 class="mb-1 text-xs font-medium uppercase text-ink-2">Approvals</h3>
    {#if detail.approvals.length === 0}
      <p class="mb-4 text-ink-2">No human decision requested on this work.</p>
    {:else}
      <table class="table mb-2" data-testid="approvals">
        <thead>
          <tr>
            <th>Approval</th><th>State</th><th>Action</th>{#if canMutate}<th>Decide</th>{/if}
          </tr>
        </thead>
        <tbody>
          {#each detail.approvals as ap}
            {@const short = ap.approvalId.slice(0, 8)}
            <tr>
              <td class="mono text-xs" title={ap.approvalId}>{short}</td>
              <td data-testid={`approval-state-${short}`}>
                <span class="badge {TONE_CLASS[stateTone(ap.state)]}">
                  <span class="dot" aria-hidden="true"></span>{stateLabel(ap.state)}
                </span>
              </td>
              <td class="mono text-xs" title={ap.actionDigest}>{ap.actionDigest.slice(0, 18)}…</td>
              {#if canMutate}
                <td>
                  {#if ap.state === "pending"}
                    <div class="flex gap-1">
                      <button
                        class="btn btn-primary"
                        disabled={busy}
                        data-testid={`approve-${short}`}
                        onclick={() => respond(ap.approvalId, "approved")}>approve</button
                      >
                      <button
                        class="btn btn-danger"
                        disabled={busy}
                        data-testid={`deny-${short}`}
                        onclick={() => respond(ap.approvalId, "denied")}>deny</button
                      >
                    </div>
                  {:else}
                    <span class="text-ink-2" title={stamp(ap.decidedAt)}
                      >{ap.decidedAt ? ago(ap.decidedAt).text : "—"}</span
                    >
                  {/if}
                </td>
              {/if}
            </tr>
          {/each}
        </tbody>
      </table>
      {#if canMutate}
        <input
          class="input mb-4 max-w-md"
          placeholder="reason for the decision (stored on the timeline)"
          bind:value={comment}
          aria-label="approval comment" />
      {/if}
    {/if}

    {#if detail.manifests.length > 0}
      <h3 class="mb-1 text-xs font-medium uppercase text-ink-2">
        Results <span class="normal-case">· {detail.manifests.length}</span>
      </h3>
      <table class="table mb-4">
        <thead>
          <tr>
            <th>Manifest</th><th>Rev</th><th>Result</th><th>Artifact</th
            >{#if canMutate}<th>Re-mark</th>{/if}
          </tr>
        </thead>
        <tbody>
          {#each detail.manifests as m}
            {@const short = m.manifestId.slice(0, 12)}
            <tr>
              <td class="mono text-xs" title={m.manifestId}>{short}</td>
              <td class="mono">{m.revision}</td>
              <td data-testid={`manifest-state-${short}`}>
                <span class="badge {TONE_CLASS[stateTone(m.resultState)]}">
                  <span class="dot" aria-hidden="true"></span>{stateLabel(m.resultState)}
                </span>
              </td>
              <td><ArtifactRef artifactId={m.artifactId} /></td>
              {#if canMutate}
                <td>
                  <div class="flex gap-1" role="group" aria-label="re-mark result state">
                    {#each RESULT_STATES as state}
                      <button
                        class="btn {m.resultState === state ? 'btn-primary' : ''}"
                        disabled={busy || m.resultState === state}
                        title={m.resultState === state
                          ? "current state"
                          : `re-mark this result as ${state}`}
                        data-testid={`remark-${short}-${state}`}
                        onclick={() => remark(m.manifestId, state)}>{state}</button
                      >
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
    <p class="text-ink-2">loading…</p>
  {/if}

  {#if timeline}
    <h3 class="mb-1 text-xs font-medium uppercase text-ink-2">
      Activity <span class="normal-case">· {timeline.length} entries, newest last</span>
    </h3>
    <table class="table" data-testid="timeline">
      <thead><tr><th>At</th><th>Kind</th><th>Actor</th><th>Detail</th><th>Trace</th></tr></thead>
      <tbody>
        {#each timeline as row}
          <tr data-testid={`timeline-${row.kind}`}>
            <td class="mono text-xs whitespace-nowrap" title={stamp(row.createdAt)}>
              {ago(row.createdAt).text}
            </td>
            <td>{row.kind}</td>
            <td class="text-xs">{row.actor}</td>
            <td class="text-xs text-ink-2">{row.detail}</td>
            <td>
              {#if row.correlationId}
                <button
                  class="mono badge bg-sunken text-ink-2"
                  title="copy correlation id — every hop of this decision shares it"
                  data-testid="trace-chip"
                  onclick={() => void navigator.clipboard.writeText(row.correlationId ?? "")}
                  >{row.correlationId.slice(0, 8)}…</button
                >
              {:else}
                <span class="text-ink-2">—</span>
              {/if}
              {#if row.traceId}
                <button
                  class="mono badge bg-sunken text-ink-2"
                  title="copy OTel trace id — paste it into the OTLP viewer to see this span"
                  data-testid="trace-chip-trace"
                  onclick={() => void navigator.clipboard.writeText(row.traceId ?? "")}
                  >{row.traceId.slice(0, 8)}…</button
                >
              {/if}
            </td>
          </tr>
        {/each}
      </tbody>
    </table>
  {/if}
</section>
