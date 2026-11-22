# Typography & Iconography

This reference specifies the typography scale, font stacks, tabular alignment, icon system, and brand logo geometry for Open Facilitator.

## 1. Typography Stacks

System stacks eliminate layout reflow and network latency. No external web fonts are loaded.

```css
/* UI Sans Stack */
--font-sans: system-ui, -apple-system, "Segoe UI", Roboto, sans-serif;

/* Machine-Assigned Values & Code Mono Stack */
--font-mono: ui-monospace, "Cascadia Mono", "JetBrains Mono", Menlo, monospace;
```

Use `--font-mono` for anything machine-assigned: work IDs, run UUIDs, timestamps, file paths, ports, JSON schemas, SQL queries, and numeric figures in dense tables.

## 2. Locked Type Scale

Typography is restricted to six exact sizes and three weights (400, 500, 600).

| Size | Line Height | Weight | Role & Usage |
|---|---|---|---|
| **12px** | 16px | 400 / 500 | Metadata, chips, micro-labels, timestamps, keyboard shortcuts, mono IDs |
| **13px** | 18px | 500 / 600 | Controls, navigation links, menu items, dense table rows, button labels |
| **14px** | 20px | 400 | Body text default: paragraphs, inputs, comfortable list rows |
| **16px** | 24px | 500 / 600 | Section headings, panel titles, modal dialog headers |
| **20px** | 28px | 600 | Empty-state titles, primary view titles |
| **24px** | 32px | 600 | Main hero / display header (maximum one per screen) |

### Weights & Hierarchy
- **400 (Regular):** Running prose, input contents, description lines.
- **500 (Medium):** Form labels, table headers, menu labels, interactive navigation.
- **600 (Semibold):** Brand titles, view headings, uppercase button labels (`letter-spacing: 0.04em`).
- **Prose Measure:** Paragraphs cap around 70ch to ensure optimal readability. Hints cap around 46ch.

### Micro-Labels
Used for group headings in sidebars, popovers, and inspectors:
`font-size: 12px; font-weight: 500; text-transform: uppercase; letter-spacing: 0.04em; color: var(--ink-faint);`
Rule: Maximum one micro-label per group, never nested.

### Numbers & Tabular Alignment
Any column of numeric metrics (counts, latencies, timestamps) MUST enable tabular figures and right-align:
`font-variant-numeric: tabular-nums; text-align: right;`

## 3. Links & Selection

- **Links:** Styled with `--accent-text`, 1px underline (`text-decoration-thickness: 1px`), and 2px offset (`text-underline-offset: 2px`).
- **Dense Row Links:** May inherit `--ink` to prevent a dense table from becoming a wall of accent color.
- **Selection (`::selection`):** Fill is `--tint-primary` with `--ink` text.

## 4. Iconography System

All icons are inline SVGs built on a shared 24×24 pixel coordinate grid with clean vector strokes.

### Icon Design Constraints
- **Grid:** 24×24 viewBox, rendered at 16px or 20px in chrome.
- **Stroke:** 1.5px stroke weight, round line caps (`stroke-linecap="round"`), round line joins (`stroke-linejoin="round"`).
- **Color:** Always `currentColor` inheriting from parent token. No duotone, no multi-color fills, no colored container tiles.
- **Attributes:** `aria-hidden="true"`, `focusable="false"`, `display: block`.

### The 36 House Glyphs
`search`, `plus`, `back`, `fwd`, `chev`, `check`, `dash`, `close`, `dots`, `sort`, `filter`, `panel`, `pencil`, `file`, `folder`, `link`, `tag`, `clock`, `user`, `star`, `eye`, `eyeoff`, `bell`, `lock`, `undo`, `redo`, `down`, `copy`, `move`, `gear`, `sun`, `moon`, `play`, `keyboard`, `cmd`, `trash`.

### Button Icon Rules
- **Text First:** Chrome actions present explicit 13px labels.
- **Standalone Icons:** Only universal verbs (`✕` close, `⋯` overflow, `＋` add, `←` `→` history) may omit text, and MUST provide an `aria-label`.

## 5. Brand Logo Geometry

The Open Facilitator logo adheres to the same 24×24 geometric rules as the icon system:

- Coordinates align to multiples of 2 on a 24×24 grid.
- One unified 1.5px stroke weight with round caps.
- Simple geometric circles and straight connector lines (maximum 4 nodes + 3 connectors).
- Maximum two colors: `--ink` structure plus one filled accent dot (`r≈1`) in primary copper `#C96A2D`.
- Verified for crisp legibility at 16px and 24px favicon sizes.
- Wordmark sits beside mark: 16px/600 weight, `--ink`, `letter-spacing: 0.01em`. Under 720px viewport, wordmark collapses and glyph stands alone.
