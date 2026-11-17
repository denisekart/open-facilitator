---
name: design-language
description: "Portable 'Ink on Paper' design language and full UX spec for any web app: tokens, theming, typography, iconography, logo rules, app-shell layout, navigation and routing, disclosure tiers, menus, modals, drawers, popovers, sliding and fade motion, forms, lists and tables, density doctrine, empty/loading/error states, WCAG rules, responsive chrome, anti-patterns, review gate. Use when building or reviewing any UI so styling and interaction choices stay coherent. Concept-neutral, not tied to a repo or a product type."
---

# Design Language — "Ink on Paper"

A complete UX spec for tool-like web apps. It governs the chrome and every surface around the
primary content area: shell, navigation, menus, overlays, forms, lists, states, motion. It says
little about the content area itself, which may be a document, table, canvas, map, or timeline.

The rule underneath everything: chrome is a calm instrument, the content is the product. Cool
slate neutrals, mono for anything a machine assigned, one warm accent reserved for state.
Screens are judged by whether the primary action is obvious with no onboarding.

Feel cards, rendered rather than imagined: `references/palette.svg`,
`references/typography.svg`, `references/iconography.svg`, `references/components.svg`,
`references/navigation.svg`, `references/motion.svg`. Content notes at the end.

# I. Foundations

## 1. Token architecture

One token source file, `styles/tokens.css`. It is the only file where hex colours are allowed,
and lint (`color-no-hex`) blocks hex anywhere else.

Components consume semantic properties only: `--bg`, `--surface`, `--surface-raise`, `--ink`,
`--ink-muted`, `--ink-faint`, `--line`, `--accent`, `--accent-on`, `--danger`, `--work-bg`.

Themes switch on `<html data-theme="light|dark">`. The choice comes from
`prefers-color-scheme`, is persisted by the app, and is applied before first paint by a
six-line inline bootstrap in `index.html`. A theme flash on load counts as a defect.

Derived tokens are computed, never new hex:

```css
--line-soft: color-mix(in oklab, var(--line) 45%, var(--work-bg));
--tint-selected: color-mix(in oklab, var(--accent) 20%, var(--surface-raise));
--accent-text: var(--accent);  /* only light overrides: the darker twin, see §2 */
--accent-rule: var(--accent);  /* both themes satisfy this from --accent itself */
--hover: color-mix(in oklab, var(--ink) 6%, var(--surface-raise));
```

`--line-soft` measures about 1.2:1 against the ground it floats on: a whisper, not a drawn
frame. Third-party widgets get themed by remapping their own custom properties onto ours with
`color-mix`. A vendor's brand blue appearing in the UI is a defect.

Ship two themes, or ship one. Both carry one gold signal on cool blue-grey grounds: dark
over slate, light over paper-white fields. Both read as an editor, not as stationery.

## 2. Palette

| Token | Light | Dark | Role |
|---|---|---|---|
| `--bg` | `#eef2f6` | `#2b2e33` | the canvas: app ground and content area |
| `--surface` | `#f7f9fb` | `#33373d` | header, docked strips, chrome fills |
| `--surface-raise` | `#ffffff` | `#3b4046` | menus, popovers, fields, toolbar band, floating panels |
| `--ink` | `#21262e` | `#d6dae0` | primary text |
| `--ink-muted` | `#676f7c` | `#99a1ab` | secondary text, hints, keyboard hints |
| `--ink-faint` | `#8690a0` | `#767d85` | placeholders, disabled, shortcut glyphs only |
| `--line` | `#d5dbe3` | `#464c54` | 1px borders, frame edges, dividers, scrollbars |
| `--accent` | `#e6bf34` | `#e6bf34` | state fills: primary buttons, the selected-tint parent. Exact same hex, both themes |
| `--accent-rule` | `#ab8612` | `#e6bf34` | the accent as thin edges: 2px rules, focus rings, chip dots. Only light deviates, to clear 3:1 on white |
| `--accent-text` | `#8d6e0e` | `#e6bf34` | the accent as small text: links, Undo, shortcut glyphs. Only light deviates |
| `--accent-on` | `#14171a` | `#1b1e22` | text on accent fill (near-black ink on gold, never white). 10.2:1 light, 9.5:1 dark |
| `--danger` | `#d54e4e` | `#ff8f99` | error borders and dots |
| `--danger-text` | `#b03a3a` | `#ff8f99` | error and destructive words |
| `--line-soft` | `#e0e5ec` | `#373b42` | floating transients, skeletons |
| `--tint-selected` | `#faf2d6` | `#57523b` | selected rows and segments |

Neutrals run cool: blue sits above red in every grey. Warm greys, tans, and beige are banned,
because a warm ground beside a white card reads as packaging. One hue owns the screen,
`hsl(45.5, 81%, L)`, 46° clear of danger red's 0°, so a selection never reads as an error —
but the fill hex itself, `#e6bf34`, is identical in both themes — a state that shifts
colour between themes reads as two products. What deviates per theme are the accent's thin
roles, because 2px edges and small text must clear their floors against grounds eight
contrast steps apart. Dark satisfies fill, rule, ring, dot, and text from the one hex:
6.8:1 on slate. Light keeps the fill and its 10.2:1 near-black label, and takes the 3:1
non-text burden for rules, rings, and dots on `--accent-rule` `#ab8612`, and the 4.5:1 text
burden on `--accent-text` `#8d6e0e`; the bare gold fill sits at 1.8:1 on white, which is
acceptable precisely because a 600-weight uppercase label at 10.2:1 carries the button's
identity. The token contract keeps four names regardless; a theme satisfies them with fewer
values when its grounds allow it. Danger splits the same way: `#d54e4e` borders at 4.2:1,
`--danger-text` `#b03a3a` words at 6.0:1.

Rules:

- Accent is never decoration. A screen with nothing selected, focused, or linked contains no
  accent at all.
- Bad news does not soften. Errors keep a 1px `--danger` border, `--danger-text` words,
  and an explanation.
  Quiet transients may soften to `--line-soft`. A notice is temporal, so it separates with
  `--shadow-1` instead of a frame.
- Scrim is one value: `rgb(0 0 0 / 28%)`, used only for genuinely modal things.
- One shadow: `0 1px 3px rgb(0 0 0 / 12%)`, on floats only.
- No gradients, no glass, no blur, no second shadow, no second accent, no gradient text.
- State is never carried by colour alone. Selected means tint plus a weight shift. Error
  means colour plus border plus words. Sort direction means a glyph plus words.
- Success, warning, and info are allowed only as `color-mix` derivations of these tokens, and
  only where the product genuinely needs the distinction.

## 3. Accessibility floor (WCAG 2.2 AA, measured)

Measured against the actual painted ground, not against hope. Verified values:

| Pair | Light | Dark | Requirement |
|---|---|---|---|
| ink on surface | 14.4:1 | 8.5:1 | 4.5:1 text |
| muted on surface | 4.8:1 | 4.6:1 | 4.5:1 text, so hints use muted |
| accent-text on surface | 4.6:1 | 6.8:1 | 4.5:1 for links, Undo, shortcut glyphs |
| accent-rule on raise (thinnest edge) | 3.4:1 | 5.9:1 | 3:1 non-text: 2px rules, rings, dots |
| accent fill area on white (light) | 1.8:1 | — | exempt by size: the 600-weight label at 10.2:1 is the identifier |
| accent-on on accent | 10.2:1 | 9.5:1 | 4.5:1 button labels (ink on gold) |
| danger-text on surface | 5.7:1 | 5.5:1 | 4.5:1 error words |
| danger border on raise | 4.2:1 | 5.5:1 | 3:1 non-text borders |
| ink on tint | 13.6:1 | 5.6:1 | 4.5:1 selected rows |
| faint on surface | 3.1:1 | 2.9:1 | not text: placeholders, disabled, 3:1 UI minimum |

Beyond contrast: 2.4.7 visible focus everywhere (2px accent ring, 2px offset, never removed);
1.4.1 no colour-only state (every tint pairs with a weight shift, border, or word); 2.5.5 targets
44px on touch, 24px minimum on pointer with adequate spacing; 1.3.1 real semantics
(menubar/menu/tablist/listbox landmarks, `aria-current` on nav, `aria-sort` on tables);
2.2.1 no time limits except for the notice auto-dismiss, which pauses on hover and accepts
`Esc`; 2.4.3 focus order follows paint order, and every overlay restores focus to its opener;
prefers-reduced-motion collapses all motion (see §22); forced-colors keeps structure because
borders are real borders, not shadows. Run axe-core in CI; a token change re-runs the table.

## 4. Typography

System stack only, so nothing fetches and nothing reflows:
`system-ui, -apple-system, 'Segoe UI', Roboto, sans-serif`. One mono stack for anything a
machine assigned: ids, timestamps, paths, ports, code, counts in dense tables:
`ui-monospace, 'Cascadia Mono', 'JetBrains Mono', Menlo, monospace`.

The scale is locked at 12 / 13 / 14 / 16 / 20 / 24 px, and no other size appears anywhere.

| Size | Use |
|---|---|
| 12 | meta, chips, micro-labels, mono, timestamps, shortcuts |
| 13 | controls, menu items, dense rows, secondary copy |
| 14 | body default: paragraphs, inputs, list rows |
| 16 | panel and modal titles, section heads |
| 20 | empty-state title, page title |
| 24 | display, at most one per screen |

Weights are 400, 500, 600. Body runs 400, controls and labels 500, button labels 600
uppercase, brand and titles 600.
Emphasis in running text comes from `--ink` against `--ink-muted`, from weight.

- `line-height: 1.45`. Prose caps around 70ch, hints around 46ch.
- Micro-label, for group heads in panels, popovers, sidebars: 12px, weight 500,
  `text-transform: uppercase`, `letter-spacing: 0.04em`, `--ink-faint`. One per group, never
  nested.
- `code`, `kbd`, `time`: mono 12px. Keyboard hints use `--ink-muted`, never faint: a hint is
  text, and faint fails AA.
- Links: `--accent-text`, `text-decoration-thickness: 1px`, `text-underline-offset: 2px`. Inside
  dense rows, a link may inherit ink and keep the underline, so a table of rows does not turn
  into a field of accent.
- `::selection`: `--accent-text` at low alpha behind the ink, or the accent fill with ink on it.
- Any column of figures sets `font-variant-numeric: tabular-nums` and right-aligns.
- Letter-spacing appears only at brand (0.01em) and micro-labels (0.04em). Never negative,
  never uppercase on a long string, never a weight outside the three.

## 5. Geometry

- 4px grid: 4 / 8 / 12 / 16 / 24 / 32 (`--space-1…6`). No 10px, no 6px gaps. An optical
  exception needs a comment.
- Radii: **4px controls, 6px overlays (`--radius-overlay`), nothing else.** Pills are
  rounded rectangles, not stadiums: 999px radius is banned, because an ellipse with sharp
  ends looks generated. Chips ride their content: 20px tall, 2×8 padding, 6px dot, 8px to a
  divider.
- Borders are always 1px and always a token. Frame edges and dividers take `--line`, floating
  transients take `--line-soft`, a docked bar takes `--line` because it is a frame edge.
- **Buttons are ghost by default.** No border, no fill, `--ink-muted` text: a toolbar of
  eight verbs costs eight pixels of ink, not eight frames. Button labels are uppercase,
  13px, weight 600, `letter-spacing: 0.04em` — caps plus weight make a 13px verb readable
  at arm's length without growing the control, and it holds up on the gold fill where
  sentence-case mumbles. A button earns treatment only when
  it earns meaning: primary gets the accent fill (one per view), quiet gets a `--surface-raise`
  fill with `--line-soft` edge (it sits among frame lines, like inside a segmented group),
  hover gets the 6% ink wash, destructive keeps text-only in `--danger`. A border on a control
  means exactly one of: it is a frame, it is a field, or it is an error.
- The shadow belongs to floats. A docked element gets none, since it is attached.
- Focus ring, universal: `outline: 2px solid var(--accent-rule); outline-offset: 2px`, on
  `:focus-visible`. Never `outline: none` without a replacement. Inside dense menu rows use
  `outline-offset: -2px` so the ring stays inside the panel.
- Scrollbars: `scrollbar-width: thin`, thumb `--line`, track transparent.
- Touch targets are 44×44, reached with an absolutely positioned pseudo-element at
  `width/height: max(100%, 44px)` so the painted box never grows.
- Chrome row height is 44px. Header, primary toolbar, and the touch minimum are one number.

## 6. Elevation

Fixed integers, documented in one place, no ad-hoc numbers.

| z | Layer |
|---|---|
| 0 | work surface |
| 5 | docked strips, backdrop decoration |
| 6 | frame members: contextual toolbar, floating control pair |
| 7 | anchored menus and dropdowns on a docked bar |
| 20 | inline editing island over content |
| 30 | transient notice |
| 40 | floating inspector |
| 45 | capture strip that outranks surface toolbars |
| 50 | modal scrim and panel |
| 60 | portal context menu, always top |

A frame member outlives a transient. Two floats sharing a z value is a defect: promote one or
dock it.

## 7. Iconography

Inline SVG with `currentColor`, `aria-hidden="true"`, `focusable="false"`, `display: block`.
Grid 24×24, rendered at 16px or 20px in chrome. Stroke 1.5, round caps and joins, unfilled
shapes. No duotone, no emoji, no second colour, no icon inside a coloured tile.

The house set, one file, ~36 glyphs: search, plus, back, fwd, chev, check, dash, close, dots,
sort, filter, panel, pencil, file, folder, link, tag, clock, user, star, eye, eye-off, bell,
lock, undo, redo, down, copy, move, gear, sun, moon, play, keyboard, cmd, trash. Adding one
means drawing it on the same grid at the same weight, or the set forks.

- Icon-only buttons are the exception. Chrome buttons show words at 13px. Only universal
  glyphs go icon-only, each with an `aria-label`: `✕` close, `⋯` more, `＋` add, `←` `→`
  history, caret for disclosure. A tooltip is not a label.
- Overflow is a literal `⋯` (U+22EF). Not a hamburger, not three text dots.
- State glyphs are shapes, so they survive greyscale: check for on, dash for unset, caret for
  disclosure, filled dot for live.
- **Active nav indicator: a 2px rule in `--accent-rule`, a few px shorter than the label,
  left-aligned with its first glyph, 8px below the baseline.** Shorter than the text, never
  centred: the rule and the label share a left edge, so they read as one mark. A rule exactly
  as wide as a label reads as a smudge one pixel either way. It survives forced-colors,
  where a tint would not.
- If the content area is hand-drawn or organic, keep it. The chrome stays exact either way.

## 8. Logo rules

Any product mark built from this language follows the same construction, so the icon set and
the brand are one drawing system:

- 24×24 grid, coordinates on multiples of 2.
- One stroke weight, 1.5, round caps. Circles and straight connector lines only: no fills
  except the accent dot, no gradients, no outlines-of-outlines.
- At most four filled or stroked figures plus at most three connectors. If it needs five
  shapes it needs a redesign.
- Two colours maximum: `--ink` plus one accent dot (r≈1, filled). The dot carries identity
  the way it carries state: one warm mark, no more.
- Must legibility-check at 16px and at 24px inside a favicon. Test both at build time by
  rasterising.
- App tile variant: the mark reversed in `#fff` on an accent field, 5px corner radius.
- Wordmark sits beside the mark, 16px/600, letter-spacing 0.01em, `--ink`. Under 720px the
  wordmark drops and the glyph alone carries identity.
- The mark never animates, never glows, never gets a drop shadow.

# II. Shell and navigation

## 9. The one-frame rule

Chrome geometry is identical on every route, view, and activity. Header height, right-cluster
positions, band widths, and bottom-band order do not change when content changes or a panel
opens. Frame drift between views is the most expensive-feeling defect in this language, because
it makes a working app feel broken.

```
┌ header 44px: brand │ nav │ toolbar band (fixed width) │ search · capture · pair · chip · ⋯ ┐
├ breadcrumb row (only at depth ≥ 2) ────────────────────────────────────────────────────────┤
│  ┌ work surface ──────────────────────────┐  ┌ floating inspector 360px ┐                   │
│  └───────────────────────────────────────┘  └───────────────────────────┘                   │
├ bottom band: control pair (left) · contextual toolbar (centre) · entry strip (full width) ──┤
└ notice line (transient, top-centre under header) ┘
```

Header material is `--surface` with a 1px `--line` bottom border, 44px tall, 12px side padding,
8px under 720px.

The toolbar band is one fixed-width slot (320–520px, `min-inline-size: 0` when compact),
`--surface-raise`, 36px tall, painted once by the slot. Per-view toolbars portal into it and
draw transparent inside the painted band, so switching views never moves the status chip.
Overflow scrolls inside the band. The frame itself never reflows.

Breadcrumbs appear at depth 2 or more: 13px, `--ink-muted`, `/` separators, current segment
`--ink` and non-interactive. A breadcrumb replaces a "back to X" button, it does not join it.

Fixed overlays clear each other through published tokens. An entry strip offsets by the height
the surface toolbar publishes, so nothing docked hides another docked thing's controls.

## 10. Density doctrine

Space is the product's budget, not free. The frame reserves 76px total (44 header + 32 entry
strip), so a 900px viewport shows content across ~90% of its height. Inside the content:

- **Rows are a dial with two named settings.** Dense: 13px type, 4×8 padding, 30–32px rows,
  for data grids, file lists, and any view a power user scans all day. Comfortable: 14px,
  8×12, 44px, for touch and for sparse editorial lists. One density per view, switchable,
  never mixed inside a table.
- Columns earn their width by scan order: name first and widest, owner and status narrow and
  muted, timestamps mono and right-aligned, actions as a quiet `⋯` at the far edge. Five
  columns of substance beat eight of decoration.
- Secondary metadata belongs on a hover row expansion or in the inspector, not in a sixth
  column. If two columns always change together, they are one column.
- Progressive disclosure (§11) is what allows density without clutter: the dense frame hides
  complexity instead of shrinking type below 12px to expose it.
- Opposite failure: a "clean" screen that shows six records and wastes the rest. If a view has
  room for twelve rows, it should show twelve. The empty-state treatment (§20) is allowed to
  breathe because there is nothing else to show.
- Whitespace is structural: one 12px gap between a group and its edges, 8px between siblings,
  4px between a label and its control. Uniform gaps at the right size read as calm; oversized
  gaps read as an AI hero section.

## 11. Disclosure tiers

Complexity is hidden, not removed, and no action lives in two tiers.

| Tier | Surface | Contents | Cap |
|---|---|---|---|
| 0 | header, always visible | nav, search, create, history pair, status chip, global `⋯` | 6 controls, never grows |
| 1 | contextual toolbar | the 2 to 4 actions most likely wanted on the current context | 4 buttons plus `⋯` |
| 2 | context or overflow menu | the complete action set for that target | 9 items, 2 submenus |
| 3 | command palette, `Ctrl+K` | everything, typed | one input, grouped results |

One action registry backs all four. A menu item, a toolbar button, a shortcut, and a palette
entry are four views of one action id. Nothing runs from the palette that is not also
reachable by mouse within two clicks of its context.

Tier 1 appears only when its context exists. Its home is a known frame edge, bottom-centre,
never over the thing it acts on.

A control whose action has no owner renders visibly dead, faint with a default cursor. Hiding
it would move the layout, and a layout that jumps reads as a bug.

Discoverability comes from the palette and a `?` help surface. No tours, no tooltip
walkthroughs, no onboarding modals, no coach marks, no feature badges.

## 12. Navigation and routing

- Real routes for real views. Every view has a URL, a deep link restores view plus selection,
  and Back and Forward do what the browser promises. `Alt+←` where the host permits, plus an
  in-chrome pair.
- Back and Forward are one joined control: shared 1px border, 1px separator, no individual
  radii, disabled when the stack is empty, and the disabled state updates on navigation rather
  than on click.
- View switches crossfade over 180ms. Old and new surfaces overlap, nothing unmounts and
  reloads, nothing flashes the page background. A focused item that survives the change keeps
  its highlight ring so the eye tracks it across.
- In-app section jumps move focus to the new region's heading and scroll it in with
  `scroll-margin-top` set to the header height, so anchors do not land under fixed chrome.
- Content links are real `<a href>`. A click that opens an inspector or drawer uses
  `pushState`, so browser back closes the overlay instead of leaving the app.
- Tabs mean sibling views of one object. `role="tablist"`, 13px, 4×8 padding, 4px radius,
  selected = tint fill plus a 2px `--accent-rule` under the label, left-aligned with it and
  a few px shorter.
  Arrows move, Home and End jump,
  inactive tabs take `tabindex="-1"`.
- Side navigation earns its place past five top-level destinations: 240px, `--surface`, 1px
  right divider, 13px items at 4×8, micro-label group heads, current item in ink at 600
  weight, no stripe. It collapses into the overflow menu, never into a rail of
  unlabeled glyphs.
- Selection state goes in the URL when it is shareable, meaning object id, filter set, page.
  Incidental state, hover row or transient focus, stays out.
- With autosave present, unsaved work never blocks navigation with a modal. Persist silently
  and report through the status chip.

## 13. Sidebars, drawers, inspectors

A persistent sidebar is docked: `--surface`, 1px divider, no shadow, no animation beyond
content fades. Its width is constant, and collapsing it changes only the work-surface width.

A floating inspector appears on selection or explicit request, never by default. Fixed under
the header at top = header + 12, right 12, bottom 12, `width: min(360px, 100vw - 32px)`,
`--surface-raise`, 1px `--line`, 6px radius, `--shadow-1`. Header row with title and `✕`,
groups divided by 1px top rules, micro-labels for sections, mono 12px for ids and timestamps.
Dismissible by `✕`, `Esc`, or clicking the selection off. It opens with the 100ms fade and does
not slide.

A drawer takes over a region on purpose: mobile nav, a filter sheet, a multi-step form. This is
the one place sliding belongs. `transform: translateX(-100% → 0)` or
`translateY(100% → 0)`, 180ms in, 120ms out, standard easing, 28% scrim under it, focus moves
in on open and returns to the trigger on close, `Esc` closes, body scroll locked while open.
Width `min(400px, 100%)` at a side, or a bottom sheet capped at 70vh.

Decide by what the surface demands. Readable while the content stays interactive means
inspector, so fade and no scrim. Needs a decision means drawer or modal, so slide or scrim.
Never both at once.

# III. Surfaces

## 14. Menus

One portal-rendered ARIA `menu` implementation serves the whole app, including content, lists,
rows, and the `⋯` button. A second menu system is a defect.

- Contents are data: per-target schemas referencing registry action ids. Groups render as
  disabled micro-label rows. 9 items maximum, 2 submenus maximum.
- Panel: `--surface-raise`, 1px `--line`, 6px radius, `--shadow-1`, 4px padding,
  `min-width: 240px`, `max-height: 70vh` with internal scroll.
- Items are full-width, 4×8 padding, 4px radius, 13px, label left, shortcut right in
  `--ink-faint` with `white-space: nowrap`, optional 16px icon column. Hover fills with the
  6% ink wash. Disabled is faint with a default cursor. Destructive items sit last, separated
  by a 1px rule, labelled `--danger`, always with a plain-language verb.
- Submenus open at `left: calc(100% - 4px)`, top-aligned to their row, flipping to the
  inline-start side near the viewport edge.
- Placement blends and dodges. The panel wears the environment's own treatment, so there is no
  second elevation system, and its initial rect must not cover the element the action is about.
  Pass that target rect, open toward the free space on the pointer's side, flip at viewport
  edges. Overlap is the last resort, used only when no in-viewport placement avoids the target.
- Behaviour: no scrim, not modal, focus moves in, Arrow / Home / End / Typeahead, `Esc` closes
  and returns focus to the opener, outside click and scroll both close. Inside inputs,
  textareas, and contenteditable, the native menu stays, because spellcheck and paste outrank
  consistency.
- Anchored on a bottom-docked bar, menus open upward from the trigger and clear the fixed left
  cluster, so items are clickable rather than buried.
- The menu-bar variant keeps text roots in the header with `role="menubar"` and reuses the item
  recipe. Arrows move between roots.
- On mobile the same items render as a bottom sheet list at 44px rows. A menu never becomes a
  screen of its own.

## 15. Popovers, selects, tooltips

A popover is an anchored non-modal panel: date picker, colour picker, filter editor. Menu panel
recipe, 8px gap from the trigger, 100ms fade, no scrim, `Esc` closes and restores focus, click
outside closes, repositions on scroll and resize.

A native `<select>` stays native until the design needs icons, descriptions, or async options.
Then use a `role="listbox"` popover with typeahead and the same item recipe. Never a custom
dropdown that lost typeahead, and never a set of free-floating buttons where a segmented
control belongs.

A tooltip stays `title`-level: 12px, `--surface-raise`, 1px `--line-soft`, 4px radius, 4×8
padding, 500ms delay, no arrow animation, no action inside, never the only carrier of a label.
If it wants a button, it is a popover.

## 16. Modals

A modal is expensive, so ration it.

- Use one when the user must decide before continuing: destructive confirm, blocking conflict,
  mandatory pick. Everything else is an inspector, popover, inline expansion, or a notice with
  Undo.
- No first-run modals, no welcome modals, no "what's new" modals.
- Scrim 28%. Panel `min(560px, 100vw - 16px)`, `--surface-raise`, 1px `--line`, 6px radius,
  `--shadow-1`, `overflow: hidden`, 16px padding, title 16px/500, body 14px, footer
  right-aligned with an 8px gap.
- Motion is a 100ms opacity fade. No scale, no slide, no bounce. A slide says a place, a fade
  says a question, and a modal is a question.
- Focus moves to the first control or the least destructive button, is trapped while open,
  `Esc` cancels, focus returns to the trigger. A scrim click cancels only when cancel is
  harmless.
- Buttons: exactly one primary, the accent fill; secondary is a quiet button; destructive keeps
  the ghost recipe with a `--danger` label and is never the default-focused control.
- One modal at a time. A second dialog means one of them is a popover.
- Long content caps at 70vh with internal scroll and a pinned header and footer.

## 17. Command palette and overlays

`Ctrl/Cmd+K` opens the palette, `/` opens search in results mode, `?` opens help generated from
the action registry.

- One input: 14px, 12×16 padding, no border, 1px `--line` bottom rule, surface fill. Plain text
  searches, a leading `>` switches to commands.
- Results group under micro-label heads. Arrow selects, Enter runs, `Ctrl+Enter` runs the
  secondary meaning such as open elsewhere, `Esc` returns to the previous surface with focus and
  scroll or camera state untouched.
- The active row takes the surface fill, 600 weight, and `box-shadow: inset 2px 0 0
  var(--accent-rule)` (a palette row is a control being aimed at, so a leading stripe is
  wayfinding, not decoration), with the shortcut right-aligned in mono 12px `--ink-faint`.
- Panel 560px, top-anchored at 12vh, 6px radius, `--shadow-1`, 100ms fade, 28% scrim, 70vh
  maximum with internal scroll. Substring matching plus the existing search index is enough.
  No fuzzy library.
- Counts and metadata sit at 12px `--ink-faint`, right-aligned.
- A quick-add box is not a command surface. Text starting with `/` or `>` is content.

## 18. Notices, toasts, banners

A notice is one quiet `aria-live` line, top-centre under the header, 13px, `--surface-raise`,
6px radius, 4×12 padding, separated by `--shadow-1` alone, since it is temporal and gets said
rather than mounted. Auto-dismiss near 6s, pause on hover, `Esc` dismisses. One at a time, and a
new notice replaces the old.

Error notices regain a 1px `--danger` border, plus an inline action such as Retry or Undo. Undo
belongs in the notice rather than in a confirm dialog: "Deleted 3 items · Undo".

A banner is persistent and app-level: offline, quota, expiry. It docks under the header at full
width, `--surface`, 1px bottom border, 13px, 8×12 padding, dismissed with `✕`, no shadow,
because it is docked. State that belongs to the status chip shares the chip slot instead, so one
place reports system state.

Never notify for the routine success of a routine action. The chip already says it.

## 19. Forms and inputs

- Visible label above every field, 13px/500, 4px gap. A placeholder is an example, never a
  label. Help text 12px `--ink-muted` below, 4px gap.
- Field: 14px text, 8×12 padding, 4px radius, 1px `--line-soft`, `--surface-raise`. That border
  is a hint, not a frame, and the focus ring is the real state cue. Focus takes the 2px accent
  outline at 2px offset. Error takes a 1px `--danger` border plus a 13px `--danger` message that
  names the reason and the fix.
- One column per group, labels above inputs. Inline labels only in dense filters. Related fields
  group behind a 1px top rule and a micro-label.
- Width follows the data, capped near 40ch for prose. A full-viewport text box for a
  six-character code is the usual tell.
- Validate on blur, not per keystroke. Submit-time failures render as anchors that focus the
  field. Do not disable submit until the form is valid, since saying why beats a dead button.
- Async submit keeps the button's width, swaps the label to a present-tense verb ("Saving…"),
  sets `aria-busy`, uses no spinner larger than 14px, and blocks double submit.
- Drafts need no save button. The chip reports it, a failed save stays visible until resolved,
  and the notice carries retry.
- Destructive inline actions are ghost buttons in `--danger`, placed in the row or section
  they act on.
- Search and filter inputs use `type="search"`, clear on `Esc`, debounce 300ms on the network,
  and announce result counts politely.
- Segmented groups are the radiogroup pattern: a quiet container of 13px segments, min-height
  28px, 1px `--line-soft` separators, selected segment gets the tint plus a 2px
  `--accent-rule` under its label, left-aligned with it and a few px shorter, chosen-but-currently-unsatisfiable segments take `aria-disabled` (focusable,
  still announced, intent recorded) rather than a silently blanked control.

## 20. Status, lists, tables, and states

**Status chip.** One chip in the header reports system state, and no other surface may claim
it. Rounded rectangle (4px radius), 20px tall, 2×8 padding, 12px text, a 6px round dot,
`--line-soft` edges so the shape comes from the fill. The pill rides its content; generous
padding around a twelve-pixel word makes a status readout look like a sticker.

| State | Label | Dot | Treatment |
|---|---|---|---|
| idle | Saved | `--ink-muted` | soft edge, muted text |
| pending | Saving… | `--accent-rule` | soft edge, muted text |
| error | Not saved, with Retry | `--danger` | 1px `--danger` border, danger text |
| unavailable | Storage unavailable | none | soft, faint |

Availability badges share the chip slot behind a 1px `--line-soft` divider, so an offline
session reads "Saved · Offline" instead of growing a second pill. Report only what is measured:
an offline write to a local store is a saved write, so the chip says Offline, not Unsaved.
Nothing claims "up to date" while a write is in flight.

**Rows.** 14px comfortable (44px) or 13px dense (32px), 8×12 / 4×8 padding, 1px `--line-soft`
bottom rule, hover is the 6% ink wash, selected is tint plus 600 weight on the row's primary
column — no leading stripe: a front-painted rule on every selected row reads as decoration,
not state, and the tint alone fails 1.4.1 without a second signal.
Numbers right-align with `tabular-nums`. Timestamps mono 12px, absolute, relative form in
`title`. Sort is a header button with `aria-sort`, a glyph, and words. Selection uses a 40px
checkbox column; selecting anything reveals the bulk bar in the tier-1 slot: count, 2 to 4
ghost actions, `✕`, fixed at a known edge, 100ms fade. Cursor or "Load more" for feeds with a
real end message, numbered pages for admin tables, never both; loading more keeps scroll
position and does not reorder seen rows.

**States, all designed:**

- First-use empty: `--work-bg` ground, brand glyph watermark at 8% opacity, 20px/500 title,
  13px `--ink-muted` hint capped near 46ch, one offer. No wizard, no card grid of suggestions.
- Filtered empty: no watermark, one line naming the filter, "Clear filters".
- Loading: a skeleton shaped like the incoming content in `--line-soft`, static (no shimmer);
  anything resolving under 200ms shows nothing.
- Bulk progress: a 2px accent bar or a count, "412 of 1,000". Never a determinate-looking
  indeterminate spinner.
- Surface error: a docked banner with what failed, what was kept, retry. No stack trace, no
  blank region. Whole-view error: the empty-state recipe with a danger title.
- Offline: banner or chip badge stating the consequence, "Changes will sync when you're back".
- 404: centred, brand glyph, one line, the attempted path echoed in mono 13px, a link home.
- Partial data: show what loaded plus one line about what did not. One failed panel never
  blocks a view.

# IV. Motion

## 21. Motion spec

Only `opacity` and `transform` animate, only at these durations, only with this easing.

| Token | Value | Used for |
|---|---|---|
| `--ease-standard` | `cubic-bezier(0.2, 0, 0, 1)` | everything |
| `--duration-fade` | 100ms | menus, popovers, modals, notices, inspectors, bulk bar: opacity only |
| `--duration-exit` | 120ms | any exit, level-of-detail crossfade, filter result swap |
| `--duration-flip` | 180ms | FLIP re-layout, route crossfade, drawer in, the ornament |

What slides: drawers and mobile sheets, `translateX` or `translateY` on the panel body, 180ms
in, 120ms out, over a 28% scrim, focus trapped.

What never slides: menus, notices, modals, inspectors, dropdowns, bulk bars. They fade. A slide
claims spatial movement, and a panel that fades in over its own anchor travels nowhere.

View changes crossfade at 180ms. Nothing unmounts and reloads, and the page background never
flashes.

Re-layout uses FLIP: measure, apply the new layout, interpolate the transform to identity.
Entering items fade with `scale(0.96 → 1)` over 180ms, staggered at most 12ms per item by
distance and capped at 120ms total. Exiting items fade over 120ms, then unmount.

Interrupted transitions retarget from current interpolated values, never snap.

The anchor rule: on any re-layout, the focused or selected item keeps its screen position and
the container moves. Nothing the user is watching teleports.

Level-of-detail and density changes crossfade opacity over at least 120ms with hysteresis
thresholds, so they never flicker and never reflow positions.

Hover and focus changes are instant. No transition on hover colour, since a button that must
feel immediate cannot take 150ms to say so.

One expressive motion is allowed: the active-nav rule draws in on a nav change,
`scaleX(0 → 1)` with a fade, 180ms, `transform-origin: left center`.

Decorative backdrops run one rAF loop, pause when the tab hides and once real content exists,
stay under 50ms of script, and take `pointer-events: none`.

`prefers-reduced-motion: reduce` collapses everything to instant, with state still correct and
motion absent. Backdrops draw one static frame. This is a requirement, not a courtesy.

# V. Responsiveness, performance, copy

## 22. Compact chrome, 720px and under

- Drop decoration, not glyphs: wordmark, keyboard hints, chip words, which stay in the
  accessibility tree.
- Hit areas reach 44px through the pseudo-element trick, so painted layout matches desktop.
- What still does not fit scrolls inside its own row, `overflow-x: auto`,
  `overscroll-behavior-x: contain`, scrollbar hidden. Never the document, because a document
  scroll dismisses the transient that the tap just opened.
- Frame height stays 44px, bands keep height and width with `min-inline-size: 0`, and tools
  stay reachable by scrolling to them. Hiding tools on a phone is a defect, since a small
  screen is not a lesser product.
- Overlays become bottom sheets at 70vh, or full-width menus with 44px rows.
- QA viewports: 1440×900, 1280×800, 390×844, both themes.

## 23. Perceived performance

- User writes go optimistic: reflect at once, reconcile on response, roll back visibly with a
  danger notice on failure, never silently.
- Interaction holds 55fps or better. Animate only opacity and transform, keep `will-change` on
  animating elements, and never transition `width`, `height`, `top`, or `left`. If a mid-tier
  laptop cannot hold 55fps, the motion is cut, not the frame rate.
- Chrome paints under 200ms. It is a few KB of token CSS, and nothing blocking-fetches a font or
  an icon set.
- Debounce network to 300ms, throttle scroll work to rAF, virtualize past 200 rows.
- Heavy overlays, palette, picker, help, lazy-load at 12KB of app code each and open with no
  perceived delay. Warm the module on idle after first paint.
- Menus and overlays mount while open, one portal each, and animate nothing but a 100ms fade.
- Cap export payloads near 4MB, revoke object URLs, and never serialize synchronously on a
  keystroke.

## 24. Copy tone

Plain, honest, verb-first. No marketing voice.

> Saved · Not saved · Retry · Deleted 3 items · Undo · No results for "export", clear filters ·
> Offline, changes will sync when you're back

Never "Oops!", "Great job!", or "Welcome to your productivity hub!". Destructive labels name the
consequence, "Delete 3 files", not "OK". Empty states say what to do next in fourteen words or
fewer.

# VI. Enforcement

## 25. Anti-patterns

Reject on sight: gradients, glass, blur, multi-shadow card systems, warm greys and beige in the
neutrals, a brown accent, white text on the gold fill, decorative accent, second accent, an
unremapped vendor brand colour, bordered ghost buttons (bordered by default), stadium-radius
pills, slides or springs or bounces on transient panels, a menu covering its own target, tours
and tooltip walkthroughs and onboarding modals and coach marks, unlabeled icon buttons,
`--ink-faint` on text a user must read, colour-only state, off-grid spacing, hex outside the
token file, frame geometry that shifts between views or when a panel opens, spinners under
200ms, shimmer skeletons, notices for routine saves, a confirm dialog where an Undo belongs, a
full-width box for a short value, a placeholder standing in for a label, a custom dropdown that
lost typeahead, two densities in one table, hover-only actions, focus lost on close, two modals,
a screen of empty padding posing as clean design.

## 26. Review gate

Static checks first: hex only in the token file, no durations outside 100, 120, 180ms, no
transition on a layout property, no z-index outside the ladder, every icon button labelled,
every field labelled, contrast table re-measured after any token change.

Then real screenshots at 1440×900, 1280×800, 390×844, both themes, plus axe-core. Score ten
criteria, minimum 3 of 4 each, evidence attached: clarity, restraint, orientation, continuity,
density, typography and layout, states, accessibility, perceived performance, authenticity. The
last one asks whether it reads as an instrument rather than a dashboard.

# Reference graphics

Read these with the spec. Where a number in prose and a number in a card disagree, the card
shows intent and the prose is the rule. All six are generated by `references/_gen.cjs`
(`node _gen.cjs`), which measures text widths, tracks region bounds, and fails the build on
overflow.

- `references/palette.svg`: both themes side by side, every token with hex and role, the
  selected tint, chip rows, the four button treatments, focus ring, and measured AA ratios.
- `references/typography.svg`: the 12–24 scale at all three weights in both themes, the
  micro-label, mono provenance rows, tabular figures, the keyboard-hint contrast proof, and a
  rejected example.
- `references/iconography.svg`: the full 36-glyph house set on slot grids in both themes, the
  2px active rule, logo construction on the 24-grid at 88px and 16px, the app-tile variant, and
  rejected icon treatments.
- `references/components.svg`: buttons, chips in all states, the joined history pair, the
  segmented radiogroup, fields, the context menu, both notice kinds, the docked entry strip and
  the inline island, each drawn twice, light and dark.
- `references/navigation.svg`: the one frame with 44px header, fixed band, breadcrumb, dense
  30px table rows, selection, bulk bar, inspector, the four disclosure tiers, the z ladder, and
  the compact rule.
- `references/motion.svg`: duration bars at 1px/ms, the easing curve, the FLIP pair with
  stagger notes, the slides-versus-fades split, and the reduced-motion rule on dark.
