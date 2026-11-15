<script lang="ts">
  import { href, route, type Route } from "../lib/route.svelte.js";

  // One crumb per path segment of the canonical route, each a link to the
  // truncated route — route-as-state guarantees every truncation is a real,
  // parseable route (href(parse(h)) === h). Only shown when a route deeper
  // than the category root is open.
  let { r = route.route }: { r?: Route } = $props();

  const CRUMB_LABEL: Record<string, string> = {
    "monitor-runs": "monitor",
    "monitor-run": "run",
    "monitor-deliveries": "deliveries",
    "monitor-audit": "audit",
    "monitor-runtime": "runtime",
    "monitor-definition": "definition",
    flows: "flows",
    team: "team",
    config: "config",
    works: "works",
    work: "work",
    hit: "hit",
  };

  const crumbs = $derived.by(() => {
    const out: { label: string; href: string }[] = [];
    switch (r.view) {
      case "work":
        out.push({ label: "works", href: "#/works" });
        out.push({ label: `work ${r.id?.slice(0, 8) ?? ""}`, href: href(r) });
        break;
      case "flows":
        out.push({ label: "flows", href: "#/flows" });
        out.push({ label: r.id ?? "", href: href(r) });
        break;
      case "monitor-run":
        out.push({ label: "monitor", href: "#/monitor/runs" });
        out.push({ label: `run ${r.id?.slice(0, 8) ?? ""}`, href: href(r) });
        break;
      case "monitor-definition":
        out.push({ label: "monitor", href: "#/monitor/runs" });
        out.push({ label: `definition ${r.id ?? ""}`, href: href(r) });
        break;
      case "monitor-runs":
      case "monitor-deliveries":
      case "monitor-audit":
      case "monitor-runtime":
        out.push({ label: "monitor", href: "#/monitor/runs" });
        out.push({ label: CRUMB_LABEL[r.view] ?? r.view, href: href(r) });
        break;
      case "hit":
        out.push({ label: "search", href: "#/" });
        out.push({ label: `${r.kind} ${r.id?.slice(0, 8) ?? ""}`, href: href(r) });
        break;
      default:
        // Top-level categories carry no breadcrumb: the rail already says it.
        break;
    }
    return out;
  });
</script>

{#if crumbs.length > 1}
  <nav class="mb-3 flex items-center gap-1 text-xs text-ink-3" aria-label="breadcrumbs" data-testid="breadcrumbs">
    {#each crumbs as crumb, i}
      {#if i > 0}<span aria-hidden="true">/</span>{/if}
      {#if i === crumbs.length - 1}
        <span class="font-medium text-ink-2">{crumb.label}</span>
      {:else}
        <a class="underline decoration-dotted" href={crumb.href}>{crumb.label}</a>
      {/if}
    {/each}
  </nav>
{/if}
