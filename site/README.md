# open-facilitator.org

The public website: a static landing page and documentation browser. The site is decoupled from the facilitator runtime with no shared code, no backend, and no private registry dependencies.

## Directory structure

```
site/
├── build.mjs              # build pipeline: markdown -> HTML + search index + explorer bundle
├── arch-explorer.vite.config.mjs  # IIFE bundle config for src/arch-explorer
├── mermaid-ssr.mjs        # build-time diagram renderer
├── serve.mjs              # dev server: node serve.mjs [port]
├── package.json           # private npm package configuration
└── src/
    ├── config.mjs         # navigation, content manifest, and landing-page copy
    ├── architecture.mjs   # explorer graph model: nodes, zones, edges, scenarios
    ├── arch-render.mjs    # renders graph model to inline SVG and JSON
    ├── schema-ref.mjs     # wire schema step palette reference generator
    ├── arch-explorer/     # interactive architecture map (Svelte 5 island)
    ├── tooltips.mjs       # hover tooltip text for diagram nodes
    ├── styles.css         # visual theme with light and dark mode support
    ├── pages/index.mjs    # landing page and 404 templates
    ├── pages/architecture.mjs  # architecture explorer shell
    ├── pages/*.md         # consumer-facing product documentation
    ├── js/app.js          # client-side router, search engine, and doc viewer
    ├── guides/*.md        # getting-started guides for humans and agents
    └── assets/            # brand icons and interface screenshots
```

Pages reference screenshots as `../assets/<name>.png`. The build pipeline rewrites paths to flat `dist/` filenames.

Interface screenshots are cut from `.omp/evidence/` — the copper-era frames captured by `tools/capture-evidence.mjs` against the reference environment. Each site asset carries a stable role name (`console-activities`, `console-search`, `console-work-inspector`, `console-audit`, `console-config`, `console-assistant`, `console-flow-editor`, `console-run-detail`, `console-mobile-drawer`) with a `-dark` twin; the per-frame mapping is documented beside `SHOT_NAMES` in `build.mjs`, and that table is the re-sync contract.

## Site pages

Pages in `src/pages/*.md` and `src/guides/*.md` follow standard consumer documentation principles: an opening sentence defining purpose, structured technical details, and a closing "Where to go next" navigation list. Copy references published artifact coordinates rather than repository source paths.

Mermaid diagrams declare standard theme initialization blocks and `title="..."` info-strings for accessible captions.

## Architecture explorer

The `/architecture` route renders from `architecture.mjs`. The data model defines layout coordinates and interactive scenarios, rendered as inline SVG by `arch-render.mjs`. The client runtime provides node focusing, details panels, and animated scenario steps.

## Build and preview

```bash
npm ci
npm run build     # compiles to dist/
npm run serve     # serves at http://localhost:8788
```

The build processes `docs/` Markdown files and resolves relative cross-references. Mermaid diagrams render to inline SVG when headless Chromium is available, falling back to client-side rendering with identical themes.

Design constraints:

- **Zero runtime dependencies on core paths.** `app.js` runs vanilla JavaScript, using a pre-generated inverted index (`search-index.json`) for local search.
- **Relative asset paths.** All links and assets use relative paths to support domain roots and subdirectory deployments.
- **Consumer and operator separation.** Site pages target end users, while `docs/` provides comprehensive wire and protocol specifications.

## Deploying to GitHub Pages

The `.github/workflows/site.yml` workflow builds `site/dist` and publishes the static output to GitHub Pages.

1. Enable GitHub Actions as the Pages source under repository settings.
2. (Optional) Configure custom domains in DNS settings.

The build workflow runs on changes to `site/**`, `docs/**`, and `README.md`.

## Visual identity

The logo depicts a kernel core surrounded by four linked environments. The gold signal represents active work in transit across an outbound link. The design supports monochrome rendering and high contrast in light and dark themes.
