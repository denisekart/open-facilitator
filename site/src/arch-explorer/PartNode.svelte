<script>
  import { Handle, Position } from "@xyflow/svelte";
  import { OWN_STYLE } from "./from-model.js";

  // One part of the system. The label is ALWAYS visible (every band);
  // bands only add detail: mid adds the sub-line, near adds the click
  // affordance. The border carries the zone's ownership hue so the node
  // stays classifiable even when its zone scrolls off.
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
  {#if band !== "far" && n.sub}
    <span class="arch-part-sub">{n.sub}</span>
  {/if}
</div>

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
    background: var(--arch-card, #fff);
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
  }
  .arch-part.selected {
    box-shadow: 0 0 0 3px color-mix(in srgb, var(--accent, #0b5fbf) 35%, transparent);
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
    color: var(--ink, #18222f);
  }
  .arch-part.far .arch-part-t {
    font-size: 10.5px;
  }
  .arch-part-sub {
    font-size: 10px;
    color: var(--ink-3, #64748b);
    white-space: nowrap;
    overflow: hidden;
    text-overflow: ellipsis;
  }
</style>
