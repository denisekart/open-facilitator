<script lang="ts">
  // One of the console's only three "nothing here" components (with
  // EmptyState for empty and ProblemNote for failure): rows shaped like the
  // content that is coming, so the layout does not jump when it lands.
  let {
    rows = 3,
    testid = "skeleton",
  }: {
    rows?: number;
    testid?: string;
  } = $props();
</script>

<div class="grid gap-2" data-testid={testid} aria-hidden="true">
  {#each Array(rows) as _, i (i)}
    <div class="h-5 animate-pulse rounded bg-sunken"></div>
  {/each}
</div>
