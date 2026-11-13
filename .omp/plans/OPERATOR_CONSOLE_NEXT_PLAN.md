# Operator console next: queue, audit, deep links, runtime, role-scoped AiDeck

## Context

`.omp/ideas/operator-console-next.md` lists the seven gaps an operator hits on
day one with the embedded console (`src/facilitator/ui`): no work queue, no
audit surface, a correlation id that goes nowhere, dead-end search hits, no
deep links, three invisible ops endpoints, and an AiDeck chat plane that cannot
say *why* it answered, cannot carry a decision note, and can only propose reads
instead of answering them. This plan closes all seven. End state: the console is
a triage tool with shareable URLs (`#/works?state=blocked`), the audit trail is
browsable and joinable by correlation id, replicas/provisioning/sweeps are
operator-visible, and AiDeck answers read questions as the operator's own
principal — never exceeding what that role can already reach through the API.

Decisions taken (not open): `ai-operator` stays a distinct realm role but is an
**entry gate only** — turn contents are filtered by the principal's effective
roles/team scope. The new works list filters by the caller's team scope
automatically (same law as `GET /api/works`), with `?team=` narrowing further.

## Findings this plan rests on (all read this session)

- Kernel admin surface: `src/facilitator/Kernel/Admin/AdminEndpoints.cs` (group
  `/api/admin` at `:28`, no `RequireAuthorization` — authorization is 100%
  config bindings), stores in `AdminReadStores.cs` (`SearchAsync:246`,
  `AuditAsync:508`, `AuditExportAsync:597`, `ReplicasAsync:543`,
  `ProvisioningAsync:490`, `SummaryAsync:211`).
- `facilitator.audit` HAS a `correlation_id` column
  (`Kernel/Shared/Persistence/Configurations/AuditEventEntityConfiguration.cs:28`)
  but `AuditAsync`'s projection (`AdminReadStores.cs:531`) omits it and there is
  no filter for it. `AuditRecord` (`Kernel/Shared/Identity/AuditWriter.cs:18-26`)
  has 8 positional params and no correlation field.
- Team scoping: `Kernel/Teams/TeamScope.cs` (`TeamsOf`, `MayReadAsync`) and the
  private `VisibleTeamsOf(HttpContext, TeamScope)` in
  `Kernel/Work/WorkEndpoints.cs:255-267` (`null` = unscoped for
  `kernel-operator`/`framework-admin`/no-team-claim). NO `/api/admin` read
  applies team scope today.
- A queue-shaped list read model exists but is NOT under `/api/admin`:
  `IngressStore.ListRecentAsync(IReadOnlyList<string>? teamSlugs, string? state,
  string? team, int limit, int offset, CancellationToken)`
  (`Kernel/Shared/Persistence/IngressStore.cs:122`, cap 200, newest first),
  surfaced only as `GET /api/works?state=&team=&limit=&offset=`
  (`WorkEndpoints.cs:42`). Work wire states: `open planning active review
  completed blocked cancelled failed` (`Kernel/Work/WorkState.cs:66`). A
  stalled/expired consent parks the work in `blocked`
  (`Kernel/Work/ApprovalSweeper.cs:235-240`).
- Chat plane: `Kernel/Ai/ChatEndpoints.cs` (no DTO read on
  `POST /actions/{id}/confirm|cancel` at `:220`/`:253`; `ChatMessageView:28`
  DOES carry `basis`; `ChatActionRowView:53` does not), `ChatProcessor.cs`
  (`PostMessageAsync:125`, tx opens `:129`, model branch `:166`,
  `ModelTurnAsync:265`, prompt render `:282`, catalog slice `:285`, gateway call
  `:295`, `TryParseTurn:346`, per-proposal catalog check `:326`,
  `FallbackReply:428`, ctor `:47`), `ChatActionExecutor.cs` (`ConfirmAsync:63`,
  audit append `:108`, `ReplayAsync` loopback `:150` forwarding `Authorization` +
  `X-Change-Reason`, base `BaseUrl:183`, `TruncateUtf8`).
- Schema constraints that bind 7b/7c: `chat_messages_basis_chk IN
  ('model','fallback','slash')`, `chat_actions_status_chk IN
  ('pending','executed','failed','cancelled')`, no `expires_at`, no decision-note
  column (`Migrations/20261102153127_AddAiDecoderChat.cs:92,:125,:133`;
  `Configurations/ChatEntityConfigurations.cs:35,58,65`). `chat_actions` FKs to
  sessions AND messages are `ON DELETE CASCADE`
  (`ChatEntityConfigurations.cs:66-69`).
- Gate matching: `IngressRoleGateMiddleware.MatchBinding` (`:133`) + `Segmentwise`
  (`:158`) are private statics; the same segmentwise law is public on
  `CatalogEntry.Matches` (`Kernel/Ai/ActionCatalog.cs:9-49`), whose entries carry
  `Method, PathTemplate, Summary, BodyHint, Notes` (`Entries:61`).
- Bindings live in TWO files and are OUT OF SYNC:
  `src/facilitator/appsettings.Development.json` (`admin.read` lists
  `/api/admin/audit`, `:159`) and `reference/config/facilitator.json` (`admin.read`
  lists only `/api/admin/audit/export`, `:170`).
- SPA: `App.svelte` (322 lines) — `type Panel`/`panel` state (`:15-19`),
  `VIEW_BUTTONS` (`:36-45`), testids `view-links|view-deliveries|view-config|view-chat`,
  `openHit:121` reacts to works only, zero routing. `lib/auth.svelte.ts`
  `login()` passes `state: window.location.hash || "/"` (`:113`) and after the
  callback runs `history.replaceState({}, "", "/")` (`:95-97`) — the OIDC
  `User.state` already round-trips a hash and is thrown away. `lib/status.ts`
  exports `stateLabel/stateTone/TONE_CLASS/ago/stamp` and has NO tone for work
  states `planning/active/review/completed`. `lib/api.ts` exports only
  `ApiError/toApiError/makeApiFetch/getJson/uiBootstrap`; mutation headers are
  written inline per component (`DeliveriesView.svelte:42`
  `headers: { "X-Change-Reason": reason.trim() }`).
- Gates: `pnpm -C src/facilitator/ui typecheck|test|build`; repo root `pnpm lint`,
  `make docs-check` (AGENTS.md 80-line cap — `src/facilitator/AGENTS.md` is at
  79), `make admin-e2e`. Migration recipe: `dotnet tool restore && dotnet ef
  migrations add <Name> -p src/facilitator -s src/facilitator`
  (`.config/dotnet-tools.json` pins dotnet-ef 10.0.14; design-time factory
  `Kernel/Shared/Persistence/FacilitatorDbContextFactory.cs`).
- Test patterns: `tests/Facilitator.Tests/Admin/AdminSurfaceTests.cs`
  (`[Collection("postgres:dispatch-link")]`, `new KernelTestFactory(fixture,
  noDispatcher: true)`, `factory.CreateAuthedClient()`,
  `KernelOps.GetJsonAsync/PostJsonAsync`, `CreateUserClientAsync(factory,"bob")`
  at `:297` for 403 paths); seeders in
  `tests/Facilitator.Tests/Infrastructure/TestDbProbe.cs` (`SeedWork:756`,
  `SeedWorkOwned:452`, `SeedTeam:734`, `SeedProject:741`, `SeedRosterMember:423`,
  `SeedAuditRow:502`, `SeedApproval:1292`, `SeedTimelineRow:1252`,
  `SeedAdminConfig:1265`); chat fakes `tests/Facilitator.Tests/Infrastructure/AiFakes.cs:151`
  (`ScriptedLlmHandler`: `Enqueue(pathContains, responseJson, status)`,
  `Captured`).
- Sweeps POST today requires NO header and audits nothing but the gate row; live
  call sites that must keep passing:
  `tests/Facilitator.ScenarioWalks/CapacityNudgeSagaWalkTests.cs:72,81,85`,
  `HeldActionChainWalkTests.cs:43,46,87`, `RestartNoDoubleAskWalkTests.cs:55,58`,
  `tests/Facilitator.Tests/Work/OpsSurfaceTests.cs:233,236,254`.
- Wire mirror to update: `protocol/openapi/admin-v1.yaml` (paths start `:24`,
  sweeps at `:233`), `docs/protocol.md:143`.

## Approach

Ordered for a building tree; each numbered step leaves the repo compiling and its
own tests green. Steps 1-4 are the read-model + shell backbone; 5-7 are the chat
plane (7's audit deep link needs step 2); 8 is the docs/mirror tail that follows
the kernel changes.

### 1. Extract the two shared identity helpers (no behavior change)

- New file `src/facilitator/Kernel/Shared/Identity/OperationMatcher.cs`: move the
  private statics `MatchBinding` and `Segmentwise` out of
  `IngressRoleGateMiddleware.cs:133-163` into
  `public static OperationBinding? Match(IReadOnlyList<OperationBinding> operations, HttpContext context, PathString path)`,
  `public static OperationBinding? MatchPath(IReadOnlyList<OperationBinding>
  operations, string method, string path)` (the method loop + `SegmentsMatch`
  against a literal path, with NO endpoint-template branch), and
  `public static bool SegmentsMatch(string candidate, string other)`, bodies
  verbatim; `Match` keeps its endpoint-template branch and delegates its
  path-fallback to `MatchPath`. The middleware's only callsite
  (`IngressRoleGateMiddleware.cs:78`) becomes
  `OperationMatcher.Match(options.Value.Authorization.Operations, context, path)`.
  `MatchPath` is what step 7.1 needs: there is no resolved endpoint for a
  catalog entry's own path template, and matching one against the CURRENT
  request's endpoint template would answer a different question than "may this
  principal call that path".
  The AiDeck catalog filter (step 7) calls the same matcher, so "what may this
  principal invoke" has exactly one answer in the codebase.
- `Kernel/Teams/TeamScope.cs`: add
  `public static IReadOnlyList<string>? VisibleTeams(FacilitatorPrincipal? principal)`
  with the body of the private `WorkEndpoints.VisibleTeamsOf`
  (`WorkEndpoints.cs:255-267`): null when the principal is null, holds
  `kernel-operator` or `framework-admin`, or has no `/teams/<slug>` groups;
  otherwise the slugs. Delete `WorkEndpoints.VisibleTeamsOf` and point its two
  callsites (`:47`, `:57`) at
  `TeamScope.VisibleTeams(http.GetFacilitatorPrincipal())`.
- `Kernel/Shared/Identity/AuditWriter.cs`: `AuditRecord` (`:18`) gains a trailing
  optional `string? CorrelationId = null`; `AuditWriter.Build` (`:50`) sets
  `CorrelationId = record.CorrelationId`. All 29 existing positional
  `new AuditRecord(...)` call sites keep compiling untouched.
- Prove: `dotnet build OpenFacilitator.slnx -c Release` and
  `dotnet test tests/Facilitator.Tests -c Release --filter "FullyQualifiedName~Identity"`.

### 2. Kernel read models: works queue, audit filters, sweep discipline

All in `src/facilitator/Kernel/Admin/AdminEndpoints.cs` + `AdminReadStores.cs`.

- New `GET /api/admin/works?state=&team=&limit=&offset=` returning
  `Results.Ok(new { items = ... })` — the same wrapper `/deliveries` (`:67`) and
  `/provisioning` (`:70`) use. The handler resolves
  `TeamScope.VisibleTeams(http.GetFacilitatorPrincipal())` and calls the new store
  method.
- `AdminReadStores.WorksAsync(string? state, string? team, int limit, int offset,
  IReadOnlyList<string>? visibleTeams, CancellationToken ct)` →
  `IReadOnlyList<AdminWorkRow>` (record already at `:122`). Body: delegate to the
  existing `IngressStore.ListRecentAsync(visibleTeams, singleState, team, limit,
  offset, ct)` and map `WorkView` (`Kernel/Work/ICoordinationStore.cs:21`) →
  `AdminWorkRow`. `state` may be a comma-separated list of wire states: pass it
  through when it parses to exactly one, otherwise filter the returned rows in
  memory by the parsed set; unparseable words are dropped silently (never an
  error). `AdminReadStores` ctor (`:204`) gains an `IngressStore` parameter —
  already a singleton (`KernelServiceCollection.cs:95`), no new registration.
- `AuditAsync` (`:508`): new params `string? decision, string? correlation`;
  predicates `a.Decision == decision`, `a.CorrelationId == correlation`; the
  projection (`:531`) selects `a.CorrelationId`; `AdminAuditRow` (`:185`) gains a
  trailing `string? CorrelationId` (JSON name `correlationId`).
  `GET /api/admin/audit` (`:81`) forwards both new query params.
- `POST /api/admin/sweeps/{name}` (`:108`): read `X-Change-Reason` the way the
  config PATCH does (`:183-186`); blank or > 512 chars →
  `Results.Problem(ProblemErrors.ConfigInvalid("the X-Change-Reason header is required (non-empty, <= 512 chars)"))`.
  On a fired sweep, append one audit row through `AuditWriter` (inject
  `AuditWriter` + `HttpContext` into the handler): actor
  `http.GetFacilitatorPrincipal()?.Actor`, action `admin.sweep`, resource
  `/api/admin/sweeps/{name}`, decision `allow`, reason null, detail
  `{"sweep":"<name>","effects":<N>,"reason":"<header>"}`,
  `CorrelationId = name`. Response body unchanged (`{ sweep, effects }`).
- Bindings, BOTH files, same commit: add `"/api/admin/works"` to
  `admin.read.paths`; also add the missing `"/api/admin/audit"` to
  `reference/config/facilitator.json` (`admin.read` there stops at
  `/api/admin/audit/export`, `:170`). Dev file:
  `src/facilitator/appsettings.Development.json:159`.
- No migration in this step.
- New facts in `tests/Facilitator.Tests/Admin/AdminSurfaceTests.cs` (existing
  fixture + `KernelOps` style):
  - `WorksQueueFiltersByStateNewestFirst` — seed one `blocked` and one `open`
    work; `?state=blocked` returns the blocked id only.
  - `WorksQueueInheritsTheCallersTeamScope` — seed team-beta (`SeedTeam:734`) +
    owned project (`SeedProject:741`) + owned blocked work (`SeedWorkOwned:452`),
    then `SeedRosterMember("team-beta", <bobActor>, "human", "operator")` where
    `bobActor = await KeycloakFixture.ActorOfAsync("bob", "bob")` (pattern:
    `tests/Facilitator.Tests/Teams/TeamRosterTests.cs:32`); the `bob` client
    (`CreateUserClientAsync(factory, "bob")`, `:297`) sees it, the unscoped
    service-account client sees both, and `?team=team-alpha` returns `[]`.
  - `AuditFiltersByDecisionAndCorrelation` — extend `TestDbProbe.SeedAuditRow`
    (`:502`) with an optional `string? correlation = null` column in the INSERT,
    seed one deny row with a fresh guid, assert
    `/api/admin/audit?decision=deny&correlation=<guid>` returns exactly that row.
  - `SweepRequiresAChangeReasonAndAuditsTheTrigger` — `POST /api/admin/sweeps/nudge`
    with no header → 400 `config-invalid`; with `X-Change-Reason: manual triage`
    → 200 plus an `admin.sweep` row (assert through a new
    `TestDbProbe.AuditActionPresent(string action)` mirroring
    `AuditActionMissing:514`). Give `KernelOps.PostJsonAsync` an optional
    `string? changeReason = null` parameter
    (`tests/Facilitator.Tests/Infrastructure/KernelOps.cs:15`) and update the four
    sweep call-site files listed in **Findings** to pass one.
- `protocol/openapi/admin-v1.yaml`: new `/api/admin/works` path (`state`, `team`,
  `limit`, `offset`; 200 `{items: WorkRow[]}`), `decision` + `correlation` params
  on `/api/admin/audit`, `correlationId` on its row schema, and a required
  `X-Change-Reason` header parameter on `/api/admin/sweeps/{name}` (`:233`).

### 3. Hash routing: deep links for every view (Gap 5)

New `src/facilitator/ui/src/lib/route.svelte.ts` (no router dependency):

- `export type RouteView = "works" | "work" | "audit" | "links" | "deliveries" |
  "config" | "chat" | "runtime" | "hit" | "none";` and
  `export type Route = { view: RouteView; id: string | null; kind: string | null;
  params: URLSearchParams }`.
- `export function parse(hash: string): Route` — grammar, exact:
  `#/works?state=<csv>&team=<slug>`, `#/work/<id>`,
  `#/audit?decision=&actor=&action=&correlation=&since=`, `#/links`,
  `#/deliveries`, `#/config`, `#/chat` or `#/chat/<id>`, `#/runtime`,
  `#/hit/<kind>/<id>` (`kind` ∈ the six `SearchSection` values). Anything else,
  `""`, `"#/"` ⇒ `{ view: "none", id: null, kind: null, params: new
  URLSearchParams() }`.
- `class RouteStore { route = $state(parse(location.hash)) }`,
  `export const route = new RouteStore()`,
  `export function startRouting(): void` (adds a `hashchange` listener that
  reassigns `route.route = parse(location.hash)`),
  `export function go(hash: string): void` (assigns `location.hash`, which fires
  the listener), `export function href(r: Route): string`. Call `startRouting()`
  from `src/main.ts` before `mount()` — the theme-init law forbids the first
  state write from inside a component `$effect`.
- `src/lib/auth.svelte.ts`: the callback branch (`:95-97`) restores the
  round-tripped route instead of flattening it to `/`:
  `window.history.replaceState({}, "", typeof this.user.state === "string" && (this.user.state as string).startsWith("#/") ? (this.user.state as string) : "/");`
  (`User.state` is the value `login()` passed as `state`; `oidc-client-ts`
  declares `User.state: unknown`.)
- `App.svelte`: delete `type Panel`, `panel`, `selectedWork`. `VIEW_BUTTONS`
  (`:36-45`) gains `{ key: "works", label: "Works", hint: "the queue — everything, newest first" }`
  and `{ key: "runtime", label: "Runtime", hint: "replicas, provisioning, manual sweeps" }`;
  the existing `data-testid={`view-${key}`}` template (`:301`) then yields
  `view-works` and `view-runtime` for free. onclick becomes
  `go(route.route.view === key ? "#/" : "#/" + key)` and `aria-pressed` reads
  `route.route.view === key`. The panel chain (`:308-316`) switches on
  `route.route.view`, adding `WorksView`, `AuditView`, `RuntimeView`.
  `operatorRoles` (`:135`) and `viewButtons` (`:139`) keep working unchanged.
- Inspector mount (`:290-293`) becomes
  `{#if route.route.view === "work" && route.route.id}` with
  `onclose={() => go("#/")}`. `openHit(section, id)` (`:121`) becomes
  `go(section === "works" ? `#/work/${id}` : `#/hit/${section}/${id}`)` and the
  hit button's `cursor-default` branch (`:272-274`) goes away.
- `HealthStrip.svelte`: the "Awaiting a human" badge (`:85-90`) becomes an anchor
  to `#/works?state=blocked,review` and the "audit denies" sparkline label
  (`:20`) a link to `#/audit?decision=deny`, both built with `href(...)`. Keep
  `health-strip` and `deadletters` testids and all visible text verbatim.
- New `src/facilitator/ui/src/lib/route.test.ts` (plain-function vitest like
  `lib/api.test.ts`): `#/works?state=blocked` → `{view:"works",
  params.get("state")==="blocked"}`; `#/work/<guid>` → `{view:"work", id}`;
  `#/audit?correlation=x` → `params.get("correlation")==="x"`; `"#/nope"`, `""`,
  `"#/"` → `view:"none"`; and `href(parse(h)) === h` for each canonical form.

### 4. Three new views: Works, Audit, Runtime

Each clones the `DeliveriesView.svelte` anatomy verbatim: `<section class="card"
data-testid="…-view">`, a chip row (`chip` / `chip chip-active` +
`aria-pressed`), `<table class="table">`, `ProblemNote`, badges via
`TONE_CLASS[stateTone(x)]` + `<span class="dot">` + `stateLabel(x)`, times via
`ago()` with the exact stamp in `title`. Reuse `getJson` (`lib/api.ts:73`),
`apiFetch`/`session` (`lib/auth.svelte.js`); mutations write the header inline
exactly as `DeliveriesView.svelte:40-42`.

- `src/lib/status.ts`: extend `TONES` with
  `planning: "progress", active: "progress", review: "wait", completed: "ok",
  blocked: "wait", failed: "bad", executed: "ok", cancelled: "neutral",
  expired: "neutral", allow: "ok", deny: "bad"`, and `LABELS` with
  `review: "in review"`. One vocabulary; no view invents its own colors.
- `src/lib/types.ts`: add `AdminAuditRow`, `AdminReplicasView`,
  `AdminReplicaRow`, `AdminProvisioningRow` as camelCase mirrors of the kernel
  records (step 2 gives the audit row its `correlationId`).
- `WorksView.svelte` (`data-testid="works-view"`): reads `route.route.params` for
  `state`/`team`; chip row `all`, `blocked`, `review`, `active`, `open` with
  `data-testid={`works-filter-${key}`}` — a click rewrites the hash via `go()` so
  a filtered queue stays shareable; fetches
  `/api/admin/works?state=<csv>&limit=50`; a row per work
  (`data-testid={`work-row-${id.slice(0,8)}`}`) with the objective as a button
  that `go(`#/work/${id}`)`, state badge, team slug, `ago(createdAt)`, version.
  Empty line: `Nothing here — the queue is clear.`
- `AuditView.svelte` (`data-testid="audit-view"`): fetches
  `/api/admin/audit?limit=100` plus `decision|actor|action|correlation|since`
  from the route params; chips `all`/`allow`/`deny`
  (`data-testid={`audit-filter-${key}`}`), actor + action text inputs, a
  correlation input pre-filled from `params.get("correlation")`, and
  `<a data-testid="audit-export" href="/api/admin/audit/export?format=csv">export CSV</a>`
  (the kernel already serves CSV). Rows: `data-testid={`audit-row-${auditId.slice(0,8)}`}`
  and `data-testid={`audit-decision-${auditId.slice(0,8)}`}` whose visible text
  stays the wire word (`allow`/`deny`) — the admin-e2e badge law; actor, action,
  resource, `ago(occurredAt)`, and a `correlationId` cell whose short form links
  to `#/audit?correlation=<full>`.
- `RuntimeView.svelte` (`data-testid="runtime-view"`): replica table from
  `GET /api/admin/replicas` (`data-testid={`replica-${r.replicaId}`}`, an
  "answering" marker on `answeringReplicaId`), provisioning ledger from
  `GET /api/admin/provisioning` (`{items}`), and sweep buttons for exactly
  `["nudge","approvals","schedules","session-loss","audit-retention"]`
  (`data-testid={`sweep-${name}`}`). A click reveals a reason input
  (`data-testid="sweep-reason"`, confirm disabled until non-blank) and a confirm
  button (`data-testid={`sweep-confirm-${name}`}`); the POST sends
  `headers: { "X-Change-Reason": reason.trim() }` and the response renders in a
  `data-testid="sweep-notice"` badge as `ran <name> — <effects> effects`. The
  action column renders only when `session.canMutate`.
- New `src/facilitator/ui/src/components/WorksView.test.ts` cloning
  `EnrollmentTokensCard.test.ts` (mount/flushSync/unmount, fake `Fetcher`,
  `jwt(["framework-admin"])`, `health.start`): stub
  `/api/admin/works?state=blocked&limit=50` with one row, set
  `location.hash = "#/works?state=blocked"`, mount, assert a `work-row-` line
  renders and its badge text contains `blocked`.

### 5. Search finds the correlation id (Gap 3, kernel)

In `AdminReadStores.SearchAsync` (`:246`) the guid arm currently reaches only
`MessageHitsAsync` (`:647`). Beside it, when `guid is not null`, also query
`db.WorkEvents` for `e.CorrelationId == g`, join `db.Works`, and project
`new AdminSearchHit(w.WorkId.ToString(), w.Objective, w.State, w.UpdatedAt)`;
`Take(take)`, de-duplicate against the ILIKE work hits by id, and merge into the
`works` section. Everything else (ILIKE columns, ordering, per-section cap,
`search-query-required`) stays verbatim.

Test in `AdminSurfaceTests`: `SearchMatchesTheCorrelationIdOfAWorkEvent` —
`SeedWork("blocked")`, then a `work_events` row carrying a chosen
`correlation_id` (extend `TestDbProbe.SeedTimelineRow` (`:1252`) with an optional
`string? correlation = null` column in the INSERT);
`GET /api/admin/search?q=<that guid>` must list the work under `works`.

### 6. Every search hit opens something (Gap 4)

New `src/facilitator/ui/src/components/HitDetail.svelte`
(`data-testid="hit-detail"`, close button `data-testid="hit-detail-close"`),
rendered by `App.svelte` when `route.route.view === "hit"`, fed by
`GET`s that already exist:

- `projects` → `GET /api/admin/projects/{id}` (name, team slug, created, the
  work list it already returns).
- `artifacts` → `GET /api/artifacts/{id}` (the exact shape `ArtifactRef.svelte:17`
  consumes: `artifactId`, `mediaType`, `size`, `state`) plus
  `<a data-testid="artifact-content-link" href={`/api/artifacts/${id}/content`}>`
  — a plain anchor is right, the kernel brokers bytes to `artifacts.read` holders.
- `memories` → `GET /api/memory/{id}`.
- `assignments`, `messages` → the panel renders the hit line and exactly
  `No detail surface for this kind yet.` (no single-get kernel endpoint exists
  for either; adding one is out of scope).

`route.svelte.ts` grammar gains `#/hit/<kind>/<id>` (already listed in step 3);
`openHit` from step 3 produces it.

Test: extend the step-4 vitest file set with a `HitDetail.test.ts` that stubs
`/api/artifacts/<id>` and asserts both the metadata badge and a
`artifact-content-link` whose `href` ends `/content`.

### 7. AiDeck: rendering, decision ergonomics, role-scoped read loop (Gap 7a/7b/7c)

Kernel first — the SPA has nothing to render otherwise.

**7.1 Catalog/verbs endpoint (role-filtered by construction).**
New `GET /api/chat/catalog` in `Kernel/Ai/ChatEndpoints.cs`, covered by the
existing `chat.use` binding: add `"/api/chat/catalog"` to that binding's paths in
BOTH `src/facilitator/appsettings.Development.json:174-179` and
`reference/config/facilitator.json`. Inject `IOptions<IdentityOptions>`.
Response (anonymous object is fine, matching the preferences handlers at `:275`):

```
{ catalog: [{ method, path, summary, bodyHint, notes, effect }],
  verbs:   [{ verb, description, args: [{ name, required }] }] }
```

`path` is `CatalogEntry.PathTemplate`; `effect` is `"read"` for `GET` and
`"write"` otherwise — the catalog's method is the whole label. The list is the
**principal's slice**: include an entry when
`OperationMatcher.MatchPath(identityOptions.Value.Authorization.Operations,
entry.Method, entry.PathTemplate)` (step 1) returns `null` (no binding ⇒ any
authenticated principal passes — the gate's own law, now shared) or when that
binding's `RolesAnyOf` intersects `principal.Roles`. Verbs from
`CommandRegistry.All()` (`CommandRegistry.cs:118`).
`ActionCatalog.AllowedFor(IReadOnlyList<CatalogEntry> entries, IReadOnlySet<string>
roles, Func<string, string, OperationBinding?> bindingFor)` returns the filtered
list so 7.4 reuses it verbatim; `bindingFor` is the `MatchPath` closure above.

**7.2 Migration `AddChatDecisionErgonomics`.** Generate with
`dotnet tool restore && dotnet ef migrations add AddChatDecisionErgonomics -p
src/facilitator -s src/facilitator` after editing the entities/configurations:

- `chat_actions`: `note varchar(1024) NULL`, `expires_at timestamptz NULL`;
  recreate `chat_actions_status_chk` as
  `status IN ('pending','executed','failed','cancelled','expired')`
  (`Configurations/ChatEntityConfigurations.cs:65`, entity `Entities.cs:1278`).
- `chat_messages`: `reads jsonb NULL` (`Entities.cs:1234`,
  `Configurations/ChatEntityConfigurations.cs:35` region).
- Basis literals stay `slash|model|fallback` — the plan adds no `degraded-*`
  vocabulary, so `chat_messages_basis_chk` is untouched (it is asserted nowhere in
  `tests/Facilitator.Tests/Persistence/MigrationTests.cs`, so that file needs no
  change).
- TTL config: `AiOptions` (`Kernel/Ai/AiOptions.cs`) gains
  `public int ChatActionTtlMinutes { get; init; } = 60;` and
  `public int ChatReadLimit { get; init; } = 3;` (keys `Ai:ChatActionTtlMinutes`,
  `Ai:ChatReadLimit`; `0` TTL = never expires).

**7.3 Expiry + decision note.**

- `ChatProcessor` proposal insert (`ChatProcessor.cs:217-231`) sets
  `ExpiresAt = policy/opts TTL > 0 ? now.AddMinutes(ttl) : null`.
- `AiActionGaugeSweeper.SweepOnceAsync` (`Kernel/Ai/AiActionGaugeSweeper.cs:44`)
  first runs, then counts:
  `db.ChatActions.Where(a => a.Status == "pending" && a.ExpiresAt != null && a.ExpiresAt <= now).ExecuteUpdateAsync(s => s.SetProperty(a => a.Status, "expired"))`
  — a silently-expired proposal becomes a visible terminal state, and the
  `ai.chat.actions.pending` gauge agrees with the cards.
- `POST /api/chat/actions/{id}/confirm` and `/cancel` (`ChatEndpoints.cs:220`,
  `:253`) read an optional JSON body `{"note": "..."}` the way
  `AdminEndpoints.cs:271` does (`new StreamReader(request.Body, Encoding.UTF8)`);
  blank or > 1024 → 400 `invalid-decision-note` via
  `ProblemErrors.Create("invalid-decision-note", "Decision note invalid", 400, "note must be non-empty and <= 1024 chars")`.
  The note lands on the row and in the `chat.action.executed` audit detail as
  `decisionNote` (`ChatActionExecutor.cs:107-114`), and that audit row now passes
  `CorrelationId: actionId.ToString("N")` — this is the join the card links
  through. `ChatActionExecutor.ConfirmAsync`/`CancelAsync` gain a
  `string? note` parameter; their callsites are `ChatEndpoints.cs:236,261` only.
- DTOs: `ChatActionRowView` (`ChatEndpoints.cs:53`) and `ChatActionView`
  (`ChatProcessor.cs:22`) gain trailing `string? Note, DateTime? ExpiresAt,
  string? Summary`; `Summary` comes from the catalog entry whose `Matches`
  method+path, falling back to `Description`. Update `ActionView`/`RowView`
  (`ChatEndpoints.cs:356,361`) and the proposal mapping (`ChatProcessor.cs:229`).

**7.4 The bounded read phase (7c).**

- `ChatProcessor.PostMessageAsync` (`:125`) signature gains the request:
  `PostMessageAsync(Guid sessionId, string text, FacilitatorPrincipal principal,
  HttpRequest request, CancellationToken ct)`; its single callsite
  (`ChatEndpoints.cs:205`) passes `http.Request`. Thread `request` into
  `ModelTurnAsync` (`:265`). `ChatProcessor`'s ctor (`:47`) also gains a plain
  `AiOptions aiOptions` parameter (it has no options access today, and
  `AddAiFeature` registers the instance directly —
  `builder.Services.AddSingleton(ai)` at `KernelServiceCollection.cs:326-327`, not
  `IOptions<>`), so the TTL and the read cap come from one place; the type
  inference at `KernelServiceCollection.cs:350` needs no change.
- Prompt: `src/facilitator/Ai/Templates/chat.scriban` renders `{{ catalog }}`
  from the **filtered** slice (7.1's `AllowedFor`), replacing the raw
  `ActionCatalog.Entries` at `ChatProcessor.cs:285`, and the per-proposal
  validation at `:326` checks against that same filtered set. A team operator
  therefore cannot be offered a kernel-wide sweep, because the principal cannot
  run one.
- Output contract (`chat.scriban:28-34`) gains one optional first shape, exact
  wording: a turn may answer
  `{"reads": [{"method": "GET", "path": "/api/..."}]}` (at most 3, each matching a
  catalog line the operator was given) before the final `{"reply", "actions"}`.
- New `src/facilitator/Kernel/Ai/ChatReadLoop.cs`:
  `public sealed class ChatReadLoop(ChatActionExecutor executor, int limit, int
  maxBodyChars)` with
  `Task<IReadOnlyList<(string Method, string Path, int Status, string Body)>> RunAsync(
  IReadOnlyList<(string Method, string Path)> reads, IReadOnlyList<CatalogEntry> allowed,
  FacilitatorPrincipal principal, HttpRequest request, CancellationToken ct)` —
  drops any read outside `allowed` (no invented endpoints), caps at `limit`,
  executes each through a new
  `public Task<(int Status, string Body)> ExecuteReadAsync(string method, string
  path, FacilitatorPrincipal principal, HttpRequest request, CancellationToken ct)`
  on `ChatActionExecutor`, which reuses the existing loopback mechanics of
  `ReplayAsync` (`:150`: same `"chat-loopback"` client, same `Authorization`
  forwarding, same `BaseUrl` `:183`, same `TruncateUtf8`) but trims to
  `maxBodyChars` (4000) and writes no audit row of its own — the gate audits it,
  exactly as the confirm path already relies on. Register `ChatReadLoop` in
  `AddAiFeature` (`Kernel/KernelServiceCollection.cs:321-354`, next to
  `ChatProcessor`/`ChatActionExecutor` at `:350-351`).
- Flow in `ModelTurnAsync`: first completion parses as `reads` → run the loop →
  second `gateway.CompleteAsync` (`:295`) with the fetched bodies appended to the
  user prompt under a `READ RESULTS` heading → parse `{reply, actions}` exactly as
  today. A read answering non-2xx, a `reads` list with no usable entry, or a
  second unparseable response degrades through the existing fallback paths
  (`:312`, `:331`) — never a guess. Writes are untouched: propose-then-confirm.
- Persist + surface: store `{"reads":[{"method","path","status"}]}` in
  `chat_messages.reads`, and add a trailing `JsonElement? Reads` to
  `ChatMessageView` (`ChatEndpoints.cs:28`) and `ChatTurnResult`
  (`ChatProcessor.cs:29`).
- Session hygiene: `PATCH /api/chat/sessions/{id}` body `{"title": "..."}`
  (owner-only, 400 `invalid-chat-title` when blank or > 120 — the column cap at
  `ChatEntityConfigurations.cs:20`) and `DELETE /api/chat/sessions/{id}`
  (owner-only, 204; the message/action cascades already exist,
  `ChatEntityConfigurations.cs:66-69`). Add `"PATCH"` and `"DELETE"` to the
  `chat.use` binding's methods in both config files.

**7.5 SPA (`ChatView.svelte`, 371 lines).** Preserve every existing testid
(`chat-view`, `chat-sessions`, `chat-verbosity`, `chat-input`, `chat-send`,
`chat-md`, `chat-option-card`, `chat-option-confirm-all`,
`chat-option-cancel-all`, `chat-pending`, `chat-action-confirm-*`,
`chat-action-cancel-*`, `chat-action-status-*`) and add:

- Basis chip replacing the bare `{m.role} · {m.basis}` line (`:281`),
  `data-testid={`chat-basis-${i}`}`, labels `model`→`model`,
  `fallback`→`canned`, `slash`→`command` through a local `BASIS_LABEL` map plus
  `TONE_CLASS` (progress / wait / neutral).
- Card header shows the kernel-supplied `summary` as the headline, the effect word
  (`read`/`write`) beside it, and `METHOD path` demoted to a mono subtitle; the
  body JSON moves into `<details data-testid="chat-action-body">` showing at most
  5 lines before the disclosure (replaces the always-open `<pre>` at `:327-329`).
- Terminal-state discipline: `badgeClass` (`:219-228`) delegates to
  `TONE_CLASS[stateTone(status)]` (step 4 added the tones), a `failed` card renders
  `statusCode` + `result` inline (both already on the row), and
  `expiresAt` renders as an `ago()` line with `stamp()` in `title`
  (`data-testid="chat-action-expires"`).
- `chat-option-confirm-all` renders only when EVERY pending action in the group is
  `effect === "read"`; write groups get per-action confirm buttons only.
- Confirm/cancel POSTs (`:165`, `:187`) send `{ note }` from an optional per-card
  input `data-testid={`chat-action-note-${actionId}`}`.
- After a terminal `executed`, an anchor `data-testid="chat-action-audit-link"`
  calling `go(`#/audit?correlation=${actionId}`)` (needs step 2 + step 4).
- Session rows gain rename (inline input + `PATCH`) and delete
  (`data-testid={`chat-session-delete-${sessionId}`}`); the session list refreshes
  on the existing `pollAction` cadence (`:150`) plus a 15 s interval while the
  panel is open, and a session updated since it was last opened shows
  `data-testid="chat-session-updated"`.
- Scroll pin: stick to the bottom only while the operator is at the bottom,
  otherwise show `data-testid="chat-jump-latest"`.
- Verb palette: typing `/` into `chat-input` opens
  `data-testid="chat-verb-palette"` listing the `verbs` from
  `/api/chat/catalog` (principal-filtered by construction); the canned/fallback
  reply's verb list becomes the same trigger instead of prose.

**7.6 Tests.**

- `tests/Facilitator.Tests/Ai/ChatCatalogScopeTests.cs` — a `bob` (team-only)
  client's `GET /api/chat/catalog` omits every `POST /api/admin/*` entry while a
  framework-admin client's includes it; `verbs` verbs equal
  `CommandRegistry.BuiltIns.All()` verbs.
- `tests/Facilitator.Tests/Ai/ChatDecisionNoteTests.cs` — seed a session + pending
  action (insert through the chat API with a scripted turn, or seed rows directly
  via a new `TestDbProbe.SeedChatAction(...)`); confirm with a note →
  `GET /api/chat/actions/{id}` shows `note`; the `chat.action.executed` audit row
  carries `decisionNote` and that action id as `correlation_id`. Expiry: seed
  `expires_at` in the past, call `AiActionGaugeSweeper.SweepOnceAsync`, assert
  status `expired`.
- `tests/Facilitator.Tests/Ai/ChatReadLoopTests.cs` — `ScriptedLlmHandler`
  (`tests/Facilitator.Tests/Infrastructure/AiFakes.cs:151`): enqueue a
  `{"reads":[{"method":"GET","path":"/api/admin/summary"}]}` turn then a
  `{"reply","actions"}` turn, post a message, assert the assistant row's `reads`
  jsonb names `/api/admin/summary` with status 200; then a turn whose read path is
  outside the caller's slice degrades (basis `fallback`, zero action rows).
- SPA vitest `ChatView.test.ts` (step-4 pattern) stubbing `/api/chat/catalog`,
  `/api/chat/sessions`, `/messages`: a `basis: "fallback"` message renders the chip
  text `canned`, and a proposal group containing one `POST` renders NO
  `chat-option-confirm-all`.

### 8. Docs and mirrors

- `docs/protocol.md`: extend the `/api/admin/*` row (`:143`) with the works queue,
  the audit filters (`decision`, `correlation`), and the sweep `X-Change-Reason`
  law; extend the `/api/chat/*` row with `catalog`, `PATCH`/`DELETE` sessions.
- `src/facilitator/AGENTS.md`: the `Kernel/Admin/` bullet may gain
  `/api/admin/works` as a route literal only — the file is at 79 lines against the
  80-line `make docs-check` cap, so a line must be spent, not added.

## Critical files & anchors

- `src/facilitator/Kernel/Admin/AdminEndpoints.cs` — `/audit` `:81`,
  `/audit/export` `:86`, `/sweeps/{name}` `:108`, the `{ items = ... }` wrapper at
  `:67,:70`; `/works` goes beside them.
- `src/facilitator/Kernel/Admin/AdminReadStores.cs` — `SearchAsync:246` +
  `MessageHitsAsync:647` (guid arm), `AuditAsync:508` + projection `:531`,
  `AdminAuditRow:185`, `AdminWorkRow:122`, ctor `:204`.
- `src/facilitator/Kernel/Ai/ChatProcessor.cs` — `PostMessageAsync:125`,
  `ModelTurnAsync:265`, prompt + catalog slice `:282-289`, gateway call `:295`,
  proposal insert `:217-231`, `TryParseTurn:346`, `FallbackReply:428`.
- `src/facilitator/ui/src/App.svelte` — `Panel`/`panel` `:15-19`,
  `VIEW_BUTTONS` `:36-45`, `openHit:121`, hit markup `:270-281`, inspector +
  panel chain `:290-316`.
- `src/facilitator/appsettings.Development.json` +
  `reference/config/facilitator.json` — the two binding sets that must move
  together (`admin.read`, `chat.use`).

## Verification

From the repo root, in order; each gate stops the line.

1. `dotnet build OpenFacilitator.slnx -c Release` — 0 warnings (law).
2. `dotnet ef migrations add AddChatDecisionErgonomics -p src/facilitator -s
   src/facilitator` after step 7.2's entity edits, then a follow-up
   `dotnet ef migrations add <anything> -p src/facilitator -s src/facilitator`
   must produce an EMPTY diff (delete that probe file) — the AGENTS.md model-shift
   proof.
3. `dotnet test tests/Facilitator.Tests -c Release --filter "FullyQualifiedName~Admin"`
   — new facts pass. New-behavior proof: `WorksQueueInheritsTheCallersTeamScope`
   shows bob (team-beta operator) seeing only team-beta's blocked work while the
   unscoped client sees both and `?team=team-alpha` returns `[]`;
   `SweepRequiresAChangeReasonAndAuditsTheTrigger` shows 400 `config-invalid`
   without `X-Change-Reason` and an `admin.sweep` audit row with it.
4. `dotnet test tests/Facilitator.Tests -c Release --filter "FullyQualifiedName~Ai"`
   — `ChatCatalogScopeTests`, `ChatDecisionNoteTests`, `ChatReadLoopTests` pass.
   Role-scoped-read proof: the read of `/api/admin/summary` is recorded with
   status 200 and an out-of-slice read degrades the turn to `fallback` with zero
   action rows.
5. `dotnet test tests/Facilitator.ScenarioWalks -c Release` — the three walks that
   POST `/api/admin/sweeps/*` still pass now that the header is mandatory.
6. `pnpm -C src/facilitator/ui typecheck && pnpm -C src/facilitator/ui test &&
   pnpm -C src/facilitator/ui build` (output lands in `src/facilitator/wwwroot`).
7. `pnpm lint && make docs-check`.
8. Manual/dev proof. Prereqs: slim env up (`./referencectl up slim`) so Keycloak
   is on :8480 and the kernel on :8088, then as a named service
   `pnpm --filter @ofac/ui dev -- --port 5173 --strictPort`; verify on 5173 only
   (8088 serves the container's baked `wwwroot`). If new CSS seems missing,
   `rm -rf src/facilitator/ui/node_modules/.vite` and restart; on a first load
   wait ~2.5 s for vite's dep optimizer before signing in (alice/alice, an SSO
   session may skip the form — retry up to 3×).
   - Type `http://localhost:5173/#/works?state=blocked,review` in the address bar
     → `works-view` renders pre-filtered, no reload needed; the queue shows
     seeded blocked works and each row's badge reads `blocked`.
   - Click a queue row → URL is `#/work/<id>` and `work-inspector` appears;
     reload the tab → the inspector is still open.
   - In the inspector click a timeline trace chip, then open
     `#/audit?correlation=<that id>` → `audit-view` lists exactly the rows sharing
     it. For a confirmed chat action, expect its `chat.action.executed` row.
   - `#/runtime` → a replica row for the answering replica exists; a sweep button
     with a blank reason keeps confirm disabled; with `manual triage` typed,
     confirm shows `sweep-notice` reading `ran nudge — N effects`, and
     `#/audit?decision=deny` + `#/audit?decision=allow` filter as expected.
   - AiDeck: ask "what's stuck right now?" → the bubble shows a collapsed
     "looked at: GET /api/admin/summary" trail, a prose answer, and the basis chip
     `model`; a write proposal card shows a human summary, a mono `POST /api/...`
     subtitle, a folded body, no `confirm all`, and after confirming, a
     `chat-action-audit-link` that lands on that action's audit row. Then stop the
     LiteLLM endpoint and ask again → chip reads `canned` and no card appears.
9. `make admin-e2e` last (slow; background it) — the existing 6 admin + 3 chat
   browser facts must stay green, every testid preserved.

## Assumptions & contingencies

- "Awaiting a human" means works in `blocked` or `review`: a stalled/expired
  consent parks the work in `blocked` (`Kernel/Work/ApprovalSweeper.cs:235-240`)
  and `review` is where a human decision is next. If the default filter proves to
  hide work operators want, change only the href `HealthStrip.svelte` generates —
  the endpoint accepts any comma list.
- `GET /api/admin/works` reuses `IngressStore.ListRecentAsync` instead of a new
  query. If the in-memory multi-state filter is too coarse at real volumes, add a
  `string[]? states` overload to `IngressStore.ListRecentAsync` (`WHERE state =
  ANY(...)`) rather than a second store.
- `X-Change-Reason` becomes mandatory on `POST /api/admin/sweeps/{name}`. The four
  call-site files in **Findings** are the complete set today
  (`grep -rn "api/admin/sweeps" --include=*` re-derives it); if a new one appears,
  send the header there too — do not loosen the check.
- Audit export keeps its two existing formats (JSON, `?format=csv`); the idea's
  NDJSON is not added, since the audit view downloads CSV and no consumer needs a
  third shape.
- Team scope in the chat plane is inherited, never stored: `chat_sessions` gains no
  team column, because every read inside a turn is that principal's own API call
  through the same gate. If team-attributable sessions are ever needed, add
  `chat_sessions.team_slug` then — nothing here blocks it.
- `ai-operator` stays a distinct realm role and a pure entry gate
  (`chat.read-all`/`chat.use`/`chat.execute` keep naming it), so no Keycloak realm
  change is in scope; retiring it as an alias was explicitly rejected.
- `chat_messages.reads` is a jsonb trail, not a table: it is display context for
  one bubble, and a per-read table would need retention, access rules, and an
  audit story for data whose whole job is "what the bubble already says".
