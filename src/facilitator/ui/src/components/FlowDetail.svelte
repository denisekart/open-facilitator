<script lang="ts">
  import { ApiError, getJson, toApiError } from "../lib/api.js";
  import { apiFetch, session } from "../lib/auth.svelte.js";
  import { go } from "../lib/route.svelte.js";
  import { ago, flowTone, stamp, TONE_CLASS } from "../lib/status.js";
  import type { WorkflowInstanceDetail, WorkflowStepLogRow } from "../lib/types.js";
  import ProblemNote from "./ProblemNote.svelte";

  // One instance as the vertical checklist it is: step-log rows oldest
  // first, the lineage around it (ancestors up, children down), and the
  // operator's cancel. Read-only except that one action.

  let { instanceId, onclose }: { instanceId: string; onclose: () => void } = $props();

  let detail = $state<WorkflowInstanceDetail | null>(null);
  let log = $state<WorkflowStepLogRow[] | null>(null);
  let error = $state<ApiError | null>(null);
  let notice = $state<string | null>(null);
  let busy = $state(false);

  const canMutate = $derived(session.canMutate);

  const STEP_TONE: Record<string, string> = {
    completed: TONE_CLASS.ok,
    running: TONE_CLASS.progress,
    waiting: TONE_CLASS.wait,
    failed: TONE_CLASS.bad,
    skipped: TONE_CLASS.neutral,
  };

  function stepTone(status: string): string {
    return STEP_TONE[status] ?? TONE_CLASS.neutral;
  }

  async function load(): Promise<void> {
    detail = null;
    log = null;
    error = null;
    try {
      const [d, l] = await Promise.all([
        getJson<WorkflowInstanceDetail>(apiFetch, `/api/admin/workflows/instances/${instanceId}`),
        getJson<{ items: WorkflowStepLogRow[] }>(
          apiFetch,
          `/api/admin/workflows/instances/${instanceId}/log`,
        ),
      ]);
      detail = d;
      log = l.items;
    } catch (err) {
      error = err instanceof ApiError ? err : new ApiError(0, "fetch-failed", String(err), null);
    }
  }

  $effect(() => {
    void load();
  });

  async function cancel(): Promise<void> {
    busy = true;
    notice = null;
    try {
      const response = await apiFetch(`/api/admin/workflows/instances/${instanceId}/cancel`, {
        method: "POST",
        headers: { "X-Change-Reason": "operator cancel from the console" },
      });
      if (!response.ok) {
        const problem = await toApiError(response);
        notice = `${problem.title} (${problem.code})`;
      } else {
        notice = "cancelled (the subtree too)";
        await load();
      }
    } catch (err) {
      error = err instanceof ApiError ? err : new ApiError(0, "fetch-failed", String(err), null);
    } finally {
      busy = false;
    }
  }

  function shortOutput(raw: string | null): string {
    if (!raw) {
      return "";
    }
    const flat = raw.replace(/\s+/g, " ");
    return flat.length > 160 ? `${flat.slice(0, 157)}…` : flat;
  }
</script>

<section class="card" data-testid="flow-detail" aria-label="flow instance">
  <div class="mb-2 flex items-start justify-between gap-2">
    <div>
      <button class="btn btn-ghost btn-xs mb-1" onclick={onclose}>← flows</button>
      {#if detail}
        <h2 class="text-lg font-semibold">
          <span class="font-mono">{detail.definitionId}</span>@{detail.definitionVersion}
          <span class="badge {flowTone(detail.status)} ml-1">
            {detail.status}
            {#if detail.status === "running" || detail.status === "waiting"}
              <span class="ml-1 opacity-70">· {detail.currentStep ?? "start"}</span>
            {:else if detail.endReason}
              <span class="ml-1 opacity-70">· {detail.endReason}</span>
            {/if}
          </span>
        </h2>
        <p class="mono text-[11px] text-ink-3">
          {instanceId} · depth {detail.depth} · started {ago(detail.startedAt).text}
          {#if detail.primaryWorkId}
            · <a class="text-accent underline" href={`#/work/${detail.primaryWorkId}`}>work</a>
          {/if}
        </p>
      {/if}
    </div>
    {#if detail && canMutate && (detail.status === "running" || detail.status === "waiting")}
      <button class="btn btn-sm" data-testid="flow-cancel" disabled={busy} onclick={() => void cancel()}>cancel</button>
    {/if}
  </div>

  {#if notice}
    <p class="badge mb-2 bg-accent-veil text-accent ring-accent/25" data-testid="flow-notice" role="status">{notice}</p>
  {/if}
  {#if error}
    <ProblemNote {error} />
  {/if}

  {#if detail && (detail.ancestors.length > 0 || detail.children.length > 0)}
    <div class="mb-4 text-sm" data-testid="lineage">
      {#if detail.ancestors.length > 0}
        <p class="mb-1 text-ink-3">
          called by:
          {#each detail.ancestors as a, i (a.instanceId)}
            {#if i > 0} →{/if}
            <button
              class="mono text-accent underline"
              onclick={() => go(`/flow/${a.instanceId}`)}>{a.definitionId}</button
            >
          {/each}
        </p>
      {/if}
      {#if detail.children.length > 0}
        <p class="mb-1 text-ink-3">invokes:</p>
        <ul class="ml-4 list-disc">
          {#each detail.children as c (c.instanceId)}
            <li>
              <button class="mono text-accent underline" onclick={() => go(`/flow/${c.instanceId}`)}
                >{c.definitionId}</button
              >
              <span class="badge {flowTone(c.status)} ml-1">{c.status}</span>
            </li>
          {/each}
        </ul>
      {/if}
    </div>
  {/if}

  <div class="overflow-x-auto" data-testid="step-log">
    <table class="table">
      <caption class="caption-bottom pt-2 text-xs text-ink-3">
        Step log · oldest first · what each leg recorded
      </caption>
      <thead>
        <tr>
          <th>#</th>
          <th>step</th>
          <th>status</th>
          <th>recorded</th>
          <th>updated</th>
        </tr>
      </thead>
      <tbody>
        {#each log ?? [] as row (row.seq)}
          <tr>
            <td>{row.seq}</td>
            <td class="font-mono">{row.stepId}</td>
            <td><span class="badge {stepTone(row.status)}">{row.status}</span></td>
            <td class="mono max-w-md truncate text-[11px] text-ink-2" title={row.output ?? ""}>
              {shortOutput(row.output)}
            </td>
            <td title={stamp(row.updatedAt)}>{ago(row.updatedAt).text}</td>
          </tr>
        {:else}
          <tr>
            <td colspan="5" class="text-ink-3">
              {log === null ? "loading…" : "no steps recorded"}
            </td>
          </tr>
        {/each}
      </tbody>
    </table>
  </div>
</section>
