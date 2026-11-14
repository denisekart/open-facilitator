# UX overhaul: a professional console and a site that shows it

**Status:** idea, ready to be planned.
**Governing law:** the product is **Open Facilitator** (prose),
**open-facilitator** (repo/slug identifiers), **ofac** (namespaces, ids,
short form). Never "Facilitator" alone, never anything else. Today the
console violates its own law — `App.svelte` renders a bare "Facilitator"
wordmark while the login gate says "Open Facilitator — operator console".

## Why

The console grew feature-first: each phase added a view, a testid, a
route. It works, it is honest, it is tested — and it reads like AI slop:
flat cards stacked in one column, labels that change register between
screens, search buried under the heading, the identity chip splayed in
the header band, a "Flows" view that mixes an editor with a monitor,
and a hand-rolled SVG graph that will never carry the richness the
workflow plane deserves. The site inherits the same gravity: good bones
(the architecture explorer is curated and honest) but inconsistent
chrome around them.

This idea sets the design law, picks the tools (Svelte Flow for graphs,
CodeMirror 6 for YAML, an icon set, a category-based IA), and applies it
in two waves: console first, site second, with the skills learned in the
first feeding the second.

## 1. Design principles (the laws)

1. **Categories by capability, not features.** Every surface belongs to
   exactly one category; the navigation is the category map, not a
   feature dump.
2. **Chrome is cheap, content is expensive.** Header, nav, and footer
   get the minimum pixels that keep them usable; the content area gets
   everything else.
3. **One placement for one kind of thing.** Global search lives in the
   top heading level. Everything about *you* (identity, role, sign out,
   theme) lives top-right. Deep navigation gets breadcrumbs. Menus do
   top-level actions.
4. **YAML is the editor; the graph is the picture.** Graphs are
   read-only, always rendered from the document, and react live to
   what is typed. Editing happens in YAML or through the AI assistant —
   never by dragging boxes.
5. **AiDeck follows a relevance law** (below). Either AI is the way you
   work, or it is an assistant that acts *on the screen you are on*.
6. **Modern controls only.** A choice list longer than three items is
   an inline-search combobox, never a `<select>`. Help is inline and
   contextual, not a wall of prose.
7. **Clean, light by default, a real dark theme.** Icons everywhere
   that a icon beats a word; every icon, badge, and graph node styled
   for *both* themes from the same tokens.
8. **Language is consistent.** One vocabulary across labels, badges,
   docs, and errors (the existing status-vocabulary design system is
   the seed). No screen explains a concept differently than its
   neighbor.

## 2. Information architecture

Navigation is a capability map; names are final nouns, not verbs:

| category | contains | today's views |
| --- | --- | --- |
| **Works** | the queue, the inspector, search results | Works, WorkInspector, search |
| **Flows** | *editorial*: definitions, editor, gallery, import/export | FlowsView (defs half), FlowEditor |
| **Monitor** | *observational*: runs, deliveries, audit, runtime, shadow report | FlowsView (instances half), Deliveries, Audit, Runtime |
| **Team & Access** | links/gateways, enrollment, roles/teams | Links, EnrollmentTokens |
| **Config** | policy, settings, schedules | ConfigView |

Rules that fall out:

- **Monitor and editorial never share a screen.** "Runs of this
  definition" is a link from an editor card into Monitor filtered by
  `def=`; the instance checklist does not live inside the editor's
  scroll.
- Routes change to mirror the map (`#/works`, `#/flows/<id>`,
  `#/monitor/runs?def=`, `#/monitor/audit`), keeping old hashes as
  redirects so shared links survive. Every existing `data-testid` is
  kept or aliased (reskin-guard rule).
- The left rail becomes category icons with labels (collapsible to a
  true icon rail on narrow screens); view-level tabs sit inside the
  content area, not beside it.

## 3. The AiDeck placement law

- **Decision rule:** if the AI is the *only* means of an interaction,
  it owns the screen (ChatGPT-style center). Today nothing in the
  console is only-AI, so **AiDeck is an assistant, not a destination.**
- Therefore: **AiDeck becomes a right-side drawer**, available on every
  screen, contextual to the route you are on:
  - it receives the screen's context (open definition, filtered
    instance, work under inspection) so "what's stuck here?" means what
    it says;
  - proposals act *on the current screen* (patch the YAML you are
    editing, apply a filter, fill a form) instead of describing an
    action you must go perform;
  - the standalone chat view survives only as the drawer maximized —
    one implementation.
- The same law governs anywhere AI shows up (e.g. flow authoring: AI
  drafts YAML into the editor, it does not own a tab).

## 4. Graphs: Svelte Flow, one component, every diagram

Replace the hand-rolled inline-SVG graph with
**`@xyflow/svelte`** (MIT, Svelte 5-native since 1.0, ~323K weekly
installs). Free-tier facts that matter:

- The whole library is MIT — nodes, edges, custom node/edge components,
  viewport, minimap, background, `fitView`, `toObject`,
  subflow-style grouping (parent nodes with `extent: 'parent'`) are all
  free. "Pro" is templates, helpers, and a paid license that removes the
  small attribution link. **Decision: keep the attribution** (an OSS
  project pointing at an OSS library is honest); revisit only if it
  reads badly at our sizes.
- Subflows: group a `fan-out`/`invoke` expansion as a parent node
  ("pane") that child instances render inside — the composition story
  (parent awaiting a child, the child's legs) finally gets a shape.

Usage sites:

1. **Flow editor** — read-only graph, top-down (`nodesep`/rank
   direction vertical per the requirement), always paired with the YAML
   pane; keystroke → parse → nodes/edges update immediately (debounced
   ~200 ms, parse errors surface as a badge, never a blanked graph).
   Step nodes carry kind icon + status tone; edges show branch labels
   (`completed`/`timeout`/`failed`) only when a node has >1 out-edge.
   Clicking a node reveals a side card (step payload + "jump to YAML
   line").
2. **Monitor: run view** — same graph, wearing the run: per-node
   leg history, current park, dashed future legs; clicking a node
   opens the leg's recorded output (today's `graph-inspect`, upgraded).
   Live children of an `invoke` render as subflow children of the
   invoke node.
3. **Site architecture page** — the explorer becomes the same Svelte
   Flow machinery (see §8), ending the two-implementation split.

**Skill to write first** (`svelte-flow-ofac`, managed skill): the
catalog of what the free tier actually contains (vs. Pro), Svelte 5
runes idioms that work with it (store bridge vs. `flow` bindings,
`nodeTypes` as `svelte` prop components), layout recipe (dagre or
elkjs layered, rankdir TB, port ordering so edges don't cross labels),
theme-token hookup (CSS vars from our `@theme`, `colorMode` switch),
subflow grouping law, minimap/background defaults, `onlyRenderVisibleElements` on large graphs,
attribution handling, and the "read-only unless explicitly editable"
default. The site work consumes the same skill.

## 5. YAML-first editing (the form builder goes)

- **Delete the form-first editor face.** The palette form is a
  maintenance floor that can never express the schema's richness. YAML
  is the authoring surface; the graph is its picture; AI drafts into
  it.
- **Editor: CodeMirror 6** (MIT, tree-shakeable, ~the right size) with:
  - a **JSON-Schema-driven** lint + completion extension
    (`@codemirror/lang-yaml` + `@leanra/yaml`/`yaml-language-server`
    worker, or the browser build of it): inline contextual help —
    property names, enum values (`event` types from
    `GET /api/admin/workflows/registry`, commands from `KnownCommands`,
    capability ids from live manifests, step ids from the document
    itself), and inline squiggles for schema violations *before* save —
    this is the SELECT-only law expressed as typing assistance;
  - save bar that states the act ("store as v4 · reason: …") +
    the existing diff-before-save block;
  - the existing `POST /definitions/validate` stays the authority; the
    editor's squiggles are advisory, the 422 is law.
- Clone/new/posture toggles stay as buttons (they're actions, not form
  fields). Version history list moves to Monitor's definition detail.
- `protocol/json-schema/workflow-definition.v1.json` is the single
  source for completion, lint, and the graph — served to the browser
  (`/api/admin/workflows/schema`, or bundled), never re-encoded by hand
  in UI code.

## 6. Shell: header, search, user, breadcrumbs

- **Header (one row, ≤48 px):** wordmark "Open Facilitator" +
  category-agnostic global search (⌘K, top heading level, results
  grouped by category) + right cluster: user chip (name, role,
  team-scope), theme toggle, sign out. The identity chip leaves the
  nav band; the band loses the wordmark (it's in the header now).
- **Breadcrumbs** for every deep route (`Monitor / Runs /
  nudge-escalation-copy@1`), with each crumb a real link (route-as-state
  already guarantees this works).
- **Health strip** demotes from a card to a thin status bar inside
  Monitor (it is monitoring data), or collapses to header dots with
  hover detail.
- **Density:** card chrome only where grouping needs it; tables get
  sticky headers, row hover, keyboard nav; no view may need the user to
  scroll past chrome to reach data.

## 7. Visual system

- **Icons:** `lucide-svelte` (MIT, runes-safe) — stroke icons, 1.5 px
  weight, `currentColor`, so both themes inherit them for free.
  Category icons, step-kind icons (they *are* the graph node
  vocabulary), action icons with tooltips (aria-labelled, testids
  preserved).
- **Tokens:** extend the existing `@theme` system: two surfaces
  (canvas, raised), one accent, semantic tones already standardized
  (`TONE_CLASS`); graph CSS consumes the same vars (Svelte Flow
  supports `colorMode`, we bind it to the theme store). Dark theme
  becomes a first-class pass: every screenshot, story, and e2e runs
  both modes.
- **Type:** one scale (12/14/16/20/24), two weights per role; drop the
  mixed `font-medium/semibold/bold` drift.
- **Empty/loading/error states:** one component each, used everywhere;
  skeletons instead of spinners; errors speak the Problem code
  (`version-conflict`) plus a human sentence plus the action.

## 8. The site, second wave

- **Architecture page → Svelte Flow.** Rebuild the explorer on the same
  library (it already has the node/edge/tooltips data model —
  `architecture.mjs` maps to Svelte Flow nodes nearly 1:1):
  - **semantic zoom:** far → the five zones as labeled blocks;
    mid → nodes with titles; near → node detail (bullets, docs link).
    Zoom bands are explicit, not CSS luck.
  - **fullscreen mode** (button + `F`), deep-linkable focus
    (`?focus=coord`), minimap at mid zoom.
  - **interactions:** click a node → side sheet (what/bullets/docs/
    related nodes); edges animate on hover of either endpoint;
    **data direction is visible by default** (arrowheads + optional
    dashed "packet" animation for the scenario playback, replacing the
    today's separate scenario strip with a play-mode overlay).
  - scenario walkthroughs ride `setCenter` + edge highlighting —
    the existing `scenarios` data drives it.
  - SSR note: the build renders a static SVG snapshot (current
    `arch-render` precedent) and hydrates to interactive, so the site
    stays functional without JS.
- **Flows documentation:** rewrite `/workflows` as the real manual —
  concept, the trigger kinds, the palette (per-step pages generated
  from the schema `description` fields so docs can't drift), composition
  (parent/child, budgets, hand-off), lifecycle state machine diagram
  (same Svelte Flow machinery, static export), and a cookbook
  ("nudge ladder", "review fork") each with the full YAML + the graph.
- **Rest of the site:** the console's visual system applies here too —
  same icons, tokens, header pattern (search belongs on docs pages),
  consistent naming (audit the site copy for bare "Facilitator"), and
  light/dark parity (the site currently has a dark stylesheet; make it
  the same token source as the console).
- Screenshots are re-shot from the overhauled console (the
  `OFAC_E2E_SHOTS` hook already exists; it gains a themed/viewport
  matrix).

## 9. Wave plan (for the eventual plan doc)

1. **Foundations:** `svelte-flow-ofac` + `ofac-ux-system` skills;
   token/typography/icon decisions locked; naming sweep (console +
   site copy) — cheap, mechanical, do first.
2. **Shell:** header/search/user-cluster/breadcrumbs/categories; AiDeck
   becomes the drawer (move `ChatView` wholesale, keep its tests).
3. **Flows split:** editor (YAML/CodeMirror + live graph, no form) vs.
   Monitor (runs, log, graph-wearing-run, actions); routes + redirects.
4. **Site:** architecture page on Svelte Flow with semantic zoom +
   fullscreen; flows manual; visual parity pass; re-shot screenshots.

Each wave gates the same way as everything else here: component tests,
e2e legs (both themes for the new surfaces), docs-check, honest
screenshots.

## 10. Open questions

- Svelte Flow attribution: keep (recommended) vs. Pro license — decide
  once it's visible at real sizes.
- Layout engine for the layered TB graph: dagre (small) vs. elkjs
  (better edges, heavier) — flow graphs are ≤20 nodes, dagre probably
  suffices; the skill should measure both.
- YAML LSP in the browser: the official worker bundle vs.
  `yaml-language-server` subset hand-wired to CodeMirror with our
  schema — prototype in wave 3, timebox, fall back to custom completion
  from the schema's enums + step-id harvest.
- Does Monitor keep any per-view auto-refresh, or one global "live"
  toggle (the 3 s poll currently baked into FlowsView)?

## Research anchors (verified 2026-11-14)

- Svelte Flow: MIT, v1.x, Svelte 5-era; free library vs. Pro
  distinction and attribution policy: svelteflow.dev (+ /remove-attribution);
  npm `@xyflow/svelte` (xyflow/xyflow monorepo). Subflow-style grouping =
  parent nodes + `extent`, demonstrated in their examples.
- CodeMirror 6: MIT, modular (`@codemirror/lang-yaml`); YAML language
  mode with JSON-Schema completion exists via yaml-language-server
  (browser worker) — needs a small spike.
- Icons: `lucide-svelte` (MIT; Svelte 5 runes compatibility resolved
  upstream, lucide#2312 closed).
- Current violations to fix by name: `App.svelte:196` bare
  "Facilitator" wordmark; search below the heading band
  (`search-go` area); identity chip in the nav band
  (`identity-chip`); FlowsView mixing defs+instances; `FlowGraph.svelte`
  hand-rolled SVG; `select`s over 3 items in FlowEditor (event types,
  commands, callable defs — all unbounded lists today).
- Prior art to inherit: `operator-console-next.md` (the functional gaps,
  largely shipped — this idea supersedes its visual assumptions),
  the TONE_CLASS/status-vocabulary system from the reskin, and the
  site's curated-coordinate architecture explorer (data model reused).
