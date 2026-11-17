---
name: ofac-doc-gif
description: "Produce animated GIF demos for open-facilitator docs/site (a concept walk or a real UI tour) with free/OSS tooling only (Playwright screencast + ffmpeg palette GIF, VHS/asciinema+agg for terminal legs), keeping the house design language and voice in every frame."
---

# Animated GIF demos for docs

Enrich a page where motion carries the point (a work walking the funnel,
an approval round trip, the console reacting). One GIF = one claim, same
law as diagrams (ofac-diagram-style). Free/OSS toolchain only.

## Tool picks (all OSI licenses)

| Subject | Capture | To GIF |
|---|---|---|
| UI (console/site) | Playwright (Apache-2.0) — the repo already ships a Playwright .NET E2E project and the npm package is one `pnpm dlx` away; drive the page, screenshot the interval | ffmpeg `palettegen`/`paletteuse` (LGPL build) |
| Terminal (CLI, `referencectl`, localctl walks) | **VHS** (MPL-2.0, `curl https://vhs.charm.sh/vhs-2.sh | bash` or `go install github.com/charmbracelet/vhs@latest`): a `.tape` file is the script | VHS emits the GIF directly (`Output GIF`, `Set Framerate 15`) |
| Plain tty session already recorded | asciinema (GPL-3) cast | **agg** (AGPL-3) cast→GIF |

No ScreenToGif-without-ffmpeg, no paid recorders, no proprietary encoders.

## UI capture recipe (Playwright → GIF)

```bash
pnpm dlx playwright@latest screenshot --viewport-size=1280,800 <url> shot.png   # smoke a frame
```
For motion, a ~20-line node script: launch chromium, `newPage({viewport})`,
`page.screenshot()` per step (or CDP `Page.startScreencast` for smooth
video), then:

```bash
ffmpeg -framerate 8 -i f%03d.png -vf "scale=960:-1:flags=lanczos,\
  split[a][b];[a]palettegen=stats_mode=diff[p];[b][p]paletteuse=dither=bayer" \
  -loop 0 demo.gif
```

Design laws per frame:

- Shoot against the **same theme as the host page**: force
  `data-theme="dark"` via `page.emulateMedia`/init script for dark
  instrument pages, light otherwise; palette parity is the whole trick —
  a GIF whose chrome doesn't match the page reads as a stock image.
- Viewport multiples of the site grid (1280×800 → 960-wide GIF), no
  browser UI unless the browser IS the subject, no cursor artifacts
  mid-drag, real seed data from the reference stack (no lorem).
- Fidelity law: a UI GIF is a recording, never a mockup; if the flow
  degrades, capture the degraded leg, that is the honest shot the site's
  assets already follow (see `site/src/assets/console-*.png`).

## Terminal recipe (VHS)

`.tape` beside the page's assets; theme the TypeSet block to the house
code surface (`Set Theme "Demo"` with background `#323944`, the
`--surface-alt` token of the Refined Copper dark theme, ink `#F3F4F6`) and
`Set Width 1200 / Height 400`. Keep tapes
re-runnable against the local stack — a tape is a test: `vhs demo.tape`
must succeed from a clean tree, else the GIF rots silently.

## Placement + gates

- Site pages: asset next to the existing honest shots in
  `site/src/assets/`, referenced with `loading="lazy"` + alt text naming
  the claim; a caption line states what happens at which second when the
  timing matters.
- `docs/`: `docs/assets/` (create it with the first GIF); README-level
  GIFs stay under ~2 MB — reduce with `-vf fps=6` or `gifsicle -O3`
  (GPL, allowed) before the GIF joins a page.
- Size sanity: GIF > 4 MB at ≤ 960 px means the capture is too long or
  too smooth — cut the walk, don't shrink the palette below 128 colours.
- Regenerability: the `.tape`/script lives beside the GIF (`demo.tape`,
  `demo.sh`); "recreate the shot" must be one command, same law as the
  site's honest-screenshot rule.
