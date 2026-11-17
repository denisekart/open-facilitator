<!-- The product mark (§8): one construction system with the icon set —
     stroke + currentColor, one accent dot carrying identity, legible at
     16px, no animation, no shadow. -->
<script lang="ts">
  let { class: klass = "" }: { class?: string } = $props();
</script>

<svg class={klass} viewBox="0 0 32 32" aria-hidden="true"
  ><g stroke="currentColor" stroke-width="2.6" stroke-linecap="round"
    ><path d="M11.8 11.8 L6.4 6.4" /><path d="M20.2 11.8 L25.6 6.4" /><path
      d="M11.8 20.2 L6.4 25.6" /><path d="M20.2 20.2 L25.6 25.6" /></g
  ><rect x="9.6" y="9.6" width="12.8" height="12.8" rx="3.4" fill="currentColor" /><circle
    cx="4.6" cy="4.6" r="2.7" fill="currentColor" opacity="0.55" /><circle
    cx="27.4" cy="4.6" r="2.7" fill="currentColor" opacity="0.55" /><circle
    cx="4.6" cy="27.4" r="2.7" fill="currentColor" opacity="0.55" /><circle
    cx="27.4" cy="27.4" r="2.7" style="fill: var(--accent)" /></svg
>
