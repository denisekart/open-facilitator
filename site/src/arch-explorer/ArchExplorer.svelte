<script>
  import { SvelteFlowProvider } from "@xyflow/svelte";
  import ExplorerInner from "./ExplorerInner.svelte";

  // The provider must exist ABOVE the component that calls useSvelteFlow,
  // so this wrapper owns it and the inner component owns the canvas.
  let { data, focus = null } = $props();
</script>

<SvelteFlowProvider>
  <ExplorerInner {data} {focus} />
</SvelteFlowProvider>
