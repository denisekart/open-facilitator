<script lang="ts">
  import { ApiError, getJson } from "../lib/api.js";
  import { apiFetch } from "../lib/auth.svelte.js";
  import { ago, stateLabel, stateTone, stamp, TONE_CLASS } from "../lib/status.js";
  import { go, href, parse, route } from "../lib/route.svelte.js";
  import type { AdminWorkRow } from "../lib/types.js";
  import ProblemNote from "./ProblemNote.svelte";

  // The work queue: everything the kernel is holding, newest first. The
  // filter lives in the hash, so a filtered queue is a shareable link.

  const FILTERS = ["all", "blocked", "review", "active", "open"] as const;

  let items = $state<AdminWorkRow[]>([]);
  let error = $state<ApiError | null>(null);

  const wanted = $derived(route.route.params.get("state") ?? "");
  const team = $derived(route.route.params.get("team") ?? "");

  async function load(): Promise<void> {
    try {
      const params = new URLSearchParams();
      if (wanted) {
        params.set("state", wanted);
      }
      if (team) {
        params.set("team", team);
      }
      params.set("limit", "50");
      const query = params.toString().replace(/%2C/gi, ",").replace(/%3A/gi, ":");
      const page = await getJson<{ items: AdminWorkRow[] }>(
        apiFetch,
        `/api/admin/works?${query}`,
      );
      items = page.items;
      error = null;
    } catch (err) {
      error = err instanceof ApiError ? err : new ApiError(0, "fetch-failed", String(err), null);
    }
  }

  $effect(() => {
    void load();
  });

  function select(state: string): void {
    const params = new URLSearchParams();
    if (state && state !== "all") {
      params.set("state", state);
    }
    if (team) {
      params.set("team", team);
    }
    go(href(parse(`#/works?${params.toString()}`)));
  }
</script>

<section class="card" data-testid="works-view">
  <div class="mb-3 flex items-center justify-between">
    <h2 class="text-sm font-medium">
      Works
      <span class="ml-1 text-xs font-normal text-ink-3"
        >— everything the kernel is holding, newest first</span
      >
    </h2>
    <button class="btn" onclick={() => void load()}>refresh</button>
  </div>
  <div class="mb-3 flex flex-wrap gap-2" role="group" aria-label="queue filter">
    {#each FILTERS as key}
      <button
        class="chip {(wanted || "all") === key ? 'chip-active' : ''}"
        data-testid={`works-filter-${key}`}
        aria-pressed={(wanted || "all") === key}
        onclick={() => select(key)}>{key === "all" ? "all" : stateLabel(key)}</button
      >
    {/each}
  </div>
  <ProblemNote {error} />
  <table class="table">
    <thead>
      <tr>
        <th>Objective</th><th>State</th><th>Team</th><th>Created</th><th>Version</th>
      </tr>
    </thead>
    <tbody>
      {#each items as row}
        {@const short = row.workId.slice(0, 8)}
        <tr data-testid={`work-row-${short}`}>
          <td>
            <button
              class="text-left text-accent underline"
              title="open this work"
              data-testid={`work-open-${short}`}
              onclick={() => go(`#/work/${row.workId}`)}>{row.objective}</button
            >
          </td>
          <td data-testid={`work-state-${short}`}>
            <span class="badge {TONE_CLASS[stateTone(row.state)]}">
              <span class="dot" aria-hidden="true"></span>{stateLabel(row.state)}
            </span>
          </td>
          <td class="mono text-xs">{row.teamSlug ?? "—"}</td>
          <td
            class="mono text-xs text-ink-2"
            title={stamp(row.createdAt)}>{ago(row.createdAt).text}</td
          >
          <td class="mono">v{row.version}</td>
        </tr>
      {/each}
      {#if items.length === 0}
        <tr>
          <td colspan="5" class="text-ink-3">Nothing here — the queue is clear.</td>
        </tr>
      {/if}
    </tbody>
  </table>
</section>
