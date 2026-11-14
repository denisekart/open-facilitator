<script lang="ts">
  import { ApiError, getJson, toApiError } from "../lib/api.js";
  import { apiFetch, session } from "../lib/auth.svelte.js";
  import type { WorkflowRegistry } from "../lib/types.js";
  import FlowGraph from "./FlowGraph.svelte";

  // Form-first definition editor over the frozen palette. Every picker is
  // SELECT-only, populated from GET /api/admin/workflows/registry — the
  // browser never invents an event type, command, capability, or callable.
  // Kinds the runtime does not execute yet render as a labelled payload
  // face, never a dead dropdown. The YAML textarea is the alternate face:
  // it validates before it may save, and save always lands a NEW version.

  let { defId, clone = false, onclose }: { defId: string; clone?: boolean; onclose: () => void } =
    $props();

  type Doc = Record<string, unknown>;
  type StepForm = {
    id: string;
    title: string;
    on: string;
    kind: string;
    payload: Record<string, unknown>;
    raw: string; // JSON face for kinds without a form
    rawError: string | null;
  };

  const EXECUTED = [
    "select",
    "emit",
    "wait",
    "timer",
    "approval",
    "decide",
    "fork-work",
    "work-create",
    "escalate",
    "fan-out",
    "signal",
    "invoke",
  ];
  const PALETTE = [...EXECUTED, "join", "memory"];

  let registry = $state<WorkflowRegistry | null>(null);
  let original = $state<Doc | null>(null); // the stored doc, for the diff
  let digest = $state<string | null>(null); // latest digest = the If-Match etag
  let loadedVersion = $state<number>(0);
  let savedYaml = $state("");

  // svelte-ignore state_referenced_locally -- the editor mounts per route id,
  // so the props are the initial AND only values by construction.
  let id = $state(defId === "new" || clone ? "" : defId);
  let title = $state("");
  let description = $state("");
  let team = $state("");
  let enabled = $state(false);
  let shadow = $state(false);
  let triggerKind = $state<"event" | "schedule" | "invoked">("event");
  let triggerEvent = $state("");
  let triggerWhere = $state<{ key: string; value: string }[]>([]);
  let triggerSchedule = $state("");
  let steps = $state<StepForm[]>([]);
  let composition = $state<Doc | null>(null); // carried through untouched

  let mode = $state<"form" | "yaml">("form");
  let yamlText = $state("");
  let notice = $state<string | null>(null);
  let formError = $state<string | null>(null);
  let validation = $state<{ ok: boolean; errors: { code: string; detail: string }[] } | null>(null);
  let busy = $state(false);

  const canMutate = $derived(session.canMutate);
  const isNew = $derived(original === null);

  const STEP_TITLES: Record<string, string> = {
    select: "project (select)",
    emit: "send a command (emit)",
    wait: "wait for an event (wait)",
    timer: "park on a timer (timer)",
    approval: "ask a human (approval)",
    decide: "branch (decide)",
    "fork-work": "fork the subject work (fork-work)",
    "work-create": "create work (work-create)",
    escalate: "escalate (escalate)",
    "fan-out": "fan out children (fan-out)",
    signal: "signal a waiter (signal)",
    invoke: "call a child flow (invoke)",
    join: "join children (join)",
    memory: "memory (memory)",
  };

  // The editor anchors on a definition id that starts at the route and
  // MOVES once a clone lands: saving a clone re-anchors on the copy, so
  // the form, the etag, and the toggles all speak the new definition.
  // svelte-ignore state_referenced_locally -- props are the initial AND
  // only values: the route remounts the editor on every id change.
  let anchorId = $state(defId);
  // svelte-ignore state_referenced_locally
  let freshId = $state(clone || defId === "new"); // no stored version under `id` yet

  async function boot(): Promise<void> {
    const asClone = clone;
    try {
      registry = await getJson<WorkflowRegistry>(apiFetch, "/api/admin/workflows/registry");
      if (anchorId !== "new") {
        const view = await getJson<{
          summary: { version: number; digest: string; enabled: boolean; shadow: boolean };
          document: Doc;
          yaml: string;
        }>(apiFetch, `/api/admin/workflows/definitions/${encodeURIComponent(anchorId)}`);
        const first = original === null;
        loadDoc(view.document, view.summary.version, view.summary.digest, view.yaml);
        // The live posture is the summary's: a toggle edits the column,
        // the stored payload keeps the posture it was stored with.
        enabled = view.summary.enabled;
        shadow = view.summary.shadow;
        if (asClone && first) {
          // rename exactly once: a re-anchor on the copy must not double-suffix
          id = `${anchorId}-copy`;
          title = `${title} (copy)`;
        }
      }
    } catch (err) {
      formError = err instanceof ApiError ? `${err.title}: ${err.detail}` : String(err);
    }
  }

  function loadDoc(doc: Doc, version: number, docDigest: string, yaml: string): void {
    original = doc;
    loadedVersion = version;
    digest = docDigest;
    savedYaml = yaml;
    yamlText = yaml;
    id = String(doc.id ?? "");
    title = String(doc.title ?? "");
    description = String(doc.description ?? "");
    team = String(doc.team ?? "");
    enabled = doc.enabled === true;
    shadow = doc.shadow === true;
    composition = (doc.composition as Doc) ?? null;
    const on = (doc.on ?? { event: "" }) as Doc;
    if ("event" in on) {
      triggerKind = "event";
      triggerEvent = String(on.event ?? "");
      triggerWhere = Object.entries((on.where ?? {}) as Record<string, string>).map(
        ([key, value]) => ({ key, value: String(value) }),
      );
    } else if ("schedule" in on) {
      triggerKind = "schedule";
      triggerSchedule = String(on.schedule ?? "");
    } else {
      triggerKind = "invoked";
    }
    steps = ((doc.steps ?? []) as Doc[]).map((s) => {
      const kind = PALETTE.find((k) => k in s) ?? "select";
      const payload = { ...((s[kind] ?? {}) as Record<string, unknown>) };
      return {
        id: String(s.id ?? ""),
        title: String(s.title ?? ""),
        on: String(s.on ?? "always"),
        kind,
        payload,
        raw: JSON.stringify(payload, null, 2),
        rawError: null,
      };
    });
  }

  // ---- serialize (form -> document) --------------------------------------

  function parseRaw(step: StepForm): Record<string, unknown> | null {
    try {
      const parsed = JSON.parse(step.raw) as unknown;
      if (parsed === null || typeof parsed !== "object" || Array.isArray(parsed)) {
        return null;
      }
      return parsed as Record<string, unknown>;
    } catch {
      return null;
    }
  }

  function jsonField(step: StepForm, key: string): string {
    const v = step.payload[key];
    return v === undefined ? "" : JSON.stringify(v);
  }

  function serialize(): Doc | null {
    const on: Doc =
      triggerKind === "event"
        ? {
            event: triggerEvent,
            ...(triggerWhere.length > 0
              ? {
                  where: Object.fromEntries(
                    triggerWhere.filter((w) => w.key !== "").map((w) => [w.key, w.value]),
                  ),
                }
              : {}),
          }
        : triggerKind === "schedule"
          ? { schedule: triggerSchedule }
          : { invoked: {} };
    const stepDocs: (Doc | null)[] = steps.map((s) => {
        const payload = EXECUTED.includes(s.kind) ? formPayload(s) : parseRaw(s);
        if (payload === null) {
          return null;
        }
        return { id: s.id, title: s.title, on: s.on, [s.kind]: payload };
      });
    if (stepDocs.some((s) => s === null)) {
      return null;
    }
    const out: Doc = {
      id,
      version: loadedVersion > 0 ? loadedVersion : 1,
      title,
      description,
      enabled,
      on,
      steps: stepDocs as Doc[],
    };
    if (team !== "") {
      out.team = team;
    }
    if (shadow) {
      out.shadow = true;
    }
    if (original && "output" in original) {
      out.output = original.output;
    }
    if (composition) {
      out.composition = composition;
    }
    return out;
  }

  function formPayload(s: StepForm): Record<string, unknown> | null {
    const p = s.payload;
    const keep = (keys: string[]): Record<string, unknown> => {
      const out: Record<string, unknown> = {};
      for (const k of keys) {
        if (p[k] !== undefined) {
          out[k] = p[k];
        }
      }
      return out;
    };
    switch (s.kind) {
      case "emit":
      case "work-create":
      case "invoke":
      case "signal":
      case "fan-out":
      case "approval":
      case "escalate":
      case "fork-work":
      case "wait":
      case "timer":
      case "decide":
        return p; // forms mutate payload in place
      case "select": {
        try {
          const project = JSON.parse(jsonField(s, "project") || "{}") as unknown;
          if (typeof project !== "object" || project === null) {
            return null;
          }
          return { ...keep(["source", "defaults"]), project };
        } catch {
          return null;
        }
      }
      default:
        return p;
    }
  }

  // The picture over the current form: the graph component reads a plain
  // definition document, so the preview re-derives one from the form on
  // every keystroke. Invalid payload faces fall back to their last parse.
  const preview = $derived.by((): Doc => {
    const on: Doc =
      triggerKind === "event"
        ? { event: triggerEvent }
        : triggerKind === "schedule"
          ? { schedule: triggerSchedule }
          : { invoked: {} };
    return {
      id,
      version: loadedVersion > 0 ? loadedVersion : 1,
      title,
      description,
      enabled,
      on,
      steps: steps.map((s) => ({
        id: s.id,
        title: s.title,
        on: s.on,
        [s.kind]: EXECUTED.includes(s.kind) ? s.payload : (parseRaw(s) ?? s.payload),
      })),
    };
  });
  let pickedStep = $state<number | null>(null);

  // ---- diff against the stored doc ---------------------------------------

  const changes = $derived.by(() => {
    if (original === null) {
      return ["new definition (version 1)"];
    }
    const next = serialize();
    if (next === null) {
      return [];
    }
    const paths: string[] = [];
    const walk = (a: unknown, b: unknown, at: string): void => {
      if (JSON.stringify(a) === JSON.stringify(b)) {
        return;
      }
      if (
        a !== null && b !== null && typeof a === "object" && typeof b === "object" &&
        !Array.isArray(a) && !Array.isArray(b)
      ) {
        const ka = a as Doc;
        const kb = b as Doc;
        for (const key of new Set([...Object.keys(ka), ...Object.keys(kb)])) {
          if (key === "version") {
            continue; // the store stamps the version
          }
          walk(ka[key], kb[key], at === "" ? key : `${at}.${key}`);
        }
        return;
      }
      if (Array.isArray(a) && Array.isArray(b) && a.length === b.length) {
        for (let i = 0; i < a.length; i += 1) {
          walk(a[i], b[i], `${at}[${i}]`);
        }
        return;
      }
      paths.push(at);
    };
    walk(original, next, "");
    return paths;
  });

  // ---- actions ------------------------------------------------------------

  function addStep(kind: string): void {
    const n = steps.length + 1;
    steps = [
      ...steps,
      {
        id: `step-${n}`,
        title: STEP_TITLES[kind] ?? kind,
        on: steps.length === 0 ? "always" : `${steps[steps.length - 1]?.id ?? "step-1"}:completed`,
        kind,
        payload: defaultPayload(kind),
        raw: JSON.stringify(defaultPayload(kind), null, 2),
        rawError: null,
      },
    ];
  }

  function defaultPayload(kind: string): Record<string, unknown> {
    switch (kind) {
      case "emit":
        return { command: registry?.commands[0] ?? "", inputs: {} };
      case "wait":
        return {
          event: registry?.eventTypes[0] ?? "",
          correlate: "",
          timeout: "PT30M",
          onTimeout: "end",
        };
      case "timer":
        return { for: "PT30M" };
      case "approval":
        return { question: "approve?", approvers: "role:admin" };
      case "decide":
        return { source: "guard", guard: { field: "work.state", in: ["completed"] }, fallback: "end" };
      case "select":
        return { project: { value: "instance" } };
      case "fork-work":
        return { noteRef: "carried note" };
      case "work-create":
        return { objectiveRef: "new objective", capability: registry?.capabilities[0] ?? "" };
      case "escalate":
        return { to: "$work.owner", message: "escalation" };
      case "fan-out":
        return { over: "$event.data.items", step: { title: "child", emit: { inputs: {} } } };
      case "signal":
        return {
          target: "$event.data.payload.target",
          event: { type: registry?.eventTypes[0] ?? "", data: {} },
        };
      case "invoke":
        return {
          workflow: registry?.callable[0]?.id ?? "",
          await: true,
          timeout: "PT30M",
        };
      default:
        return {};
    }
  }

  function removeStep(index: number): void {
    steps = steps.filter((_, i) => i !== index);
  }

  function moveStep(index: number, delta: number): void {
    const to = index + delta;
    if (to < 0 || to >= steps.length) {
      return;
    }
    const next = [...steps];
    const [moved] = next.splice(index, 1);
    next.splice(to, 0, moved);
    steps = next;
  }

  function stepIds(): string[] {
    return [...steps.map((s) => s.id), "end"];
  }

  async function save(): Promise<void> {
    busy = true;
    notice = null;
    try {
      const targetId = id.trim();
      if (targetId === "") {
        formError = "the definition needs an id";
        return;
      }
      const doc = serialize();
      if (doc === null) {
        formError = "the form holds invalid JSON in a payload face";
        return;
      }
      const headers: Record<string, string> = {
        "Content-Type": "application/json",
        "X-Change-Reason": "flow edited in the console",
      };
      if (digest !== null && !freshId) {
        headers["If-Match"] = digest;
      }
      const response = await apiFetch(`/api/admin/workflows/definitions/${encodeURIComponent(targetId)}`, {
        method: "PUT",
        headers,
        body: JSON.stringify(doc),
      });
      if (!response.ok) {
        const problem = await toApiError(response);
        notice = `${problem.title} (${problem.code}): ${problem.detail}`;
        return;
      }
      const summary = (await response.json()) as { version: number };
      notice = `stored as version ${summary.version}`;
      anchorId = targetId;
      freshId = false;
      await boot(); // re-anchor the form on the stored version
    } catch (err) {
      formError = err instanceof ApiError ? err.detail : String(err);
    } finally {
      busy = false;
    }
  }

  async function validateYaml(): Promise<void> {
    validation = null;
    // The validate face speaks both stored-doc languages verbatim.
    const res = await apiFetch("/api/admin/workflows/definitions/validate", {
      method: "POST",
      headers: { "Content-Type": "text/yaml" },
      body: yamlText,
    });
    validation = (await res.json()) as { ok: boolean; errors: { code: string; detail: string }[] };
  }

  async function saveYaml(): Promise<void> {
    busy = true;
    notice = null;
    try {
      const headers: Record<string, string> = {
        "Content-Type": "application/yaml",
        "X-Change-Reason": "flow edited as YAML in the console",
      };
      if (digest !== null && !freshId) {
        headers["If-Match"] = digest;
      }
      const response = await apiFetch(`/api/admin/workflows/definitions/${encodeURIComponent(id.trim())}`, {
        method: "PUT",
        headers,
        body: yamlText,
      });
      if (!response.ok) {
        const problem = await toApiError(response);
        notice = `${problem.title} (${problem.code}): ${problem.detail}`;
        return;
      }
      const summary = (await response.json()) as { version: number };
      notice = `stored as version ${summary.version}`;
      anchorId = id.trim();
      freshId = false;
      await boot();
    } finally {
      busy = false;
    }
  }

  async function toggle(on: boolean, toShadow: boolean): Promise<void> {
    notice = null;
    const body = on
      ? { enabled: true }
      : toShadow
        ? { shadow: false }
        : { enabled: false };
    const response = await apiFetch(
      `/api/admin/workflows/definitions/${encodeURIComponent(id)}/versions/${loadedVersion}/toggle`,
      {
        method: "POST",
        headers: { "Content-Type": "application/json", "X-Change-Reason": "posture toggle from the console" },
        body: JSON.stringify(body),
      },
    );
    if (!response.ok) {
      const problem = await toApiError(response);
      notice = `${problem.title} (${problem.code})`;
      return;
    }
    notice = on ? "enabled (subscriptions refreshed)" : "posture changed";
    await boot();
  }

  $effect(() => {
    void boot();
  });

  function updatePayload(step: StepForm, key: string, value: unknown): void {
    step.payload = { ...step.payload, [key]: value };
    if (!EXECUTED.includes(step.kind)) {
      step.raw = JSON.stringify(step.payload, null, 2);
    }
  }

  function updateJsonField(step: StepForm, key: string, text: string): void {
    try {
      updatePayload(step, key, text === "" ? undefined : (JSON.parse(text) as unknown));
      step.rawError = null;
    } catch {
      step.rawError = "invalid JSON";
    }
  }
</script>

<section class="card" data-testid="flow-editor" aria-label="flow editor">
  <div class="mb-4 flex items-start justify-between gap-4">
    <div>
      <h2 class="mb-1 text-lg font-semibold">
        {isNew ? "New flow" : `Edit ${defId}`}
        {#if !isNew}<span class="text-sm text-ink-3">· stored v{loadedVersion}</span>{/if}
      </h2>
      <p class="text-sm text-ink-3">
        Every picker is populated from the live registry; a save lands a new version.
      </p>
    </div>
    <button class="btn btn-ghost btn-sm" data-testid="editor-close" onclick={onclose}>close</button>
  </div>

  {#if formError}
    <p class="mb-2 rounded bg-bad-veil px-3 py-2 text-sm text-bad" role="alert">{formError}</p>
  {/if}
  {#if notice}
    <p class="mb-2 rounded bg-ok-veil px-3 py-2 text-sm text-ok" data-testid="editor-notice">{notice}</p>
  {/if}

  <div class="mb-3 flex gap-2" role="tablist">
    <button class="btn btn-sm {mode === 'form' ? 'btn-active' : ''}" onclick={() => (mode = "form")}>
      form
    </button>
    <button class="btn btn-sm {mode === 'yaml' ? 'btn-active' : ''}" onclick={() => (mode = "yaml")}>
      YAML
    </button>
  </div>

  {#if mode === "form"}
    <div class="mb-4 grid gap-3 md:grid-cols-2">
      <label class="field-label"
        >id
        <input
          class="input mono"
          data-testid="editor-id"
          bind:value={id}
          disabled={!isNew && !clone}
          required
        />
      </label>
      <label class="field-label"
        >title
        <input class="input" data-testid="editor-title" bind:value={title} required maxlength="120" />
      </label>
      <label class="field-label md:col-span-2"
        >description
        <textarea class="input" data-testid="editor-description" bind:value={description} rows="2" maxlength="2000"></textarea>
      </label>
      <label class="field-label"
        >team
        <select class="input" data-testid="editor-team" bind:value={team}>
          <option value="">— none —</option>
          {#each registry?.teams ?? [] as slug}
            <option value={slug}>{slug}</option>
          {/each}
        </select>
      </label>
      <div class="flex items-end gap-4 pb-1">
        <label class="flex items-center gap-2 text-sm">
          <input type="checkbox" data-testid="editor-enabled" bind:checked={enabled} />
          enabled
          {#if enabled}<span class="text-xs text-amber">(a save force-enables the new version)</span>{/if}
        </label>
        <label class="flex items-center gap-2 text-sm">
          <input type="checkbox" data-testid="editor-shadow" bind:checked={shadow} />
          shadow
        </label>
      </div>
    </div>

    <fieldset class="mb-4 rounded border border-edge p-3">
      <legend class="px-1 text-sm font-semibold">trigger</legend>
      <div class="mb-2 flex gap-4 text-sm">
        {#each ["event", "schedule", "invoked"] as kind}
          <label class="flex items-center gap-1">
            <input
              type="radio"
              name="trigger-kind"
              value={kind}
              checked={triggerKind === kind}
              onchange={() => (triggerKind = kind as "event" | "schedule" | "invoked")}
              data-testid={`trigger-${kind}`}
            />
            {kind}
          </label>
        {/each}
      </div>
      {#if triggerKind === "event"}
        <label class="field-label mb-2 block"
          >event type
          <select class="input" data-testid="trigger-event" bind:value={triggerEvent}>
            {#each registry?.eventTypes ?? [] as t}
              <option value={t}>{t}</option>
            {/each}
          </select>
        </label>
        {#each triggerWhere as row, i}
          <div class="mb-1 flex gap-2">
            <input class="input mono flex-1" placeholder="data.field" bind:value={row.key} />
            <input class="input flex-1" placeholder="value or *" bind:value={row.value} />
            <button class="btn btn-ghost btn-xs" onclick={() => (triggerWhere = triggerWhere.filter((_, j) => j !== i))}>
              remove
            </button>
          </div>
        {/each}
        <button class="btn btn-ghost btn-xs" onclick={() => (triggerWhere = [...triggerWhere, { key: "", value: "" }])}>
          + where
        </button>
      {:else if triggerKind === "schedule"}
        <label class="field-label block"
          >cadence spec
          <input
            class="input mono"
            data-testid="trigger-schedule"
            bind:value={triggerSchedule}
            placeholder="interval:15m | daily:07:30 | cron:<5 fields>"
          />
        </label>
      {:else}
        <p class="text-sm text-ink-3">callable only: this flow runs when another flow invokes it.</p>
      {/if}
    </fieldset>

    <div class="mb-4">
      <FlowGraph doc={preview} selected={pickedStep === null ? null : (steps[pickedStep]?.id ?? null)}
        onselect={(sid) => {
          const i = steps.findIndex((x) => x.id === sid);
          pickedStep = i >= 0 ? i : null;
        }} />
    </div>

    <div class="mb-2 flex items-center justify-between">
      <h3 class="text-sm font-semibold">steps ({steps.length})</h3>
      <div class="flex gap-2">
        {#each PALETTE as kind}
          {#if !EXECUTED.includes(kind)}
            <button
              class="btn btn-ghost btn-xs"
              disabled
              title={`${kind}: not yet executable`}
              data-testid={`add-${kind}`}
            >+ {kind} (not yet executable)</button>
          {/if}
        {/each}
        {#each EXECUTED as kind}
          <button class="btn btn-ghost btn-xs" onclick={() => addStep(kind)} data-testid={`add-${kind}`}>
            + {kind}
          </button>
        {/each}
      </div>
    </div>

    {#each steps as step, i (i)}
      <div class="mb-3 rounded border border-edge p-3" data-testid={`step-${step.id}`}>
        <div class="mb-2 flex flex-wrap items-center gap-2">
          <span class="badge bg-sunken text-ink-2 ring-edge">{step.kind}</span>
          <input class="input mono w-40" bind:value={step.id} data-testid={`step-id-${i}`} />
          <input class="input flex-1" bind:value={step.title} maxlength="120" />
          <label class="text-sm text-ink-3"
            >on
            <select class="input w-48" bind:value={step.on}>
              {#each ["always", "timeout", "failed"] as fixed}
                <option value={fixed}>{fixed}</option>
              {/each}
              {#each steps.filter((_, j) => j !== i) as other}
                <option value={`${other.id}:completed`}>{other.id}:completed</option>
              {/each}
            </select>
          </label>
          <button class="btn btn-ghost btn-xs" onclick={() => moveStep(i, -1)} disabled={i === 0}>↑</button>
          <button class="btn btn-ghost btn-xs" onclick={() => moveStep(i, 1)} disabled={i === steps.length - 1}>↓</button>
          <button class="btn btn-ghost btn-xs" onclick={() => removeStep(i)} data-testid={`remove-step-${i}`}>remove</button>
        </div>

        {#if EXECUTED.includes(step.kind)}
          <div class="grid gap-2 md:grid-cols-2">
            {#if step.kind === "emit"}
              <label class="field-label"
                >mode
                <select
                  class="input"
                  value={step.payload.command !== undefined ? "command" : "capability"}
                  onchange={(ev) => {
                    const which = (ev.target as HTMLSelectElement).value;
                    step.payload =
                      which === "command"
                        ? { command: registry?.commands[0] ?? "", inputs: step.payload.inputs ?? {} }
                        : { capability: registry?.capabilities[0] ?? "", inputs: step.payload.inputs ?? {} };
                  }}
                >
                  <option value="command">command</option>
                  <option value="capability">capability</option>
                </select>
              </label>
              {#if step.payload.command !== undefined}
                <label class="field-label"
                  >command
                  <select class="input" data-testid="emit-command" value={String(step.payload.command)}
                    onchange={(ev) => updatePayload(step, "command", (ev.target as HTMLSelectElement).value)}>
                    {#each registry?.commands ?? [] as c}
                      <option value={c}>{c}</option>
                    {/each}
                  </select>
                </label>
              {:else}
                <label class="field-label"
                  >capability
                  <select class="input" data-testid="emit-capability" value={String(step.payload.capability ?? "")}
                    onchange={(ev) => updatePayload(step, "capability", (ev.target as HTMLSelectElement).value)}>
                    {#each registry?.capabilities ?? [] as c}
                      <option value={c}>{c}</option>
                    {/each}
                  </select>
                </label>
              {/if}
              <label class="field-label md:col-span-2"
                >inputs (JSON)
                <textarea
                  class="input mono"
                  rows="3"
                  value={jsonField(step, "inputs")}
                  oninput={(ev) => updateJsonField(step, "inputs", (ev.target as HTMLTextAreaElement).value)}
                ></textarea>
              </label>
            {:else if step.kind === "wait"}
              <label class="field-label"
                >event
                <select class="input" value={String(step.payload.event ?? "")}
                  onchange={(ev) => updatePayload(step, "event", (ev.target as HTMLSelectElement).value)}>
                  {#each registry?.eventTypes ?? [] as t}
                    <option value={t}>{t}</option>
                  {/each}
                </select>
              </label>
              <label class="field-label"
                >correlate
                <input class="input mono" value={String(step.payload.correlate ?? "")}
                  oninput={(ev) => updatePayload(step, "correlate", (ev.target as HTMLInputElement).value)} />
              </label>
              <label class="field-label"
                >timeout (ISO-8601)
                <input class="input mono" data-testid="wait-timeout" value={String(step.payload.timeout ?? "")}
                  oninput={(ev) => updatePayload(step, "timeout", (ev.target as HTMLInputElement).value)} />
              </label>
              <label class="field-label"
                >onTimeout
                <select class="input" value={String(step.payload.onTimeout ?? "end")}
                  onchange={(ev) => updatePayload(step, "onTimeout", (ev.target as HTMLSelectElement).value)}>
                  {#each stepIds() as sid}
                    <option value={sid}>{sid}</option>
                  {/each}
                </select>
              </label>
            {:else if step.kind === "timer"}
              <label class="field-label"
                >for (ISO-8601)
                <input class="input mono" value={String(step.payload.for ?? "")}
                  oninput={(ev) => updatePayload(step, "for", (ev.target as HTMLInputElement).value)} />
              </label>
            {:else if step.kind === "invoke"}
              <label class="field-label"
                >child definition
                <select class="input" data-testid="invoke-workflow" value={String(step.payload.workflow ?? "")}
                  onchange={(ev) => updatePayload(step, "workflow", (ev.target as HTMLSelectElement).value)}>
                  {#each registry?.callable ?? [] as c}
                    <option value={c.id}>{c.id} — {c.title}</option>
                  {/each}
                </select>
              </label>
              <label class="field-label"
                >timeout (ISO-8601)
                <input class="input mono" data-testid="invoke-timeout" value={String(step.payload.timeout ?? "")}
                  oninput={(ev) => updatePayload(step, "timeout", (ev.target as HTMLInputElement).value)} />
              </label>
              <label class="flex items-center gap-2 text-sm">
                <input type="checkbox" checked={step.payload.await !== false}
                  onchange={(ev) => updatePayload(step, "await", (ev.target as HTMLInputElement).checked)} />
                await the child
              </label>
            {:else if step.kind === "select"}
              <label class="field-label md:col-span-2"
                >project (JSON map: field → ref)
                <textarea
                  class="input mono"
                  rows="3"
                  data-testid="select-project"
                  value={jsonField(step, "project") || "{\"value\": \"instance\"}"}
                  oninput={(ev) => updateJsonField(step, "project", (ev.target as HTMLTextAreaElement).value)}
                ></textarea>
              </label>
              {#if step.rawError}<p class="text-xs text-bad">{step.rawError}</p>{/if}
            {:else}
              <label class="field-label md:col-span-2"
                >payload (JSON)
                <textarea
                  class="input mono"
                  rows="4"
                  value={step.raw}
                  oninput={(ev) => {
                    step.raw = (ev.target as HTMLTextAreaElement).value;
                    const parsed = parseRaw(step);
                    step.rawError = parsed === null ? "payload is not a JSON object" : null;
                    if (parsed !== null) {
                      step.payload = parsed;
                    }
                  }}
                ></textarea>
              </label>
              {#if step.rawError}<p class="text-xs text-bad" data-testid="step-raw-error">{step.rawError}</p>{/if}
            {/if}
          </div>
        {:else}
          <p class="mb-1 text-xs text-amber">{step.kind} is stored, not yet executable — JSON face:</p>
          <textarea
            class="input mono"
            rows="4"
            value={step.raw}
            oninput={(ev) => {
              step.raw = (ev.target as HTMLTextAreaElement).value;
              const parsed = parseRaw(step);
              step.rawError = parsed === null ? "payload is not a JSON object" : null;
            }}
          ></textarea>
        {/if}
      </div>
    {/each}

    {#if changes.length > 0}
      <div class="mb-3 rounded border border-edge bg-sunken/40 p-3" data-testid="editor-diff">
        <h3 class="mb-1 text-sm font-semibold">changes vs stored v{loadedVersion || "—"}</h3>
        <ul class="list-inside list-disc text-sm text-ink-2">
          {#each changes as path}
            <li class="mono">{path}</li>
          {/each}
        </ul>
      </div>
    {/if}

    {#if canMutate}
      <div class="flex gap-2">
        <button class="btn btn-primary btn-sm" data-testid="editor-save" onclick={save} disabled={busy}>
          {busy ? "saving…" : isNew ? "store v1" : "save as new version"}
        </button>
        {#if !isNew && !freshId}
          {#if !enabled}
            <button class="btn btn-ghost btn-sm" data-testid="editor-enable" onclick={() => toggle(true, false)}>
              enable v{loadedVersion}
            </button>
          {/if}
          {#if enabled}
            <button class="btn btn-ghost btn-sm" data-testid="editor-disable" onclick={() => toggle(false, false)}>
              disable v{loadedVersion}
            </button>
          {/if}
        {/if}
      </div>
    {:else}
      <p class="text-sm text-ink-3">read-only principal: the form mirrors the doc, saving needs a mutator.</p>
    {/if}
  {:else}
    <p class="mb-2 text-sm text-ink-3">
      YAML face: validation runs before the save may land, and the save stamps a new version.
    </p>
    <textarea class="input mono mb-2" rows="18" data-testid="editor-yaml" bind:value={yamlText}></textarea>
    {#if validation}
      <p class="mb-2 text-sm {validation.ok ? 'text-ok' : 'text-bad'}" data-testid="yaml-validation">
        {validation.ok ? "valid" : `rejected: ${validation.errors.map((e) => e.detail).join("; ")}`}
      </p>
    {/if}
    {#if yamlText !== savedYaml}
      <p class="mb-2 text-sm text-amber" data-testid="yaml-dirty">the YAML differs from stored v{loadedVersion}</p>
    {/if}
    {#if canMutate}
      <div class="flex gap-2">
        <button class="btn btn-ghost btn-sm" data-testid="yaml-validate" onclick={validateYaml}>validate</button>
        <button
          class="btn btn-primary btn-sm"
          data-testid="yaml-save"
          onclick={saveYaml}
          disabled={busy || !(validation?.ok ?? false)}
        >
          save YAML as new version
        </button>
      </div>
    {/if}
  {/if}
</section>
