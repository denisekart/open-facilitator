# Plan 21 — Admin surface, leg 2: embedded Svelte operator inspector + E2E
**Status:** ⬜ not-started (flips 🔵 at the replanning gate, then 🟡)
**Depends on:** 20
**Phase E leg 5 of 5.** Completes the operator surface (20 → 21) and Phase E.

## Objective
Ship the §17 embedded admin SPA: a Svelte 5 + Tailwind + shadcn/svelte
single-page inspector built into the Facilitator's `wwwroot` (one deployable),
bound exclusively to Plan 20's `/api/admin/*` surface. Optimized ruthlessly for
simplicity — one coherent inspector, not a page-count target. Universal search
→ contextual inspector (Work/Gateway/workspace/artifact/memory) → activity
timeline → inline metrics strip; trustMode badge per enrollment; dead-letter
replay button; the minimal safe §17.3 edit affordances over the config PATCH.
After this plan an operator traces a slim acceptance-run Work from search to
artifact to trace link **without psql** (§21.1's inspector claim, previously
API-only).

## Known facts (as-built anchors; code wins, divergence goes in the status table)
- `src/facilitator/wwwroot/` holds only `.gitkeep`; no `UseStaticFiles`/`MapFallbackToFile`/`DefaultFiles` anywhere; no UI package in the pnpm workspace (5 packages, none a UI).
- Plan 20 freezes the complete API surface (`20-admin-read-models-config-api.md`): summary/search/detail/timeline/links(trustMode)/deliveries/metrics + replay + manifest re-mark + `PATCH config` w/ `If-Match`. The SPA MUST NOT grow that surface; a gap replans 20, it doesn't add client-side aggregation.
- Approval UI builds on the existing pair (`11-approval-lifecycle.md:20`): approvals GET + respond POST, realm user `alice` via `facilitator-api` password grant is the scripted-approver pattern (`12-gateway-policy-approval-loop.md:24`).
- Stale-Gateway law: lease-age from `link_sessions` drives online/offline/stale display; a stale gateway renders its last session + affected assignments (§17.2's interpretation row) — read model already shaped for it.
- §17.4: OTLP traces link by `traceId`; the SPA links out to the Aspire dashboard (running in the slim env via 17/19), it never embeds telemetry storage.
- §17's "single-page" title is a simplicity constraint: multi-view is fine when it removes navigation; if the gate decides to deviate, record it as an errata note beside the spec pointer, not a silent divergence.
- Playwright .NET integration-test precedent to establish (none in repo yet — `playwright-testing` project skill covers the pattern; Aspire-less: point at the slim compose stack or a spawned kernel + built `wwwroot`).
- `make build` must build the SPA (`vite build` → `wwwroot`) before the kernel publish step; zero-warning bar on both stacks (`TreatWarningsAsErrors`, `eslint --max-warnings 0`, strict tsconfig).

## Locked decisions (from planning; replanning may only amend with recorded reason)
- **Inspector-first:** read-heavy single screen (search bar, health strip, inspector pane, activity panel); edit affordances limited to dead-letter replay, approval respond, manifest re-mark, and the small safe config-toggle set exposed by 20 — each requiring an explicit reason field (§17.1).
- SPA auth = Keycloak OIDC authorization-code + PKCE in-browser (human identity, realm role from 20's authorization ops); no separate session store — kernel cookie/JWT choice pinned at the gate against existing middleware.
- `GET /` serves the SPA (`DefaultFiles` + `MapFallbackToFile`), API routes stay under `/api/*`; dev token + `FACILITATOR_URL` env surface documented for headless poking.
- Health strip consumes ONLY `/api/admin/summary` + `/api/admin/metrics?window=` (bounded polling, no websockets in v1).
- New pnpm package `src/facilitator/ui` (or `wwwroot`-sibling — pinned at gate), vite build output into the kernel's static assets; CI typecheck/lint cover it automatically via `pnpm -r`.

## Steps
0. **Research (mandatory):** walk §17.1–17.4 field-by-field against 20's DTOs and write the view-model coverage sheet (any DTO gap → back to 20, not client-side invention); study shadcn/svelte + svelte5 runes skills for current idioms; pin the Playwright .NET bring-up mode (compose stack vs spawned kernel + built SPA); verify Keycloak PKCE config additions to `realm.json` (redirect URIs for `http://facilitator.localhost:8088`).
1. Scaffold `src/facilitator/ui`: vite + Svelte 5 + Tailwind + shadcn/svelte, linted, typechecked, CI-wired; embed pipeline (build order, `wwwroot` output, fallback route) with cache-busting hash filenames.
2. OIDC login flow + role-gated rendering; logout; identity chip.
3. Core views: universal search (typed result sections), health strip, contextual inspector (detail + timeline + artifact/manifest/approval/memory panels), links table with trustMode badge + stale-gateway treatment, deliveries incl. dead-letters with reason + replay button (confirm + reason), inline metrics strip.
4. Edit affordances: approval respond, manifest re-mark, config toggles via `PATCH` + `If-Match` (409 → reload-and-explain), versioned-preference view (author/reason, rollback = re-apply prior).
5. Trace-link egress: `traceId` renders as an Aspire dashboard deep link when the env provides the dashboard base URL.
6. Playwright .NET E2E project: against a live slim stack — search an acceptance-run Work, open timeline, verify artifact + manifest + memory-candidate panels, force a dead-letter (stop gateway mid-delivery), replay it, verify trustMode badge, exercise one config PATCH + 409 conflict. Run at the 19-tier (main/nightly), PR tier gets a lighter page-load smoke if timing demands — decided at gate.
7. Docs: `docs/getting-started.md` admin-tour section (screenshot-less, exact steps); SPA README; `.omp/skills/facilitator-architecture` + `facilitator-reference-env` final pass.

## State after
`referencectl up slim` → browse `http://facilitator.localhost:8088` → Keycloak login → the acceptance-run Work is findable, its timeline/artifacts/approvals/trace link visible, a dead-letter replayable, one config toggle changeable with optimistic concurrency — all from the single embedded SPA, single deployable image.

## Testing
- Playwright .NET E2E (step 6) is the acceptance proof; scenarios per view, deterministic data from `referencectl verify`'s seeded run or an e2e-owned Work.
- Vitest unit coverage only for non-trivial client logic (search-state machine, If-Match retry), not component snapshots.
- API-level If-Match conflict tests already live in 20; SPA test covers only the user-visible conflict path once.

## Documentation
- `docs/getting-started.md` admin tour; SPA README (build/embed, dev loop); `docs/contributing.md` UI toolchain row; §17 errata note if multi-view deviates.

## Architecture fit
§17 — operational inspector over the kernel, never a second source of truth; §21.1's inspector acceptance claim becomes literal. Appendix §4.3 (trustMode visible) and §11.2 (replay control) close here.

## Assumptions & contingencies (replanning caveat)
- If Playwright-against-compose proves too heavy for CI timing, cut to spawned-kernel + built-wwwroot with a seeded DB fixture for the PR smoke, keeping the full-slim Playwright pass at main/nightly — tier decision, not a scope cut.
- If shadcn/svelte maturity forces a component substitution, plain Tailwind components are acceptable; the simplicity bar (§17) outranks component-library purity.
- If Keycloak PKCE in-browser collides with the headless e2e token pattern, tests may reuse the CLI-minted token against the same routes — UI auth path still covered by one dedicated login scenario.
- Replanning or optimization may be required before implementation begins; implementation begins with additional research (Step 0).
- **Questions may be asked during planning, but not during implementation.**

## Implementation Status
| Step | Status | Notes |
|---|---|---|
| 0 Research | Not started | |
| 1 UI scaffold + embed | Not started | |
| 2 OIDC login | Not started | |
| 3 Core views | Not started | |
| 4 Edit affordances | Not started | |
| 5 Trace-link egress | Not started | |
| 6 Playwright E2E | Not started | |
| 7 Docs | Not started | |

## Verification
`make build` embeds the SPA; slim stack + browser walkthrough of a real acceptance-run Work end-to-end; Playwright suite green in its CI tier; `pnpm -r typecheck` + lint + `make docs-check` clean.
