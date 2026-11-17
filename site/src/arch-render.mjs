// Build-time renderer for the interactive architecture explorer.
// Emits a fixed-layout inline SVG (no mermaid, no layout engine) plus the
// JSON payload the runtime uses for focus, detail, and scenario playback.
import { canvas, zones, nodes, edges, scenarios } from "./architecture.mjs";

const esc = (s) => String(s).replace(/&/g, "&amp;").replace(/</g, "&lt;").replace(/>/g, "&gt;");

// Fixed dark-slate instrument surface in BOTH site themes (the code-block
// precedent) — keeps the static snapshot and the hydrated explorer the
// same picture. Tints are alpha washes so the slate shows through.
const OWN = {
  you: { border: "#e6bf34", fill: "rgba(230,191,52,0.10)", tag: "yours" },
  infra: { border: "#99a1ab", fill: "rgba(153,161,171,0.10)", tag: "yours" },
  team: { border: "#e6bf34", fill: "rgba(230,191,52,0.10)", tag: "team's" },
  external: { border: "#767d85", fill: "rgba(118,125,133,0.10)", tag: "external" },
};

const byId = Object.fromEntries(nodes.map((n) => [n.id, n]));

function anchor(n, side, frac = 0.5) {
  const f =
    side === "left" || side === "right"
      ? [n.x + (side === "left" ? 0 : n.w), n.y + n.h * frac]
      : [n.x + n.w * frac, n.y + (side === "top" ? 0 : n.h)];
  return f;
}
const NORMAL = { top: [0, -1], bottom: [0, 1], left: [-1, 0], right: [1, 0] };

function edgePath(e) {
  const a = anchor(byId[e.from], e.fs, e.frac ?? 0.5);
  const b = anchor(byId[e.to], e.ts, e.tfrac ?? 0.5);
  const na = NORMAL[e.fs];
  const nb = NORMAL[e.ts];
  if (e.via) {
    // Polyline with rounded corners; final approach bends along the
    // target's normal so the arrow enters square-on.
    const pts = [a, ...e.via];
    const r = 14;
    let d = `M ${a[0]} ${a[1]}`;
    for (let i = 1; i < pts.length - 1; i++) {
      const p = pts[i];
      const prev = pts[i - 1];
      const next = pts[i + 1];
      const v1 = norm2([p[0] - prev[0], p[1] - prev[1]]);
      const v2 = norm2([next[0] - p[0], next[1] - p[1]]);
      const rr = Math.min(r, dist(prev, p) / 2, dist(p, next) / 2);
      const x1 = p[0] - v1[0] * rr;
      const y1 = p[1] - v1[1] * rr;
      const x2 = p[0] + v2[0] * rr;
      const y2 = p[1] + v2[1] * rr;
      d += ` L ${x1} ${y1} Q ${p[0]} ${p[1]} ${x2} ${y2}`;
    }
    const last = pts[pts.length - 1];
    const bx = b[0] + nb[0] * 26;
    const by = b[1] + nb[1] * 26;
    d += ` C ${last[0] + (bx - last[0]) * 0.2} ${last[1] + (by - last[1]) * 0.2}, ${bx} ${by}, ${b[0]} ${b[1]}`;
    const segs = [a, ...e.via, b];
    const total = segs.slice(1).reduce((acc, p, i) => acc + dist(segs[i], p), 0);
    let want = total * (e.lpos ?? 0.5);
    let mid = segs[0];
    for (let i = 1; i < segs.length && want > 0; i++) {
      const L = dist(segs[i - 1], segs[i]);
      if (want <= L) {
        const f = want / L;
        mid = [
          segs[i - 1][0] + (segs[i][0] - segs[i - 1][0]) * f,
          segs[i - 1][1] + (segs[i][1] - segs[i - 1][1]) * f,
        ];
        want = -1;
      } else want -= L;
    }
    return { d, mid };
  }
  const dist2 = Math.hypot(b[0] - a[0], b[1] - a[1]);
  const d = e.d ?? Math.min(150, Math.max(46, dist2 * 0.28));
  let px = 0;
  let py = 0;
  if (e.bend) {
    const dx = b[0] - a[0];
    const dy = b[1] - a[1];
    const len = Math.hypot(dx, dy) || 1;
    px = (-dy / len) * e.bend;
    py = (dx / len) * e.bend;
  }
  const c1 = [a[0] + na[0] * d + px, a[1] + na[1] * d + py];
  const c2 = [b[0] + nb[0] * d + px, b[1] + nb[1] * d + py];
  const mid = [
    0.125 * a[0] + 0.375 * c1[0] + 0.375 * c2[0] + 0.125 * b[0],
    0.125 * a[1] + 0.375 * c1[1] + 0.375 * c2[1] + 0.125 * b[1],
  ];
  return {
    d: `M ${a[0]} ${a[1]} C ${c1[0]} ${c1[1]}, ${c2[0]} ${c2[1]}, ${b[0]} ${b[1]}`,
    mid,
  };
}

const dist = (a, b) => Math.hypot(b[0] - a[0], b[1] - a[1]);
const norm2 = (v) => {
  const l = Math.hypot(v[0], v[1]) || 1;
  return [v[0] / l, v[1] / l];
};

const KIND = {
  internal: { stroke: "#565d66", dash: "", width: 1.6 },
  sync: { stroke: "#e6bf34", dash: "", width: 2 },
  idp: { stroke: "#99a1ab", dash: "6 5", width: 1.8 },
  link: { stroke: "#f0d27a", dash: "1 6", width: 2.6 },
  spawn: { stroke: "#e6bf34", dash: "", width: 1.8 },
  webhook: { stroke: "#99a1ab", dash: "2 5", width: 1.8 },
  async: { stroke: "#565d66", dash: "8 6", width: 1.6 },
};

function wrap(s, max) {
  const words = s.split(" ");
  const lines = [];
  let cur = "";
  for (const w of words) {
    if ((cur + " " + w).trim().length > max) {
      if (cur) lines.push(cur);
      cur = w;
    } else cur = (cur + " " + w).trim();
  }
  if (cur) lines.push(cur);
  return lines.slice(0, 2);
}

export function renderArchitecture() {
  const zoneSvg = zones
    .map((z) => {
      const o = OWN[z.own] || OWN.external;
      const dash =
        z.dashed || z.own === "external" || z.own === "infra" ? ` stroke-dasharray="7 6"` : "";
      return (
        `<g class="arch-zone" data-zone="${z.id}">` +
        `<rect x="${z.x}" y="${z.y}" width="${z.w}" height="${z.h}" rx="14" fill="${o.fill}" stroke="${o.border}" stroke-opacity="0.55" stroke-width="1.4"${dash}/>` +
        `<text class="arch-zt" x="${z.x + 16}" y="${z.y + 24}">${esc(z.title)}</text>` +
        `<text class="arch-ztag" x="${z.x + 16}" y="${z.y + 39}" text-anchor="start">${esc(z.tag)}</text>` +
        `</g>`
      );
    })
    .join("\n");

  const edgeSvg = edges
    .map((e) => {
      const k = KIND[e.kind] || KIND.internal;
      const p = edgePath(e);
      const marker =
        e.kind === "link"
          ? "arch-dot"
          : e.kind === "spawn" || e.kind === "sync"
            ? "arch-arb"
            : "arch-ar";
      const label = e.label
        ? e.rotate
          ? `<text class="arch-el" x="${p.mid[0]}" y="${p.mid[1] + 3}" text-anchor="middle" transform="rotate(${e.rotate} ${p.mid[0]} ${p.mid[1]})">${esc(e.label)}</text>`
          : `<text class="arch-el" x="${p.mid[0]}" y="${p.mid[1] - 2}" text-anchor="middle">${esc(e.label)}</text>`
        : "";
      return (
        `<g class="arch-edge" data-id="${e.id}" data-from="${e.from}" data-to="${e.to}">` +
        `<path d="${p.d}" fill="none" stroke="${k.stroke}" stroke-width="${k.width}" stroke-dasharray="${k.dash}" stroke-linecap="round" marker-end="url(#${marker})"/>` +
        label +
        `</g>`
      );
    })
    .join("\n");

  const nodeSvg = nodes
    .map((n) => {
      const tl = wrap(n.t, Math.floor((n.w - 14) / 6.6)).slice(0, 2);
      const sl = n.sub ? wrap(n.sub, Math.floor((n.w - 10) / 5.4)).slice(0, 2) : [];
      const ghost = n.ghost ? ` stroke-dasharray="5 5"` : "";
      const core =
        n.zone === "kernel" &&
        ["coord", "policy", "hub", "ingress", "apisrv", "broker"].includes(n.id);
      const stroke =
        n.zone === "team" || n.zone === "teamB" ? "#e6bf34" : core ? "#e6bf34" : "#99a1ab";
      const LH = 13.5;
      const SH = 11.5;
      const GAP = 3;
      const block = tl.length * LH + (sl.length ? GAP + sl.length * SH : 0);
      let y = n.y + n.h / 2 - block / 2 + 10;
      const title = tl
        .map((l) => {
          const t = `<text class="arch-nt" x="${n.x + n.w / 2}" y="${y.toFixed(1)}" text-anchor="middle">${esc(l)}</text>`;
          y += LH;
          return t;
        })
        .join("");
      const subt = sl
        .map((l) => {
          y += GAP + SH - LH;
          const t = `<text class="arch-ns" x="${n.x + n.w / 2}" y="${y.toFixed(1)}" text-anchor="middle">${esc(l)}</text>`;
          y += SH;
          return t;
        })
        .join("");
      return (
        `<g class="arch-node${n.ghost ? " arch-ghost" : ""}" data-id="${n.id}" tabindex="0" role="button" aria-label="${esc(n.t)}">` +
        `<rect x="${n.x}" y="${n.y}" width="${n.w}" height="${n.h}" rx="9" fill="#33373d" stroke="${stroke}" stroke-width="1.6"${ghost}/>` +
        title +
        subt +
        `</g>`
      );
    })
    .join("\n");

  const svg =
    `<svg class="arch-svg" viewBox="0 0 ${canvas.w} ${canvas.h}" role="img" aria-label="System architecture map">` +
    `<defs>` +
    `<marker id="arch-ar" viewBox="0 0 10 10" refX="9" refY="5" markerWidth="7" markerHeight="7" orient="auto-start-reverse"><path d="M0 0L10 5L0 10z" fill="#99a1ab"/></marker>` +
    `<marker id="arch-arb" viewBox="0 0 10 10" refX="9" refY="5" markerWidth="7" markerHeight="7" orient="auto-start-reverse"><path d="M0 0L10 5L0 10z" fill="#e6bf34"/></marker>` +
    `<marker id="arch-dot" viewBox="0 0 6 6" refX="3" refY="3" markerWidth="6" markerHeight="6" orient="auto"><circle cx="3" cy="3" r="2.6" fill="#e6bf34"/></marker>` +
    `</defs>` +
    zoneSvg +
    edgeSvg +
    nodeSvg +
    `</svg>`;

  const payload = JSON.stringify({
    canvas,
    zones: zones.map((z) => ({
      id: z.id,
      x: z.x,
      y: z.y,
      w: z.w,
      h: z.h,
      title: z.title,
      tag: z.tag || "",
      own: z.own,
      dashed: z.dashed === true,
      ghost: z.ghost === true,
    })),
    nodes: nodes.map((n) => ({
      id: n.id,
      t: n.t,
      sub: n.sub || "",
      zone: n.zone,
      x: n.x,
      y: n.y,
      w: n.w,
      h: n.h,
      ghost: n.ghost === true,
      what: n.what,
      bullets: n.bullets,
      // site routes ride the SPA router: "/docs/x" -> "#/docs/x"
      docs: n.docs ? `#${n.docs}` : null,
      artifact: n.artifact || null,
    })),
    edges: edges.map((e) => ({
      id: e.id,
      from: e.from,
      to: e.to,
      kind: e.kind,
      label: e.label || "",
      fs: e.fs || "right",
      ts: e.ts || "left",
      via: e.via || null,
    })),
    scenarios,
  }).replace(/</g, "\\u003c");

  return { svg, payload, scenarios };
}

// A tiny non-interactive twin for other pages (the "where am I" teaser).
export function renderArchitectureMini() {
  const zoneSvg = zones
    .map((z) => {
      const o = OWN[z.own] || OWN.external;
      return (
        `<rect x="${z.x}" y="${z.y}" width="${z.w}" height="${z.h}" rx="14" fill="${o.fill}" stroke="${o.border}" stroke-opacity="0.55" stroke-width="1.4"/>` +
        `<text class="arch-mzt" x="${z.x + z.w / 2}" y="${z.y + z.h / 2 + 4}" text-anchor="middle">${esc(z.title)}</text>`
      );
    })
    .join("");
  const nodeSvg = nodes
    .map(
      (n) =>
        `<rect x="${n.x}" y="${n.y}" width="${n.w}" height="${n.h}" rx="7" fill="#33373d" stroke="${n.zone === "team" || n.zone === "teamB" ? "#e6bf34" : "#565d66"}" stroke-opacity="0.5" stroke-width="1"/>`,
    )
    .join("");
  return `<svg class="arch-mini" viewBox="0 0 ${canvas.w} ${canvas.h}" aria-hidden="true">${zoneSvg}${nodeSvg}</svg>`;
}
