<script lang="ts">
  import { ApiError, getJson, toApiError } from "../lib/api.js";
  import { apiFetch, session } from "../lib/auth.svelte.js";
  import {
    CONFIG_RESOURCES,
    describeConflict,
    localValidationError,
    type ConflictState,
    type ResourceSpec,
  } from "../lib/config-shapes.js";
  import type {
    AdminConfigHistoryEntry,
    AdminConfigListRow,
    AdminConfigView,
  } from "../lib/types.js";
  import ProblemNote from "./ProblemNote.svelte";

  let listing = $state<AdminConfigListRow[]>([]);
  let open = $state<ResourceSpec | null>(null);
  let view = $state<AdminConfigView | null>(null);
  let form = $state<Record<string, string>>({});
  let reason = $state("");
  let saveError = $state<ApiError | string | null>(null);
  let conflict = $state<ConflictState | null>(null);
  let history = $state<AdminConfigHistoryEntry[]>([]);
  let notice = $state<string | null>(null);
  let busy = $state(false);
  // Form fields render only once the open resource's GET settled: filling a
  // field before `form` initialises would silently clobber the typed value.
  let loaded = $state(false);

  const canMutate = $derived(session.canMutate);

  async function refreshList(): Promise<void> {
    try {
      listing = await getJson<AdminConfigListRow[]>(apiFetch, "/api/admin/config");
    } catch (err) {
      saveError = err instanceof ApiError ? err : "list-failed";
    }
  }

  $effect(() => {
    void refreshList();
  });

  async function openResource(spec: ResourceSpec): Promise<void> {
    open = spec;
    loaded = false;
    view = null;
    conflict = null;
    notice = null;
    saveError = null;
    history = [];
    try {
      view = await getJson<AdminConfigView>(apiFetch, `/api/admin/config/${spec.resource}`);
      form = payloadToForm(view.payload);
    } catch (err) {
      if (err instanceof ApiError && err.code === "config-not-set") {
        form = {};
      } else {
        saveError = err instanceof ApiError ? err : "load-failed";
      }
    }
    loaded = true;
    try {
      history = await getJson<AdminConfigHistoryEntry[]>(
        apiFetch,
        `/api/admin/config/${spec.resource}/history`,
      );
    } catch {
      history = [];
    }
  }

  function payloadToForm(payload: Record<string, unknown>): Record<string, string> {
    const formValues: Record<string, string> = {};
    for (const [key, value] of Object.entries(payload)) {
      formValues[key] = String(value);
    }
    return formValues;
  }

  function buildPayload(spec: ResourceSpec): Record<string, unknown> {
    const payload: Record<string, unknown> = {};
    for (const field of spec.fields) {
      const raw = form[field.name];
      if (raw === undefined || raw === "") {
        continue;
      }
      payload[field.name] = field.kind === "int" ? Number.parseInt(raw, 10) : raw;
    }
    return payload;
  }

  async function save(preferred?: Record<string, unknown>): Promise<void> {
    if (!open || !reason.trim()) {
      return;
    }
    busy = true;
    conflict = null;
    notice = null;
    saveError = null;
    const payload = preferred ?? buildPayload(open);
    const shapeProblem = localValidationError(open, payload);
    if (shapeProblem) {
      saveError = shapeProblem;
      busy = false;
      return;
    }
    const etag = preferred ? "*" : view ? `"${view.rowVersion}"` : "*";
    try {
      const response = await apiFetch(`/api/admin/config/${open.resource}`, {
        method: "PATCH",
        headers: {
          "Content-Type": "application/json",
          "If-Match": etag,
          "X-Change-Reason": reason.trim(),
        },
        body: JSON.stringify(payload),
      });
      if (response.ok) {
        const stored = (await response.json()) as AdminConfigView;
        view = stored;
        form = payloadToForm(stored.payload);
        notice = `stored v${stored.rowVersion}`;
        await Promise.all([refreshList(), refreshHistory()]);
      } else {
        const problem = await toApiError(response);
        if (problem.status === 412 || problem.code === "config-version-conflict") {
          // Reload the current row and explain the collision.
          try {
            view = await getJson<AdminConfigView>(apiFetch, `/api/admin/config/${open.resource}`);
            conflict = {
              kind: "conflict",
              author: view ? view.author : "?",
              reason: problem.code,
              rowVersion: view ? view.rowVersion : 0,
            };
          } catch {
            conflict = { kind: "conflict", author: "?", reason: problem.code, rowVersion: 0 };
          }
        } else {
          saveError = problem;
        }
      }
    } catch (err) {
      saveError = err instanceof ApiError ? err : "save-failed";
    } finally {
      busy = false;
    }
  }

  async function refreshHistory(): Promise<void> {
    if (!open) {
      return;
    }
    try {
      history = await getJson<AdminConfigHistoryEntry[]>(
        apiFetch,
        `/api/admin/config/${open.resource}/history`,
      );
    } catch {
      /* history is advisory */
    }
  }

  function stamp(when: string): string {
    return when.replace("T", " ").replace(/\.\d+Z$/, "Z");
  }
</script>

<section class="card" data-testid="config-view">
  <div class="mb-3 flex items-center justify-between">
    <h2 class="text-sm font-medium">Config (stored intent — read by features that opt in)</h2>
    <button class="btn" data-testid="config-close" onclick={() => (open = null)}>close</button>
  </div>

  <ul class="mb-4 flex flex-wrap gap-2">
    {#each listing as row}
      <li>
        <button
          class="chip"
          data-testid={`config-open-${row.resource}`}
          onclick={() => {
            const spec = CONFIG_RESOURCES.find((r) => r.resource === row.resource);
            if (spec) {
              void openResource(spec);
            }
          }}>
          {row.resource} {row.exists ? `v${row.rowVersion}` : "(never written)"}
        </button>
      </li>
    {/each}
  </ul>

  {#if open && loaded}
    <div class="rounded-lg border border-edge bg-raised p-4">
      <h3 class="mb-1 font-medium">{open.resource}</h3>
      <p class="mb-3 text-xs text-ink-3">
        {view ? `stored v${view.rowVersion} · ${view.author} · ${view.reason}` : "never written — first save creates v1"}
      </p>
      <ProblemNote error={saveError} />
      {#if conflict}
        <p class="badge mb-2 bg-warn/15 text-warn" data-testid="config-conflict">{describeConflict(conflict)}</p>
      {/if}
      {#if notice}
        <p class="badge mb-2 bg-ok/15 text-ok" data-testid="config-notice">{notice}</p>
      {/if}

      <div class="mb-3 grid gap-3 sm:grid-cols-2">
        {#each open.fields as field}
          <div>
            <label class="label" for={`f-${field.name}`}>
              {field.name}
              {#if field.kind === "int"}<span class="text-ink-3">[{field.min}..{field.max}]</span>{/if}
            </label>
            {#if field.kind === "enum"}
              <select class="input" id={`f-${field.name}`} data-testid={`field-${field.name}`} bind:value={form[field.name]}>
                <option value="">(unset)</option>
                {#each field.values as value}
                  <option {value}>{value}</option>
                {/each}
              </select>
            {:else}
              <input
                class="input"
                id={`f-${field.name}`}
                type={field.kind === "int" ? "number" : "text"}
                min={field.kind === "int" ? field.min : undefined}
                max={field.kind === "int" ? field.max : undefined}
                data-testid={`field-${field.name}`}
                bind:value={form[field.name]} />
            {/if}
          </div>
        {/each}
        <div class="sm:col-span-2">
          <label class="label" for="cfg-reason">change reason (required)</label>
          <input class="input" id="cfg-reason" data-testid="config-reason" bind:value={reason} />
        </div>
      </div>
      {#if canMutate}
        <button
          class="btn btn-primary"
          disabled={busy || !reason.trim()}
          data-testid="config-save"
          onclick={() => void save()}>save (PATCH If-Match)</button>
      {/if}

      {#if history.length > 0}
        <h4 class="mb-1 mt-4 text-xs font-medium uppercase text-ink-3">History (newest first, rollback = re-PATCH)</h4>
        <table class="table" data-testid="config-history">
          <thead><tr><th>V</th><th>Payload</th><th>Author</th><th>Reason</th><th>At</th>{#if canMutate}<th></th>{/if}</tr></thead>
          <tbody>
            {#each history as entry}
              <tr data-testid={`history-v${entry.version}`}>
                <td class="mono">{entry.version}</td>
                <td class="mono text-xs">{JSON.stringify(entry.payload)}</td>
                <td>{entry.author}</td>
                <td class="text-xs">{entry.reason}</td>
                <td class="mono text-xs">{stamp(entry.updatedAt)}</td>
                {#if canMutate}
                  <td>
                    <button
                      class="btn"
                      data-testid={`rollback-v${entry.version}`}
                      onclick={() => void save(entry.payload)}>rollback</button>
                  </td>
                {/if}
              </tr>
            {/each}
          </tbody>
        </table>
      {/if}
    </div>
  {/if}
</section>
