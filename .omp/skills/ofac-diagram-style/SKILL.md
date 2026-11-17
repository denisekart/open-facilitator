---
name: ofac-diagram-style
description: "Draw diagrams in the open-facilitator house design language: the SvelteFlow architecture map is law and stays untouched; every other diagram (mermaid fences in docs/site, hand-rolled SVG) must reuse the same palette and voice. Use when adding or editing any figure."
---

# Diagrams: one language, two tiers

The house look is "Ink on Paper" (the console/site palette: cool
blue-grey grounds, one gold signal `#e6bf34` for state, ink
`#21262e` light / `#d6dae0` dark, line `#d5dbe3` / `#464c54`, light
surface `#f7f9fb` / raise `#ffffff`, dark ground `#2b2e33` / surface
`#33373d` / raise `#3b4046`; the accent as a thin edge takes
`--accent-rule` `#ab8612` in light and `#e6bf34` in dark —
`site/src/styles.css` `:root` is the single source). Diagrams borrow
it; they never invent a palette.

## Tier 1 — the architecture map (SvelteFlow): do not restyle

`site/src/architecture.mjs` (nodes/edges/corridors as data) +
`site/src/arch-render.mjs` (SSR) + `site/src/arch-explorer/` (the live
`@xyflow/svelte` view). It is hand-routed, one fixed dark surface (the
Ink dark ramp: ground `#2b2e33`, surface `#33373d`, raise `#3b4046`,
ink `#d6dae0`, line `#464c54`, gold `#e6bf34`),
click-any-part with walkable corridors, and an ``archmap` fence embeds a
mini teaser (`site/build.mjs` renders it). Editing it means the
managed `svelte-flow-ofac` laws (no `parentNode`, string edge styles, the
label-clipping fix). New plane/component → add nodes+edges to
`architecture.mjs` data and let the renderer do the drawing; never
hand-place pixels and never change the visual language.

## Tier 2 — everything else: mermaid first

Sequence/flow/state diagrams in `docs/` and site pages ride the site
theme — copy `MERMAID_THEME` from `site/src/js/app.js` verbatim (base
theme, `system-ui`, node fills on surface `#f7f9fb`, accent-rule borders
`#ab8612`, ink `#21262e`, line `#676f7c`, selected tint `#faf2d6` for
the secondary lane):

````
```mermaid title="The dispatch funnel"
%%{init: {"theme": "base", "themeVariables": {"primaryColor": "#f7f9fb",
"primaryBorderColor": "#ab8612", "primaryTextColor": "#21262e",
"lineColor": "#676f7c", "secondaryColor": "#faf2d6"}}}%%
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
