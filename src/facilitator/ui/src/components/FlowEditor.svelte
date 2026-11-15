<script lang="ts">
  import { basicSetup } from "codemirror";
  import { EditorView } from "@codemirror/view";
  import { parse as parseYaml, stringify as stringifyYaml } from "yaml";
  import { getJson, toApiError } from "../lib/api.js";
  import { apiFetch, session } from "../lib/auth.svelte.js";
  import type { WorkflowRegistry } from "../lib/types.js";
  import { ofacYamlExtensions, type SchemaDoc, type YamlEditorSources } from "../lib/yaml-editor.js";
  import FlowCanvas from "./FlowCanvas.svelte";
  import EmptyState from "./EmptyState.svelte";
  import ProblemNote from "./ProblemNote.svelte";

  // The YAML face IS the editor. CodeMirror 6 with a schema-driven
  // completion/lint extension built from GET /schema + GET /registry (the
  // same documents the 422 validator compiles — the browser never invents
  // an event type, command, capability or callable, and never outranks the
  // server's verdict). The live graph sits beside it and never blanks: a
  // broken keystroke keeps the last good picture and shows why instead.
  // The one-line title stays a plain input: title edits are metadata, not
  // YAML body.

  let { defId, clone = false, onclose }: { defId: string; clone?: boolean; onclose: () => void } =
    $props();

  type Doc = Record<string, unknown>;

  let registry = $state<WorkflowRegistry | null>(null);
  let schema = $state<SchemaDoc | null>(null);
  let original = $state<Doc | null>(null); // the stored doc, for the diff
  let digest = $state<string | null>(null); // latest digest = the If-Match etag
  let loadedVersion = $state<number>(0);

  // svelte-ignore state_referenced_locally -- the editor mounts per route id.
  let id = $state(defId === "new" || clone ? "" : defId);
  let title = $state("");
  let description = $state("");
  let notice = $state<string | null>(null);
  let formError = $state<string | null>(null);
  let validation = $state<{ ok: boolean; errors: { code: string; detail: string }[] } | null>(null);
  let busy = $state(false);

  // svelte-ignore state_referenced_locally
  let anchorId = $state(defId);
  // svelte-ignore state_referenced_locally
  let freshId = $state(clone || defId === "new");

  const canMutate = $derived(session.canMutate);
  const isNew = $derived(original === null);
  // Posture truth rides the summary, not the document: a toggle rewrites
  // the stored entity, never the canonical payload.
  let postureEnabled = $state(false);
  let postureShadow = $state(false);
  const enabled = $derived(postureEnabled);
  const shadow = $derived(postureShadow);

  // ---- CodeMirror host -----------------------------------------------------

  let host = $state<HTMLDivElement | null>(null);
  // Non-reactive: the template never reads them; the mount effect must not
  // re-run on view swaps.
  let view: EditorView | null = null;
  let docText = $state("");
  let parseError = $state<string | null>(null);
  let lastGoodDoc = $state<Doc | null>(null);
  let selectedStep = $state<string | null>(null);

  const parsed = $derived.by((): Doc | null => {
    try {
      const value = parseYaml(docText) as Doc | null;
      return value && typeof value === "object" && !Array.isArray(value) ? value : null;
    } catch (err) {
      return null;
    }
  });

  // Keystroke → 200 ms debounce → parse. A failure keeps the last good
  // graph and shows the parser message; the graph never blanks.
  let parseTimer: ReturnType<typeof setTimeout> | undefined;
  $effect(() => {
    const text = docText;
    clearTimeout(parseTimer);
    parseTimer = setTimeout(() => {
      try {
        const value = parseYaml(text) as Doc | null;
        if (value && typeof value === "object" && !Array.isArray(value)) {
          lastGoodDoc = value;
          parseError = null;
        } else {
          parseError = "the document is empty";
        }
      } catch (err) {
        parseError = err instanceof Error ? err.message : String(err);
      }
    }, 200);
    return () => clearTimeout(parseTimer);
  });

  const graphDoc = $derived(lastGoodDoc ?? { steps: [] });

  const changes = $derived.by(() => {
    const paths: string[] = [];
    const walk = (a: unknown, b: unknown, at: string): void => {
      if (a === b) return;
      if (
        a && b &&
        typeof a === "object" &&
        typeof b === "object" &&
        !Array.isArray(a) &&
        !Array.isArray(b)
      ) {
        const ka = a as Doc;
        const kb = b as Doc;
        for (const key of new Set([...Object.keys(ka), ...Object.keys(kb)])) {
          walk(ka[key], kb[key], at ? `${at}.${key}` : key);
        }
        return;
      }
      if (JSON.stringify(a) !== JSON.stringify(b)) paths.push(at || "(root)");
    };
    const next = mergedDoc();
    if (original !== null && next !== null) {
      walk(original, next, "");
    }
    return paths;
  });

  /** The doc the editor would store: YAML body + the metadata fields.
   *  Posture rides along unchanged — enabling is the toggle's act, and a
   *  plain save must never silently switch a live definition off. */
  function mergedDoc(): Doc | null {
    if (parsed === null) {
      return null;
    }
    const out: Doc = { ...parsed };
    if (id.trim() !== "") out["id"] = id.trim();
    out["title"] = title;
    if (description.trim() !== "") out["description"] = description.trim();
    out["enabled"] = postureEnabled;
    if (postureShadow) {
      out["shadow"] = true;
    }
    return out;
  }

  async function boot(): Promise<void> {
    const asClone = clone;
    try {
      registry = await getJson<WorkflowRegistry>(apiFetch, "/api/admin/workflows/registry");
      try {
        schema = await getJson<SchemaDoc>(apiFetch, "/api/admin/workflows/schema");
      } catch {
        schema = null; // completion/lint degrades; the server still rules
      }
      if (anchorId !== "new") {
        const stored = await getJson<{
          summary: { version: number; digest: string; enabled: boolean; shadow: boolean };
          document: Doc;
          yaml: string;
        }>(apiFetch, `/api/admin/workflows/definitions/${encodeURIComponent(anchorId)}`);
        const first = original === null;
        original = stored.document;
        loadedVersion = stored.summary.version;
        digest = stored.summary.digest;
        postureEnabled = stored.summary.enabled;
        postureShadow = stored.summary.shadow;
        id = String(stored.document.id ?? "");
        title = String(stored.document.title ?? "");
        description = String(stored.document.description ?? "");
        let text = stored.yaml;
        if (asClone && first) {
          // rename exactly once: a re-anchor on the copy must not double-suffix
          id = `${id}-copy`;
          title = `${title} (copy)`;
          try {
            const doc = parseYaml(text) as Doc;
            doc["id"] = id;
            doc["title"] = title;
            text = stringifyYaml(doc);
          } catch {
            // leave the body: save merges the fields anyway
          }
        }
        docText = text;
        lastGoodDoc = stored.document;
        parseError = null;
      } else {
        docText = starterYaml();
        lastGoodDoc = parsed;
      }
    } catch (err) {
      formError = err instanceof Error ? String(err) : "editor failed to load";
    }
  }

  function starterYaml(): string {
    return `id: ${id.trim() || "my-flow"}\ntitle: ${title.trim() || "My flow"}\non:\n  event: org.facilitator.event.work.created.v1\nsteps:\n  - id: first\n    title: first step\n    emit:\n      command: org.facilitator.command.communication.post.v1\n      inputs: {}\n`;
  }

  // ---- CodeMirror mount ------------------------------------------------------

  // Mount when the host exists; RE-create the view once the schema sources
  // arrive so the completion/lint extension is present. Doc text survives
  // the recreation.
  let mountedWithSources = false; // plain: only the mount effect sees it
  $effect(() => {
    const ready = schema !== null && registry !== null;
    if (host === null) {
      return;
    }
    if (view !== null && mountedWithSources === ready) {
      return;
    }
    const current = view === null ? docText : view.state.doc.toString();
    if (view !== null) {
      view.destroy();
      view = null;
    }
    const sources: YamlEditorSources | null = ready
      ? {
          definition: schema as SchemaDoc,
          registry: {
            eventTypes: (registry as WorkflowRegistry).eventTypes,
            commands: (registry as WorkflowRegistry).commands,
            capabilities: (registry as WorkflowRegistry).capabilities,
            callable: (registry as WorkflowRegistry).callable,
            teams: (registry as WorkflowRegistry).teams,
          },
        }
      : null;
    view = new EditorView({
      doc: current,
      extensions: [
        basicSetup,
        EditorView.lineWrapping,
        ...(sources ? ofacYamlExtensions(sources) : []),
        EditorView.updateListener.of((update: { docChanged: boolean; state: EditorView["state"] }) => {
          if (update.docChanged) {
            docText = update.state.doc.toString();
          }
        }),
      ],
      parent: host,
    });
    mountedWithSources = ready;
    // Unmount teardown: this effect tracks nothing (the cleanup reads the
    // view), so it runs once and destroys the editor exactly on unmount.
    return () => {
      view?.destroy();
      view = null;
    };
  });

  // First boot resolves after mount: push the loaded text into CodeMirror
  // programmatically (not a keystroke — it must not dirty the diff).
  $effect(() => {
    void boot().then(() => {
      if (view !== null && view.state.doc.toString() !== docText) {
        view.dispatch({
          changes: { from: 0, to: view.state.doc.length, insert: docText },
        });
      }
    });
  });

  function jumpToStep(stepId: string): void {
    if (view === null) {
      return;
    }
    const needle = `- id: ${stepId}`;
    const alt = `id: ${stepId}`;
    const text = view.state.doc.toString();
    const at = text.indexOf(needle) !== -1 ? text.indexOf(needle) : text.indexOf(alt);
    if (at === -1) {
      return;
    }
    view.focus();
    const line = view.state.doc.lineAt(at);
    view.dispatch({
      selection: { anchor: line.from, head: line.to },
      effects: EditorView.scrollIntoView(line.from, { y: "center" }),
    });
  }

  const selectedStepDoc = $derived.by(() => {
    if (selectedStep === null || lastGoodDoc === null) {
      return null;
    }
    const steps = (lastGoodDoc["steps"] ?? []) as Doc[];
    return steps.find((s) => String(s["id"] ?? "") === selectedStep) ?? null;
  });

  // ---- the wire ------------------------------------------------------------

  async function save(): Promise<void> {
    busy = true;
    notice = null;
    try {
      const targetId = id.trim();
      if (targetId === "") {
        formError = "the definition needs an id";
        return;
      }
      const doc = mergedDoc();
      if (doc === null) {
        notice = "the YAML body does not parse — fix the highlighted lines first";
        return;
      }
      const headers: Record<string, string> = {
        "Content-Type": "application/json",
        "X-Change-Reason": "flow edited as YAML in the console",
      };
      if (digest !== null && !freshId) {
        headers["If-Match"] = digest;
      }
      const response = await apiFetch(
        `/api/admin/workflows/definitions/${encodeURIComponent(targetId)}`,
        { method: "PUT", headers, body: JSON.stringify(doc) },
      );
      if (!response.ok) {
        const problem = await toApiError(response);
        notice = `${problem.title} (${problem.code}): ${problem.detail}`;
        return;
      }
      const summary = (await response.json()) as { version: number };
      notice = `stored as version ${summary.version}`;
      anchorId = targetId;
      freshId = false;
      await reloadInto(targetId);
    } catch (err) {
      formError = err instanceof Error ? String(err) : "save failed";
    } finally {
      busy = false;
    }
  }

  async function reloadInto(targetId: string): Promise<void> {
    const stored = await getJson<{
      summary: { version: number; digest: string; enabled: boolean; shadow: boolean };
      document: Doc;
      yaml: string;
    }>(apiFetch, `/api/admin/workflows/definitions/${encodeURIComponent(targetId)}`);
    original = stored.document;
    loadedVersion = stored.summary.version;
    digest = stored.summary.digest;
    postureEnabled = stored.summary.enabled;
    postureShadow = stored.summary.shadow;
    docText = stored.yaml;
    lastGoodDoc = stored.document;
    parseError = null;
    if (view !== null && view.state.doc.toString() !== docText) {
      view.dispatch({ changes: { from: 0, to: view.state.doc.length, insert: docText } });
    }
  }

  async function validateYaml(): Promise<void> {
    validation = null;
    const res = await apiFetch("/api/admin/workflows/definitions/validate", {
      method: "POST",
      headers: { "Content-Type": "text/yaml" },
      body: docText,
    });
    validation = (await res.json()) as { ok: boolean; errors: { code: string; detail: string }[] };
  }

  async function toggle(on: boolean, toShadow: boolean): Promise<void> {
    notice = null;
    const body = on ? { enabled: true } : toShadow ? { shadow: false } : { enabled: false };
    const response = await apiFetch(
      `/api/admin/workflows/definitions/${encodeURIComponent(id)}/versions/${loadedVersion}/toggle`,
      {
        method: "POST",
        headers: {
          "Content-Type": "application/json",
          "X-Change-Reason": "posture toggle from the console",
        },
        body: JSON.stringify(body),
      },
    );
    if (!response.ok) {
      const problem = await toApiError(response);
      notice = `${problem.title} (${problem.code})`;
      return;
    }
    notice = on ? "enabled (subscriptions refreshed)" : "posture changed";
    await reloadInto(id);
  }
</script>

<section class="card" data-testid="flow-editor" aria-label="flow editor">
  <div class="mb-2 flex items-start justify-between gap-4">
    <div>
      <h2 class="mb-1 text-xl font-semibold">
        {#if isNew}author a flow{:else}<span class="mono">{id}</span> · v{loadedVersion}{/if}
      </h2>
      <p class="text-sm text-ink-3">
        YAML is the source of truth; the graph redraws as you type.
        <a class="text-accent underline" href={`#/monitor/definitions/${id}`}>runs &amp; history</a>
      </p>
    </div>
    <div class="flex gap-2">
      {#if canMutate}
        {#if original !== null}
          {#if !enabled}
            <button class="btn" data-testid="editor-enable" onclick={() => void toggle(true, false)}>
              enable
            </button>
          {:else if !shadow}
            <button class="btn" data-testid="editor-shadow" onclick={() => void toggle(false, true)}>
              shadow
            </button>
            <button class="btn" data-testid="editor-disable" onclick={() => void toggle(false, false)}>
              disable
            </button>
          {:else}
            <button class="btn" data-testid="editor-enable" onclick={() => void toggle(true, false)}>
              enable
            </button>
          {/if}
        {/if}
        <button
          class="btn btn-primary"
          data-testid="editor-save"
          disabled={busy}
          onclick={() => void save()}>
          save
        </button>
      {/if}
      <button class="btn btn-ghost btn-sm" data-testid="editor-close" onclick={onclose}>close</button>
    </div>
  </div>

  {#if formError}
    <ProblemNote error={formError} />
  {/if}
  {#if notice}
    <p class="mb-2 rounded bg-ok-veil px-3 py-2 text-sm text-ok" data-testid="editor-notice">{notice}</p>
  {/if}

  <div class="mb-3 grid gap-3 md:grid-cols-2">
    <label class="block">
      <span class="label">id</span>
      <input
        class="input font-mono"
        data-testid="editor-id"
        bind:value={id}
        required
        maxlength="80"
        pattern="[a-z0-9][a-z0-9._-]*" />
    </label>
    <label class="block">
      <span class="label">title</span>
      <input class="input" data-testid="editor-title" bind:value={title} required maxlength="120" />
    </label>
  </div>

  <div class="grid gap-4 lg:grid-cols-2">
    <div class="min-w-0">
      <div class="mb-1 flex items-center justify-between">
        <span class="label mb-0">definition YAML</span>
        <div class="flex gap-2">
          <button class="btn" data-testid="yaml-validate" onclick={() => void validateYaml()}>
            validate
          </button>
          {#if canMutate}
            <button class="btn btn-primary" data-testid="yaml-save" disabled={busy} onclick={() => void save()}>
              save
            </button>
          {/if}
        </div>
      </div>
      {#if parseError}
        <p class="badge mb-1 bg-amber-veil text-amber ring-amber/25" data-testid="graph-error">
          graph shows the last good version: {parseError}
        </p>
      {/if}
      <div
        class="yaml-host rounded border border-edge"
        data-testid="editor-yaml"
        bind:this={host}></div>
      {#if validation}
        <div class="mt-2" data-testid="yaml-validation">
          {#if validation.ok}
            <p class="badge bg-ok-veil text-ok ring-ok/25">valid</p>
          {:else}
            <ul class="grid gap-1">
              {#each validation.errors as e (e.code + e.detail)}
                <li class="badge bg-danger-veil text-danger ring-danger/25">
                  <span class="mono">{e.code}</span> {e.detail}
                </li>
              {/each}
            </ul>
          {/if}
        </div>
      {/if}
      {#if changes.length > 0}
        <ul class="mt-2 flex flex-wrap gap-1" data-testid="editor-diff" aria-label="pending changes">
          {#each changes as path (path)}
            <li class="badge bg-amber-veil text-amber ring-amber/25 mono">{path}</li>
          {/each}
        </ul>
      {:else}
        <p class="mt-2 hidden" data-testid="editor-diff"></p>
      {/if}
    </div>

    <div class="min-w-0">
      {#if lastGoodDoc !== null}
        <FlowCanvas
          doc={graphDoc}
          selected={selectedStep}
          onselect={(stepId) => (selectedStep = stepId && stepId !== "trigger" && stepId !== "end" ? stepId : null)} />
      {:else}
        <EmptyState title="the graph waits for a parseable document" testid="graph-empty" />
      {/if}
      {#if selectedStepDoc}
        <div class="card mt-3" data-testid="step-card">
          <div class="mb-2 flex items-center justify-between">
            <span class="mono text-sm font-medium">{String(selectedStepDoc["id"])}</span>
            <button
              class="btn"
              data-testid="step-jump"
              onclick={() => jumpToStep(String(selectedStepDoc["id"]))}>
              jump to YAML
            </button>
          </div>
          <pre class="mono overflow-x-auto rounded bg-bg p-2 text-xs">{JSON.stringify(selectedStepDoc, null, 2)}</pre>
        </div>
      {/if}
    </div>
  </div>
</section>

<style>
  .yaml-host :global(.cm-editor) {
    border-radius: 6px;
    font-size: 13px;
  }
  .yaml-host :global(.cm-editor.cm-focused) {
    outline: 1px solid var(--color-accent);
  }
  .yaml-host :global(.cm-scroller) {
    font-family: var(--font-mono);
  }
</style>
