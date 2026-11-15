---
name: ofac-diagram-style
description: "Draw diagrams in the open-facilitator house design language: the SvelteFlow architecture map is law and stays untouched; every other diagram (mermaid fences in docs/site, hand-rolled SVG) must reuse the same palette and voice. Use when adding or editing any figure."
---

# Diagrams: one language, two tiers

The house look is the console/site palette (dark instrument surface, cool
slate, one blue for action, one amber reserved for the thing in flight —
`site/src/styles.css` `:root` is the single source: `--accent #0550ae`,
`--amber #b45309`, ink slate ramp). Diagrams borrow it; they never invent
a palette.

## Tier 1 — the architecture map (SvelteFlow): do not restyle

`site/src/architecture.mjs` (nodes/edges/corridors as data) +
`site/src/arch-render.mjs` (SSR) + `site/src/arch-explorer/` (the live
`@xyflow/svelte` view). It is hand-routed, one fixed dark surface,
click-any-part with walkable corridors, and an ``archmap` fence embeds a
mini teaser (`site/build.mjs` renders it). Editing it means the
managed `svelte-flow-ofac` laws (no `parentNode`, string edge styles, the
label-clipping fix). New plane/component → add nodes+edges to
`architecture.mjs` data and let the renderer do the drawing; never
hand-place pixels and never change the visual language.

## Tier 2 — everything else: mermaid first

Sequence/flow/state diagrams in `docs/` and site pages ride the site
theme — copy `MERMAID_THEME` from `site/src/js/app.js` verbatim (base
theme, `system-ui`, blue node borders `#0B5FBF`, ink `#1F2328`, line
`#5B6B7C`, amber veil `#FDF0DC` for the secondary lane):

````
```mermaid title="The dispatch funnel"
%%{init: {"theme": "base", "themeVariables": {"primaryColor": "#EAF1F9",
"primaryBorderColor": "#0B5FBF", "primaryTextColor": "#1F2328",
"lineColor": "#5B6B7C", "secondaryColor": "#FDF0DC"}}}%%
flowchart LR ...
```
````

- The `title="…"` info-string becomes the site figcaption; docs/ fences may
  omit it but then need a prose caption line above.
- Validate with the managed `mermaid-doc-validation` skill (real
  `mermaid.parse` + HEAD baseline) — never eyeball a fence.
- In `docs/` (GitHub renders mermaid natively) the init block keeps the
  same tokens so both renderers read the same family.

## Tier 3 — bespoke SVG (posters, site heroes)

Hand-written `<svg>` inline in a page is allowed when a graph tool cannot
lay it out (the map's corridor style is the reference): flat fills from
the token ramp, 1.5px `--line-2` strokes, arrowheads one weight, labels in
`--sans` at page sizes, `--mono` for identifiers. Rules: no gradients, no
shadows, no emoji, dark-mode variants use the `[data-theme="dark"]` hexes,
and every figure keeps a text alternative (figcaption or `<title>`).
Rendering to PNG for READMEs: chromium headless screenshot of the SVG
page — the same chromium the site build already probes.

## Diagram content laws

- Every node names a mechanism that exists in the tree (or is explicitly
  marked external); a diagram is a signpost, and the same honesty law
  applies — an edge drawn is an edge the code takes.
- One diagram = one claim. If a reader asks "what am I supposed to learn",
  the figcaption answers it.
- Staleness is a bug: a refactor that moves/renames a box edits the figure
  in the same commit (site map data files and mermaid fences included).
