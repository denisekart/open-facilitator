<script lang="ts">
  import { Inbox } from "lucide-svelte";

  // One of the console's only three "nothing here" components (with Skeleton
  // for loading and ProblemNote for failure): say what is empty, say what
  // fills it, and hand over the action that does.
  let {
    title,
    hint = null,
    actionHref = null,
    actionLabel = null,
    testid = "empty-state",
  }: {
    title: string;
    hint?: string | null;
    actionHref?: string | null;
    actionLabel?: string | null;
    testid?: string;
  } = $props();
</script>

<div class="flex flex-col items-center gap-2 py-8 text-center" data-testid={testid}>
  <Inbox class="size-6 text-ink-2" strokeWidth={1.5} aria-hidden="true" />
  <p class="text-sm font-medium text-ink-2">{title}</p>
  {#if hint}
    <p class="max-w-sm text-xs text-ink-2">{hint}</p>
  {/if}
  {#if actionHref && actionLabel}
    <a class="mono text-sm text-accent underline" href={actionHref}>{actionLabel}</a>
  {/if}
</div>
