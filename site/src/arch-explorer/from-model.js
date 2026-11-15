// Data adapter: architecture.mjs -> Svelte Flow primitives, 1:1.
// The curated x/y/w/h layout is the source of truth (no auto-layout on
// the published map: the reading order is designed, not computed), so
// zones become parent group nodes and nodes keep their authored rects.

export const OWN_STYLE = {
  you: { border: "#0B5FBF", fill: "#F2F7FD", chip: "yours: central deploy" },
  infra: { border: "#8A97A6", fill: "#F6F8FA", chip: "yours: commodity infra" },
  team: { border: "#B25E09", fill: "#FDFAF3", chip: "team's" },
  external: { border: "#9AA6B2", fill: "#F7F8FA", chip: "external" },
};

// Same kind -> stroke table as arch-render.mjs (the static twin).
export const KIND_STYLE = {
  internal: { stroke: "#B9C4D0", dash: "", width: 1.6 },
  sync: { stroke: "#0B5FBF", dash: "", width: 2 },
  idp: { stroke: "#6C7A89", dash: "6 5", width: 1.8 },
  link: { stroke: "#0B5FBF", dash: "1 6", width: 2.6 },
  spawn: { stroke: "#B25E09", dash: "", width: 1.8 },
  webhook: { stroke: "#6C7A89", dash: "2 5", width: 1.8 },
  async: { stroke: "#8A97A6", dash: "8 6", width: 1.6 },
};

const ZONE_PAD = 8;

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
      const parent = z && !z.ghost ? `zone:${z.id}` : undefined;
      return {
        id: n.id,
        type: "part",
        position: parent
          ? { x: n.x - z.x + ZONE_PAD, y: n.y - z.y + ZONE_PAD }
          : { x: n.x, y: n.y },
        width: n.w,
        height: n.h,
        ...(parent ? { parentNode: parent, extent: "parent" } : {}),
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
      type: "default",
      ...(e.label ? { label: e.label } : {}),
      style,
      markerEnd: { type: "arrowclosed", color: k.stroke },
      data: { kind: e.kind },
    };
  });
  return { nodes, edges };
}
