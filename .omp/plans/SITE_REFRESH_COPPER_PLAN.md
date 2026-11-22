# Plan: bring `site/` current — content vs. real functionality, three new `docs/` pages, full Refined-Copper UI parity

## Context

The public site (`site/`) was last content-synced at commit `10fae7d`; since then the console gained its final copper theming, nav renames ("Works" → **Activities**, "AiDeck" → **Assistant**), the `#/monitor/*` route family, config history/rollback, enrollment tokens, and the full AiDeck/chat plane. The repo's design language also moved to **Refined Copper** ("Ink on Paper", `.omp/skills/design-language/`) — the console (`src/facilitator/ui/src/app.css`) implements it, the site still ships the **old gold-signal palette** (`#e6bf34`/`#ab8612`/`#faf2d6`, dark ground `#2b2e33`). The site's four console screenshots (`site/src/assets/console-{explorer,work,editor,instance}.png` + `-dark` twins) are gold-era and no longer match the product; `.omp/evidence/` holds 44 fresh copper screenshots (captured 2026-11-19 by `tools/capture-evidence.mjs`, desktop/mobile × light/dark) that are the source of truth for what the product looks like today.

End state: site copy matches implemented functionality; `docs/` gains the three missing reference pages (workflows, console, observability) registered on the site; the site's entire visual surface (CSS tokens, inline hexes, arch explorer, mermaid, hero animation, screenshots) is at parity with the copper spec; the four stale screenshots are deleted.

User decisions (already made): **curated** screenshot set (~8 desktop pairs + 1 mobile drawer pair, old 4 deleted) · **three** new docs pages · **full** theming parity (mirror console token block verbatim, retint all surfaces).

## Step 1 — Refined-Copper token parity in `site/src/styles.css`

Replace the `:root` and `[data-theme="dark"]` token blocks (lines ~10–67) with the console's copper values, verbatim from `src/facilitator/ui/src/app.css:60–155` (re-read that file before editing; it is the reference implementation — "two files, one palette" parity law stated in the site's own preamble):

- **Light** (`:root`): `--bg:#f1f3f5; --surface:#ffffff; --surface-alt:#e8ecf0; --surface-raise:#ffffff; --ink:#1a202c; --ink-2:#505a69; --ink-3:#7a8699; --line:#cfd6e0; --line-2:#dee3eb; --line-soft:#dee3eb; --accent:#c96a2d; --accent-hover:#b3581e; --accent-rule:#c96a2d; --accent-text:#ac531c; --accent-on:#ffffff; --tint-selected:#f8efe8; --border-primary:#e2b696; --info:#127377; --info-tint:#e5f3f4; --info-border:#92cad0; --success:#447a38; --success-tint:#eaf3e8; --success-border:#9fc497; --warning:#a87400; --warning-tint:#faf2de; --warning-border:#dec177; --danger:#ba3c3c; --danger-text:#ba3c3c; --danger-tint:#f9e8e8; --danger-border:#df9e9e; color-scheme: light;`
- **Dark** (`[data-theme="dark"]`): `--bg:#232831; --surface:#2b313b; --surface-alt:#323944; --surface-raise:#3b4350; --ink:#f3f4f6; --ink-2:#9ca3af; --ink-3:#6b7280; --line:#3f4754; --line-2:#343b47; --line-soft:#343b47; --accent:#c96a2d; --accent-hover:#e58a45; --accent-rule:#c96a2d; --accent-text:#e58a45; --accent-on:#ffffff; --tint-selected:#3d332d; --border-primary:#8a4820; --info:#168b90; --info-tint:#223538; --info-border:#175b5f; --success:#58894c; --success-tint:#273426; --success-border:#3b5832; --warning:#c48a00; --warning-tint:#373121; --warning-border:#7a5b0b; --danger:#c75454; --danger-text:#c75454; --danger-tint:#382626; --danger-border:#7d2e2e; color-scheme: dark;`
- Keep the existing legacy aliases (`--paper`, `--sunken`, `--card`, `--accent-dark`, `--accent-veil`, `--amber`, `--amber-veil`) — they now resolve to copper automatically. `--amber` alias the *warning* token (`var(--warning)`) since copper owns `--accent`.
- Code blocks stay a fixed dark instrument surface in both themes (existing precedent, styles.css comment): retarget `--code-bg: #232831; --code-ink: #f3f4f6;` and add `--code-line: #3f4754` if the token block uses a border.
- Add spec rules the site currently lacks (from `.omp/skills/design-language/references/`):
  - `:where(button, a, input, select, textarea):focus-visible { outline: 2px solid var(--accent-rule); outline-offset: 2px; }` (mirrors app.css).
  - `@media (prefers-reduced-motion: reduce)` block collapsing all transition/animation durations to 0ms.
  - 44px minimum height on `.topnav a`, `.nav-toggle`, `.btn`, `.topbar #theme-toggle` (touch law).
  - `--radius-control: 4px; --radius-overlay: 6px;` tokens; keep `--r: 4px` referencing `--radius-control`.
- Sweep the rest of styles.css for stray old-gold hexes outside the token block (the file is 1525 lines; the only ones present outside tokens are inside the hard-coded "dark slate" explorer block, handled in Step 3) and the `--dfe9f5/#9fb2c9/#7ea2c9` integration-filter chips near the bottom → retarget to `--info-tint`/`--info-border`/`--info` so they follow the theme.

## Step 2 — Screenshots: delete stale, ship curated copper set

Old files to delete (8): `site/src/assets/console-{explorer,work,editor,instance}.png` and their `-dark.png` twins.

Copy these from `.omp/evidence/` into `site/src/assets/` under stable role names (light + dark twin each). The mapping table below is the re-sync contract — document it as a comment in `site/build.mjs` beside the asset-copy block:

| Site asset name (light + `-dark`) | Source evidence frame | Placement |
|---|---|---|
| `console-activities` | `desktop-{light,dark}-02-shell-home` | landing console section (replaces `console-explorer` in `index.mjs` `consoleHtml()`) and top of `site/src/pages/console.md` |
| `console-search` | `desktop-{light,dark}-39-search-results` | console.md "Search and Work inspection" |
| `console-work-inspector` | `desktop-{light,dark}-17-work-inspector` | console.md, same section |
| `console-audit` | `desktop-{light,dark}-10-monitor-audit` | console.md Audit bullet area |
| `console-config` | `desktop-{light,dark}-24-config-resource-edit` | console.md Config bullet area (shows history/versioned edit) |
| `console-assistant` | `desktop-{light,dark}-27-aideck-answer` | console.md Assistant section (new, Step 4) |
| `console-flow-editor` | `desktop-{light,dark}-07-flow-editor-new` | `site/src/pages/workflows.md` (replaces `console-editor`) |
| `console-run-detail` | `desktop-{light,dark}-20-monitor-run-detail` | workflows.md (replaces `console-instance`) |
| `console-mobile-drawer` | `mobile-{light,dark}-30-mobile-nav-drawer` | console.md navigation section |

All references use the light filename (`../assets/<name>.png`); the build's dark-twin mechanism (Step 5) pairs them.

## Step 3 — Retint every site-drawn graphic

- **`site/src/pages/index.mjs`** line ~255 (`RELAY_JS.show()`): inline `"#e6bf34"`, `"#ab8612"`, `"#767d85"` → `"#e58a45"` (active step), `"#c96a2d"` (passed step), `"#6b7280"` (future step) — dark-ground copper values, since the relay scene sits on the hero ground in both themes; alternatively read `getComputedStyle` vars — hexes are acceptable and match the existing style.
- **`site/src/pages/architecture.mjs`** legend lines 29–32: replace inline hexes with classes (`.al-key` variants styled in styles.css using `--accent`/`--surface`/`--tint-selected`/`--ink-3`) so the legend re-tints with the theme; reword "Gold outline" → **"Copper outline"** (line 30) and the dotted "The link" swatch to `var(--accent)`.
- **`site/src/arch-render.mjs`** (SSR inline SVG): zone fills/strokes lines 12–15 (`#e6bf34` + rgba fills → copper `#c96a2d` borders with **opaque** fills: light `#f8efe8` / dark `#3d332d` — the render already picks a dark/light branch; check how the `#33373d` node fill at 199/279 is selected and mirror that for fills); edge styles 104–110 (`#e6bf34` → `#c96a2d`, `#f0d27a` link-dotted → `#e58a45`, greys → `#505a69`/`#9ca3af`); markers 210–212; node fill `#33373d` → `#2b313b`.
- **`site/src/styles.css`** explorer section (~lines 594–646, "hard-coded slate block") → copper slate: ground `#232831`, surfaces `#2b313b`/`#323944`/`#3b4350`, lines `#3f4754`/`#5a616a`, accent `#c96a2d`/`#e58a45`, selected tint `#3d332d`.
- **`site/src/arch-explorer/ExplorerInner.svelte`**: every old-hex in the scoped styles (~lines 208–465: `#e6bf34`, `#4d4a3a`, `#57523b`, `#2b2e33`, `#33373d`, `#464c54`, `#565d66`, `#99a1ab`, `#d6dae0`, `#ab8612`, `#1b1e22`) → the copper equivalents (`#c96a2d`, `#232831`, `#2b313b`, `#3b4350`, `#3f4754`, `#9ca3af`, `#f3f4f6`, `#e58a45`, `#3d332d`). The explorer is a deliberately-dark surface in both themes — keep it dark-only, just on the new ground. Also `from-model.js` if it carries palette data.
- **Mermaid — one copper light palette in three places:**
  - `site/src/js/app.js` `MERMAID_THEME` (lines 261–279): `primaryColor:"#f8efe8", primaryBorderColor:"#c96a2d", primaryTextColor:"#1a202c", lineColor:"#505a69", secondaryColor:"#e5f3f4", tertiaryColor:"#f1f3f5", edgeLabelBackground:"#ffffff", clusterBkg:"#ffffff", clusterBorder:"#cfd6e0", actorBkg:"#f8efe8", actorBorder:"#c96a2d", activationBkgColor:"#f8efe8", sequenceNumberColor:"#ffffff", labelBoxBkgColor:"#f8efe8", labelBoxBorderColor:"#c96a2d"`.
  - `site/mermaid-ssr.mjs` (lines 39–45): same value set (it currently ships an unrelated light-blue palette — this unifies SSR and client rendering).
  - Every `%%{init: …}%%` fence in markdown — `docs/README.md:31`, `docs/overview.md:96,148`, `docs/protocol.md` (if it has a fence — grep confirms), `docs/security.md:242`, `docs/advanced.md:57`, `docs/getting-started.md:77`, `docs/getting-started-agents.md:136`, `docs/release-setup.md:17`, `site/src/pages/{what,integrations,artifacts,extend,workflows}.md`, `site/src/guides/{human,agent}.md` — replace the old-gold `themeVariables` (`primaryColor #f7f9fb`, `primaryBorderColor #ab8612`, `secondaryColor #faf2d6`) with the copper set above. `grep -rln "ab8612" docs site/src` is the complete checklist; after the change it must return nothing.

## Step 4 — Content updates so the site matches implemented functionality

- **`site/src/config.mjs`**:
  - Console page entry body (line ~215): `search, links, deliveries, config, AiDeck` → `search, activities, flows, deliveries, config, Assistant`.
  - Docs manifest `docs` array, "Reference" group: add three entries (see Step 5 for exact objects).
- **`site/src/pages/console.md`** — rewrite to match the console as shipped:
  - Nav surfaces section already lists the nine labels correctly (Activities, Flows, Monitor, Deliveries, Audit, Runtime, Team, Config, Assistant) — keep, but correct details against the evidence frames: Audit lives at `#/monitor/audit`, Deliveries at `#/monitor/deliveries`, Runtime at `#/monitor/runtime`, Monitor runs at `#/monitor/runs`; mention per-step retry/skip and instance pause/resume on Flows/Monitor (endpoints exist: `/api/admin/workflows/instances/{id}/{pause,resume,retry-step,skip-step}`), config resource history + rollback form with mandatory change reason (evidence frames 24–26), Team enrollment tokens card (frame 15), read-only mode for non-operator identities (frames 90–91), definition history (frame 92).
  - Replace the two image blocks with `console-search` + `console-work-inspector` (new filenames); add `console-audit`, `console-config`, `console-mobile-drawer` (mobile nav) and a new **"The Assistant"** section with `console-assistant`, describing it as the AI drawer that answers from live state under the operator's own permissions, slash-verbs `/help /work /status /cancel /approve /deny /link` (`src/facilitator/Kernel/Ai/CommandRegistry.cs:49–55`), the three verbosity modes `terse | balanced | proactive` (`Kernel/Ai/ChatEndpoints.cs:77,436`), session history, page actions (the `ScreenContext`/`ScreenAction` plane, `src/facilitator/ui/src/lib/deck.svelte.ts:3–40,100–120`), and that proposed actions run only after human confirmation (`POST /api/chat/actions/{id}/confirm|cancel`).
  - Search paragraph: the section taxonomy is **six** sections — `works, projects, assignments, artifacts, memories, messages` (`Kernel/Admin/AdminReadStores.cs:34–42`). Current site copy lists only five and omits `messages`; fix.
  - URL-scheme paragraph: `#/works?state=blocked,review`, `#/work/<id>`, `#/audit?correlation=<id>` → `#/monitor/audit?correlation=<id>`.
- **`site/src/pages/workflows.md`**: swap the two screenshots to `console-flow-editor` and `console-run-detail`; check the prose against `docs/workflows.md` (Step 5) for the exact executable step kinds; the generated tail (lifecycle diagram, palette table from `protocol/json-schema/workflow-definition.v1.json`, gallery YAML) is schema/seeder-driven. Fix its one stale count: `site/build.mjs` `workflowsAppendix()` says "Two of the six shipped definitions" → **eight** (`src/facilitator/Kernel/Workflows/Gallery/` holds 8 JSON files).
- **`site/src/pages/index.mjs`** `consoleHtml()`: image `console-explorer.png` → `console-activities.png`, update the `alt` text (activities queue with state filters, top nav, search box) and width/height to match the new 1440×900 frame.
- **`site/src/config.mjs` `components`**: kernel card duty list — add the **workflow plane** and the **operator Assistant** (`/api/admin/workflows*`, `/api/chat`; `Kernel/Workflows/WorkflowEndpoints.cs`, `Kernel/Ai/ChatEndpoints.cs`); Dev Container Features card — name the Workspace Link SDK behind it (`@ofac/workspace-link`, `ofac-linkd` supervisor bin, `src/workspace-link/package.json:17–18`).
- **`site/src/pages/what.md` Work-lifecycle diagram (~L21–33)**: replace the invented states. Work wire states are `open | planning | active | review | completed | blocked | cancelled | failed` with the legal-edge table at `src/facilitator/Kernel/Work/WorkState.cs:36–50,66–79`; `routed/accepted/running` are **Assignment** states (`WorkState.cs:15–26`) — draw them on the assignment lane or drop them; `done` → `completed`; there is no `AwaitingApproval` work state (the gate parks as `blocked`/`review`). Update the matching tooltip keys in `site/src/tooltips.mjs:37–59` (`routed`, `inprogress`, `awaitingapproval`, `done`) to the corrected labels so tooltips still bind.
- **`site/src/pages/what.md` "does not proxy model traffic" (~L78)**: add one clarifying sentence — team model traffic is never proxied, but the kernel has an operator-configured decisions gateway (LiteLLM/OpenAI-compatible, `Kernel/Ai/LiteLlmDecisionsGateway.cs`, `llm` admin config resource) used by workflow `decide` steps and the Assistant.
- **`site/src/pages/integrations.md`**: Kafka (`L11`, `L80`) is a **documented stub** (`src/adapters/communication/kafka/` holds only a README; `src/adapters/AGENTS.md:17`), not a shippable "optional ingress leg" — retag it as planned/stub. `L67` "Deterministic (built into runtime)" → published package `@ofac/harness-deterministic`.
- **`site/src/pages/console.md` Deliveries bullet**: "poison queues, and retry attempts" → delivery ledger with state filter and **single-use dead-letter replay** carrying a mandatory change reason (`ui/src/components/DeliveriesView.svelte:22–57`, `AdminEndpoints.cs:77,307`).
- **`site/src/architecture.mjs` (~L251–261)**: PostgreSQL "the only mandatory dependency" contradicts the site's own three-dependency law (`benefits` card, `artifacts.md:15–20`) — align to PostgreSQL + OIDC + S3-compatible/local storage.
- **`site/src/guides/*.md`, other pages**: rename any "AiDeck" label to "Assistant" where it names the console surface (grep says only `config.mjs:215` carries it today); otherwise the copy survived the console changes — no other functional claims conflict with `src/facilitator/Kernel/Admin/AdminEndpoints.cs` routes or the published coordinates in `site/src/pages/artifacts.md` (verified: `@ofac/gateway`, `@ofac/protocol`, `@ofac/workspace-link`, `@ofac/provider-core`, `@ofac/provisioning-oci`, `@ofac/otel`, ghcr `.../kernel`, `.../gateway`, `.../node-runtime`, `.../features/*`).
- Update the screenshot provenance comment/README section in `site/README.md` (`src/assets/` line) to state assets are cut from `.omp/evidence/` via the mapping table.

## Step 5 — Three new `docs/` pages + registration

Author in the house style (`.omp/skills/ofac-writing`): purpose-first opening sentence, one diagram at a concept hinge using the copper mermaid `%%{init}%%` fence, closing "Where to go next" list. Every factual claim from the code, not the specs tree.

1. **`docs/workflows.md`** — "The workflow plane". Flow definitions as immutable versions keyed `(id, version)` with instances pinning their version, active/shadow/inactive toggles (`POST /api/admin/workflows/definitions/{id}/versions/{v}/toggle`); triggers: exactly one of `on.event` (CloudEvents type from the protocol registry), `on.schedule` (`interval:<n>m` | `daily:HH:mm` | `cron:<5 fields>`), callable `invoked` (`protocol/json-schema/workflow-definition.v1.json:102–190`); the step palette — **13 executable kinds** in `WorkflowDefinitionValidator.ExecutableKinds` (`WorkflowDefinitionValidator.cs:39–42`): `emit, wait, timer, approval, decide, select, fan-out, fork-work, work-create, sign-over, escalate, invoke, signal`; the schema palette has **15 kinds** (adds `join`, `memory`) that validate structurally but reject at enable time with `step-kind-not-supported` — state that law; instance lifecycle (running/waiting/shadowed/completed/failed/cancelled/paused) matching the build's generated lifecycle diagram; operator controls (`pause`/`resume`/`retry-step`/`skip-step`), shadow report (`GET /api/admin/workflows/shadow/report`), the **8** gallery definitions in `src/facilitator/Kernel/Workflows/Gallery/*.json` (agentic-research, assign-rules, ensure-capacity, nudge-escalation, pr-on-manifest, review-fork, router-ingress, team-home) seeded by `WorkflowGallerySeeder.cs`. Cross-link site `/#/workflows` (generated palette table) and `docs/protocol.md` type registry.
2. **`docs/console.md`** — "The operator console". Served at kernel origin, OIDC-gated; role gates from the config bindings (`admin.read`, `admin.works.read`, `admin.mutate`, `chat.use`), read-only presentation for identities without mutate rights; the full URL scheme (`#/works`, `#/work/<id>`, `#/flows`, `#/flows/<id>`, `#/monitor/{runs,deliveries,audit,runtime}`, `#/monitor/definitions/<id>`, `#/team`, `#/config`, `#/chat`); one short section per nav surface mirroring `src/facilitator/ui/src/App.svelte` NAV entries; config edit law (`If-Match` + `X-Change-Reason`, bounded history, rollback); enrollment tokens on Team; Assistant as a thin client of `/api/chat/*` (pointer to `docs/protocol.md` "Kernel ingress"). Embed the same curated light screenshots from `site/src/assets/` via repo-relative paths only if the site build can resolve them — otherwise link to the site console page and keep docs text-only (decide by checking how existing docs reference images; **current docs embed no images** — follow that: no images in `docs/console.md`).
3. **`docs/observability.md`** — "Telemetry and health". OTLP export surface from `src/facilitator/Kernel/KernelServiceCollection.cs:413–460`: `Otel:Endpoint` enables traces+metrics export, `OTEL_EXPORTER_OTLP_METRICS_ENDPOINT` overrides the metrics URL verbatim (full URL, no signal path appended), logs export requires `OTEL_LOGS_EXPORTER` **and** the kernel's `LogsExportEnabled` switch; resource attributes `service.instance.id` (replica id) and `ofac.deployment` (`Otel:Deployment`); instrumented sources: `KernelActivity` tracing source, ASP.NET Core + HttpClient instrumentation (health/dev paths excluded), `KernelMetrics` meter (enumerate the instruments from `src/facilitator/Kernel/Shared/Dispatch/KernelMetrics.cs`); pull side: `GET /api/admin/metrics`, `/api/admin/replicas`, sweeps (`POST /api/admin/sweeps/{name}` with mandatory `X-Change-Reason`), `/api/health`; gateway-side telemetry package `@ofac/otel`.
4. **Gap fixes inside existing pages** (no new files): `docs/overview.md` "How a piece of work flows" — add the router sign-over + delivery-scoped artifact-grant law (ownership is a column; sign-over transfer minted by the router, delivery-scoped grants minted at reply and consumed on receipt; `Migrations/20261116234551_AddRouterTeamSignOver.cs`, `WorkflowRunner.ExecuteSignOverAsync`, schema step `sign-over`); `docs/protocol.md` admin-surface row (`:165`) — append the flows endpoints family (`/api/admin/workflows*`: definitions, validate, toggle, registry, shadow report, instance pause/resume/retry-step/skip-step) and link the new `workflows.md`.
5. **Registration (load-bearing, else pages don't ship):**
   - `site/src/config.mjs` → `docs` array, "Reference" group, insert after the `/docs/security` entry:
     ```js
     { route: "/docs/workflows", src: "docs/workflows.md", title: "The workflow plane",
       body: "Flow definitions, versions, triggers, the step palette, instance lifecycle, shadow mode, operator controls." },
     { route: "/docs/console", src: "docs/console.md", title: "The operator console",
       body: "Nav surfaces, URL scheme, role gates and read-only mode, config law, the Assistant as an API client." },
     { route: "/docs/observability", src: "docs/observability.md", title: "Telemetry and health",
       body: "OTLP traces, metrics and logs, instrumented sources, admin metrics and sweeps, gateway telemetry." },
     ```
   - `docs/README.md`: add rows to "Pick your path" (authoring automations → workflows.md; operating the console → console.md; watching a live system → observability.md) and to "The documents" table; the README mermaid graph may gain one node per page — optional, keep node ids consistent with existing style.

## Step 6 — Build pipeline (`site/build.mjs`)

- `SHOT_NAMES` (line ~277): replace the four old names with the nine new role names from Step 2.
- **Fix the dark-twin mechanism** (bug: `media="(prefers-color-scheme: dark)"` in `withDarkShots()` ignores the site's own persisted toggle — a user who forces light on an OS-dark machine sees dark screenshots). Change `withDarkShots()` to emit, for each known shot name, a paired markup block
  `<img class="shot-img shot-light" src="X.png" …><img class="shot-img shot-dark" src="X-dark.png" …>`
  and add CSS in styles.css: `.shot-dark{display:none} [data-theme="dark"] .shot-light{display:none} [data-theme="dark"] .shot-dark{display:inline}` (inside the existing `.shot` frame styling; only mark shots that have a twin, same existsSync guard). The initial theme is already set pre-paint from `localStorage "ofac-ui-theme"` falling back to `prefers-color-scheme` (`site/src/pages/index.mjs:35–42`), so the CSS rule and the toggle can never disagree.
- Asset copy block (~lines 405–424): copy `styles.css`, `app.js`, `icon.svg`, then loop the nine light names + `-dark` twins (keep the existsSync guard); add the mapping-table comment from Step 2.
- `rm` the old PNG references: after deleting the 8 files, `grep -rn "console-explorer\|console-work\|console-editor\|console-instance" site/` must be empty.

## Critical files & anchors

- `site/src/styles.css` — token block lines ~10–67: the whole retint hinges on this one block.
- `src/facilitator/ui/src/app.css:60–155` — verbatim copper token source to mirror (do not retype from this plan; copy from file).
- `site/build.mjs` — `withDarkShots` (~277–306) + asset copy (~405–424): screenshot pipeline.
- `site/src/config.mjs` — nav/manifest: gates which docs pages and copy ship.
- `src/facilitator/ui/src/App.svelte:73–146` — NAV entries: the authoritative list the console docs/page copy must mirror.

## Verification

1. `grep -rn "e6bf34\|ab8612\|faf2d6\|8d6e0e\|f0d27a" site/src docs | grep -v node_modules` → **no matches** (old gold fully evicted; run after Steps 1/3/5).
2. `grep -rn "console-explorer\|console-work\.\|console-editor\|console-instance" site/` → no matches; `ls site/src/assets/*.png | wc -l` → 18 (nine pairs).
3. `npm --prefix site ci && npm --prefix site run build` → succeeds; `site/dist/` contains the 18 PNGs, `content/console.html` etc.; open `dist/index.html` via `npm --prefix site run serve` (http://localhost:8788):
   - landing renders copper header/buttons on cool grounds; toggle to dark → grounds flip to `#232831`, screenshots swap to dark twins **while the toggle stays on light** test: force light via the header toggle on an OS-dark profile (or devtools emulate) → screenshots stay light (proves Step 6 fix).
   - `/workflows`, `/console` pages show the new screenshots; `/docs/workflows`, `/docs/console`, `/docs/observability` resolve from the docs rail and search index (search "shadow" → workflow page hit).
   - every mermaid figure renders with copper borders/teal-or-copper accents in both themes.
4. `make docs-check` → passes (docs/ is skipped by that gate, but new cross-links must still resolve for the site build: the build's relative-link rewrite silently emits GitHub links for unregistered paths — visually confirm new docs pages' internal links land as `#/docs/...` not github.io).
5. `pnpm --prefix site exec node -e "import('./src/config.mjs').then(m=>console.log(m.docs.flatMap(g=>g.entries).filter(e=>['/docs/workflows','/docs/console','/docs/observability'].includes(e.route)).length))"` → `3`.
6. Spot-check claims: `console.md` URL `#/monitor/audit` exists in `App.svelte:116`; `docs/observability.md` env-var names match `KernelServiceCollection.cs:413–460` verbatim; step kinds in `docs/workflows.md` match the validator switch.

## Assumptions & contingencies

- Evidence frames dated 2026-11-19 are treated as current. If any chosen frame shows UI that differs from `src/facilitator/ui` on disk when implemented, prefer prose from code and drop/replace that frame with the nearest matching evidence frame (mapping table gives per-surface fallbacks: 02→04 works, 10→11 audit-deny, 24→16 config, 20→21 step log, 27-* aideck family).
- The `docs/` tree stays text+mermaid only (current convention: no images in docs); if the implementer wants visual proof there, link to the site console page instead of adding image paths that don't resolve on GitHub.
- `.omp/evidence/` regeneration (re-running `tools/capture-evidence.mjs`) is out of scope — requires the reference env (`./referencectl up slim`); the Nov-19 frames already reflect current functionality.
- If `mermaid-ssr.mjs`'s headless-Chromium leg is unavailable in the implementer's environment, the build falls back to client-side rendering — verify the fence palettes visually via the client path instead.
