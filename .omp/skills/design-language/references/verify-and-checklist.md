# Verification, QA Checklist & Quality Gate

This reference provides the review rubric, automated testing criteria, QA viewport matrix, and anti-pattern ban list for evaluating any UI implementation in Open Facilitator.

## 1. The 10-Point Design Review Gate

Every pull request touching UI surfaces must score at least 3 out of 4 points on all ten evaluation criteria:

| Category | Passing Standard (Score 3–4) | Failure Smell (Score 1–2) |
|---|---|---|
| **1. Clarity** | Primary action is unambiguous within 2 seconds with zero onboarding. | Multiple competing colored buttons or missing primary action. |
| **2. Restraint** | Copper `#C96A2D` used solely for state and primary action. Teal used for Link status. | Decorative accents, colored cards, multi-color icons. |
| **3. Orientation** | Active route, selection, and breadcrumbs are immediately visible. | Unmarked active tabs or missing location indicators. |
| **4. Continuity** | One-frame rule respected; header height and toolbars never jump. | Layout reflows or jumps when panels or drawers open. |
| **5. Density** | 32px dense rows for data grids, 44px comfortable for touch. Space is budgeted. | Oversized whitespace grids wasting screen real estate. |
| **6. Typography & Layout** | Strict 12–24px scale, tabular numerals for figures, mono for IDs. | Unscaled font sizes, centered data tables, unaligned columns. |
| **7. System States** | Designed empty, filtered, loading, error, and offline states present. | Blank screens, raw error stack traces, unstyled null values. |
| **8. Accessibility** | 100% WCAG 2.2 AA contrast passed, visible 2px focus rings, screen reader landmarks. | Contrast failures, removed focus outlines, missing `aria-label`s. |
| **9. Perceived Performance** | 55+ fps transitions, optimistic writes, <200ms paint times. | Janky layout transitions, layout property animations (`width`/`height`). |
| **10. Authenticity** | Reads as a precision developer instrument (Linear/IDE feel). | Marketing dashboard aesthetic, bubbly rounded pills, AI sparkles. |

## 2. Anti-Patterns Ban List (Unslop Quality Law)

Reject on sight:

- **No Blurry Halos or Glows:** Gaussian blur filters, neon glow shadows, and hazy ambient lighting are banned. Replace with crisp 1px borders and solid vector lines.
- **No Excessive Transparency:** Avoid translucent `rgba` stacks where solid opaque tokens (`--tint-primary`, `--tint-info`, `--tint-success`, etc.) provide clean contrast.
- **No Rustic Beige:** Canvas ground in light theme must use crisp technical cool-neutral grey (`#F1F3F5`), never yellowish/beige tones.
- **No Stadium Pills:** 999px border radius is banned. Use exact 4px radius for controls and 6px for overlays.
- **No Ghost Button Borders by Default:** Ghost buttons are borderless and fill-free until hovered or active.
- **No Decorative Accents:** Screens with nothing selected or active must contain zero accent color.
- **No Sliders on Floating Transients:** Menus, popovers, dropdowns, and modals must fade in (100ms). Only drawers and bottom sheets slide.
- **No Unlabeled Icon Buttons:** Standalone icon buttons must have accessible `aria-label` attributes.
- **No Colour-Only State:** Status must pair color with text, icon shape, or weight shift to survive grayscale.
- **No Layout Animation:** Never animate `width`, `height`, `top`, or `left`. Animate only `opacity` and `transform`.

## 3. QA Viewports & Test Matrix

Validate layout and interaction across both Light and Dark themes at:

1. **Desktop Studio:** 1440 × 900 px (Primary widescreen IDE/console view).
2. **Standard Laptop:** 1280 × 800 px (Compact docked toolbar view).
3. **Mobile Screen:** 390 × 844 px (Bottom sheets, 44px touch targets, horizontally scrollable bands).

## 4. Automated Verification Commands

```bash
# Documentation & Signpost Cross-Reference Integrity
node protocol/tools/agents-docs-check.mjs

# UI Console Build & Svelte Linting
pnpm --filter @ofac/ui build
pnpm --filter @ofac/ui check

# Static Documentation Site Build
npm --prefix site run build
```
