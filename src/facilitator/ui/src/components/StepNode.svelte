<script lang="ts">
  import {
    Check,
    CircleAlert,
    CircleStop,
    CircleQuestionMark,
    Clock,
    GitMerge,
    GitBranchPlus,
    Mail,
    MessageSquare,
    Play,
    Server,
    ShieldCheck,
    Timer,
    Workflow,
    Brain,
    Split,
  } from "lucide-svelte";
  import { Handle, Position } from "@xyflow/svelte";
  import type { FlowNode } from "../lib/flow-layout.js";

  // One custom node for every kind: lucide icon + id + sub-line, ringed in
  // the tone its latest leg earned. Icons are lucide at strokeWidth 1.5.
  // Mounted by Svelte Flow as a nodeType, so it speaks NodeProps: xyflow
  // owns `selected` (elementsSelectable), the flow node rides in `data`.
  let {
    data,
    selected = false,
  }: { data: { flow: FlowNode }; selected?: boolean } = $props();

  const node = $derived(data.flow);

  const iconFor: Record<string, typeof Play> = {
    trigger: Play,
    end: CircleStop,
    emit: Mail,
    wait: Clock,
    timer: Timer,
    approval: ShieldCheck,
    decide: CircleQuestionMark,
    select: Check,
    "fan-out": Split,
    join: GitMerge,
    "fork-work": GitBranchPlus,
    "work-create": Workflow,
    "sign-over": GitBranchPlus,
    escalate: MessageSquare,
    memory: Brain,
    signal: Server,
    invoke: Server,
    child: Workflow,
    "?": CircleAlert,
  };
</script>

<div
  class="step-node tone-{node.tone} {selected ? 'ring-2 ring-accent' : ''}"
  data-testid={`canvas-node-${node.id}`}>
  <Handle type="target" position={Position.Top} />
  <div class="flex items-center gap-2">
    <svelte:component
      this={iconFor[node.kind] ?? CircleAlert}
      class="size-4 shrink-0"
      strokeWidth={1.5}
      aria-hidden="true" />
    <span class="truncate text-sm font-medium">{node.label}</span>
  </div>
  <p class="mt-0.5 truncate text-xs text-ink-3">{node.sub}</p>
  <Handle type="source" position={Position.Bottom} />
</div>

<style>
  .step-node {
    width: 190px;
    min-height: 58px;
    border-radius: 10px;
    border: 1.5px solid var(--color-edge);
    background: var(--color-sunken);
    color: var(--color-ink);
    padding: 8px 10px;
    text-align: left;
  }
  .tone-ok {
    background: var(--color-ok-veil);
    border-color: var(--color-ok);
  }
  .tone-bad {
    background: var(--color-danger-veil);
    border-color: var(--color-danger);
  }
  .tone-skip,
  .tone-pending {
    background: transparent;
    border-style: dashed;
    border-color: var(--color-ink-3);
  }
  .tone-shadow {
    border-style: dotted;
    border-color: var(--color-ink-3);
  }
  .tone-live {
    border-color: var(--color-accent);
    border-width: 2.5px;
  }
  .tone-park-live {
    border-color: var(--color-amber);
    border-width: 2.5px;
  }
  .tone-paused {
    border-color: var(--color-amber);
    border-style: dashed;
  }
  .tone-trigger {
    border-color: var(--color-accent);
    background: var(--color-accent-veil);
  }
</style>
