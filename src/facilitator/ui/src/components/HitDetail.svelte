<script lang="ts">
  import { ApiError, getJson } from "../lib/api.js";
  import { apiFetch } from "../lib/auth.svelte.js";
  import { ago, stateLabel, stateTone, stamp, TONE_CLASS } from "../lib/status.js";
  import type { AdminWorkRow } from "../lib/types.js";
  import ProblemNote from "./ProblemNote.svelte";

  // One non-work search hit, opened. Every kind the search returns either
  // has a kernel single-get here or says so out loud — a hit that opens
  // nothing is a dead end, and dead ends are the bug this component kills.

  let { kind, id, onclose }: { kind: string; id: string; onclose: () => void } = $props();

  interface ArtifactMeta {
    artifactId: string;
    mediaType: string;
    size: number;
    state: string;
  }

  interface ProjectDetail {
    projectId: string;
    name: string;
    teamSlug: string | null;
    createdAt: string;
    works: AdminWorkRow[];
  }

  interface MemoryDetail {
    entryId: string;
    scopeId: string;
    type: string;
    contentSummary: string | null;
    mediaType: string;
    classification: string;
    status: string;
    revision: number;
    observedAt: string | null;
    createdAt: string;
    createdBy: string;
  }

  let project = $state<ProjectDetail | null>(null);
  let artifact = $state<ArtifactMeta | null>(null);
  let memory = $state<MemoryDetail | null>(null);
  let error = $state<ApiError | null>(null);

  const path = $derived(
    kind === "projects"
      ? `/api/admin/projects/${id}`
      : kind === "artifacts"
        ? `/api/artifacts/${id}`
        : kind === "memories"
          ? `/api/memory/${id}`
          : null,
  );

  async function load(): Promise<void> {
    project = null;
    artifact = null;
    memory = null;
    error = null;
    if (!path) {
      return;
    }
    try {
      const fetched = await getJson<unknown>(apiFetch, path);
      if (kind === "projects") {
        project = fetched as ProjectDetail;
      } else if (kind === "artifacts") {
        artifact = fetched as ArtifactMeta;
      } else {
        memory = fetched as MemoryDetail;
      }
    } catch (err) {
      error = err instanceof ApiError ? err : new ApiError(0, "fetch-failed", String(err), null);
    }
  }

  $effect(() => {
    void load();
  });

  // Esc dismisses, same law as the work inspector.
  function onKeydown(ev: KeyboardEvent): void {
    if (ev.key === "Escape") onclose();
  }
</script>

<svelte:window onkeydown={onKeydown} />

<section class="card" data-testid="hit-detail" aria-label="search hit detail">
  <div class="mb-3 flex items-center justify-between gap-2">
    <h2 class="text-sm font-medium">
      {kind}
      <span class="mono ml-1 text-xs font-normal text-ink-3" title={id}>{id.slice(0, 8)}…</span>
    </h2>
    <div class="flex items-center gap-2">
      <button class="btn" title="reload" onclick={() => void load()}>↻</button>
      <button class="btn" data-testid="hit-detail-close" onclick={onclose}>close</button>
    </div>
  </div>

  <ProblemNote {error} />

  {#if project}
    <div class="grid grid-cols-[auto_1fr] gap-x-4 gap-y-1 text-sm">
      <span class="text-ink-3">Name</span><span data-testid="hit-project-name">{project.name}</span>
      <span class="text-ink-3">Team</span
      ><span class="mono text-xs">{project.teamSlug ?? "—"}</span>
      <span class="text-ink-3">Created</span>
      <span class="mono text-xs text-ink-2" title={stamp(project.createdAt)}>
        {ago(project.createdAt).text}
      </span>
      <span class="text-ink-3">Works</span>
      <span>
        {#if project.works.length > 0}
          <ul class="grid gap-1">
            {#each project.works as work (work.workId)}
              <li>
                <a
                  class="underline"
                  data-testid={`hit-project-work-${work.workId.slice(0, 8)}`}
                  href={`#/work/${work.workId}`}>{work.objective}</a
                >
                <span class="badge ml-2 {TONE_CLASS[stateTone(work.state)]}">
                  <span class="dot" aria-hidden="true"></span>{stateLabel(work.state)}
                </span>
              </li>
            {/each}
          </ul>
        {:else}
          <span class="text-ink-3">no works in this project yet</span>
        {/if}
      </span>
    </div>
  {:else if artifact}
    <span
      class="badge bg-sunken text-ink-2 ring-edge"
      data-testid="hit-artifact-meta">
      {artifact.state} · {artifact.mediaType} · {artifact.size} B
    </span>
    <a
      class="btn btn-primary ml-2"
      data-testid="artifact-content-link"
      href={`/api/artifacts/${id}/content`}>download bytes</a
    >
  {:else if memory}
    <div class="grid grid-cols-[auto_1fr] gap-x-4 gap-y-1 text-sm">
      <span class="text-ink-3">Scope</span><span class="mono text-xs">{memory.scopeId}</span>
      <span class="text-ink-3">Type</span><span>{memory.type}</span>
      <span class="text-ink-3">Status</span>
      <span>
        <span class="badge {TONE_CLASS[stateTone(memory.status)]}">
          <span class="dot" aria-hidden="true"></span>{stateLabel(memory.status)}
        </span>
        <span class="ml-2 text-xs text-ink-3">r{memory.revision}</span>
      </span>
      <span class="text-ink-3">Classified</span><span>{memory.classification}</span>
      <span class="text-ink-3">By</span><span class="mono text-xs">{memory.createdBy}</span>
      <span class="text-ink-3">Summary</span>
      <span>{memory.contentSummary ?? "—"}</span>
    </div>
  {:else if !path && !error}
    <p class="text-ink-3" data-testid="hit-no-detail">No detail surface for this kind yet.</p>
  {/if}
</section>
