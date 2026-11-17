<script>
  import {
    SvelteFlow,
    Background,
    Controls,
    MiniMap,
    useSvelteFlow,
  } from "@xyflow/svelte";
  import "@xyflow/svelte/dist/style.css";
  import { toFlow, OWN_STYLE } from "./from-model.js";
  import ZoneNode from "./ZoneNode.svelte";
  import PartNode from "./PartNode.svelte";
  import CorridorEdge from "./CorridorEdge.svelte";

  // The interactive architecture map. The static SVG snapshot stays the
  // no-JS face; this bundle replaces it when present. Laws:
  // - node labels are visible at EVERY zoom band: zoom adds detail, never
  //   removes the label (user law).
  // - ownership readable at a glance: zone fill/border by `own`, chip on
  //   the zone header, same hue on node borders.
  // - scenario playback lights a complete dependency chain: every step's
  //   edges are real edges, and the camera centers on the step.

  let { data, focus = null } = $props();

  const flow = useSvelteFlow();
  const base = $derived(toFlow(data));

  let zoomBand = $state("far"); // far | mid | near | deep
  let selection = $state(null); // node id
  let scenario = $state(null); // {sc, i}
  let hover = $state(null);

  const nodeById = $derived(Object.fromEntries(data.nodes.map((n) => [n.id, n])));

  function band(k) {
    return k < 0.45 ? "far" : k < 0.9 ? "mid" : k < 1.7 ? "near" : "deep";
  }

  // incident-edge highlight set for hover/selection/scenario
  function litSets() {
    if (scenario) {
      const st = scenario.sc.steps[scenario.i];
      return [new Set(st.nodes), new Set(st.edges)];
    }
    const anchor = selection ?? hover;
    if (!anchor) return [null, null];
    const ns = new Set([anchor]);
    const es = new Set();
    for (const e of data.edges) {
      if (e.from === anchor || e.to === anchor) {
        es.add(e.id);
        ns.add(e.from);
        ns.add(e.to);
      }
    }
    return [ns, es];
  }

  const nodes = $derived.by(() => {
    const [lit, litE] = litSets();
    return base.nodes.map((n) => {
      if (n.type === "zone") {
        return {
          ...n,
          hidden: false,
          data: {
            ...n.data,
            band: zoomBand,
            dim: lit !== null && lit.size > 0 && false,
          },
        };
      }
      const dim = lit !== null && !lit.has(n.id);
      return {
        ...n,
        // Deep-band annotations paint above neighbouring cards.
        ...(zoomBand === "deep" ? { zIndex: 5 } : {}),
        data: {
          ...n.data,
          band: zoomBand,
          selected: selection === n.id,
          dim,
          lit: lit !== null && lit.has(n.id) && litE !== null,
        },
      };
    });
  });

  const edges = $derived.by(() => {
    const [, litE] = litSets();
    const packet = scenario ? new Set(scenario.sc.steps[scenario.i].edges) : null;
    return base.edges.map((e) => {
      const dim = litE !== null && !litE.has(e.id);
      return {
        ...e,
        animated: packet !== null && packet.has(e.id),
        zIndex: packet !== null && packet.has(e.id) ? 10 : 0,
        data: { ...e.data, dim },
        style:
          e.style +
          (packet !== null && packet.has(e.id) && !e.style.includes("dasharray")
            ? ";stroke-dasharray:7 7"
            : "") +
          (dim ? ";opacity:0.12" : ""),
      };
    });
  });

  const nodeTypes = { zone: ZoneNode, part: PartNode };
  const edgeTypes = { corridor: CorridorEdge };

  // The map is a fixed dark-slate instrument surface in BOTH site themes
  // (the code-block precedent), so xyflow is pinned dark and the header
  // toggle does not touch it.

  function onNodeClick({ node }) {
    if (node.type !== "part") return;
    scenario = null;
    selection = node.id;
  }
  function onPaneClick() {
    selection = null;
    scenario = null;
  }
  // xyflow svelte calls move handlers positionally: (event, viewport).
  function onMoveEnd(_event, viewport) {
    zoomBand = band(viewport?.zoom ?? flow.getViewport()?.zoom ?? 1);
  }
  function onInit(viewport) {
    zoomBand = band(viewport?.zoom ?? flow.getViewport()?.zoom ?? 1);
  }

  function centerOn(nodeIds) {
    const xs = [];
    const ys = [];
    for (const id of nodeIds) {
      const n = nodeById[id];
      if (!n) continue;
      xs.push(n.x + n.w / 2);
      ys.push(n.y + n.h / 2);
    }
    if (xs.length === 0) return;
    const cx = xs.reduce((a, b) => a + b, 0) / xs.length;
    const cy = ys.reduce((a, b) => a + b, 0) / ys.length;
    flow.setCenter(cx, cy, { zoom: 0.9, duration: 400 });
  }

  function startScenario(sc) {
    selection = null;
    scenario = { sc, i: 0 };
    centerOn(sc.steps[0].nodes);
  }
  function stepScenario(delta) {
    if (!scenario) return;
    const next = scenario.i + delta;
    if (next < 0 || next >= scenario.sc.steps.length) return;
    scenario = { sc: scenario.sc, i: next };
    centerOn(scenario.sc.steps[next].nodes);
  }
  function exitScenario() {
    scenario = null;
  }

  // Deep link: ?focus=<nodeId> / #focus= opens that node's sheet.
  $effect(() => {
    if (focus && nodeById[focus]) {
      selection = focus;
      centerOn([focus]);
    }
  });

  function keydown(event) {
    if (event.key === "f" && !event.metaKey && !event.ctrlKey) {
      const host = event.currentTarget?.closest?.(".arch-explorer") || event.currentTarget;
      if (document.fullscreenElement) document.exitFullscreen();
      else host?.requestFullscreen?.();
    }
  }

  const step = $derived(scenario ? scenario.sc.steps[scenario.i] : null);
</script>

<!-- svelte-ignore a11y_no_noninteractive_element_interactions -->
  <!-- svelte-ignore a11y_no_noninteractive_tabindex -->
  <div class="arch-explorer" role="application" aria-label="architecture explorer" tabindex="0" onkeydown={keydown}>
    <SvelteFlow
      {nodes}
      {edges}
      {nodeTypes}
      {edgeTypes}
      colorMode="dark"
      minZoom={0.2}
      maxZoom={2.4}
      nodesConnectable={false}
      elementsSelectable={false}
      zoomOnDoubleClick={false}
      fitView
      fitViewOptions={{ padding: 0.06 }}
      onnodeclick={onNodeClick}
      onpaneclick={onPaneClick}
      oninit={onInit}
      onnodeenter={(e) => (hover = e.node?.id ?? null)}
      onnodeleave={() => (hover = null)}
      onmovestart={() => undefined}
      onmoveend={onMoveEnd}
    >
      <Background gap={24} size={1} color="#464c54" />
      <Controls showInteractive={false} />
      {#if zoomBand === "mid"}
        <MiniMap
          pannable
          zoomable
          position="bottom-right"
          ariaLabel="minimap"
          maskColor="rgba(6,10,16,0.55)"
          nodeColor={(n) =>
            n.type === "zone"
              ? { you: "#4d4a3a", infra: "#3b4046", team: "#57523b", external: "#3b4046" }[
                  n.data?.zone?.own
                ] || "#3b4046"
              : n.data?.own === "team"
                ? "#e6bf34"
                : "#4d4a3a"}
          nodeStrokeWidth={0}
        />
      {/if}
    </SvelteFlow>

    <button
      class="arch-full"
      type="button"
      aria-label="Toggle fullscreen (F)"
      onclick={() => {
        const host = document.querySelector(".arch-explorer");
        if (document.fullscreenElement) document.exitFullscreen();
        else host?.requestFullscreen?.();
      }}>⛶</button>

    <div class="arch-scenrow" role="group" aria-label="Ways to explore">
      {#each data.scenarios as sc (sc.id)}
        <button class="arch-scen" data-scenario={sc.id} onclick={() => startScenario(sc)}>
          {sc.title}
        </button>
      {/each}
    </div>

    <div class="arch-ownkey" aria-label="ownership legend">
      {#each Object.entries(OWN_STYLE) as [own, s] (own)}
        <span><i style="background:{s.fill};border-color:{s.border}"></i>{s.chip}</span>
      {/each}
    </div>

    {#if step}
      <div class="arch-player" aria-live="polite">
        <p class="arch-cap">
          <span class="arch-step-n">Step {scenario.i + 1} of {scenario.sc.steps.length}</span>
          {step.caption}
        </p>
        <div class="arch-nav">
          <button data-p="prev" disabled={scenario.i === 0} onclick={() => stepScenario(-1)}>← Previous</button>
          <button
            data-p="next"
            class="primary"
            disabled={scenario.i === scenario.sc.steps.length - 1}
            onclick={() => stepScenario(1)}>Next →</button>
          <button data-p="exit" onclick={exitScenario}>Exit walk</button>
        </div>
      </div>
    {/if}

    {#if selection && nodeById[selection]}
      {@const n = nodeById[selection]}
      <aside class="arch-sheet" aria-label="details">
        <button class="arch-sheet-x" aria-label="close details" onclick={() => (selection = null)}>×</button>
        <h3>{n.t}</h3>
        {#if n.sub}<p class="arch-sub">{n.sub}</p>{/if}
        <p>{n.what}</p>
        {#if n.bullets?.length}
          <ul>{#each n.bullets as b (b)}<li>{b}</li>{/each}</ul>
        {/if}
        {#if n.artifact}<p class="arch-art">{n.artifact}</p>{/if}
        {#if n.docs}<p><a class="arch-more" href={n.docs}>Read more →</a></p>{/if}
      </aside>
    {/if}
  </div>

<style>
  .arch-explorer :global(.svelte-flow__handle) {
    opacity: 0;
    pointer-events: none;
  }
  /* Edge labels ride the slate as small chips - legible over zone
     borders and never clipped mid-word, and painted ABOVE the zone
     fills (the container defaults to z:auto and loses to nodes). */
  .arch-explorer :global(.svelte-flow__edge-labels) {
    z-index: 3;
  }
  .arch-explorer :global(.svelte-flow__edge-label) {
    max-width: none;
    width: auto;
    overflow: visible;
    text-overflow: clip;
    color: #99a1ab;
    background: #2b2e33;
    border: 1px solid #464c54;
    padding: 1px 6px;
    border-radius: 5px;
    font-size: 10.5px;
  }
  .arch-explorer :global(.svelte-flow__minimap) {
    border: 1px solid #565d66;
    border-radius: 8px;
    overflow: hidden;
    background: #2b2e33;
  }
  @media (prefers-reduced-motion: reduce) {
    .arch-explorer :global(.svelte-flow__edge.animated path) {
      animation: none;
    }
  }
  .arch-explorer {
    position: relative;
    height: min(78vh, 860px);
    border: 1px solid #464c54;
    border-radius: 12px;
    overflow: hidden;
    background: #2b2e33;
    color-scheme: dark;
    --chrome-line: #565d66;
    --chrome-bg: rgba(13, 20, 32, 0.88);
    --chrome-ink: #d6dae0;
  }
  .arch-full {
    position: absolute;
    top: 10px;
    left: 10px;
    z-index: 5;
    border: 1px solid var(--chrome-line);
    border-radius: 8px;
    background: var(--chrome-bg);
    color: var(--chrome-ink);
    font-size: 15px;
    line-height: 1;
    padding: 5px 8px;
    cursor: pointer;
  }
  .arch-scenrow {
    position: absolute;
    top: 10px;
    left: 50%;
    transform: translateX(-50%);
    z-index: 5;
    display: flex;
    gap: 8px;
  }
  .arch-scen {
    border: 1px solid var(--chrome-line);
    border-radius: 999px;
    background: var(--chrome-bg);
    color: var(--chrome-ink);
    font-size: 12px;
    padding: 4px 12px;
    cursor: pointer;
  }
  .arch-scen:hover {
    border-color: #e6bf34;
  }
  .arch-ownkey {
    position: absolute;
    left: 10px;
    bottom: 10px;
    z-index: 5;
    display: flex;
    gap: 12px;
    padding: 6px 10px;
    border-radius: 8px;
    background: var(--chrome-bg);
    border: 1px solid var(--chrome-line);
    color: var(--chrome-ink);
    font-size: 12px;
  }
  .arch-ownkey span {
    display: inline-flex;
    align-items: center;
    gap: 5px;
  }
  .arch-ownkey i {
    display: inline-block;
    width: 12px;
    height: 12px;
    border-radius: 3px;
    border: 2px solid;
  }
  .arch-player {
    position: absolute;
    right: 12px;
    bottom: 12px;
    z-index: 6;
    max-width: 420px;
    padding: 12px 14px;
    border-radius: 10px;
    /* Solid card: on the slate map a translucent panel lets edges and
       zones bleed through the caption text. */
    background: #33373d;
    border: 1px solid #565d66;
    box-shadow: 0 6px 24px rgba(4, 8, 14, 0.5);
    color: #d6dae0;
  }
  .arch-cap {
    margin: 0 0 10px;
    font-size: 13px;
    line-height: 1.45;
    color: #d6dae0;
  }
  .arch-step-n {
    display: inline-block;
    font-weight: 700;
    margin-right: 6px;
    color: #e6bf34;
  }
  .arch-nav {
    display: flex;
    gap: 8px;
  }
  .arch-nav button {
    border: 1px solid var(--chrome-line);
    border-radius: 8px;
    background: #33373d;
    color: var(--chrome-ink);
    font-size: 12px;
    padding: 4px 10px;
    cursor: pointer;
  }
  .arch-nav button.primary {
    background: #ab8612;
    border-color: #ab8612;
    color: var(--accent-on, #1b1e22);
  }
  .arch-nav button:disabled {
    opacity: 0.45;
    cursor: default;
  }
  .arch-sheet {
    position: absolute;
    top: 12px;
    right: 12px;
    z-index: 6;
    width: 320px;
    max-height: calc(100% - 24px);
    overflow: auto;
    padding: 12px 14px;
    border-radius: 10px;
    background: #33373d;
    border: 1px solid #565d66;
    box-shadow: 0 6px 24px rgba(4, 8, 14, 0.5);
    color: #d6dae0;
  }
  .arch-sheet p {
    color: #d6dae0;
  }
  .arch-sheet ul {
    color: #d6dae0;
  }
  .arch-sheet h3 {
    color: #d6dae0;
  }
  .arch-sheet a {
    color: #e6bf34;
  }
  .arch-sheet li {
    color: #d6dae0;
  }
  .arch-sheet-x {
    float: right;
    border: 0;
    background: none;
    color: #99a1ab;
    font-size: 18px;
    cursor: pointer;
  }
  .arch-sub {
    color: #99a1ab;
    font-size: 12px;
  }
  .arch-art {
    font-size: 12px;
    color: #99a1ab;
  }
</style>
