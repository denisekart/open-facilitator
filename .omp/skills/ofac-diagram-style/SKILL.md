---
name: ofac-diagram-style
description: "Draw diagrams in the open-facilitator house design language: the SvelteFlow architecture map is law and stays untouched; every other diagram (mermaid fences in docs/site, hand-rolled SVG) must reuse the Refined Copper palette and voice. Use when adding or editing any figure."
---

# Diagrams: one language, two tiers

The house look is "Ink on Paper" with the Refined Copper palette (cool neutral/slate grounds, precision copper `#C96A2D` for state/paths, rich teal `#168B90` for Link hub/gates, ink `#1A202C` light / `#F3F4F6` dark, line `#CFD6E0` / `#3F4754`, light surface `#FFFFFF` / raise `#DFE4EA`, dark ground `#232831` / surface `#2B313B` / raise `#3B4350`; see `.omp/skills/design-language/references/tokens-and-color.md` for full token specs). Diagrams borrow it; they never invent a palette.

## Tier 1 — the architecture map (SvelteFlow): do not restyle

`site/src/architecture.mjs` (nodes/edges/corridors as data) + `site/src/arch-render.mjs` (SSR) + `site/src/arch-explorer/` (the live `@xyflow/svelte` view). It is hand-routed, one fixed dark surface (the Refined Copper dark ramp: ground `#232831`, surface `#2B313B`, raise `#3B4350`, ink `#F3F4F6`, line `#3F4754`, copper `#C96A2D`, teal `#168B90`), click-any-part with walkable corridors, and an `archmap` fence embeds a mini teaser (`site/build.mjs` renders it). Editing it means the managed `svelte-flow-ofac` laws (no `parentNode`, string edge styles, the label-clipping fix). New plane/component → add nodes+edges to `architecture.mjs` data and let the renderer do the drawing; never hand-place pixels and never change the visual language.

## Tier 2 — everything else: mermaid first

Sequence/flow/state diagrams in `docs/` and site pages are a fixed dark instrument surface in BOTH themes (the code-block and Tier-1 precedent — the SVGs render once, so they ride the copper dark ramp, never the page theme): copy `MERMAID_THEME` from `site/src/js/app.js` verbatim (base theme, `darkMode`, `system-ui`, node fills on dark surface `#2B313B`, copper border `#C96A2D`, ink `#F3F4F6`, line `#9CA3AF`, selected copper tint `#3D332D` for the secondary lane). The `.diagram` frame in `site/src/styles.css` carries the matching dark ground `#232831`:

````
```mermaid title="The dispatch funnel"
%%{init: {"theme": "base", "themeVariables": {"primaryColor": "#2B313B", "primaryBorderColor": "#C96A2D", "primaryTextColor": "#F3F4F6", "lineColor": "#9CA3AF", "secondaryColor": "#3D332D"}}}%%
flowchart LR ...
```
````

- The `title="…"` info-string becomes the site figcaption; docs/ fences may omit it but then need a prose caption line above.
- Validate with the managed `mermaid-doc-validation` skill (real `mermaid.parse` + HEAD baseline) — never eyeball a fence.
- In `docs/` (GitHub renders mermaid natively) the init block keeps the same tokens so both renderers read the same family.

## Tier 3 — bespoke SVG (posters, site heroes)

Hand-written `<svg>` inline in a page is allowed when a graph tool cannot lay it out (the map's corridor style is the reference): flat fills from the token ramp, 1.5px `--line` strokes, arrowheads one weight, labels in `--sans` at page sizes, `--mono` for identifiers. Rules: no gradients, no blurry shadows/glows, no emoji, dark-mode variants use the `[data-theme="dark"]` hexes, and every figure keeps a text alternative (figcaption or `<title>`). Rendering to PNG for READMEs: chromium headless screenshot of the SVG page — the same chromium the site build already probes.

## Diagram content laws

- Every node names a mechanism that exists in the tree (or is explicitly marked external); a diagram is a signpost, and the same honesty law applies — an edge drawn is an edge the code takes.
- One diagram = one claim. If a reader asks "what am I supposed to learn", the figcaption answers it.
- Staleness is a bug: a refactor that moves/renames a box edits the figure in the same commit (site map data files and mermaid fences included).
