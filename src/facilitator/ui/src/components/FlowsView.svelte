<script lang="ts">
  import { ApiError, getJson } from "../lib/api.js";
  import { apiFetch } from "../lib/auth.svelte.js";
  import { go } from "../lib/route.svelte.js";
  import { ago, flowTone, stamp } from "../lib/status.js";
  import type {
    WorkflowDefinitionRow,
    WorkflowInstanceRow,
  } from "../lib/types.js";
  import ProblemNote from "./ProblemNote.svelte";

  // Read-only window onto the workflow plane: what definitions exist (and
  // whether they're live, shadow, or off), and the instances they've run —
  // each a vertical checklist one click away. Nothing here mutates: the
  // editor is a separate stage.

  let definitions = $state<WorkflowDefinitionRow[]>([]);
  let instances = $state<WorkflowInstanceRow[]>([]);
  let error = $state<ApiError | null>(null);
  let onlyDef = $state<string | null>(null);

  async function load(): Promise<void> {
    try {
      const query = onlyDef ? `?def=${encodeURIComponent(onlyDef)}` : "";
      const [defs, insts] = await Promise.all([
        getJson<WorkflowDefinitionRow[]>(apiFetch, "/api/admin/workflows/definitions"),
        getJson<{ items: WorkflowInstanceRow[] }>(
          apiFetch,
          `/api/admin/workflows/instances${query}`,
        ),
      ]);
      definitions = defs;
      instances = insts.items;
      error = null;
    } catch (err) {
      error = err instanceof ApiError ? err : new ApiError(0, "fetch-failed", String(err), null);
    }
  }

  $effect(() => {
    void load();
  });
</script>

<section class="card" data-testid="flows-view" aria-label="flows">
  <h2 class="mb-1 text-lg font-semibold">Flows</h2>
  <p class="mb-4 text-sm text-ink-3">
    The workflow plane, read-only: every definition (latest version) and every instance it ran.
  </p>

  {#if error}
    <ProblemNote {error} />
  {/if}

  <div class="mb-4 overflow-x-auto">
    <table class="table">
      <caption class="caption-bottom pt-2 text-xs text-ink-3">
        Definitions · clicking one filters the instance list
      </caption>
      <thead>
        <tr>
          <th>id</th>
          <th>v</th>
          <th>title</th>
          <th>state</th>
          <th>team</th>
          <th>running</th>
        </tr>
      </thead>
      <tbody>
        {#each definitions as d (d.id)}
          <tr
            class={onlyDef === d.id ? "bg-accent-veil/40" : ""}
            class:cursor-pointer={onlyDef !== d.id}
            onclick={() => {
              onlyDef = onlyDef === d.id ? null : d.id;
            }}
            onkeydown={(ev) => ev.key === "Enter" && (onlyDef = d.id)}
            role={onlyDef === d.id ? undefined : "button"}
            tabindex={onlyDef === d.id ? undefined : 0}
          >
            <td class="font-mono">{d.id}</td>
            <td>{d.version}</td>
            <td>{d.title}</td>
            <td>
              {#if !d.enabled}
                <span class="badge bg-sunken text-ink-2 ring-edge">off</span>
              {:else if d.shadow}
                <span class="badge bg-amber-veil text-amber ring-amber/25">shadow</span>
              {:else}
                <span class="badge bg-ok-veil text-ok ring-ok/25">live</span>
              {/if}
            </td>
            <td>{d.team ?? "—"}</td>
            <td>{d.instancesRunning}</td>
          </tr>
        {/each}
      </tbody>
    </table>
  </div>

  {#if onlyDef}
    <p class="mb-2 text-sm">
      filtered to <span class="font-mono">{onlyDef}</span>
      <button class="btn btn-ghost btn-xs" onclick={() => (onlyDef = null)}>clear</button>
    </p>
  {/if}

  <div class="overflow-x-auto" data-testid="instance-list">
    <table class="table">
      <caption class="caption-bottom pt-2 text-xs text-ink-3">
        Instances · newest first · open one for its step-by-step checklist
      </caption>
      <thead>
        <tr>
          <th>started</th>
          <th>definition</th>
          <th>status</th>
          <th>current step</th>
          <th>depth</th>
          <th>work</th>
        </tr>
      </thead>
      <tbody>
        {#each instances as row (row.instanceId)}
          <tr
            class="cursor-pointer"
            role="button"
            tabindex="0"
            onclick={() => go(`/flow/${row.instanceId}`)}
            onkeydown={(ev) => ev.key === "Enter" && go(`/flow/${row.instanceId}`)}
          >
            <td title={stamp(row.startedAt)}>{ago(row.startedAt)}</td>
            <td class="font-mono">{row.definitionId}@{row.definitionVersion}</td>
            <td>
              <span class="badge {flowTone(row.status)}">
                {row.status}
                {#if row.status === "running" || row.status === "waiting"}
                  <span class="ml-1 opacity-70">· {row.currentStep ?? "start"}</span>
                {:else if row.endReason}
                  <span class="ml-1 opacity-70">· {row.endReason}</span>
                {/if}
              </span>
            </td>
            <td class="font-mono">{row.currentStep ?? "—"}</td>
            <td>{row.depth}</td>
            <td>
              {#if row.primaryWorkId}
                <a
                  class="mono text-accent underline"
                  href={`#/work/${row.primaryWorkId}`}
                  onclick={(ev) => ev.stopPropagation()}
                >{row.primaryWorkId.slice(0, 8)}</a
                >
              {:else}
                —
              {/if}
            </td>
          </tr>
        {:else}
          <tr>
            <td colspan="6" class="text-ink-3">no instances yet</td>
          </tr>
        {/each}
      </tbody>
    </table>
  </div>
</section>
