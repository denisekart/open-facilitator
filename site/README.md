# open-facilitator.org

The public website: a static landing page + documentation browser.
**Fully separate from the facilitator runtime** — same repository, no shared
code, nothing published to any registry, no backend.

## What's here

```
site/
├── build.mjs              # the whole build: markdown -> HTML + search index
├── mermaid-ssr.mjs        # diagrams rendered at build time (headless chromium)
├── serve.mjs              # dev server: node serve.mjs [port]
├── package.json           # npm-managed, private; NOT part of the pnpm workspace
└── src/
    ├── config.mjs         # navigation, docs manifest, landing-page copy
    ├── styles.css         # the "Keystone" theme
    ├── pages/index.mjs    # landing/404 template
    ├── js/app.js          # hash router, doc viewer, browser search (no deps)
    ├── guides/*.md        # site-only getting-started guides
    └── assets/icon.svg    # the keystone-arch mark
```

## Build & preview

```bash
npm ci
npm run build     # -> dist/
npm run serve     # -> http://localhost:8788
```

The build reads the repository's `docs/` tree as the source of truth and
renders every relative link correctly (site routes for published docs,
GitHub links for everything else). Mermaid diagrams render to inline SVG
at build time when a chromium binary is present; without one they degrade
to visible source blocks instead of failing the build.

Design constraints (they keep the site deployable anywhere):

- **No runtime dependencies.** `app.js` is vanilla; search is an
  inverted index (`search-index.json`) scored in the browser.
- **No absolute paths.** Every asset reference is relative, so the site
  works at a domain root *or* under a project-page subpath.
- **Content is never forked.** Site pages link into `docs/`; only the
  two getting-started guides under `src/guides/` are site-only prose.

## Deploying to GitHub Pages

`.github/workflows/site.yml` builds `site/dist` and deploys through the
official Pages artifact actions. One-time enablement:

1. Repository **Settings → Pages → Source: GitHub Actions**.
2. (Optional) add a custom domain (`open-facilitator.org`) in the same
   screen and point DNS at GitHub. The workflow's `OFAC_SITE_BASE`
   defaults to `/` (custom domain or project page both work because all
   references are relative); set it to `/<repo>/` if you serve from a
   project path and want path-style deep links to survive the 404
   forwarder.

Deploys fire on pushes that touch `site/**`, `docs/**`, or `README.md`;
pull requests build the site without deploying. Anything else that
serves static files (`nginx`, S3, Netlify, `docs/index.html`-style
subfolders) can host `site/dist` verbatim — it is a plain folder.

## The mark

A **keystone arch**: team-owned workspaces are the voussoirs, the
Facilitator is the lifted keystone that lets the arch stand — it holds
everything up but carries no ground itself (the foundation is your
PostgreSQL). Pine green + keystone amber on warm paper; the wordless
mark works at 16 px and in monochrome.
