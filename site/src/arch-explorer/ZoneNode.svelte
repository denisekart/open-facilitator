<script>
  import { OWN_STYLE } from "./from-model.js";

  // A zone: the ownership band. Far band shows only the zone title, the
  // chip always rides the header (ownership readable at a glance).
  let { data } = $props();
  const z = $derived(data.zone);
  const style = $derived(OWN_STYLE[z.own] || OWN_STYLE.external);
</script>

<div
  class="arch-zone own-{z.own}"
  class:dashed={data.dashed}
  style="background:{style.fill};border-color:{style.border}"
>
  <span class="arch-zone-title">{z.title}</span>
  <span class="arch-zone-chip" style="border-color:{style.border};color:{style.border}">
    {z.tag || style.chip}
  </span>
</div>

<style>
  .arch-zone {
    position: relative;
    width: 100%;
    height: 100%;
    border: 1.6px solid;
    border-radius: 14px;
    padding: 6px 10px;
    box-sizing: border-box;
    pointer-events: none;
    opacity: 0.9;
  }
  .arch-zone.dashed {
    border-style: dashed;
  }
  .arch-zone-title {
    display: block;
    font-size: 13px;
    font-weight: 700;
    line-height: 1.25;
    color: #f3f4f6;
  }
  .arch-zone-chip {
    position: absolute;
    top: 26px;
    left: 10px;
    font-size: 10px;
    border: 1px solid;
    border-radius: 999px;
    padding: 1px 7px;
    white-space: nowrap;
    background: rgba(13, 20, 32, 0.55);
  }
</style>
