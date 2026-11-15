<script lang="ts">
  import { ApiError, getJson, toApiError } from "../lib/api.js";
  import { apiFetch, session } from "../lib/auth.svelte.js";
  import { go } from "../lib/route.svelte.js";
  import { ago, flowTone, stamp, TONE_CLASS } from "../lib/status.js";
  import type { WorkflowInstanceDetail, WorkflowStepLogRow } from "../lib/types.js";
  import FlowCanvas from "./FlowCanvas.svelte";
  import ProblemNote from "./ProblemNote.svelte";

  // One instance as the vertical checklist it is: step-log rows oldest
  // first, the lineage around it (ancestors up, children down), and the
  // operator's park: cancel, pause/resume, skip-step, retry-step.

  let { instanceId, onclose }: { instanceId: string; onclose: () => void } = $props();

  let detail = $state<WorkflowInstanceDetail | null>(null);
  let log = $state<WorkflowStepLogRow[] | null>(null);
  let definitionDoc = $state<Record<string, unknown> | null>(null);
  let picked = $state<string | null>(null);
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
      try {
        const view = await getJson<{ document: Record<string, unknown> }>(
          apiFetch,
          `/api/admin/workflows/definitions/${encodeURIComponent(d.definitionId)}?version=${d.definitionVersion}`,
        );
        definitionDoc = view.document;
      } catch {
        definitionDoc = null; // the pinned version was pruned: table-only view
      }
    } catch (err) {
      error = err instanceof ApiError ? err : new ApiError(0, "fetch-failed", String(err), null);
    }
  }

  $effect(() => {
    void load();
  });

  // The operator's park: pause/resume/skip/retry, each fenced by the
  // runner's status law (409 comes back as an honest notice, never a
  // silent no-op). Every one rides a change reason like cancel does.
  async function act(name: string, body?: Record<string, string>): Promise<void> {
    busy = true;
    notice = null;
    try {
      const response = await apiFetch(`/api/admin/workflows/instances/${instanceId}/${name}`, {
        method: "POST",
        headers: body
          ? { "X-Change-Reason": `operator ${name} from the console`, "Content-Type": "application/json" }
          : { "X-Change-Reason": `operator ${name} from the console` },
        ...(body ? { body: JSON.stringify(body) } : {}),
      });
      if (!response.ok) {
        const problem = await toApiError(response);
        notice = `${problem.title} (${problem.code})`;
      } else {
        notice = name;
        await load();
      }
    } catch (err) {
      error = err instanceof ApiError ? err : new ApiError(0, "fetch-failed", String(err), null);
    } finally {
      busy = false;
    }
  }

  const failedSteps = $derived(
    (log ?? []).filter((l) => l.status === "failed").map((l) => l.stepId),
  );

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
      <button class="btn btn-ghost btn-xs mb-1" onclick={onclose}>← runs</button>
      {#if detail}
        <a
          class="mono ml-2 text-xs text-accent underline"
          data-testid="detail-to-editor"
          href={`#/flows/${detail.definitionId}`}>edit definition</a
        >
        <h2 class="text-xl font-semibold">
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
        <p class="mono text-xs text-ink-3">
          {instanceId} · depth {detail.depth} · started {ago(detail.startedAt).text}
          {#if detail.primaryWorkId}
            · <a class="text-accent underline" href={`#/work/${detail.primaryWorkId}`}>work</a>
          {/if}
        </p>
      {/if}
    </div>
    {#if detail && canMutate}
      <div class="flex gap-2">
        {#if detail.status === "running" || detail.status === "waiting"}
          <button class="btn btn-sm" data-testid="flow-pause" disabled={busy} onclick={() => void act("pause")}>pause</button>
          <button class="btn btn-sm" data-testid="flow-skip" disabled={busy} onclick={() => void act("skip-step")}>skip step</button>
          <button class="btn btn-sm" data-testid="flow-cancel" disabled={busy} onclick={() => void act("cancel")}>cancel</button>
        {:else if detail.status === "paused"}
          <button class="btn btn-sm" data-testid="flow-resume" disabled={busy} onclick={() => void act("resume")}>resume</button>
          <button class="btn btn-sm" data-testid="flow-skip" disabled={busy} onclick={() => void act("skip-step")}>skip step</button>
        {:else if detail.status === "failed" && failedSteps.length > 0}
          <button
            class="btn btn-sm"
            data-testid="flow-retry"
            disabled={busy}
            onclick={() => void act("retry-step", { stepId: failedSteps[failedSteps.length - 1] })}
          >retry '{failedSteps[failedSteps.length - 1]}'</button>
        {/if}
      </div>
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
              onclick={() => go(`#/monitor/runs/${a.instanceId}`)}>{a.definitionId}</button
            >
          {/each}
        </p>
      {/if}
      {#if detail.children.length > 0}
        <p class="mb-1 text-ink-3">invokes:</p>
        <ul class="ml-4 list-disc">
          {#each detail.children as c (c.instanceId)}
            <li>
              <button class="mono text-accent underline" onclick={() => go(`#/monitor/runs/${c.instanceId}`)}
                >{c.definitionId}</button
              >
              <span class="badge {flowTone(c.status)} ml-1">{c.status}</span>
            </li>
          {/each}
        </ul>
      {/if}
    </div>
  {/if}

  {#if definitionDoc}
    <div class="mb-4">
      <FlowCanvas
        doc={definitionDoc}
        log={log?.map((l) => ({ stepId: l.stepId, status: l.status, output: l.output })) ?? null}
        selected={picked}
        instanceStatus={detail?.status ?? null}
        currentStep={detail?.currentStep ?? null}
        onselect={(id) => (picked = id)}
      />
      {#if picked}
        {@const legs = (log ?? []).filter((l) => l.stepId === picked)}
        {@const last = legs[legs.length - 1]}
        <div class="mt-1 rounded border border-edge bg-sunken/40 p-2 text-sm" data-testid="graph-inspect">
          <span class="font-mono">{picked}</span>
          {#if last}
            · leg {last.seq} · <span class="badge {STEP_TONE[last.status] ?? TONE_CLASS.neutral}">{last.status}</span>
            {#if last.output}
              <pre class="mono mt-1 max-h-40 overflow-auto whitespace-pre-wrap text-xs text-ink-2">{shortOutput(last.output)}</pre>
            {/if}
          {:else}
            · no leg recorded yet
          {/if}
        </div>
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
            <td class="mono max-w-md truncate text-xs text-ink-2" title={row.output ?? ""}>
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
