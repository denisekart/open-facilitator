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

  interface ResourceMeta {
    resource: string;
    label: string;
    description: string;
    fieldNotes: Record<string, string>;
  }

  interface CategoryDef {
    id: string;
    title: string;
    description: string;
    resources: ResourceMeta[];
  }

  const CONFIG_CATEGORIES: CategoryDef[] = [
    {
      id: "operational",
      title: "Operational & Delivery Policies",
      description: "Tuning for outbox delivery pumps, dispatch retries, stale step detection, and worker autoscaling.",
      resources: [
        {
          resource: "retry-policy",
          label: "Retry & Backoff Policy",
          description: "Controls maximum delivery attempts and exponential backoff ceiling before routing to the dead-letter queue.",
          fieldNotes: {
            maxAttempts: "Maximum retry attempts before marking delivery as dead letter (1–20)",
            backoffCapSeconds: "Maximum exponential backoff delay in seconds (1–3600s)",
          },
        },
        {
          resource: "nudge-policy",
          label: "Activity Stale Detection",
          description: "Defines the duration of inactivity before an unacknowledged work step or proposal is flagged as stale.",
          fieldNotes: {
            staleAfterMinutes: "Threshold in minutes before inactive activity steps trigger an automated nudge (1–1440m)",
          },
        },
        {
          resource: "workload-scale",
          label: "Worker Concurrency & Scaling",
          description: "Sets execution concurrency limits and idle worker shutdown thresholds across execution environments.",
          fieldNotes: {
            minWorkers: "Minimum standby worker instances (0–8)",
            maxWorkers: "Maximum concurrent worker instances (1–32)",
            idleShutdownMinutes: "Idle worker lifespan before automatic scale-down (1–1440m)",
          },
        },
      ],
    },
    {
      id: "routing",
      title: "Routing & Operator Feedback",
      description: "Defaults for activity provider selection, preferred gateway teams, and operator notification verbosity.",
      resources: [
        {
          resource: "routing-defaults",
          label: "Dispatch & Provider Routing",
          description: "Default execution model (deterministic rules vs autonomous model reasoning) and team affinity.",
          fieldNotes: {
            provider: "Default workload executor model: deterministic engine or model-driven agent",
            preferTeam: "Default target team slug for unassigned or general workloads",
          },
        },
        {
          resource: "notifications",
          label: "Notifications & Progress",
          description: "Format and verbosity for feedback messages sent back to chat and webhook adapters.",
          fieldNotes: {
            replyVsReaction: "Operator notification mode: post thread replies or lightweight emoji reactions",
            progressVerbosity: "Granularity of step progress events (minimal, normal, or verbose)",
          },
        },
      ],
    },
    {
      id: "intelligence",
      title: "Assistant Runtime & Intelligence",
      description: "Hot policies for the operator assistant (LLM reasoning mode, token budget) and custom slash commands.",
      resources: [
        {
          resource: "llm",
          label: "Assistant LLM Policy",
          description: "Controls autonomous decision modes, response verbosity, endpoint routing, and max output tokens.",
          fieldNotes: {
            enabled: "Enable or disable operator assistant reasoning (absent/false = inert)",
            verbosityDefault: "Default assistant detail level (terse, balanced, proactive)",
            replyMode: "Response generation method (model-generated or deterministic template)",
            decisionEndpoint: "Dedicated inference endpoint for autonomous policy decisions",
            decisionMode: "Autonomous decision mode (auto, llm, or off)",
            maxOutputTokens: "Maximum output token budget per response (40–4000)",
          },
        },
        {
          resource: "commands",
          label: "Custom Slash Commands",
          description: "Custom slash command registry verbs merged over built-in kernel commands.",
          fieldNotes: {},
        },
      ],
    },
  ];

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
  function getRow(resource: string): AdminConfigListRow | undefined {
    return listing.find((r) => r.resource === resource);
  }
</script>

<section class="card flex flex-col gap-6" data-testid="config-view">
  <div class="flex items-center justify-between border-b border-edge pb-3">
    <div>
      <h2 class="text-base font-semibold text-ink">
        Configuration
        <span class="ml-1 text-xs font-normal text-ink-2"
          >— runtime policy resources, operator intents, and change history</span
        >
      </h2>
      <p class="text-xs text-ink-2 mt-0.5">
        Scrollable configuration console organized by operational domain. Click any policy card to edit values.
      </p>
    </div>
    {#if open}
      <button class="btn" data-testid="config-close" onclick={() => (open = null)}>close editor</button>
    {/if}
  </div>

  <!-- All Policies Rendered by Category on a Single Scrollable Page -->
  {#each CONFIG_CATEGORIES as category, catIndex (category.id)}
    {#if catIndex > 0}
      <hr class="border-edge my-1" />
    {/if}

    <div class="flex flex-col gap-3">
      <div>
        <h3 class="text-xs font-semibold uppercase tracking-wider text-accent">{category.title}</h3>
        <p class="text-xs text-ink-2">{category.description}</p>
      </div>

      <div class="grid gap-3 sm:grid-cols-2 lg:grid-cols-3">
        {#each category.resources as meta (meta.resource)}
          {@const row = getRow(meta.resource)}
          {@const spec = CONFIG_RESOURCES.find((r) => r.resource === meta.resource)}
          {@const isOpen = open?.resource === meta.resource}

          <div
            class="rounded border transition-all p-3 flex flex-col justify-between {isOpen
              ? 'border-accent bg-surface-raise ring-1 ring-accent'
              : 'border-edge bg-surface hover:border-line'}"
          >
            <div>
              <div class="flex items-start justify-between gap-2 mb-1.5">
                <div>
                  <h4 class="text-xs font-semibold text-ink">{meta.label}</h4>
                  <span class="mono text-[11px] text-ink-3">{meta.resource}</span>
                </div>
                <span class="badge {row?.exists ? 'bg-ok-veil text-ok' : 'bg-sunken text-ink-2'}">
                  {row?.exists ? `v${row.rowVersion}` : "never written"}
                </span>
              </div>
              <p class="text-xs text-ink-2 mb-3">{meta.description}</p>
            </div>

            <button
              class="chip {isOpen ? 'chip-active' : ''} self-start text-xs font-semibold"
              data-testid={`config-open-${meta.resource}`}
              onclick={() => {
                if (spec) {
                  if (isOpen) {
                    open = null;
                  } else {
                    void openResource(spec);
                  }
                }
              }}>
              {isOpen ? "Editing" : "Configure & History →"}
            </button>
          </div>
        {/each}
      </div>

      <!-- If a resource in this category is open, render its inline editor right here -->
      {#if open && category.resources.some((r) => r.resource === open?.resource) && loaded}
        {@const currentMeta = category.resources.find((r) => r.resource === open?.resource)}
        <div class="mt-2 rounded-lg border border-border-primary bg-surface-alt p-4 shadow-sm">
          <div class="flex items-baseline justify-between mb-2">
            <div>
              <h4 class="font-semibold text-sm text-ink">{currentMeta?.label ?? open.resource}</h4>
              <p class="text-xs text-ink-2">
                {view
                  ? `Stored v${view.rowVersion} · updated by ${view.author} (${view.reason})`
                  : "Never written — first save creates v1 with audited intent"}
              </p>
            </div>
            <button class="btn text-xs h-7 min-h-7" onclick={() => (open = null)}>dismiss</button>
          </div>

          <ProblemNote error={saveError} />
          {#if conflict}
            <p class="badge mb-2 bg-amber-veil text-amber" data-testid="config-conflict">
              {describeConflict(conflict)}
            </p>
          {/if}
          {#if notice}
            <p class="badge mb-2 bg-ok-veil text-ok" data-testid="config-notice">{notice}</p>
          {/if}

          <div class="my-3 grid gap-3 sm:grid-cols-2">
            {#each open.fields as field}
              <div>
                <label class="label mb-1" for={`f-${field.name}`}>
                  {field.name}
                  {#if field.kind === "int"}<span class="text-ink-2">[{field.min}..{field.max}]</span>{/if}
                </label>
                {#if currentMeta?.fieldNotes[field.name]}
                  <p class="text-[11px] text-ink-2 mb-1">{currentMeta.fieldNotes[field.name]}</p>
                {/if}

                {#if field.kind === "enum"}
                  <select
                    class="input"
                    id={`f-${field.name}`}
                    data-testid={`field-${field.name}`}
                    bind:value={form[field.name]}>
                    <option value="">(unset)</option>
                    {#each field.values as value}
                      <option {value}>{value}</option>
                    {/each}
                  </select>
                {:else if field.kind === "bool"}
                  <select
                    class="input"
                    id={`f-${field.name}`}
                    data-testid={`field-${field.name}`}
                    bind:value={form[field.name]}>
                    <option value="true">true (enabled)</option>
                    <option value="false">false (disabled)</option>
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
              <label class="label mb-1" for="cfg-reason">Change reason (audited requirement)</label>
              <input
                class="input"
                id="cfg-reason"
                placeholder="e.g. increase retry ceiling for high-latency child links"
                data-testid="config-reason"
                bind:value={reason} />
            </div>
          </div>

          {#if canMutate}
            <button
              class="btn btn-primary"
              disabled={busy || !reason.trim()}
              data-testid="config-save"
              onclick={() => void save()}>
              Save policy changes (PATCH)
            </button>
          {/if}

          {#if history.length > 0}
            <div class="mt-4 border-t border-edge pt-3">
              <h5 class="mb-2 text-xs font-semibold uppercase text-ink-2 tracking-wider">
                Audited version history (newest first)
              </h5>
              <div class="overflow-x-auto">
                <table class="table" data-testid="config-history">
                  <thead>
                    <tr><th>V</th><th>Payload</th><th>Author</th><th>Reason</th><th>Updated At</th>{#if canMutate}<th></th>{/if}</tr>
                  </thead>
                  <tbody>
                    {#each history as entry}
                      <tr data-testid={`history-v${entry.version}`}>
                        <td class="mono font-semibold">v{entry.version}</td>
                        <td class="mono text-xs">{JSON.stringify(entry.payload)}</td>
                        <td>{entry.author}</td>
                        <td class="text-xs text-ink-2">{entry.reason}</td>
                        <td class="mono text-xs text-ink-2">{stamp(entry.updatedAt)}</td>
                        {#if canMutate}
                          <td>
                            <button
                              class="btn text-xs py-0.5 h-6 min-h-6"
                              data-testid={`rollback-v${entry.version}`}
                              onclick={() => void save(entry.payload)}>rollback</button>
                          </td>
                        {/if}
                      </tr>
                    {/each}
                  </tbody>
                </table>
              </div>
            </div>
          {/if}
        </div>
      {/if}
    </div>
  {/each}
</section>
