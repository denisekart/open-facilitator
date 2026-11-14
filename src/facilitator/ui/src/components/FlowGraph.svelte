<script lang="ts">
  // The flow as a picture: a layered DAG rendered as inline SVG, in the
  // same idiom as the public architecture explorer — a curated layout with
  // no engine, focusable nodes, click-to-select, hover-highlighted edges.
  // In the editor it reflects the form live (author by eye, not just by
  // field); on an instance it wears the run's own skin: each node carries
  // the tone of its latest recorded leg, and clicking a node opens that
  // leg's output.

  type Doc = Record<string, unknown>;
  type StepForm = { id: string; title: string; on: string; kind: string; [k: string]: unknown };

  let {
    doc,
    log = null,
    selected = null,
    instanceStatus = null,
    currentStep = null,
    onselect = () => {},
  }: {
    doc: Doc;
    log?: { stepId: string; status: string; output: string | null }[] | null;
    selected?: string | null;
    instanceStatus?: string | null;
    currentStep?: string | null;
    onselect?: (stepId: string | null) => void;
  } = $props();

  type Node = {
    id: string; // step id, "trigger", or "end"
    label: string;
    sub: string;
    tone: string; // tone class suffix
    x: number;
    y: number;
    kind: string;
  };
  type Edge = { from: string; to: string; label: string; dashed: boolean };

  const NODE_W = 172;
  const NODE_H = 54;
  const GAP_X = 90;
  const GAP_Y = 78;

  const KINDS = [
    "emit",
    "wait",
    "timer",
    "approval",
    "decide",
    "select",
    "fan-out",
    "join",
    "fork-work",
    "work-create",
    "escalate",
    "memory",
    "signal",
    "invoke",
  ];

  // ---- graph extraction (mirrors the runner's edge law) -------------------

  const graph = $derived.by((): { nodes: Node[]; edges: Edge[]; w: number; h: number } => {
    const steps = ((doc.steps ?? []) as Doc[]).map((s) => {
      const kind = KINDS.find((k) => k in s) ?? "?";
      return {
        id: String(s.id ?? "?"),
        title: String(s.title ?? ""),
        on: String(s.on ?? "always"),
        kind,
        payload: (s[kind] ?? {}) as Doc,
      } satisfies StepForm & { kind: string; payload: Doc };
    });
    const ids = new Set(steps.map((s) => s.id));
    const edges: Edge[] = [];
    const push = (from: string, to: string, label: string, dashed = false): void => {
      if (from !== to && (to === "end" || ids.has(to)) && (from === "trigger" || from === "end" || ids.has(from))) {
        edges.push({ from, to, label, dashed });
      }
    };

    steps.forEach((s, i) => {
      const prev = i === 0 ? "trigger" : steps[i - 1].id;
      if (s.on === "always") {
        push(prev, s.id, "");
      } else if (s.on === "timeout" || s.on === "failed") {
        push(prev, s.id, s.on, true);
      } else if (s.on.endsWith(":completed")) {
        push(s.on.slice(0, -":completed".length), s.id, "");
      } else {
        push(prev, s.id, `?${s.on}`, true);
      }
      // Branch legs the payload itself declares.
      if (s.kind === "wait" || s.kind === "invoke") {
        const t = String(s.payload.onTimeout ?? "");
        if (t) push(s.id, t, "timeout", true);
      }
      if (s.kind === "decide") {
        const fb = String(s.payload.fallback ?? "");
        if (fb) push(s.id, fb, "fallback", true);
        const options = s.payload.options;
        if (options && typeof options === "object") {
          for (const [branch, target] of Object.entries(options as Record<string, unknown>)) {
            if (typeof target === "string") push(s.id, target, branch);
          }
        }
      }
    });
    // Instance law: a run that finished walks to 'end'.
    if (steps.length > 0) {
      const terminal = ["completed", "failed", "cancelled"].includes(instanceStatus ?? "");
      if (terminal && instanceStatus === "completed") {
        push(steps[steps.length - 1].id, "end", "");
      }
    }

    // ---- layered layout: BFS depth columns, stable rows ----------------
    const depth = new Map<string, number>();
    const incoming = new Map<string, string[]>();
    for (const e of edges) {
      incoming.set(e.to, [...(incoming.get(e.to) ?? []), e.from]);
    }
    depth.set("trigger", 0);
    let frontier = ["trigger"];
    let guard = 0;
    while (frontier.length > 0 && guard < 200) {
      guard += 1;
      const next: string[] = [];
      for (const f of frontier) {
        for (const e of edges) {
          if (e.from === f && !depth.has(e.to)) {
            depth.set(e.to, (depth.get(f) ?? 0) + 1);
            next.push(e.to);
          }
        }
      }
      frontier = next;
    }
    // Unreached nodes (isolated while authoring): park them after the deepest column.
    const maxDepth = Math.max(0, ...[...depth.values()]);
    let orphanCol = maxDepth + 1;
    for (const s of steps) {
      if (!depth.has(s.id)) {
        depth.set(s.id, orphanCol);
        orphanCol += 1; // chain orphans so they never stack
      }
    }
    if (!depth.has("end")) {
      depth.set("end", maxDepth + 1);
    }

    const latest = new Map<string, string>();
    for (const row of log ?? []) {
      latest.set(row.stepId, row.status); // log is oldest-first: last write wins
    }
    const toneFor = (step: (typeof steps)[number]): string => {
      const st = latest.get(step.id);
      if (st === "done") return "ok";
      if (st === "failed") return "bad";
      if (st === "skipped") return "skip";
      if (st === "shadow") return "shadow";
      if (log !== null && !st) return "pending";
      const K: Record<string, string> = {
        wait: "park",
        timer: "park",
        approval: "park",
        invoke: "call",
        decide: "branch",
        "fan-out": "branch",
        join: "branch",
        emit: "act",
        signal: "act",
        escalate: "act",
        "work-create": "act",
        "fork-work": "act",
        memory: "act",
        select: "calc",
      };
      return K[step.kind] ?? "calc";
    };

    const columns = new Map<number, Node[]>();
    const mk = (id: string, label: string, sub: string, tone: string, kind = ""): Node => ({
      id,
      label,
      sub,
      tone,
      x: 0,
      y: 0,
      kind,
    });
    const place = (col: number, node: Node): void => {
      columns.set(col, [...(columns.get(col) ?? []), node]);
    };
    place(depth.get("trigger") ?? 0, {
      ...mk("trigger", triggerLabel(doc), "trigger", "trigger"),
    });
    for (const s of steps) {
      place(depth.get(s.id) ?? 1, { ...mk(s.id, s.id, `${s.kind}${s.title ? ` · ${s.title}` : ""}`, toneFor(s), s.kind) });
    }
    if (ids.size > 0 && edges.some((e) => e.to === "end")) {
      place(depth.get("end") ?? maxDepth + 1, mk("end", "end", "flow finished", "end"));
    }

    let w = 0;
    let h = 0;
    for (const [col, nodes] of columns) {
      nodes.forEach((n, i) => {
        n.x = 20 + col * (NODE_W + GAP_X);
        n.y = 20 + i * (NODE_H + GAP_Y);
        w = Math.max(w, n.x + NODE_W + 20);
        h = Math.max(h, n.y + NODE_H + 20);
      });
    }
    // The live instance's current step wears the run's status tone.
    if (currentStep) {
      const n = steps.find((s) => s.id === currentStep);
      const node = [...columns.values()].flat().find((x) => x.id === currentStep);
      if (node && n && (instanceStatus === "running" || instanceStatus === "waiting" || instanceStatus === "paused")) {
        node.tone = instanceStatus === "running" ? "live" : instanceStatus === "paused" ? "paused" : "park-live";
      }
    }
    return { nodes: [...columns.values()].flat(), edges, w: Math.max(w, 320), h: Math.max(h, 140) };
  });

  function triggerLabel(doc: Doc): string {
    const on = (doc.on ?? {}) as Doc;
    if ("event" in on) return `on ${String(on.event).replace("org.facilitator.event.", "")}`;
    if ("schedule" in on) return `every ${String(on.schedule)}`;
    return "invoked";
  }

  const byId = $derived(new Map(graph.nodes.map((n) => [n.id, n])));

  function edgePath(e: Edge): string {
    const a = byId.get(e.from);
    const b = byId.get(e.to);
    if (!a || !b) return "";
    const x1 = a.x + NODE_W;
    const y1 = a.y + NODE_H / 2;
    const x2 = b.x;
    const y2 = b.y + NODE_H / 2;
    if (x2 <= x1) {
      // backward or same-column edge: route around, labelled
      const mid = Math.max(x1, x2) + 40;
      return `M ${x1} ${y1} C ${mid} ${y1}, ${mid} ${y2}, ${x2} ${y2}`;
    }
    const mx = (x1 + x2) / 2;
    return `M ${x1} ${y1} C ${mx} ${y1}, ${mx} ${y2}, ${x2} ${y2}`;
  }

  let hovered = $state<string | null>(null);

  function incident(e: Edge): boolean {
    return hovered !== null && (e.from === hovered || e.to === hovered);
  }

  function activate(id: string): void {
    onselect(id === selected ? null : id);
  }
</script>

<div class="overflow-x-auto rounded border border-edge bg-sunken/20" data-testid="flow-graph">
  <svg
    viewBox="0 0 {graph.w} {graph.h}"
    width={graph.w}
    height={graph.h}
    role="img"
    aria-label="flow graph"
    class="min-w-full"
  >
    <defs>
      <marker id="fg-arrow" viewBox="0 0 10 10" refX="9" refY="5" markerWidth="7" markerHeight="7" orient="auto-start-reverse">
        <path d="M 0 0 L 10 5 L 0 10 z" class="fill-ink-3" />
      </marker>
    </defs>
    {#each graph.edges as e, i (i)}
      {@const p = edgePath(e)}
      <path
        d={p}
        class="stroke-edge transition-[stroke] duration-150"
        class:stroke-accent={incident(e)}
        fill="none"
        stroke-width={hovered === e.from || hovered === e.to ? 2 : 1.5}
        stroke-dasharray={e.dashed ? "5 4" : undefined}
        marker-end="url(#fg-arrow)"
      />
      {#if e.label}
        <text x={(byId.get(e.from)?.x ?? 0) + NODE_W + 8} y={(byId.get(e.from)?.y ?? 0) + NODE_H / 2 - 6} class="fill-ink-3 text-[10px]">
          {e.label}
        </text>
      {/if}
    {/each}
    {#each graph.nodes as n (n.id)}
      <g
        class="cursor-pointer outline-none"
        tabindex="0"
        role="button"
        aria-label={`${n.id}: ${n.sub}`}
        onpointerenter={() => (hovered = n.id)}
        onpointerleave={() => (hovered = null)}
        onfocus={() => (hovered = n.id)}
        onblur={() => (hovered = null)}
        onclick={() => activate(n.id)}
        onkeydown={(ev) => ev.key === "Enter" && activate(n.id)}
      >
        <rect
          x={n.x}
          y={n.y}
          width={NODE_W}
          height={NODE_H}
          rx="10"
          class="stroke-edge transition-[stroke,width] duration-150"
          class:fg-done={n.tone === "ok"}
          class:fg-failed={n.tone === "bad"}
          class:fg-skipped={n.tone === "skip"}
          class:fg-shadow={n.tone === "shadow"}
          class:fg-pending={n.tone === "pending"}
          class:fg-park={n.tone === "park"}
          class:fg-park-live={n.tone === "park-live"}
          class:fg-call={n.tone === "call"}
          class:fg-branch={n.tone === "branch"}
          class:fg-act={n.tone === "act"}
          class:fg-calc={n.tone === "calc"}
          class:fg-trigger={n.tone === "trigger"}
          class:fg-end={n.tone === "end"}
          class:fg-live={n.tone === "live"}
          class:fg-paused={n.tone === "paused"}
          class:fg-selected={selected === n.id}
        />
        <text x={n.x + 12} y={n.y + 22} class="fill-ink text-[13px] font-semibold">{n.label}</text>
        <text x={n.x + 12} y={n.y + 40} class="fill-ink-3 text-[10px]">{n.sub}</text>
      </g>
    {/each}
  </svg>
</div>

<style>
  /* Node fills/strokes keyed off the leg's recorded outcome — the tones
     the step log actually speaks, not the instance badge's vocabulary. */
  rect {
    fill: var(--color-sunken);
    stroke-width: 1.5;
  }
  .fg-done {
    fill: var(--color-ok-veil);
    stroke: var(--color-ok);
  }
  .fg-failed {
    fill: var(--color-danger-veil);
    stroke: var(--color-danger);
  }
  .fg-skipped,
  .fg-pending {
    fill: none;
    stroke-dasharray: 4 4;
    stroke: var(--color-ink-3);
  }
  .fg-shadow {
    fill: var(--color-sunken);
    stroke: var(--color-ink-3);
    stroke-dasharray: 2 3;
  }
  .fg-park,
  .fg-call,
  .fg-branch,
  .fg-act {
    fill: var(--color-sunken);
    stroke: var(--color-edge);
  }
  .fg-park-live,
  .fg-live {
    stroke-width: 2.5;
  }
  .fg-calc,
  .fg-end {
    fill: var(--color-sunken);
    stroke: var(--color-edge);
  }
  .fg-trigger {
    fill: var(--color-accent-veil);
    stroke: var(--color-accent);
  }
  .fg-live {
    fill: var(--color-accent-veil);
    stroke: var(--color-accent);
    stroke-width: 2.5;
  }
  .fg-park-live {
    fill: var(--color-amber-veil);
    stroke: var(--color-amber);
  }
  .fg-paused {
    fill: var(--color-amber-veil);
    stroke: var(--color-amber);
    stroke-dasharray: 6 3;
    stroke-width: 2.5;
  }
  .fg-selected {
    stroke-width: 3;
    stroke: var(--color-accent);
  }
  g:focus-visible rect,
  g:hover rect {
    stroke: var(--color-accent);
  }
</style>
