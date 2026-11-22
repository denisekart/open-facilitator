# Motion, States & Copy Tone

This reference defines the animation physics, transition budgets, state machine UI patterns, and copy rules for Open Facilitator.

## 1. Motion Specification

Only `opacity` and `transform` are permitted to animate. Animating layout geometry (`width`, `height`, `top`, `left`, `margin`, `padding`) is strictly banned.

```css
--ease-standard: cubic-bezier(0.2, 0, 0, 1);
--duration-fade: 100ms;
--duration-exit: 120ms;
--duration-flip: 180ms;
```

### Durations & Surface Assignments

| Token | Duration | Permitted Surfaces & Usages |
|---|---|---|
| `--duration-fade` | 100ms | Menus, popovers, modals, transient notices, inspectors, bulk action bar |
| `--duration-exit` | 120ms | Panel exit, filter result swaps, dismissals |
| `--duration-flip` | 180ms | Route crossfades, FLIP container transitions, drawer slide-ins, active nav underline |

### The Slides vs Fades Distinction

- **What Slides:** Drawers and mobile sheets (`translateX(-100% → 0)` or `translateY(100% → 0)` over 180ms).
- **What Fades:** Menus, popovers, notices, modals, inspectors, bulk bars. A panel positioned over its triggering element travels nowhere spatially; sliding it introduces artificial visual noise.

### View Switches & The Anchor Rule
- View changes crossfade over 180ms. Surfaces overlap during crossfade; background grounds never flash.
- **The Anchor Rule:** During container re-layout or data filtering, focused and selected items maintain screen coordinates while the surrounding container moves. Elements under user focus never teleport.

### Accessibility (`prefers-reduced-motion`)
`@media (prefers-reduced-motion: reduce)` collapses all animation durations to `0ms`. State updates remain immediate and structurally identical.

## 2. Designed System States

Every state is explicitly designed to keep the operator informed and in control:

### First-Use Empty State
- Centered on `--bg` ground.
- Faint brand glyph watermark at 8% opacity.
- 20px/600 title + 13px `--ink-muted` guidance sentence (capped at 46ch).
- Exactly one primary call-to-action button in copper `#C96A2D` (e.g. "Create Workflow").

### Filtered Empty State
- No brand watermark.
- Single line naming active filter: "No works matching `status: failed`".
- Immediate action link: "Clear filters".

### Loading & Skeletons
- Requests resolving under 200ms show nothing (avoids jarring flickers).
- Requests over 200ms display a static structural skeleton rendered in `--line-soft`.
- Shimmer animations and pulsing glow effects are banned.

### Bulk Progress
- Displayed as an exact numeric progress indicator ("412 of 1,000 processed") or a thin 2px copper progress rule.
- Determinate indicators are used only when accurate counts exist; otherwise use quiet indeterminate indicator.

### Surface & Whole-View Errors
- **Surface Error (Single Panel):** Docked banner indicating failure reason, preserved input state, and explicit "Retry" action.
- **Whole-View Error:** Centered empty-state layout with `--danger-text` title, error description, and recovery link.

### Offline & Sync Degradation
- The status chip reports "Saved · Offline".
- A docked top banner appears: "Offline — changes will synchronize when connection is restored".

### 404 Not Found
- Centered brand glyph, 20px title "Page not found", requested path echoed in mono 13px, and a link to return home.

## 3. Copy Tone & Voice Law

Copy is plain, concrete, active, and verb-first. Marketing enthusiasm and colloquialisms are rejected.

### Rules of Engagement
- **No Marketing Fluff:** Banned: "Oops!", "Great job!", "Awesome!", "Welcome to your productivity hub!".
- **Explicit Verbs:** Actions specify consequences: "Delete 3 works", "Revoke Token", not "OK" or "Continue".
- **Concise Empty States:** Empty states instruct next steps in 14 words or fewer.
- **Honest Feedback:** Status messages name what occurred: "Saved", "Saving…", "Not saved · Retry", "Deleted 3 items · Undo".
