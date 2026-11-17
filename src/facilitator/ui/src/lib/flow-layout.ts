// The flow picture's brain: graph extraction, tone law and layered layout,
// as pure functions the vitest suite can exercise without a DOM. Rendering
// (FlowCanvas.svelte) consumes this; the extraction law mirrors the
// runner's edge semantics (ported from the old hand-rolled FlowGraph).

import dagre from "dagre";

export type Doc = Record<string, unknown>;

export type FlowNode = {
  id: string; // step id, "trigger", or "end"
  label: string;
  sub: string;
  kind: string;
  tone: Tone;
  x: number;
  y: number;
  width: number;
  height: number;
};

export type FlowEdge = {
  id: string;
  from: string;
  to: string;
  label: string;
  dashed: boolean;
};

export type Tone =
  | "ok"
  | "bad"
  | "skip"
  | "shadow"
  | "pending"
  | "park"
  | "call"
  | "branch"
  | "act"
  | "calc"
  | "trigger"
  | "end"
  | "live"
  | "paused"
  | "park-live";

export type Leg = { stepId: string; status: string; output: string | null };

export const KINDS = [
  "emit",
  "wait",
  "timer",
  "approval",
  "decide",
  "select",
  "fan-out",
  "join",
  "fork-work",
  "work-create",
  "sign-over",
  "escalate",
  "memory",
  "signal",
  "invoke",
] as const;

export const NODE_W = 190;
export const NODE_H = 58;

export type Extracted = {
  steps: { id: string; title: string; on: string; kind: string; payload: Doc }[];
  edges: FlowEdge[];
  triggerLabel: string;
};

export function extract(doc: Doc, instanceStatus: string | null = null): Extracted {
  const steps = ((doc.steps ?? []) as Doc[]).map((s) => {
    const kind = KINDS.find((k) => k in s) ?? "?";
    return {
      id: String(s.id ?? "?"),
      title: String(s.title ?? ""),
      on: String(s.on ?? "always"),
      kind,
      payload: (s[kind] ?? {}) as Doc,
    };
  });
  const ids = new Set(steps.map((s) => s.id));
  const edges: FlowEdge[] = [];
  let seq = 0;
  const push = (from: string, to: string, label: string, dashed = false): void => {
    if (
      from !== to &&
      (to === "end" || ids.has(to)) &&
      (from === "trigger" || from === "end" || ids.has(from))
    ) {
      edges.push({ id: `e${seq++}:${from}->${to}:${label}`, from, to, label, dashed });
    }
  };

  steps.forEach((s, i) => {
    const prev = i === 0 ? "trigger" : steps[i - 1].id;
    if (s.on === "always") {
      push(prev, s.id, "");
    } else if (s.on === "timeout" || s.on === "failed") {
      push(prev, s.id, s.on, true);
    } else if (s.on.endsWith(":completed")) {
      push(s.on.slice(0, -":completed".length), s.id, "");
    } else {
      push(prev, s.id, `?${s.on}`, true);
    }
    // Branch legs the payload itself declares.
    if (s.kind === "wait" || s.kind === "invoke") {
      const t = String(s.payload.onTimeout ?? "");
      if (t) push(s.id, t, "timeout", true);
    }
    if (s.kind === "decide") {
      const fb = String(s.payload.fallback ?? "");
      if (fb) push(s.id, fb, "fallback", true);
      const options = s.payload.options;
      if (options && typeof options === "object") {
        for (const [branch, target] of Object.entries(options as Record<string, unknown>)) {
          if (typeof target === "string") push(s.id, target, branch);
        }
      }
    }
  });
  // Instance law: a completed run walks to 'end'.
  if (steps.length > 0 && instanceStatus === "completed") {
    push(steps[steps.length - 1].id, "end", "");
  }
  return { steps, edges, triggerLabel: triggerLabel(doc) };
}

export function triggerLabel(doc: Doc): string {
  const on = (doc.on ?? {}) as Doc;
  if ("event" in on) return `on ${String(on.event).replace("org.facilitator.event.", "")}`;
  if ("schedule" in on) return `every ${String(on.schedule)}`;
  return "invoked";
}

/** Latest leg status per step: the log is oldest-first, last write wins. */
export function latestLegs(log: Leg[] | null | undefined): Map<string, string> {
  const latest = new Map<string, string>();
  for (const row of log ?? []) {
    latest.set(row.stepId, row.status);
  }
  return latest;
}

const KIND_TONE: Record<string, Tone> = {
  wait: "park",
  timer: "park",
  approval: "park",
  invoke: "call",
  decide: "branch",
  "fan-out": "branch",
  join: "branch",
  emit: "act",
  signal: "act",
  escalate: "act",
  "work-create": "act",
  "sign-over": "act",
  "fork-work": "act",
  memory: "act",
  select: "calc",
};

export function toneFor(
  step: { id: string; kind: string },
  log: Leg[] | null | undefined,
  latest: Map<string, string> = latestLegs(log),
): Tone {
  const st = latest.get(step.id);
  if (st === "done") return "ok";
  if (st === "failed") return "bad";
  if (st === "skipped") return "skip";
  if (st === "shadow") return "shadow";
  if (log !== null && log !== undefined && !st) return "pending";
  return KIND_TONE[step.kind] ?? "calc";
}

/**
 * dagre layered layout, rankdir TB. Port ordering law: outgoing edges of a
 * node sort completed → timeout → failed (then others), so the happy path
 * reads left-to-right. Mutates and returns the node list with x/y.
 */
export function layout(
  nodes: Omit<FlowNode, "x" | "y" | "width" | "height">[],
  edges: FlowEdge[],
): FlowNode[] {
  const g = new dagre.graphlib.Graph();
  g.setGraph({ rankdir: "TB", nodesep: 40, ranksep: 60, marginx: 16, marginy: 16 });
  g.setDefaultEdgeLabel(() => ({}));
  const outRank = (label: string): number =>
    label === "" || label === "completed"
      ? 0
      : label === "timeout"
        ? 1
        : label === "failed"
          ? 2
          : 3;
  const sorted = [...edges].sort((a, b) => outRank(a.label) - outRank(b.label));
  for (const n of nodes) {
    g.setNode(n.id, { width: NODE_W, height: NODE_H });
  }
  for (const e of sorted) {
    if (g.hasNode(e.from) && g.hasNode(e.to)) {
      g.setEdge(e.from, e.to);
    }
  }
  dagre.layout(g);
  return nodes.map((n) => {
    const p = g.node(n.id);
    return { ...n, x: p.x - NODE_W / 2, y: p.y - NODE_H / 2, width: NODE_W, height: NODE_H };
  });
}

export type CanvasModel = { nodes: FlowNode[]; edges: FlowEdge[]; w: number; h: number };

/** The full picture for a document + optional run skin. Pure and total. */
export function buildModel(opts: {
  doc: Doc;
  log?: Leg[] | null;
  instanceStatus?: string | null;
  currentStep?: string | null;
  childInstances?: { parentId: string; child: { id: string; status: string }[] }[];
}): CanvasModel {
  const { doc, log = null, instanceStatus = null, currentStep = null } = opts;
  const { steps, edges, triggerLabel: tLabel } = extract(doc, instanceStatus);
  const ids = new Set(steps.map((s) => s.id));
  const latest = latestLegs(log);

  const bare: Omit<FlowNode, "x" | "y" | "width" | "height">[] = [];
  bare.push({ id: "trigger", label: "trigger", sub: tLabel, kind: "trigger", tone: "trigger" });
  for (const s of steps) {
    bare.push({
      id: s.id,
      label: s.id,
      sub: `${s.kind}${s.title ? ` · ${s.title}` : ""}`,
      kind: s.kind,
      tone: toneFor(s, log, latest),
    });
  }
  if (ids.size > 0 && edges.some((e) => e.to === "end")) {
    bare.push({ id: "end", label: "end", sub: "flow finished", kind: "end", tone: "end" });
  }
  // Invoke children render as their own nodes, pinned under the invoke.
  for (const { parentId, child } of opts.childInstances ?? []) {
    if (!ids.has(parentId)) continue;
    for (const c of child) {
      bare.push({
        id: `child:${c.id}`,
        label: c.id.slice(0, 8),
        sub: `child run · ${c.status}`,
        kind: "child",
        tone: c.status === "completed" ? "ok" : c.status === "failed" ? "bad" : "call",
      });
      edges.push({
        id: `child-edge:${c.id}`,
        from: parentId,
        to: `child:${c.id}`,
        label: "",
        dashed: true,
      });
    }
  }

  // The live instance's current step wears the run's status tone.
  if (currentStep) {
    const node = bare.find((n) => n.id === currentStep);
    if (
      node &&
      (instanceStatus === "running" || instanceStatus === "waiting" || instanceStatus === "paused")
    ) {
      node.tone =
        instanceStatus === "running"
          ? "live"
          : instanceStatus === "paused"
            ? "paused"
            : "park-live";
    }
  }

  // Unreached nodes still need a rank: dagre places orphans on their own.
  const nodes = layout(bare, edges);
  let w = 0;
  let h = 0;
  for (const n of nodes) {
    w = Math.max(w, n.x + n.width + 16);
    h = Math.max(h, n.y + n.height + 16);
  }
  // Labels show only where a node actually branches (>1 out-edge).
  const outCount = new Map<string, number>();
  for (const e of edges) outCount.set(e.from, (outCount.get(e.from) ?? 0) + 1);
  const visible = edges.map((e) =>
    (outCount.get(e.from) ?? 0) > 1 ? e : { ...e, label: e.label.startsWith("?") ? e.label : "" },
  );
  return { nodes, edges: visible, w: Math.max(w, 320), h: Math.max(h, 240) };
}
