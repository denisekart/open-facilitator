<script lang="ts">
  import {
    Brain,
    Check,
    CircleAlert,
    CircleQuestionMark,
    CircleStop,
    Clock,
    GitBranchPlus,
    GitMerge,
    Mail,
    MessageSquare,
    Play,
    Server,
    ShieldCheck,
    Split,
    Timer,
    Workflow,
  } from "lucide-svelte";
  import { Handle, Position } from "@xyflow/svelte";
  import type { FlowNode } from "../lib/flow-layout.js";

  // One custom node for every kind: lucide icon + id + sub-line, ringed in
  // the tone its latest leg earned. Structured header pill with category
  // and body with clear technical typography.
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

  const kindLabels: Record<string, string> = {
    trigger: "Trigger",
    end: "End",
    emit: "Emit Event",
    wait: "Wait For",
    timer: "Timer Delay",
    approval: "Policy Gate",
    decide: "Branch Rule",
    select: "Condition",
    "fan-out": "Fan-Out",
    join: "Join Merge",
    "fork-work": "Fork Work",
    "work-create": "Create Work",
    "sign-over": "Sign-Over",
    escalate: "Escalate",
    memory: "Recall / Store",
    signal: "Dispatch Signal",
    invoke: "Invoke Workflow",
    child: "Subflow Leg",
    "?": "Step",
  };

  const IconComponent = $derived(iconFor[node.kind] ?? CircleAlert);
  const kindTitle = $derived(kindLabels[node.kind] ?? node.kind);
</script>

<div
  class="step-node tone-{node.tone}"
  class:selected={selected}
  data-testid={`canvas-node-${node.id}`}>
  <Handle type="target" position={Position.Top} class="node-handle" />

  <div class="node-header">
    <span class="node-kind-badge">
      <IconComponent class="node-icon" strokeWidth={1.5} aria-hidden="true" />
      <span class="node-kind-text">{kindTitle}</span>
    </span>
    <span class="node-status-dot" aria-hidden="true"></span>
  </div>

  <div class="node-body">
    <div class="node-title" title={node.label}>{node.label}</div>
    {#if node.sub}
      <div class="node-sub" title={node.sub}>{node.sub}</div>
    {/if}
  </div>

  <Handle type="source" position={Position.Bottom} class="node-handle" />
</div>

<style>
  .step-node {
    width: 200px;
    min-height: 64px;
    border-radius: var(--radius-overlay, 6px);
    border: 1px solid var(--line);
    background: var(--surface);
    color: var(--ink);
    padding: 8px 10px;
    text-align: left;
    box-shadow: var(--shadow-1);
    display: flex;
    flex-direction: column;
    gap: 4px;
    transition: border-color 0.12s ease, background-color 0.12s ease;
  }

  .node-header {
    display: flex;
    align-items: center;
    justify-content: space-between;
    gap: 6px;
  }

  .node-kind-badge {
    display: inline-flex;
    align-items: center;
    gap: 4px;
    font-size: 11px;
    font-weight: 600;
    text-transform: uppercase;
    letter-spacing: 0.04em;
    color: var(--ink-muted);
  }

  :global(.node-icon) {
    width: 14px;
    height: 14px;
    flex-shrink: 0;
    color: currentColor;
  }

  .node-kind-text {
    overflow: hidden;
    text-overflow: ellipsis;
    white-space: nowrap;
    max-width: 140px;
  }

  .node-status-dot {
    width: 6px;
    height: 6px;
    border-radius: 9999px;
    background: var(--line);
    flex-shrink: 0;
  }

  .node-body {
    display: flex;
    flex-direction: column;
    gap: 1px;
  }

  .node-title {
    font-size: 13px;
    font-weight: 600;
    color: var(--ink);
    overflow: hidden;
    text-overflow: ellipsis;
    white-space: nowrap;
    line-height: 1.3;
  }

  .node-sub {
    font-family: var(--font-mono);
    font-size: 11px;
    color: var(--ink-muted);
    overflow: hidden;
    text-overflow: ellipsis;
    white-space: nowrap;
    line-height: 1.25;
  }

  /* Tone styling */
  .tone-trigger {
    border-color: var(--border-primary);
    background: var(--tint-primary);
  }
  .tone-trigger .node-kind-badge {
    color: var(--accent-text);
  }
  .tone-trigger .node-status-dot {
    background: var(--accent);
  }

  .tone-ok {
    background: var(--success-tint);
    border-color: var(--success-border);
  }
  .tone-ok .node-kind-badge {
    color: var(--success);
  }
  .tone-ok .node-status-dot {
    background: var(--success);
  }

  .tone-bad {
    background: var(--danger-tint);
    border-color: var(--danger-border);
  }
  .tone-bad .node-kind-badge {
    color: var(--danger-text);
  }
  .tone-bad .node-status-dot {
    background: var(--danger-text);
  }

  .tone-skip,
  .tone-pending {
    background: transparent;
    border-style: dashed;
    border-color: var(--ink-faint);
  }

  .tone-shadow {
    border-style: dotted;
    border-color: var(--warning-border);
    background: var(--warning-tint);
  }
  .tone-shadow .node-kind-badge {
    color: var(--warning);
  }

  .tone-live {
    border-color: var(--border-primary);
    border-width: 2px;
    background: var(--tint-primary);
  }
  .tone-live .node-kind-badge {
    color: var(--accent-text);
  }
  .tone-live .node-status-dot {
    background: var(--accent);
  }

  .tone-park-live {
    border-color: var(--border-primary);
    border-width: 2px;
    border-style: dashed;
    background: var(--tint-primary);
  }

  .tone-paused {
    border-color: var(--warning-border);
    border-style: dashed;
    background: var(--warning-tint);
  }
  .tone-paused .node-status-dot {
    background: var(--warning);
  }

  .tone-act {
    border-color: var(--line);
    background: var(--surface);
  }

  .tone-park {
    border-color: var(--info-border);
    background: var(--info-tint);
  }
  .tone-park .node-kind-badge {
    color: var(--info);
  }
  .tone-park .node-status-dot {
    background: var(--info);
  }

  .step-node.selected {
    border: 2px solid var(--accent-rule) !important;
    background: var(--tint-primary);
  }

  :global(.node-handle) {
    width: 8px !important;
    height: 8px !important;
    background: var(--line) !important;
    border: 2px solid var(--surface) !important;
    border-radius: 9999px !important;
  }
  :global(.step-node:hover .node-handle) {
    background: var(--accent) !important;
  }
</style>
