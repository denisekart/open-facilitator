# Components, Shell & Layout

This reference defines the geometry, app shell, disclosure tiers, interactive components, forms, and data grids for Open Facilitator.

## 1. The One-Frame Rule

Chrome geometry remains identical across every route, view, and panel state. Header height, toolbar slots, and cluster alignments never shift when content changes.

```
┌ header 44px: brand │ nav │ toolbar band (fixed width) │ search · gateway status · pairs · chip ┐
├ breadcrumb row (only at depth ≥ 2) ─────────────────────────────────────────────────────────────┤
│  ┌ work surface / DAG canvas ───────────────┐  ┌ floating inspector 360px ┐                    │
│  │                                          │  │                          │                    │
│  └──────────────────────────────────────────┘  └──────────────────────────┘                    │
├ bottom band: control pair (left) · contextual toolbar (centre) · entry strip (full width) ──────┤
└ notice line (transient, top-centre under header) ───────────────────────────────────────────────┘
```

### Frame Dimensions
- **Header:** 44px height, `--surface` fill, 1px `--line` bottom border, 12px side padding (8px below 720px).
- **Toolbar Band:** Fixed-width slot (320–520px), `--surface-alt` fill, 32px height, 4px radius.
- **Breadcrumbs:** 13px text, `--ink-muted`, `/` separators, non-interactive active segment.
- **Floating Inspector:** 360px width, docked under header (top: 56px, right: 12px, bottom: 12px), `--surface` fill, 1px `--line` border, 6px radius, `--shadow-1`.

## 2. Density Doctrine

Space is structural. The frame reserves 76px total (44px header + 32px entry strip), allocating ~90% of a 900px viewport to content.

- **Dense Mode (Default for Tables & Feeds):** 13px type, 4×8 padding, 30–32px row height. Optimized for power-user monitoring and high-throughput queues.
- **Comfortable Mode (Touch & Sparse Lists):** 14px type, 8×12 padding, 44px row height.
- **Scan Order:** Name/title first and widest, status pills compact and centered, timestamps mono and right-aligned, actions as a quiet `⋯` trigger at the far edge.
- **Spacing Grid:** 4 / 8 / 12 / 16 / 24 / 32 px (`--space-1` through `--space-6`).

## 3. Disclosure Tiers

Complexity is structured hierarchically. No single action appears in multiple tiers simultaneously.

| Tier | Surface | Description | Control Cap |
|---|---|---|---|
| **Tier 0** | Header (Always visible) | Global navigation, search, create, history pair, status chip | Max 6 controls |
| **Tier 1** | Contextual Toolbar | 2 to 4 actions relevant to selected element | Max 4 buttons + `⋯` |
| **Tier 2** | Context / Overflow Menu | Complete action set for active context | Max 9 items, 2 submenus |
| **Tier 3** | Command Palette (`Ctrl+K`) | Universal searchable registry of all system actions | Grouped typed search |

## 4. Navigation & Routing

- **State in URLs:** Deep links restore active view, selected entity, filter sets, and pagination.
- **History Navigation:** Back and Forward render as one joined control (shared 1px border, 1px separator, disabled state updates on route change).
- **Tab Navigation (`role="tablist"`):** 13px type, 4×8 padding, 4px radius. Active tab takes `--tint-primary` fill with a 2px `--accent-rule` left-aligned underline.
- **Side Navigation:** 240px width, `--surface` fill, 1px right border, 13px items, micro-label group headings.

## 5. Menus & Popovers

One portal-rendered ARIA `menu` implementation serves all contextual actions across the app.

- **Panel:** `--surface-raise` fill, 1px `--line` border, 6px radius, `--shadow-1`, 4px padding, `min-width: 240px`, `max-height: 70vh`.
- **Items:** 4×8 padding, 4px radius, 13px label on left, shortcut right-aligned in mono 12px `--ink-faint`.
- **Destructive Actions:** Placed last, separated by a 1px rule, labeled with `--danger-text` and an active verb.
- **Placement & Dodging:** Opens in free viewport space without covering the triggering element. Repositions on scroll or resize.

## 6. Modals & Dialogs

Modals are strictly reserved for blocking confirmation (destructive actions, merge conflicts, mandatory inputs).

- **Scrim:** Solid 28% black scrim (`rgba(0, 0, 0, 0.28)`).
- **Panel:** `min(560px, 100vw - 16px)`, `--surface` fill, 1px `--line` border, 6px radius, `--shadow-overlay`, 16px padding.
- **Actions:** Exactly one primary button in `--accent` copper fill; secondary is quiet; destructive retains ghost styling in `--danger-text`.
- **Animation:** 100ms opacity fade (no scale, slide, or bounce). Focus trapped while open; `Esc` dismisses.

## 7. Forms & Inputs

- **Field Layout:** 13px/500 label positioned above input (4px gap). Optional help text 12px `--ink-muted` below.
- **Input Styling:** 14px type, 8×12 padding, 4px radius, 1px `--line-soft` border, `--surface` fill.
- **Focus Cue:** 2px outline in `--accent-rule` at 2px offset on `:focus-visible`.
- **Error State:** 1px `--danger` border accompanied by 12px `--danger-text` explanatory message.
- **Validation:** Triggered on blur, never per keystroke.
- **Segmented Controls:** Quiet container with 13px segments, 28px height, 1px separators. Active segment takes `--tint-primary` fill and 2px copper underline.

## 8. Status Chip & System Readouts

A single status chip in the header communicates background sync and engine health:

| State | Label | Dot Color | Visual Treatment |
|---|---|---|---|
| **Idle** | Saved | `--ink-muted` | Soft 1px edge, muted text |
| **Pending** | Saving… / Ingress… | `--accent` (Copper) | Soft 1px edge, copper dot |
| **Warning** | High Load | `--warning` (Amber Gold) | Solid warning tint, amber dot |
| **Error** | Sync Failed · Retry | `--danger` (Brick Red) | 1px danger border, danger text |
| **Offline** | Saved · Offline | `--info` (Teal) | Badge shares slot behind separator |

## 9. Data Tables & Work Queues

- **Row Heights:** 32px dense / 44px comfortable.
- **Selection:** 40px checkbox column. Selected rows take `--tint-primary` fill with 600 weight on primary column.
- **Bulk Bar:** Appears in Tier-1 slot on selection (count + 2 to 4 ghost actions + clear button).
- **Tabular Figures:** All numeric columns set `font-variant-numeric: tabular-nums` and right-align.
- **Sorting:** Interactive table header with `aria-sort`, directional indicator glyph, and accessible label.
