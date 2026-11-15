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
  }: {
    doc: Doc;
    log?: Leg[] | null;
    selected?: string | null;
    instanceStatus?: string | null;
    currentStep?: string | null;
    subflows?: Subflow[];
    onselect?: (stepId: string | null) => void;
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
        "stroke:var(--color-edge-2)" + (e.dashed ? ";stroke-dasharray:5 4" : ""),
      labelStyle: "fill:var(--color-ink-3);font-size:11px",
      markerEnd: { type: MarkerType.ArrowClosed, color: "var(--color-edge-2)" },
    })),
  );

  const nodeTypes = { step: StepNode };
  const busy = $derived(model.nodes.length > 50); // only cull at crowd sizes
</script>

<div
  class="h-[420px] overflow-hidden rounded border border-edge bg-sunken/20"
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
