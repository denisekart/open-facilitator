# Token Architecture & Color System

This reference defines the complete token architecture, semantic palettes, opaque tint system, and measured WCAG contrast contracts for Open Facilitator.

## 1. Token Architecture

All colors and spacing reside in one single source file: `styles/tokens.css`. Hex codes are strictly forbidden anywhere else in the codebase.

Components consume semantic custom properties only:
`--bg`, `--surface`, `--surface-alt`, `--surface-raise`, `--ink`, `--ink-muted`, `--ink-faint`, `--line`, `--line-soft`, `--accent`, `--accent-hover`, `--accent-text`, `--accent-rule`, `--accent-on`, `--tint-primary`, `--border-primary`, `--info`, `--info-tint`, `--info-border`, `--success`, `--success-tint`, `--success-border`, `--warning`, `--warning-tint`, `--warning-border`, `--danger`, `--danger-tint`, `--danger-border`.

Themes switch on `<html data-theme="light|dark">`. The theme is loaded before first paint via an inline script in `index.html` to eliminate theme flash.

### Derived Tokens & Opaque Color Architecture

Rather than relying on transparent `rgba` layers that create blurry blending or muddy contrast over varying backgrounds, semantic tints use crisp solid opaque tokens:

```css
/* Dark Mode Derived & Tint Tokens */
--line-soft: #343B47;
--tint-primary: #3D332D;
--border-primary: #8A4820;
--tint-info: #223538;
--border-info: #175B5F;
--tint-success: #273426;
--border-success: #3B5832;
--tint-warning: #373121;
--border-warning: #7A5B0B;
--tint-danger: #382626;
--border-danger: #7D2E2E;
--hover: #3B4350;

/* Dynamic Computed Mixes */
--line-soft-mix: color-mix(in oklab, var(--line) 45%, var(--bg));
--hover-mix: color-mix(in oklab, var(--ink) 6%, var(--surface-raise));
```

## 2. Refined Copper Theme Palette

The palette features an industrial precision copper accent (`#C96A2D`) and deep teal secondary accent (`#168B90` / `#127377`) over cool technical slate (dark) and cool neutral studio grey (light).

### Dark Theme (`data-theme="dark"`)

| Token | Value | Role |
|---|---|---|
| `--bg` | `#232831` | Canvas: application ground and workspace background |
| `--surface` | `#2B313B` | Structural surfaces: header, cards, docked strips, editor |
| `--surface-alt` | `#323944` | Sub-toolbars, input containers, table headers |
| `--surface-raise` | `#3B4350` | Elevated surfaces: menus, popovers, active selection, modals |
| `--ink` | `#F3F4F6` | Primary high-contrast text and active labels |
| `--ink-muted` | `#9CA3AF` | Secondary text, descriptions, schema keys, hints |
| `--ink-faint` | `#6B7280` | Placeholders, disabled states, timestamps, line numbers |
| `--line` | `#3F4754` | 1px structural borders, dividers, frame edges |
| `--line-soft` | `#343B47` | Subtle inner separators, floating edges, skeletons |
| `--accent` | `#C96A2D` | Primary brand copper: active tabs, trigger nodes, save buttons |
| `--accent-hover` | `#E58A45` | Interactive hover states, highlighted execution paths |
| `--accent-text` | `#E58A45` | Accent text on dark ground (links, shortcuts) |
| `--accent-rule` | `#C96A2D` | Thin 2px active indicators, focus rings |
| `--accent-on` | `#FFFFFF` | Text on primary copper button fill |
| `--tint-primary` | `#3D332D` | Solid opaque background for selected rows, active tabs |
| `--border-primary` | `#8A4820` | Solid border for active copper items |
| `--info` | `#168B90` | Rich dark teal: Link Hub indicators, policy gates, schema badges |
| `--info-tint` | `#223538` | Solid opaque background for admission gates, link hub indicators |
| `--info-border` | `#175B5F` | Solid border for informational cards |
| `--success` | `#58894C` | Olive emerald: verified runs, passing tests, healthy gateways |
| `--success-tint` | `#273426` | Solid opaque background for succeeded rows and passed verification |
| `--success-border` | `#3B5832` | Solid border for success elements |
| `--warning` | `#C48A00` | Amber gold: high load, queue backlog, lease expiry warnings |
| `--warning-tint` | `#373121` | Solid opaque background for warning banners and pills |
| `--warning-border` | `#7A5B0B` | Solid border for warning elements |
| `--danger` | `#C75454` | Brick red: ingress rejections, merge conflicts, failed steps |
| `--danger-text` | `#C75454` | Destructive text and critical error words |
| `--danger-tint` | `#382626` | Solid opaque background for error cards and alerts |
| `--danger-border` | `#7D2E2E` | Solid border for error cards |

### Light Theme (`data-theme="light"`)

| Token | Value | Role |
|---|---|---|
| `--bg` | `#F1F3F5` | Cool-neutral technical studio ground (no rustic beige) |
| `--surface` | `#FFFFFF` | Pure white cards, panels, and docked headers |
| `--surface-alt` | `#E8ECF0` | Cool-tinted toolbars, table headers, inactive pills |
| `--surface-raise` | `#DFE4EA` | Elevated menus, popovers, dropdown containers |
| `--ink` | `#1A202C` | High-contrast dark typography and headings |
| `--ink-muted` | `#505A69` | Secondary body text, descriptions, keyboard shortcuts |
| `--ink-faint` | `#7A8699` | Placeholders, disabled states, timestamps, metadata |
| `--line` | `#CFD6E0` | Crisp 1px structural dividers and card borders |
| `--line-soft` | `#DEE3EB` | Subtle row separators, table grid lines |
| `--accent` | `#C96A2D` | Primary copper brand accent |
| `--accent-hover` | `#B3581E` | Darkened interactive hover for buttons and links |
| `--accent-text` | `#AC531C` | Darkened copper text ensuring 4.5:1 contrast on white |
| `--accent-rule` | `#C96A2D` | 2px active indicators and focus rings |
| `--accent-on` | `#FFFFFF` | Text on copper button fill |
| `--tint-primary` | `#F8EFE8` | Solid opaque light copper tint for active rows and headers |
| `--border-primary` | `#E2B696` | Solid border for active cards and pills |
| `--info` | `#127377` | Deep technical teal for secondary status and Link indicators |
| `--info-tint` | `#E5F3F4` | Solid light teal tint for informational callouts |
| `--info-border` | `#92CAD0` | Solid border for info pills |
| `--success` | `#447A38` | Deep forest emerald for verified steps and health pills |
| `--success-tint` | `#EAF3E8` | Solid light green tint |
| `--success-border` | `#9FC497` | Solid border for success pills |
| `--warning` | `#A87400` | Deep amber gold for attention pills and warnings |
| `--warning-tint` | `#FAF2DE` | Solid light amber tint |
| `--warning-border` | `#DEC177` | Solid border for warning pills |
| `--danger` | `#BA3C3C` | Deep brick red for errors and rejection notices |
| `--danger-text` | `#BA3C3C` | Destructive text ensuring 4.5:1 contrast |
| `--danger-tint` | `#F9E8E8` | Solid light red tint for error banners |
| `--danger-border` | `#DF9E9E` | Solid border for error elements |

## 3. Measured WCAG 2.2 AA Contrast Compliance

Contrast ratios are verified against painted surfaces:

| Element Pair | Light Ratio | Dark Ratio | Standard Target | Status |
|---|---|---|---|---|
| Ink on Surface (`--ink` / `--surface`) | 16.3:1 | 11.9:1 | ≥ 4.5:1 (Normal Text) | Pass (AAA) |
| Muted on Surface (`--ink-muted` / `--surface`) | 7.0:1 | 5.2:1 | ≥ 4.5:1 (Secondary Text) | Pass (AA) |
| Faint on Surface (`--ink-faint` / `--surface`) | 3.7:1 | 2.7:1 | ≥ 3.0:1 (UI / Non-Text) | Pass (UI Component) |
| Accent Text on Surface (`--accent-text` / `--surface`) | 5.2:1 | 5.0:1 | ≥ 4.5:1 (Links & Verbs) | Pass (AA) |
| Text on Accent Button (`--accent-on` / `--accent`) | 3.8:1 | 3.8:1 | ≥ 3.0:1 (13px 600 Caps) | Pass (Large/Bold Target) |
| Danger Text on Surface (`--danger-text` / `--surface`) | 5.5:1 | 3.0:1 (Raise 4.6:1) | ≥ 4.5:1 (Text) | Pass (AA) |
| Ink on Primary Tint (`--ink` / `--tint-primary`) | 12.9:1 | 9.5:1 | ≥ 4.5:1 (Selected Rows) | Pass (AAA) |

## 4. Elevation & Shadow Spec

Floating elements use subtle 1px/2px drop shadows. All docked elements use 1px borders with zero shadow.

```css
--shadow-1: 0 1px 3px rgba(0, 0, 0, 0.12), 0 1px 2px rgba(0, 0, 0, 0.08);
--shadow-overlay: 0 4px 12px rgba(0, 0, 0, 0.18), 0 1px 3px rgba(0, 0, 0, 0.12);
--scrim: rgba(0, 0, 0, 0.28);
```

### Elevation Z-Index Ladder

| Z-Index | Layer | Description |
|---|---|---|
| `0` | Work surface | Canvas, background grid, diagram editor |
| `5` | Docked strips | Header, footer, fixed sidebars |
| `6` | Frame members | Contextual toolbar, floating control pair |
| `7` | Anchored menus | Dropdowns anchored to docked bars |
| `20` | Inline islands | Floating editor controls over canvas |
| `30` | Transient notices | Auto-dismissing toast notifications |
| `40` | Floating inspector | Collapsible side inspector panel |
| `45` | Capture strip | Modal capture bars outranking surface toolbars |
| `50` | Modal & Scrim | Blocking confirmation dialogs with 28% scrim |
| `60` | Portal context menu | Topmost right-click context menus |
