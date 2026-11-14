# Console & site UX overhaul: a professional console and a site that shows it

## Context

`.omp/ideas/console-and-site-ux-overhaul.md` sets a design law for the operator
console (`src/facilitator/ui`, `@ofac/ui`) and the public site (`site/`), and
applies it in two waves: console first, site second. Today the console renders
a bare "Facilitator" wordmark (`App.svelte:196`), keeps search below the
heading band, splays the identity chip into the nav band, mixes definition
editing and run monitoring in one `FlowsView`, ships a form-first
`FlowEditor` (944 lines, 9 `<select>`s) with a hand-rolled inline-SVG
`FlowGraph.svelte`, and mounts `ChatView` (AiDeck) as a standalone
view. This plan implements the idea's four waves — foundations, shell,
Flows split, site — with these decisions already made:

- **Scope:** all four waves, gated per wave (user-approved).
- **Graphs:** `@xyflow/svelte` (MIT) with **dagre** layered layout, rankdir
  TB, one shared read-only canvas component; **keep the free-tier
  attribution link**.
- **YAML editing:** **CodeMirror 6 + a hand-rolled schema-driven
  completion/lint extension** (no yaml-language-server worker — the
  idea's own fallback is chosen up front, so no spike).
- **Monitor refresh:** **one global "live" toggle** in the Monitor header
  (3 s poll, on by default when anything runs/waits); per-view polls die.
- **AiDeck:** becomes a right-side drawer on every screen (assistant law).
- **Existing `data-testid`s are kept or renamed in the same commit as their
  Playwright assertions** — the only consumers are
  `tests/Facilitator.E2E/*.cs` and the vitest component tests; both are
  in-repo and updated together. The rename set is enumerated below; every
  other testid stays byte-for-byte.

Verified anchors (read this session): routes are hand-rolled hash routing in
`src/facilitator/ui/src/lib/route.svelte.ts` (VIEWS = works, audit, links,
deliveries, config, chat, runtime, flows + id-views work/, chat/, flow/,
flowdef/, hit/); nav band is `VIEW_BUTTONS` (`App.svelte:40-57`) rendering
`view-<key>` buttons; registry endpoint `GET /api/admin/workflows/registry`
returns `{eventTypes, commands, capabilities, callable, teams}`
(`WorkflowEndpoints.cs:32-62`); validator schemas are embedded resources of
the `Ofac.Protocol` assembly (`protocol/dotnet/Ofac.Protocol/Ofac.Protocol.csproj:20`,
`LogicalName="%(Filename)%(Extension)"`); site is a standalone npm project
(`site/package.json`, NOT a pnpm workspace member) built by `node
build.mjs`, with a dependency-free vanilla `site/src/js/app.js` SPA runtime,
SSR'd static SVG explorer from `site/src/arch-render.mjs`
(`renderArchitecture()` returns `{svg, payload, scenarios}`), explorer data in
`site/src/architecture.mjs` (`canvas`, 7 `zones`, 24 `nodes`
`{id,zone,x,y,w,h,t,sub,what,bullets,docs,artifact?,ghost?}`, 23 `edges`
`{id,from,to,fs,ts,kind,label?,lpos?,frac?,tfrac?,via?}`, 3 `scenarios`
`{id,title,intro,steps:[{caption,nodes,edges}]}`).

Gates (all exist today): `pnpm -C src/facilitator/ui
{typecheck,test,build}`; `make admin-e2e` (12 Playwright .NET facts, shared
Testcontainers fixture); `make docs-check`; `make protocol` (openapi
`protocol/openapi/admin-v1.yaml` + codegen drift); site CI:
`npm ci --prefix site && npm run --prefix site build`
(`.github/workflows/site.yml`).

## Wave 1 — Foundations: deps, tokens, icons, naming sweep, skills

No behavior change; everything later waves need is locked here.

1. **New pnpm catalog entries** in `pnpm-workspace.yaml` `catalog:` (the
   catalog is the single version source; `@ofac/ui/package.json` references
   `catalog:`): `"@xyflow/svelte": "^1.18.0"`, `"lucide-svelte": "^0.545.0"`,
   `"dagre": "^0.8.5"`, `"@types/dagre": "^0.7.52"`, `"codemirror": "^6.0.2"`
   (meta-package: state/view/commands/autocomplete/lint),
   `"@codemirror/lang-yaml": "^6.1.2"`, `"yaml": already in catalog`
   (reuse for browser-side YAML parse). Add the `catalog:` refs to
   `src/facilitator/ui/package.json` **dependencies**. Verify resolved
   versions against npm at edit time (`npm view @xyflow/svelte version`);
   if a caret range has no release, use the newest that exists. Then
   `pnpm install` at repo root.
2. **Token/typography pass** in `src/facilitator/ui/src/app.css`: keep the
   existing `@theme` block and light/dark `[data-theme]` blocks as the single
   token source; add explicit two-surface names if missing (`--color-canvas`,
   `--color-surface` already exist — do not duplicate); fix the type drift by
   adopting one scale: replace ad-hoc `text-[10px]`/`font-bold` usages that
   appear in `App.svelte`, `ChatView.svelte`, `FlowEditor.svelte` with the
   12/14/16/20/24 scale classes (`text-xs sm:text-sm md:text-base xl:text-xl
   2xl:text-2xl`) and two weights (`font-medium`, `font-semibold` for
   headings only). No new CSS variables beyond what waves 2–4 need.
3. **Icon baseline:** icons come from `lucide-svelte`, stroke 1.5 via
   `strokeWidth={1.5}`, `currentColor`. Add a one-line comment at the top of
   `app.css` stating the rule (no new icon system, no emoji icons).
4. **Naming sweep (mechanical, do first):** bare "Facilitator" as product
   name → "Open Facilitator". Console: `App.svelte:196`
   `<h1 class="font-medium">Facilitator</h1>` becomes
   `Open Facilitator` (the wordmark itself moves in wave 2 — fix the string
   now). Then audit and fix every user-visible bare "Facilitator" in
   `src/facilitator/ui/src/**` and `site/src/**` copy (e.g.
   `site/src/architecture.mjs:26` zone title "The Facilitator kernel" stays —
   it is prose-modifier usage, not the bare product name; flag any *bare*
   occurrence). Wire identifiers (`Facilitator:Workflows:*` config keys,
   `org.facilitator.*` event names, `ConnectionStrings__Facilitator`) are
   protocol/config identifiers — never rename.
5. **Skills** (via the `learn`/`manage_skill` tool, isolated managed-skills
   store, not repo files): `svelte-flow-ofac` (free-tier vs Pro facts,
   Svelte 5 runes idioms — `nodeTypes` as svelte prop components,
   `$derived` store bridge; dagre rankdir-TB recipe with port ordering;
   `colorMode` bound to our theme store; read-only default;
   `onlyRenderVisibleElements` for >50 nodes; attribution kept) and
   `ofac-ux-system` (category IA map, placement laws, token/type/icon rules,
   testid discipline). Write them from the docs while wiring wave 2/3 code,
   not speculatively.

Gate: `pnpm -C src/facilitator/ui typecheck && pnpm -C src/facilitator/ui
test && pnpm -C src/facilitator/ui build` green; `make docs-check` green.

## Wave 2 — Shell: header, search, user cluster, breadcrumbs, category rail, AiDeck drawer

All in `src/facilitator/ui/src`. This wave changes routes and chrome but not
views' internals.

1. **Route categories + redirects** in `src/lib/route.svelte.ts`. New
   grammar (extends the existing comment block at the top):
   - `#/works`, `#/work/<id>` (unchanged) — **Works**
   - `#/flows`, `#/flows/<id|new>` — **Flows** (editorial; replaces
     `#/flowdef/<id>`)
   - `#/monitor/runs[?def=]`, `#/monitor/runs/<instanceId>`,
     `#/monitor/deliveries`, `#/monitor/audit?…`, `#/monitor/runtime`,
     `#/monitor/definitions/<id>` — **Monitor** (observational)
   - `#/team` — **Team & Access** (current LinksView +
     EnrollmentTokensCard; `#/links` redirects here)
   - `#/config` — **Config**
   - `#/chat/<sessionId>` stays parseable but renders the drawer opened
     (see 4), no standalone view.
   Implement in `parse()`: a `LEGACY: Record<string, string>` map —
   `flowdef/X → flows/X`, `flow/X → monitor/runs/X`, `audit →
   monitor/audit`, `deliveries → monitor/deliveries`, `runtime →
   monitor/runtime`, `links → team` — applied before the existing branch
   logic, returning the **new** route so `href(parse(h)) === h` holds for
   old hashes too (canonical output is the new form). Extend `VIEWS`,
   `PARAM_ORDER` (`monitor/runs`: `["def"]`, `monitor/audit`: the old audit
   order), and `href()` for the two-segment monitor views. Add
   `RouteView` members: `monitor-runs`, `monitor-run`, `monitor-deliveries`,
   `monitor-audit`, `monitor-runtime`, `monitor-definition`, `team`; drop
   `flow`, `flowdef`, `audit`, `deliveries`, `runtime`, `links`, `chat` as
   parse targets (they exist only as legacy keys).
   Extend `src/lib/route.test.ts`: each legacy hash parses to the new route
   and round-trips; new hashes parse unchanged (this is the redirect proof —
   a shared old link lands on the right screen).
2. **Header (`App.svelte`, one row ≤48 px):** left = the existing inline
   mark + `<h1>Open Facilitator</h1>` + "operator console" pill; center =
   global search input moved up into the header (keep
   `data-testid="search-input"`, `data-testid="search-go"` and the
   `/`-to-focus + Esc behavior in `onKeydown`; add `⌘K`/`Ctrl-K` to the
   same handler as an alias for focus). Search results render under the
   header exactly as today (sections, `hits-<key>`). Right cluster (order):
   user chip (`data-testid="identity-chip"`, keeps
   `{displayName · roles}` text — `AdminInspectorTests` asserts it contains
   "alice"), theme toggle (`theme-toggle`), sign out (`logout`). Delete the
   old header/identity layout and the separate search rows; the nav band
   loses the wordmark and the chip.
3. **Category rail + breadcrumbs:** replace `VIEW_BUTTONS`/`<nav
   aria-label="views">` with a left rail (categories, lucide icons +
   labels; collapses to icon-only ≤720 px). Rail entries and their
   **preserved testids** — `view-works`, `view-flows`, `view-monitor`
   (new, points to `#/monitor/runs`), `view-team` (new),
   `view-config`, `view-deliveries`, `view-audit`, `view-runtime`
   (the last three now point at `#/monitor/*`), and `view-chat`
   (drawer toggle, see 4). Keep the `needsChatRole` filter on `view-chat`.
   **Renames (update `tests/Facilitator.E2E/AdminInspectorTests.cs`
   (`view-config`, `view-deliveries`, `view-links`→`view-team` or a rail
   sub-item click) and `ChatPlaneTests.cs` (`view-chat`) in the same
   commit).** Add `src/components/Breadcrumbs.svelte`: derived from
   `route.route` — one crumb per path segment, each an `<a href>` of the
   canonical `href()` of the truncated route (route-as-state guarantees it
   works); rendered at the top of the content column for every non-top
   route.
4. **AiDeck drawer:** new `src/components/AiDeck.svelte` — a fixed right
   drawer (full-width bottom sheet ≤720 px), opened/closed from the
   `view-chat` rail button, Esc, and a `deck` store
   (`src/lib/deck.svelte.ts`: `{ open: boolean, sessionId: string | null,
   context: () => ScreenContext }`). Move `ChatView.svelte` **wholesale**
   into the drawer body (keep its 8 vitest cases pointing at the same
   component; only its mounting changes) and keep every `chat-*` testid —
   `ChatPlaneTests` asserts `chat-input`, `chat-send`, `chat-md`,
   `chat-action-post` and the mobile viewport leg (390×844) now runs against
   the drawer-open state, so the drawer must be full-width there.
   `#/chat/<id>` sets `deck.open` + session (parse-level: route keeps a
   `chat` legacy redirect to `#/` with the drawer flag on `deck`). Context
   plumbing: views register a `ScreenContext` (route + the ids on screen)
   via `deck.setContext()`; the drawer's prompt prefix sends that context on
   the next message (`POST /api/chat/sessions/<id>/messages` body gains
   `context` only if the wire type already has a field for it — check
   `src/protocol` chat types first; otherwise serialize the context as a
   first-message system preamble line in the existing message text, no
   protocol change).
5. **HealthStrip demotion:** move `<HealthStrip />` out of the shell footer
   into a thin status bar rendered at the top of the Monitor column
   (`#/monitor/*` only). Keep `data-testid="health-strip"` and `deadletters`
   (asserted by `AdminInspectorTests`) — the test navigates via
   `view-deliveries`, so the bar must live on the shared monitor bar region
   that includes deliveries.
6. **Empty/loading/error trio:** new `EmptyState.svelte`,
   `Skeleton.svelte`, and extend `ProblemNote.svelte` (Problem code + human
   sentence + action) as the only three such components; adopt them in the
   views touched by later steps — do not sweep untouched views.

Gate: vitest (extend `route.test.ts` + a new `AiDeck.test.ts` mount proof:
`view-chat` toggles drawer, `chat-input` reachable), typecheck, build, then
`make admin-e2e` — with the enumerated testid/navigation edits to
`AdminInspectorTests.cs`, `ChatPlaneTests.cs` applied in the same commit.
Manual pass against the slim env: `./referencectl up slim` +
`pnpm --filter @ofac/ui dev -- --port 5173 --strictPort`, login
alice/alice, old hash `#/flowdef/nudge-escalation` lands on the editor.

## Wave 3 — Flows split: YAML editor + live graph vs. Monitor

1. **New endpoint `GET /api/admin/workflows/schema`** in
   `src/facilitator/Kernel/Workflows/WorkflowEndpoints.cs` (same group as
   `/registry`): reads embedded resources `workflow-definition.v1.json` and
   `common.v1.json` from the `Ofac.Protocol` assembly
   (`typeof(Ofac.Protocol.Validation.EnvelopeValidator).Assembly.GetManifestResourceStream(name)`
   — same loader the validator uses, so schemas can never drift from the
   422 authority), returns
   `{ definition: <JsonDocument>, common: <JsonDocument> }`
   (`Results.Ok` with both as `JsonNode`). Add the path to
   `protocol/openapi/admin-v1.yaml` (mirror the `/api/admin/workflows/registry`
   entry style) and run `make protocol` (codegen + swagger validate + drift
   gate). Kernel test in
   `tests/Facilitator.Tests/Workflows/WorkflowDefinitionTests.cs`: GET
   returns 200 and `definition["$defs"]["step"]` exists.
2. **Shared graph component** `src/components/FlowCanvas.svelte` replaces
   `FlowGraph.svelte` (delete it; its `data-testid="flow-graph"` moves to
   the canvas root — `FlowEditorConsoleTests` asserts `flow-graph`
   visible). Props mirror the old ones:
   `{ doc: Doc, log?: Leg[], selected?: string|null,
   instanceStatus?: string|null, currentStep?: string|null,
   onselect?: (stepId: string|null) => void }`,
   plus `{ subflows?: { parentId: string, child: { id, status }[] }[] }`
   for invoke children. Internals: graph extraction reuses the exact
   edge/kind law ported from `FlowGraph.svelte`'s `$derived` block (keep
   `KINDS`, trigger/end pseudo-nodes, branch labels only for >1 out-edge);
   layout via `dagre.graphlib.Graph` (`rankdir: "TB"`,
   `nodesep: 40`, `ranksep: 60`, node size = Svelte Flow measured, port
   ordering by edge label `completed`→`timeout`→`failed`); render
   `<SvelteFlow colorMode={theme.value === "dark" ? "dark" : "light"}
   nodesConnectable={false} elementsSelectable>` from `@xyflow/svelte`
   with one custom node component `StepNode.svelte`: lucide kind icon
   (one per `KINDS` member) + title + `TONE_CLASS` ring when `log` gives
   that step a leg tone; edges = smoothstep with labels only when
   multi-out. Run mode additions: per-node leg count badge, current park
   pulse, dashed future legs from the document's branch edges after
   `currentStep`. Subflows: child instances render as Svelte Flow nodes
   with `parentNode`/`extent: "parent"` under an invoke node pane. Keep the
   xyflow attribution link visible in a corner of the canvas chrome.
   Import `@xyflow/svelte/dist/style.css` in `main.ts`.
   `onlyRenderVisibleElements` on when node count > 50.
3. **FlowsView split.** `FlowsView.svelte` (204 lines) splits:
   - `FlowsView.svelte` keeps the **definitions table** only
     (`data-testid="flows-view"` kept; `new-flow` kept; add per-row links:
     "edit" → `#/flows/<id>`, "runs" → `#/monitor/runs?def=<id>`).
     The 3 s poll and the instances table leave.
   - New `MonitorView.svelte` owns the instances table
     (`data-testid="instance-list"` kept), a refresh button
     (**rename `flows-refresh` → `monitor-refresh`**, updating
     `FlowsConsoleTests.cs` and `FlowEditorConsoleTests.cs` in the same
     commit) and the **global live toggle**: `data-testid="live-toggle"`,
     default on, 3 s setTimeout re-poll exactly as the old
     `FlowsView` effect, gated by toggle AND "any running/waiting"
     (E2E proof depends on it: `FlowEditorConsoleTests` polls for a new
     instance row without clicking refresh between enable and
     `instance-list` appearance).
   - New definition-detail screen at `#/monitor/definitions/<id>`:
     version-history list (moved out of `FlowEditor`'s
     `config-history`-style block if any) + the filtered instance table.
     Reached from editor header ("runs", "history").
4. **FlowEditor becomes YAML-first.** In `FlowEditor.svelte` (944 lines):
   - Delete the form palette face (per-step `<select>`s, per-kind payload
     fields — the 9 selects, 5 textareas). The YAML face becomes the only
     editor. Keep as action buttons: clone, new, posture toggles
     (`editor-enable`/`editor-disable`), save bar, diff
     (`editor-diff`), notice (`editor-notice`).
   - **Editor widget** `src/lib/yaml-editor.ts`: CodeMirror 6
     (`codemirror` meta + `@codemirror/lang-yaml`) with a
     **schema-driven extension** built once from the wave-3.1 `/schema`
     response + the live `/registry` payload: completion source offering
     property names valid at the cursor's schema path (walk
     `definition.$defs` with the parsed doc tree from the catalog `yaml`
     package), enum values for `on.event` (registry `eventTypes`),
     `emit.command` (registry `commands`), `emit.capability`
     (registry `capabilities`), `invoke.workflow` (registry `callable`),
     and step ids harvested from the document for `on:`/branch targets;
     lint source reporting YAML parse errors (positions from the `yaml`
     package) + schema violations (unknown property/kind via the schema
     `additionalProperties`/`oneOf` step kinds) as **warnings** —
     `POST /definitions/validate` stays the save authority, the 422
     remains law. No yaml-language-server anywhere.
   - **Live graph:** keystroke → 200 ms debounce → parse →
     re-render `<FlowCanvas doc>` beside the editor; a parse failure keeps
     the last good graph and shows a `badge` with the parser message
     (`data-testid="graph-error"`); never a blanked graph. Node click →
     side card with the step's JSON payload + a "jump to YAML" action that
     moves the CodeMirror cursor/selection to the step's line range (the
     `yaml` package CST gives line positions).
   - **Preserved testids** (asserted by `FlowEditorConsoleTests`):
     `flow-editor`, `editor-close`, `editor-notice`, `editor-id`,
     `editor-title`, `editor-diff`, `editor-save`, `editor-enable`,
     `editor-yaml`, `yaml-validation`, `yaml-validate`, `yaml-save`,
     `flow-graph`. `editor-title` is asserted with `ToHaveValueAsync`
     today — change the title to a CodeMirror-hosted field **or** keep the
     one-line title input; decision: **keep a plain `<input
     data-testid="editor-title">` above the editor** (title edits are
     metadata, not YAML body), so that assertion survives unchanged.
     `editor-id`, `editor-team`, per-step testids (`step-*`, `emit-*`,
     `wait-timeout`, `invoke-*`, `add-*`, `trigger-*`) die with the form —
     no E2E uses them beyond `editor-id`.
   - YAML face test changes: the leg that does
     `GetByRole(Button, Name: "YAML")` then `editor-yaml`
     (`FlowEditorConsoleTests.InvokeStepEdit_RoundTripsThroughYaml`)
     now targets the main editor — drop the tab click, fill the CodeMirror
     via `page.GetByTestId("editor-yaml").Locator(".cm-content")`
     click + `keyboard.insertText`. Root the CodeMirror div with
     `data-testid="editor-yaml"`.
5. **FlowDetail (run view) re-homes:** `FlowDetail.svelte` moves under
   `#/monitor/runs/<id>` (its testids all kept: `flow-detail`,
   `flow-pause/resume/skip/retry/cancel`, `flow-notice`, `lineage`,
   `graph-inspect`, `step-log`); the graph is now `<FlowCanvas>` wearing
   the run (step 2), `graph-inspect` keeps showing the clicked leg's
   output; add a "jump to editor" link → `#/flows/<defId>`.
6. **Vitest:** split `FlowsView.test.ts` (6 cases) into `FlowsView.test.ts`
   (definitions half), `MonitorView.test.ts` (instances + live toggle:
   fake timers prove 3 s re-poll only when live && running),
   `FlowDetail.test.ts`; new `FlowCanvas.test.ts` (dagre layout assigns
   finite TB coordinates; a 2-step doc yields 3 nodes incl. trigger/end;
   log legs flip node tone). jsdom note: stub
   `@xyflow/svelte` viewport IO with a `vi.mock` of its container
   measurements (one mock helper in `src/lib/test-harness.ts`).

Gate per step order: kernel test + `make protocol` for 3.1;
vitest/typecheck/build after 3.2–3.6; then `make admin-e2e` (both flows
facts updated) — decisive; then the 5173 dev-loop pass: open
`#/flows/nudge-escalation`, type an invalid event type → inline warning,
graph still drawn; delete a step id reference → branch-target completion
offers remaining ids.

## Wave 4 — Site: Svelte Flow explorer, flows manual, visual parity, shots

Site work builds on the `svelte-flow-ofac` skill; site stays a standalone
npm project — **new devDeps in `site/package.json` only**:
`@xyflow/svelte`, `svelte` (^5, matching the console's line), `vite`,
`@sveltejs/vite-plugin-svelte`, `dagre`.

1. **Explorer bundle.** New `site/src/arch-explorer/` — a Svelte 5 app
   (`ArchExplorer.svelte` mounting `@xyflow/svelte`), built by
   `site/build.mjs` via a programmatic `vite build` (config
   `site/arch-explorer.vite.config.mjs`, entry
   `src/arch-explorer/main.ts`, outputs `dist/arch-explorer.js` +
   `arch-explorer.css`, IIFE so `app.js` stays dependency-free). Data
   adapter `site/src/arch-explorer/from-model.mjs` maps
   `architecture.mjs` 1:1: zone → parent node (`extent: "parent"`,
   dashed style for `dashed: true`/`ghost`), node → custom node component
   carrying `t/sub/what/bullets/docs/artifact`, edge → smoothstep edge by
   the same `kind`→stroke table as `arch-render.mjs` (port sides from
   `fs/ts`, waypoints from `via` become edge `path` overrides or extra
   control points).
2. **SSR + hydration law:** keep the existing static SVG snapshot pipeline
   (`renderArchitecture()` → `${svg}` inside `.arch-scroll`) as the
   no-JS face and as the Lighthouse/OG image. `app.js`'s
   `wireArchitecture()` gains: if `window.OfacArchExplorer` exists,
   replace the snapshot with the mount point and call
   `OfacArchExplorer.mount(el, { data: <arch-data payload>, focus })`.
   Deep-link `?focus=<nodeId>` (also accept `#focus=` parse) opens that
   node's sheet and centers it (`setCenter`).
3. **Interactions:** semantic zoom with **explicit bands** computed from
   the viewport scale, with one law from the user: **node labels are
   visible at every zoom level** — zoom adds detail, it never removes the
   label. `k < 0.45` → zones as labeled blocks + node title labels only
   (no chrome, no sub-lines); `0.45 ≤ k < 0.9` → full node cards with
   title + `sub`; `k ≥ 0.9` → node detail affordances (bullets open in the
   sheet, never crammed on-canvas). Ownership must be readable at a
   glance: every `own` class (`you` = central/self-hosted deployment,
   `infra`, `team` = team-owned, `external`) gets a distinct zone treatment
   — zone fill/border hue + a persistent ownership chip on the zone header
   (reuse the zone `tag` text), legend in the canvas chrome, and the same
   hue on node borders so a node carries its ownership even when its zone
   is scrolled off. Fullscreen
   button + `F` key. Click node → right side sheet (what/bullets/docs
   link/related nodes — related = neighbors from `edges`). Hover of a node
   highlights incident edges (arrowheads always on — data direction
   visible by default). Minimap rendered only in the mid band.
4. **Scenario playback overlay:** `arch-player` drives
   `steps: {caption, nodes, edges}` — highlight set + `setCenter` on the
   step's node centroid + animated dashed `packet` flow on the step's
   edges (class toggle + CSS animation from the site tokens, respecting
   `prefers-reduced-motion`). The old per-edge scenario strip is replaced
   by this play-mode overlay; keep the player controls' accessible
   labels/text the search index weighs.
   **Fidelity law (user correction):** before wiring the player, audit
   each of the 3 scenarios (`journey`, `identity`, `offline` in
   `architecture.mjs`) against the implementation and fix the data where
   the walk is wrong or thin: every hop must be a **real edge** in the
   `edges` list (no implied jumps between highlighted nodes), each step
   highlights the *complete* dependency chain for that beat (e.g. a step
   that lights the gateway must also light the link/hub edge it travels,
   a DB write lights `pg`, an artifact lights `s3`/`broker`), captions
   match what the kernel/gateway actually does (cross-check `docs/`), and
   no step highlights a node its edges do not reach. A node highlighted
   with zero incident highlighted edges is a bug. Add missing steps/edges
   to the data rather than dropping claims from the captions.
5. **Flows manual (`site/src/pages/workflows.md` rewrite + generated
   pages):** keep the current prose (triggers/palette/composition — it is
   already close to right); generate the per-step palette reference at
   build time from `protocol/json-schema/workflow-definition.v1.json`
   `$defs.step` — one section per payload kind, from the schema's own
   `description` fields (new `site/src/schema-ref.mjs`, no hand re-encode;
   the schema file is read via the repo root, precedent: `build.mjs`
   already reads repo docs). Lifecycle state-machine diagram: a small
   mermaid block (reuses the existing mermaid SSR). Cookbook entries
   "nudge ladder" and "review fork" each embed the **actual gallery
   YAML** — read `src/facilitator/Kernel/Workflows/Gallery/nudge-escalation.json`
   and `review-fork.json` at build time (`WorkflowGallerySeeder` embeds
   the same files), rendered as YAML + a static `<FlowCanvas>`-exported
   SVG snapshot generated in the build by the explorer module's
   server-side render path (fallback decision: if SSR-mounting the
   explorer is fiddly, emit the gallery docs as YAML + a link to
   `#/flows/<id>` in the live console, and keep the graph only for
   `/architecture`).
6. **Visual parity:** move the console's token values into
   `site/src/styles.css` `:root` (site keeps its own file — a shared npm
   package is overkill — but values match: same ink/line/accent/amber/veil
   hexes as `app.css`); site has **no dark theme today** (verified:
   `styles.css` has no `prefers-color-scheme` block) — add a
   `[data-theme="dark"]` block driven by an OS default + a header toggle
   that persists in `localStorage` (same key + default law as the console:
   light default, toggle + persist; the console's
   `theme.svelte.ts` is the reference behavior, mirrored not imported).
   Icons on the site become lucide (inline the two-three SVGs needed —
   site JS stays dependency-light outside the explorer bundle). Header
   gains search (site search already exists — move the search control into
   the header row). Naming audit from wave 1 already swept copy.
7. **Screenshot matrix:** extend the `OFAC_E2E_SHOTS` hook in
   `tests/Facilitator.E2E/FlowEditorConsoleTests.cs`: when
   `OFAC_E2E_SHOTS_DARK=1` is set, additionally shoot
   `console-editor-dark.png` / `console-instance-dark.png` after
   `page.EvaluateAsync` flipping `document.documentElement.dataset.theme =
   "dark"`; shoot `console-work.png` from `AdminInspectorTests` and
   `console-explorer.png` from `ChatPlaneTests` (or the existing
   producer, whichever already opens that screen) so all four site assets
   are produced by the e2e run:
   `OFAC_E2E_SHOTS=$PWD/site/src/assets make admin-e2e`.
   `site/build.mjs:317-321` already copies the four shots conditionally —
   extend the loop to the two `-dark` files and reference them
   side-by-side on the pages that use them.

Gate: `npm run --prefix site build` green + `npm run --prefix
site serve` → manual pass of `/architecture` (zoom bands, `?focus=apisrv`,
scenario play, fullscreen, dark toggle) and `/workflows`; snapshot
`dist/content/architecture.html` still contains the static `<svg
class="arch-svg"` (no-JS proof: load with JS disabled). Then `make
docs-check`, `pnpm lint` (site is outside the pnpm lint glob;
`.svelte/.css` are outside prettier's repo glob — do not format them).
Full ladder after all waves: `make build && make test && make admin-e2e
&& make docs-check` + site build.

## Critical files & anchors

- `src/facilitator/ui/src/App.svelte` — header/nav/search/chat mount all
  rewired here (`VIEW_BUTTONS:40`, wordmark `:196`, nav band `:310`).
- `src/facilitator/ui/src/lib/route.svelte.ts` — route grammar + the
  `LEGACY` redirect map; `href(parse(h))===h` invariant.
- `src/facilitator/ui/src/components/FlowEditor.svelte` — the form→YAML
  cutover; testids the E2E law protects.
- `src/facilitator/Kernel/Workflows/WorkflowEndpoints.cs` — new
  `/schema` endpoint next to `/registry:32`.
- `site/build.mjs` + `site/src/arch-render.mjs` — snapshot SSR,
  new explorer bundle emission, conditional shot copies (`:311-324`).

## Verification (end-to-end proofs)

1. **Legacy link survives:** with the built SPA on the 5173 dev loop,
   navigate to `#/flowdef/nudge-escalation` → editor renders, address bar
   shows `#/flows/nudge-escalation`; `#/audit?decision=allow` → audit view
   with the filter intact.
2. **Graph is live:** in the editor, append an invalid event type under
   `on: event:` → CodeMirror squiggle (warning) + schema 422 still the
   gate on save (remove a required field, save → `editor-notice` shows the
   validator code); graph never blanks; `graph-error` badge appears on a
   deliberately broken YAML, graph of the last good doc remains.
3. **Monitor live toggle:** with an instance running, `live-toggle` on →
   the run's row updates within ~4 s without interaction; toggle off →
   no further `instances` fetches (network panel / test fake-timer proof).
4. **AiDeck everywhere:** open any category, press the rail's
   `view-chat` → drawer with `chat-input`; a chat "replay dead letter"
   proposal still executes end-to-end (`ChatPlaneTests` green).
5. **Both themes:** `make admin-e2e` plus a manual dark pass: flip
   `theme-toggle` on `/flows` editor and `/monitor/runs/<id>` — graph,
   nodes, minimap, drawer all legible (xyflow `colorMode` follows the
   store).
6. **Site:** `npm run --prefix site build && npm run --prefix site serve`
   → `/architecture` works without JS (static SVG + text), with JS gets
   bands/fullscreen/`?focus=console`; `/workflows` palette section contains
   a step kind absent from the old hand-written table when the schema
   gains one (prove by diffing: `grep -c '### ' dist/content/workflows.html`
   equals the schema's payload-kind count).
7. **Shots honest:** `OFAC_E2E_SHOTS=$PWD/site/src/assets
   OFAC_E2E_SHOTS_DARK=1 make admin-e2e` regenerates all
   `site/src/assets/console-*.png` from the overhauled console (file mtimes
   newer than the build).

## Assumptions & contingencies

- **npm versions:** caret lines above were reasoned, not registry-fetched
  this session. If `pnpm install` cannot resolve one, take the newest
  published and note it in the catalog comment (repo law: comment dated
  verification).
- **`@xyflow/svelte` in jsdom:** if viewport APIs still break vitest
  beyond the planned mock, fall back to rendering
  `FlowCanvas.test.ts` assertions against the dagre layout + node-model
  derivation functions (exported from a pure `flow-layout.ts` module)
  rather than mocking deeper.
- **`#/chat` deep links:** chosen behavior is "redirect to `#/` with
  drawer open". If the drawer-store coupling proves awkward inside
  `parse()` (no component context at parse time), the accepted fallback is
  a `#/chat` entry that renders Works with `deck.open=true` set by a
  one-line effect in `App.svelte` keyed on the legacy route.
- **Schema endpoint shape:** if codegen from the extended
  `admin-v1.yaml` churns generated wire types more than expected, declare
  the endpoint with a generic JSON response in the openapi (object with
  two named properties) instead of typed schemas — validation authority is
  unchanged either way.
- **Site explorer SSR of gallery graphs:** the wave-4.5 fallback (YAML +
  link, graph only for `/architecture`) is pre-approved if Svelte
  server-rendering of Svelte Flow fights the build; do not spend more
  than a morning on it.
- Zone count is 7 (idea text says "five zones" for far zoom): render **all
  zones** at far zoom, labeled; no data change.
