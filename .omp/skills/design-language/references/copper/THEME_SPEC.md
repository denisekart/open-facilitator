# Open Facilitator — Refined Copper Theme

> Canonical Theme Directory: `.omp/evidence/theme/copper/`

## Overview
Industrial precision copper (#C96A2D) and rich dark teal (#168B90) aesthetic with crisp surface hierarchy, solid opaque token palettes, and cool technical grounds.

This theme provides a high-contrast, developer-tool aesthetic influenced by studio environments (Linear, Vercel, JetBrains IDEs) and optimized for Open Facilitator's autonomous kernel, DAG workflow editor, YAML Monaco pane, and live Link Hub monitoring.

### Refinements Applied
- **Unslop Quality Philosophy:** Removed all hazy gaussian blur glowing filters and blurry drop shadows; replaced with crisp 1px borders, solid fills, and subtle 1px/2px elevation shadows.
- **Opaque Color Architecture:** Eliminated excessive transparent `rgba` overlays in favor of solid, high-contrast semantic tint tokens (`primaryTint`, `infoTint`, `successTint`, `warningTint`, `errorTint`).
- **Secondary Accent Darkening:** Deepened secondary tones (Teal `#168B90` / `#127377`, Olive Emerald `#58894C` / `#447A38`, Amber Gold `#C48A00` / `#A87400`, Brick Red `#C75454` / `#BA3C3C`) for grounded, professional contrast.
- **Modern Clean Light Theme Ground:** Transitioned light theme background from rustic beige to a crisp, technical cool-neutral grey (`#F1F3F5`) with pure white card surfaces (`#FFFFFF`).

## Token Reference

### Dark Mode Tokens (`#232831`)
| Token | Hex Value | Role |
|---|---|---|
| Ground Background | `#232831` | Primary canvas & app background |
| Surface Primary | `#2B313B` | Header, cards, editor, panels |
| Surface Alt | `#323944` | Sub-toolbars, input containers, table headers |
| Surface Highlight | `#3B4350` | Active selection, elevated cards |
| Border Standard | `#3F4754` | Primary structural dividers |
| Border Subtle | `#343B47` | Inner list separators, subtle grids |
| Primary Accent | `#C96A2D` | Active tabs, trigger nodes, save buttons, selected pills |
| Primary Hover | `#E58A45` | Interactive hover states, active paths |
| Primary Tint (Solid) | `#3D332D` | Background fill for selected rows, active tabs, node headers |
| Primary Border (Solid) | `#8A4820` | Border for active elements |
| Info / Link Secondary | `#168B90` | Link Hub status, policy gates, schema badges |
| Info Tint (Solid) | `#223538` | Background for admission gates, link hub indicators |
| Success State | `#58894C` | Verified runs, passing tests, healthy gateways |
| Success Tint (Solid) | `#273426` | Background for succeeded rows and passed verification |
| Warning State | `#C48A00` | High load, queue backlog, lease expiry warnings |
| Error State | `#C75454` | Ingress rejections, merge conflicts, failed steps |
| Text Ink | `#F3F4F6` | High-contrast headings and active labels |
| Text Muted | `#9CA3AF` | Secondary labels, descriptions, schema keys |
| Text Faint | `#6B7280` | Timestamps, metadata, line numbers |

### Light Mode Tokens (`#F1F3F5`)
| Token | Hex Value | Role |
|---|---|---|
| Ground Background | `#F1F3F5` | Modern cool-neutral technical studio ground |
| Surface Primary | `#FFFFFF` | Pure white cards and panels |
| Surface Alt | `#E8ECF0` | Crisp cool-tinted toolbars and inactive pills |
| Border Standard | `#CFD6E0` | Crisp structural border |
| Border Subtle | `#DEE3EB` | Subtle row separators |
| Primary Accent | `#C96A2D` | Primary brand accent and active states |
| Primary Hover | `#B3581E` | Interactive button and link hover |
| Primary Text | `#AC531C` | Darkened copper text for high contrast on light ground |
| Primary Tint (Solid) | `#F8EFE8` | Solid light copper tint for active rows and headers |
| Primary Border (Solid) | `#E2B696` | Border for active cards and pills |
| Info / Secondary | `#127377` | Secondary status and Link indicators |
| Info Tint (Solid) | `#E5F3F4` | Solid light teal tint |
| Success State | `#447A38` | Verified steps and healthy indicators |
| Success Tint (Solid) | `#EAF3E8` | Solid light green tint |
| Warning State | `#A87400` | Warnings and attention pills |
| Error State | `#BA3C3C` | Critical errors and rejections |
| Text Ink | `#1A202C` | High-contrast dark typography |
| Text Muted | `#505A69` | Secondary body text |
| Text Faint | `#7A8699` | Timestamps, metadata, line numbers |

## Generated Artifacts
- `open-facilitator-copper-theme-showcase.png` (2880 × 1800 side-by-side presentation mockup)
- `showcase-side-by-side.png` & `theme-showcase.png` (convenience aliases)
- `open-facilitator-light-theme.png` (2560 × 1440 standalone light theme)
- `open-facilitator-dark-theme.png` (2560 × 1440 standalone dark theme)
- `showcase-template.html`, `light-theme-template.html`, `dark-theme-template.html` (interactive HTML sources)
