<script lang="ts">
  import { MarkerType, SvelteFlow } from "@xyflow/svelte";
  import type { Edge, Node } from "@xyflow/svelte";
  import StepNode from "./StepNode.svelte";
  import { buildModel, type Doc, type Leg } from "../lib/flow-layout.js";
  import { theme } from "../lib/theme.svelte.js";

  // The one shared canvas for the workflow plane: the editor's live picture
  // and the run's worn skin are the same component, given a log. Layout is
  // dagre layered (TB) via the pure flow-layout module; rendering is Svelte
  // Flow (MIT, free tier: the attribution link stays). Read-only: pan and
  // zoom, never drag-connect.

  type Subflow = { parentId: string; child: { id: string; status: string }[] };

  let {
    doc,
    log = null,
    selected = null,
    instanceStatus = null,
    currentStep = null,
    subflows = [],
    onselect = () => {},
    class: klass = "",
  }: {
    doc: Doc;
    log?: Leg[] | null;
    selected?: string | null;
    instanceStatus?: string | null;
    currentStep?: string | null;
    subflows?: Subflow[];
    onselect?: (stepId: string | null) => void;
    class?: string;
  } = $props();

  const model = $derived(
    buildModel({ doc, log, instanceStatus, currentStep, childInstances: subflows }),
  );

  const nodes = $derived.by<Node<{ flow: (typeof model.nodes)[number] }>[]>(() =>
    model.nodes.map((n) => ({
      id: n.id,
      type: "step",
      position: { x: n.x, y: n.y },
      data: { flow: n },
      draggable: false,
      selectable: true,
    })),
  );

  const edges = $derived.by<Edge[]>(() =>
    model.edges.map((e) => ({
      id: e.id,
      source: e.from,
      target: e.to,
      type: "smoothstep" as const,
      ...(e.label ? { label: e.label } : {}),
      style:
        "stroke:var(--color-edge-2);stroke-width:1.5px" + (e.dashed ? ";stroke-dasharray:5 4" : ""),
      labelStyle: "fill:var(--color-ink-3);font-size:11px;font-family:var(--font-mono)",
      markerEnd: { type: MarkerType.ArrowClosed, color: "var(--color-edge-2)" },
    })),
  );

  const nodeTypes = { step: StepNode };
  const busy = $derived(model.nodes.length > 50); // only cull at crowd sizes
</script>

<div
  class="flow-canvas {klass}"
  data-testid="flow-graph">
  <SvelteFlow
    {nodes}
    {edges}
    {nodeTypes}
    colorMode={theme.value === "dark" ? "dark" : "light"}
    nodesConnectable={false}
    elementsSelectable
    onlyRenderVisibleElements={busy}
    fitView
    onnodeclick={({ node }) => onselect(node.id === selected ? null : node.id)}
    onpaneclick={() => onselect(null)}
  />
</div>

<style>
  /* xyflow sizes edge labels to the edge bbox and clips overflow - on
     short legs that truncates the text to a stray initial. Size to
     content; colors keep the site theme (light/dark both legible). */
  .flow-canvas :global(.svelte-flow__edge-label) {
    width: max-content !important;
    max-width: none !important;
    height: max-content !important;
    overflow: visible !important;
    text-overflow: clip !important;
    white-space: nowrap;
    border-radius: 4px;
    background: var(--surface);
    padding: 1px 4px;
    border: 1px solid var(--line-soft);
  }
  .flow-canvas {
    height: 440px;
    overflow: hidden;
    border: 1px solid var(--line);
    border-radius: var(--radius-overlay, 6px);
    background-color: var(--bg);
    background-image: radial-gradient(var(--grid-dot) 1px, transparent 1px);
    background-size: 16px 16px;
  }
</style>
