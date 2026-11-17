<script>
  import { Handle, Position } from "@xyflow/svelte";
  import { OWN_STYLE } from "./from-model.js";

  // One part of the system. The label is ALWAYS visible (every band);
  // bands only add detail: mid adds the sub-line, near adds the click
  // affordance, deep (zoomed on one component) expands the node's own
  // one-line brief and artifact right there on the card - the sheet
  // stays the home for the full dossier. The border carries the zone's
  // ownership hue so the node stays classifiable even when its zone
  // scrolls off. All colours are fixed for the dark-slate map surface
  // (constant across site themes).
  let { data } = $props();
  const n = $derived(data.part);
  const band = $derived(data.band ?? "near");
  const selected = $derived(data.selected === true);
  const dim = $derived(data.dim === true);
  const own = $derived(OWN_STYLE[data.own] || OWN_STYLE.external);
  const POSITIONS = {
    top: Position.Top,
    right: Position.Right,
    bottom: Position.Bottom,
    left: Position.Left,
  };
</script>

<div
  class="arch-part"
  class:ghost={data.ghost}
  class:selected
  class:dim
  class:far={band === "far"}
  style="border-color:{own.border}"
  role="button"
  tabindex="-1"
  aria-label={n.t}
>
  <span class="arch-part-t">{n.t}</span>
  {#if band !== "far" && band !== "deep" && n.sub}
    <span class="arch-part-sub">{n.sub}</span>
  {/if}
</div>
{#if band === "deep"}
  <!-- Deep tier: the authored rect stays the map; the brief rides as an
       attached annotation card below it, so expansion never collides
       with the fixed grid. -->
  <div class="arch-part-peek" style="border-color:{own.border}">
    {#if n.sub}<span class="arch-part-sub">{n.sub}</span>{/if}
    {#if n.what}<span class="arch-part-what">{n.what}</span>{/if}
    {#if n.artifact}<span class="arch-part-art">{n.artifact}</span>{/if}
  </div>
{/if}

{#each ["top", "right", "bottom", "left"] as side (side)}
  <Handle id="{side}-tgt" type="target" position={POSITIONS[side]} connectable={false} />
  <Handle id="{side}-src" type="source" position={POSITIONS[side]} connectable={false} />
{/each}

<style>
  .arch-part {
    width: 100%;
    height: 100%;
    box-sizing: border-box;
    border: 2px solid;
    border-radius: 8px;
    background: #33373d;
    padding: 5px 8px;
    display: flex;
    flex-direction: column;
    justify-content: center;
    gap: 2px;
    overflow: hidden;
    cursor: pointer;
    transition: opacity 160ms ease, box-shadow 160ms ease;
  }
  .arch-part.ghost {
    border-style: dashed;
    opacity: 0.85;
    background: #2b2e33;
  }
  .arch-part.selected {
    box-shadow: 0 0 0 3px rgba(76, 141, 255, 0.45);
  }
  .arch-part.dim {
    opacity: 0.18;
  }
  .arch-part.far {
    padding: 2px 5px;
    justify-content: center;
    align-items: center;
    text-align: center;
  }
  .arch-part-t {
    font-size: 12px;
    font-weight: 650;
    line-height: 1.15;
    color: #d6dae0;
  }
  .arch-part.far .arch-part-t {
    font-size: 10.5px;
  }
  .arch-part-sub {
    font-size: 10px;
    color: #99a1ab;
    white-space: nowrap;
    overflow: hidden;
    text-overflow: ellipsis;
  }
  .arch-part.deep .arch-part-t {
    font-size: 13px;
  }
  .arch-part-peek {
    position: absolute;
    top: calc(100% + 3px);
    left: 0;
    right: 0;
    display: flex;
    flex-direction: column;
    gap: 3px;
    padding: 6px 8px;
    box-sizing: border-box;
    border: 1.5px solid;
    border-radius: 8px;
    background: #2b2e33;
    box-shadow: 0 4px 16px rgba(4, 8, 14, 0.45);
  }
  .arch-part-what {
    font-size: 10.5px;
    line-height: 1.35;
    color: #d6dae0;
    display: -webkit-box;
    -webkit-line-clamp: 4;
    -webkit-box-orient: vertical;
    overflow: hidden;
  }
  .arch-part-art {
    font: 500 9.5px/1.3 var(--mono, ui-monospace, monospace);
    color: #99a1ab;
    white-space: nowrap;
    overflow: hidden;
    text-overflow: ellipsis;
  }
</style>
