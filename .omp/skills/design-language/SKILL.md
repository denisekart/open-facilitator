---
name: design-language
description: "Portable 'Ink on Paper' & Refined Copper design language and full UX spec for Open Facilitator: tokens, dark/light themes, typography, iconography, app-shell layout, disclosure tiers, motion, forms, tables, states, WCAG AA, and QA review gate. Load references/tokens-and-color.md for full token contracts and WCAG matrix, references/components-and-shell.md for frame and layout geometry, references/typography-and-icons.md for type scales and icons, references/motion-and-states.md for physics and state machines, references/verify-and-checklist.md for review gate, and references/copper/ for visual proof assets."
---

# Design Language — "Ink on Paper" (Refined Copper System)

Open Facilitator follows a precision developer-instrument aesthetic influenced by modern high-density engineering environments (Linear, Vercel, JetBrains IDEs). Chrome is a calm, neutral frame; content and autonomous workflow execution are the product.

Visual proof mockups and rendered theme cards live in `references/copper/` and `references/*.svg`.

## The Core Laws That Always Apply

1. **Unslop Quality Philosophy:** Blurry gaussian halos, ambient glowing drop shadows, and artificial neon gradients are strictly eliminated. Surfaces are defined by crisp 1px borders (`--line`), solid vector strokes, and subtle 1px/2px elevation shadows.
2. **Opaque Semantic Tints:** Active rows, selected tabs, and status callouts use solid opaque tints (`--tint-primary`, `--tint-info`, `--tint-success`, `--tint-warning`, `--tint-danger`) rather than muddy transparent overlays.
3. **Cool Technical Grounds:** Canvas neutrals run strictly cool. Dark theme uses deep slate graphite (`#232831`); light theme uses cool-neutral studio grey (`#F1F3F5`). Warm beige, yellow, and mud tones are banned.
4. **Restraint with Accent:** Industrial copper (`#C96A2D`, hover `#E58A45`) is reserved exclusively for state: active tabs, primary actions, trigger nodes, and focus rings. A screen with nothing selected or active contains zero accent color.
5. **Teal Secondary Accent:** Rich technical teal (`#168B90` dark / `#127377` light) is reserved for Link Hub telemetry, policy gates, and system info.
6. **The One-Frame Rule:** App chrome geometry (44px header height, fixed toolbar slots, cluster alignments) remains identical across every route and never jumps when panels open.
7. **Density Doctrine:** Space is budgeted. 32px dense rows with 13px type for tables and event streams; 44px comfortable for touch.
8. **Slides vs Fades:** Drawers and mobile sheets slide; menus, popovers, notices, modals, inspectors, and bulk bars fade (100ms).
9. **No Colour-Only State:** State pairs color with weight shifts, icons, or explicit labels to survive grayscale.
10. **WCAG 2.2 AA Floor:** All text and interactive controls clear 4.5:1 text and 3:1 non-text contrast against painted surfaces.

## Core Semantic Token Reference

| Token | Light Ground (`#F1F3F5`) | Dark Ground (`#232831`) | Role & Surface Assignment |
|---|---|---|---|
| `--bg` | `#F1F3F5` | `#232831` | Application canvas and DAG editor background |
| `--surface` | `#FFFFFF` | `#2B313B` | Header, cards, editor surfaces, docked strips |
| `--surface-alt` | `#E8ECF0` | `#323944` | Sub-toolbars, input containers, table headers |
| `--surface-raise` | `#DFE4EA` | `#3B4350` | Menus, popovers, active selection, modals |
| `--ink` | `#1A202C` | `#F3F4F6` | Primary high-contrast typography and headings |
| `--ink-muted` | `#505A69` | `#9CA3AF` | Secondary body text, descriptions, schema keys |
| `--ink-faint` | `#7A8699` | `#6B7280` | Placeholders, disabled states, timestamps, line numbers |
| `--line` | `#CFD6E0` | `#3F4754` | 1px structural borders and dividers |
| `--line-soft` | `#DEE3EB` | `#343B47` | Subtle inner separators and skeleton placeholders |
| `--accent` | `#C96A2D` | `#C96A2D` | Primary copper accent: active tabs, save buttons, trigger nodes |
| `--accent-hover` | `#B3581E` | `#E58A45` | Hover state on buttons and active execution paths |
| `--accent-text` | `#AC531C` | `#E58A45` | Accent text clearing 4.5:1 contrast on white and dark |
| `--tint-primary` | `#F8EFE8` | `#3D332D` | Solid opaque background for selected rows and active tabs |
| `--info` | `#127377` | `#168B90` | Rich teal for Link Hub status and policy gates |
| `--info-tint` | `#E5F3F4` | `#223538` | Solid opaque background for informational badges |
| `--success` | `#447A38` | `#58894C` | Olive emerald for verified runs and healthy gateways |
| `--warning` | `#A87400` | `#C48A00` | Amber gold for queue backlog and expiry warnings |
| `--danger` | `#BA3C3C` | `#C75454` | Brick red for ingress errors, failures, and rejections |

## Progressive Disclosure (Task Router)

Load specialized reference files on demand based on task requirements:

| Developer / Agent Task | Reference File | Topics & Specifications Covered |
|---|---|---|
| **Styling tokens, colors, themes, WCAG contrast** | `references/tokens-and-color.md` | Full light/dark token tables, semantic roles, WCAG 2.2 AA verified contrast matrix, opaque tint palette, `color-mix` formulas, elevation z-index ladder. |
| **App shell layout, navigation, menus, forms, tables** | `references/components-and-shell.md` | One-frame rule geometry, density doctrine (32px vs 44px), disclosure tiers 0–3, ARIA menus, popovers, modals, forms/inputs, status chip, data tables. |
| **Type scale, font stacks, micro-labels, icons, logo** | `references/typography-and-icons.md` | System UI and mono stacks, locked 12–24px scale, tabular numerals, prose width caps, 36-glyph 24×24 icon system, brand logo construction. |
| **Transitions, physics, system states, copy tone** | `references/motion-and-states.md` | 100/120/180ms motion budgets, slide vs fade rules, FLIP animation, empty/loading/error/offline states, plain verb-first copy rules. |
| **Design review, PR gates, anti-patterns audit** | `references/verify-and-checklist.md` | 10-point design review gate (score ≥3/4), unslop anti-patterns ban list, QA viewports (1440×900, 1280×800, 390×844), automated checks. |
| **Visual proof assets, mockups, HTML sources** | `references/copper/` | `THEME_SPEC.md`, `open-facilitator-copper-theme-showcase.png` (2880×1800 side-by-side mockup), standalone light/dark screenshots, interactive HTML templates. |
| **Vector reference cards** | `references/*.svg` | `palette.svg`, `typography.svg`, `iconography.svg`, `components.svg`, `navigation.svg`, `motion.svg`. |
