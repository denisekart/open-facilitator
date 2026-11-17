// Data adapter: architecture.mjs -> Svelte Flow primitives, 1:1.
// The curated x/y/w/h layout is the source of truth (no auto-layout on
// the published map: the reading order is designed, not computed).
// Zones are plain sized nodes at a negative zIndex; parts keep their
// authored ABSOLUTE rects on top. NO parentNode: in @xyflow/svelte 1.x a
// parent's dimensions come only from a post-mount measure, so parenting
// silently collapses every child onto the parent's top-left corner.
// The palette is deliberately site-independent (a fixed middle surface
// that survives the light/dark header toggle untouched); see the
// .arch-explorer block in the site stylesheet for the surface itself.

export const OWN_STYLE = {
  you: { border: "#e6bf34", fill: "rgba(230,191,52,0.10)", chip: "yours: central deploy" },
  infra: { border: "#99a1ab", fill: "rgba(153,161,171,0.10)", chip: "yours: commodity infra" },
  team: { border: "#e6bf34", fill: "rgba(230,191,52,0.10)", chip: "team's" },
  external: { border: "#767d85", fill: "rgba(118,125,133,0.10)", chip: "external" },
};

// Same kind -> stroke table as arch-render.mjs (the static twin). The map
// is a fixed dark-slate instrument surface in BOTH site themes (the
// --code-bg precedent), so these hexes are deliberately constant.
export const KIND_STYLE = {
  internal: { stroke: "#565d66", dash: "", width: 1.6 },
  sync: { stroke: "#e6bf34", dash: "", width: 2 },
  idp: { stroke: "#99a1ab", dash: "6 5", width: 1.8 },
  link: { stroke: "#f0d27a", dash: "1 6", width: 2.6 },
  spawn: { stroke: "#e6bf34", dash: "", width: 1.8 },
  webhook: { stroke: "#99a1ab", dash: "2 5", width: 1.8 },
  async: { stroke: "#565d66", dash: "8 6", width: 1.6 },
};

export function toFlow(data) {
  const zoneById = Object.fromEntries(data.zones.map((z) => [z.id, z]));
  const nodes = [
    ...data.zones.map((z) => ({
      id: `zone:${z.id}`,
      type: "zone",
      position: { x: z.x, y: z.y },
      width: z.w,
      height: z.h,
      draggable: false,
      selectable: false,
      zIndex: -2,
      data: {
        zone: z,
        // the static twin dashes replaceable/external bands (arch-render)
        dashed: z.dashed === true || z.ghost === true || z.own === "infra" || z.own === "external",
      },
    })),
    ...data.nodes.map((n) => {
      const z = zoneById[n.zone];
      return {
        id: n.id,
        type: "part",
        position: { x: n.x, y: n.y },
        width: n.w,
        height: n.h,
        draggable: false,
        zIndex: 1,
        data: {
          part: n,
          own: (z && z.own) || "external",
          ghost: n.ghost === true,
        },
      };
    }),
  ];
  const edges = data.edges.map((e) => {
    const k = KIND_STYLE[e.kind] || KIND_STYLE.internal;
    let style = `stroke:${k.stroke};stroke-width:${k.width}`;
    if (k.dash) {
      style += `;stroke-dasharray:${k.dash}`;
    }
    return {
      id: e.id,
      source: e.from,
      sourceHandle: `${e.fs || "right"}-src`,
      target: e.to,
      targetHandle: `${e.ts || "left"}-tgt`,
      // Hand-routed edges render through CorridorEdge so the live map
      // shows the same orthogonal corridors as the static snapshot.
      type: e.via ? "corridor" : "default",
      ...(e.label ? { label: e.label } : {}),
      style,
      markerEnd: { type: "arrowclosed", color: k.stroke },
      data: { kind: e.kind, via: e.via || null, lpos: e.lpos ?? 0.5 },
    };
  });
  return { nodes, edges };
}
