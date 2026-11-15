<script>
  // A hand-routed edge: the via waypoints are authored in
  // architecture.mjs (shared verbatim with the static twin), so the live
  // map shows the SAME orthogonal corridors as the no-JS snapshot. Only
  // the first/last points come from xyflow's handles.
  import { EdgeLabel } from "@xyflow/svelte";

  let {
    sourceX,
    sourceY,
    targetX,
    targetY,
    targetPosition,
    markerEnd,
    style,
    label,
    data = {},
  } = $props();

  const NORMAL = { top: [0, -1], bottom: [0, 1], left: [-1, 0], right: [1, 0] };
  const dist = (a, b) => Math.hypot(b[0] - a[0], b[1] - a[1]);
  const norm2 = (v) => {
    const l = Math.hypot(v[0], v[1]) || 1;
    return [v[0] / l, v[1] / l];
  };

  const path = $derived.by(() => {
    const a = [sourceX, sourceY];
    const b = [targetX, targetY];
    const via = data.via || [];
    const pts = [a, ...via, b];
    const nb = NORMAL[targetPosition] || [0, 1];
    const r = 14;
    let d = `M ${a[0]} ${a[1]}`;
    for (let i = 1; i < pts.length - 1; i++) {
      const p = pts[i];
      const prev = pts[i - 1];
      const next = pts[i + 1];
      const v1 = norm2([p[0] - prev[0], p[1] - prev[1]]);
      const v2 = norm2([next[0] - p[0], next[1] - p[1]]);
      const rr = Math.min(r, dist(prev, p) / 2, dist(p, next) / 2);
      d += ` L ${p[0] - v1[0] * rr} ${p[1] - v1[1] * rr} Q ${p[0]} ${p[1]} ${p[0] + v2[0] * rr} ${p[1] + v2[1] * rr}`;
    }
    const last = pts[pts.length - 1];
    const bx = b[0] + nb[0] * 26;
    const by = b[1] + nb[1] * 26;
    d += ` C ${last[0] + (bx - last[0]) * 0.2} ${last[1] + (by - last[1]) * 0.2}, ${bx} ${by}, ${b[0]} ${b[1]}`;
    // label point along the polyline at the authored lpos
    const total = pts.slice(1).reduce((acc, p, i) => acc + dist(pts[i], p), 0);
    let want = total * (data.lpos ?? 0.5);
    let mid = [(a[0] + b[0]) / 2, (a[1] + b[1]) / 2];
    for (let i = 1; i < pts.length && want >= 0; i++) {
      const L = dist(pts[i - 1], pts[i]);
      if (want <= L) {
        const f = L === 0 ? 0 : want / L;
        mid = [pts[i - 1][0] + (pts[i][0] - pts[i - 1][0]) * f, pts[i - 1][1] + (pts[i][1] - pts[i - 1][1]) * f];
        want = -1;
      } else want -= L;
    }
    return { d, mid };
  });
</script>

<path d={path.d} class="svelte-flow__edge-path" {markerEnd} {style} fill="none" />
<path d={path.d} stroke-opacity={0} stroke-width={20} fill="none" class="svelte-flow__edge-interaction" />
{#if label}
  <EdgeLabel x={path.mid[0]} y={path.mid[1]} class="arch-edge-chip">
    {label}
  </EdgeLabel>
{/if}

