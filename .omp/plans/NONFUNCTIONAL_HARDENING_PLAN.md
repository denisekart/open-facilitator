# Non-Functional Hardening: feature grouping, persistence consolidation, zero-warning gates, test speed

## Context

Implementation landed through Plan 04 (kernel, link server, workspace-link SDK). A four-way audit (kernel, tests, TypeScript, security) found: no functional bugs, but structural debt that will slow every future slice — technical-layer folders, several 200–400-line multi-responsibility files, raw SQL scattered across five non-persistence-named facades, flat 29-file test project that runs fully serial (87 s today, grows linearly), TypeScript lint that passes with hidden warnings, thin tsconfig strictness, and a handful of small security gaps. This plan is behavior-preserving: same flows, same wire contract, same public APIs. Goal: feature-grouped source and tests, one data-access posture, zero-warning build gates, suite comfortably < 10 min.

Baselines measured this session (must still hold after the pass, except timings should improve):
- `dotnet build OpenFacilitator.slnx -c Release` — 0 warnings (already true; `Directory.Build.props` has `TreatWarningsAsErrors`).
- `pnpm lint` — clean today, but eslint has no `--max-warnings 0` and typescript-eslint recommended rules default to *warn*, so warnings are currently invisible.
- `dotnet test` — 127 tests, 87 s, fully serial (one xUnit `"postgres"` collection).
- `pnpm -C src/workspace-link test` — 60 tests, ~2 s. `pnpm -C src/protocol test` — 47 tests, ~5 s.
- e2e tier (`pnpm test:e2e`, CI `link-e2e` job) — 6 files, serial, own Facilitator + DB per file, ~2 min.

## Step 1 — Zero-warning build gates (config only, no code moves)

1. `tsconfig.base.json`: add `"noUnusedLocals": true, "noUnusedParameters": true, "noImplicitOverride": true, "noFallthroughCasesInSwitch": true, "noImplicitReturns": true, "exactOptionalPropertyTypes": true`. Do NOT add `noUncheckedIndexedAccess` (known churn at `client.ts` backoff indexing + `schemas.ts`). The codebase already uses the `...(x !== undefined ? { x } : {})` idiom pervasively, so `exactOptionalPropertyTypes` should cost ~zero; fix any fallout at the flagged site (do not cast). Remove `"declaration": true` from base — all builds are `tsup --dts`; tsc never emits.
2. Root `package.json`: `"lint": "eslint --max-warnings 0 . && prettier --check \"src/**/*.ts\" \"*.{json,mjs,yaml,yml}\""`.
3. Add `"typecheck": "tsc --noEmit -p tsconfig.json"` to `src/protocol/package.json` and `src/gateway/package.json` scripts (their tsconfigs already include `src`+`test`; CI's `pnpm -r --if-present typecheck` currently skips both silently).
4. `protocol/dotnet/Ofac.Protocol/Ofac.Protocol.csproj`: change `<Nullable>annotations</Nullable>` to `<Nullable>enable</Nullable>` (generated `WireTypes.cs` already carries `#nullable restore`; fix any flow-analysis warnings in the hand-written validators — they're the admission path for every message, they deserve full analysis). Keep `NoWarn CS1591` (generated DTOs).
5. `src/workspace-link/package.json`: drop the redundant `--exclude '**/*.e2e.test.ts'` from the `test` script (already excluded in `vitest.config.ts`).
6. `src/protocol/package.json`: move `canonicalize` from `dependencies` to `devDependencies` (imported only by `test/fixtures.test.ts`).

Gate: `pnpm lint`, `pnpm -r build`, `pnpm -r --if-present typecheck`, `dotnet build` all pass with 0 warnings.

## Step 2 — Kernel concurrency & security hardening (small surgical fixes)

All in `src/facilitator`:

1. **`_paused` race** — `Kernel/Link/LinkDeliveryPump.cs:35`: `HashSet<string> _paused` is mutated concurrently from hub Hello/Receipt, HTTP receipt/claim, the pump loop, and `LinkSink.DeliverAsync`. Replace with `ConcurrentDictionary<string, byte>` (`TryAdd`/`TryRemove`; reads become `TryGetValue`).
2. **Query-string token** — `Kernel/Link/LinkAuth.cs:171` (`LinkBearerHandler.HandleAuthenticateAsync`): accept `Request.Query["access_token"]` only when `Context.WebSockets.IsWebSocketRequest` (the client always sends the bearer via `accessTokenFactory` on upgrade; long-polling is not used in-repo).
3. **Dev endpoints gated** — `Kernel/Link/LinkEndpoints.cs:260` (`POST /api/dev/link-outbox`) and `Kernel/Ingress/KernelEndpoints.cs:88` (`GET /api/dev/deliveries`): map only when `IHostEnvironment.IsDevelopment()`. Both test tiers (xUnit `KernelTestFactory` and the e2e harness at `src/workspace-link/test/e2e/harness.ts:89-92`) already run the server in Development, so no test changes.
4. **Enroll rate limit** — register `AddRateLimiter` with a named fixed-window policy (30 req/min per remote IP) and apply `.RequiresRateLimiting("enroll")` to `POST /enroll` only. Mitigates the enrollment-secret brute-force + token-rotation-on-re-enroll takeover lever.
5. **idToken server-side bound** — enroll endpoint (`LinkEndpoints.cs:44`): reject with 400 `problem` when `idToken` is null/empty or > 128 chars (mirrors `protocol/json-schema/common.v1.json` `$defs/idToken`). Also catch `DbUpdateException` on the enroll upsert and retry once (concurrent first-enroll race → 500 today).
6. **Advisory lock width** — `Kernel/Persistence/EfCoordinationStore.cs:209`: `hashtext(work_id::text)` → `hashtextextended(work_id::text, 0)` (64-bit key; version fence keeps correctness either way).
7. **Timeline bound** — `EfCoordinationStore.TimelineAsync`: bound to the newest 1000 rows (`OrderByDescending(createdAt).Take(1000)` then reverse to chronological). No cursor machinery.
8. **Gauge cache** — `LinkDeliveryPump.cs:60,90,161`: the per-`PumpEndpointAsync` `CountLiveAsync` COUNT becomes a 5 s-cached gauge, copying the existing `GaugeCache` pattern from `DbEventDispatcher.cs:239-255`.
9. **DeliveryLedger cap** — `Kernel/Dispatch/ISink.cs:31-41`: bound the singleton dictionary at 10 000 entries; on overflow `TryRemove` an arbitrary key before insert (it is an assertion aid, not state).
10. **CliRunner exits** — `Kernel/Cli/CliRunner.cs:95,103`: `Required()`/`Usage()` use `Environment.Exit(2)` from value-returning helpers; make them return an `int` exit code that `RunAsync` returns. CLI mode only.
11. **ReplicaId single source** — `DbEventDispatcher.cs:78-79` and `LinkService.cs:33-34` independently read `Kernel:ReplicaId` with the same machine-name/GUID fallback. Extract `Shared/KernelOptions.ReplicaId(IConfiguration)` static used by both.

## Step 3 — Persistence consolidation (the data-access "sweet spot")

**Stance (no specification-pattern machinery at this scale):** EF Core for CRUD and all reads; raw SQL permitted ONLY inside persistence classes and ONLY for: advisory locks, `pg_notify`/`LISTEN`, `FOR UPDATE SKIP LOCKED` claims, and version-fenced UPDATEs. Consolidation targets, all new files under `Kernel/Shared/Persistence/`:

1. `PostgresNotifications` — one static/class helper with `NotifyOutboxAsync(IDbContextFactory or DbContext, string? payload, ct)`; replaces the three divergent NOTIFY forms: `EfCoordinationStore.cs:262` (`SELECT pg_notify(...)`), `LinkEndpoints.cs:294` (bare `NOTIFY`).
2. `PgWakeListener` — extract the near-identical LISTEN loops (`Kernel/Dispatch/OutboxDispatcher.cs:81-123` and `Kernel/Link/LinkDeliveryPump.cs:180-222`): same NpgsqlConnection cast, `Notification +=` → `SemaphoreSlim.Release` with `catch (SemaphoreFullException)`, `WaitAsync(ct)` loop, "NOTIFY is a hint" warning. One class, both consumers inject it.
3. `OutboxRepository` — the outbox-state SETs currently written twice with identical column lists: mark-delivered at `DbEventDispatcher.cs:192-197` and `LinkLedger.cs:240-247`, plus requeue/dead-letter/claim-expired (`DbEventDispatcher.cs:141,217,228`) and the dev-insert + NOTIFY (`LinkEndpoints.cs:260-296`). All move here. After this, no `facilitator.outbox` UPDATE exists outside `OutboxRepository`/`LinkLedger` materialization.
4. **Endpoint leak fix** — `Kernel/Link/LinkEndpoints.cs:243-296`: the two `/api` lambdas inject scoped `FacilitatorDbContext` and run LINQ + `Add`/`SaveChanges` + raw NOTIFY inline. Replace with an `OutboxAdminStore` (methods: `ListDeadLettersAsync(int limit)` — newest dead letters, `AppendDevWorkAsync(objective, state)` — insert outbox row + notify). Use `IDbContextFactory<FacilitatorDbContext>` like every other persistence class (kill the scoped-context idiom entirely: after this change, no endpoint lambda injects a DbContext).
5. **Per-entity EF configurations** — `Kernel/Persistence/FacilitatorDbContext.cs:54-326`: replace the 272-line `OnModelCreating` body with `modelBuilder.ApplyConfigurationsFromAssembly(...)` and one `IEntityTypeConfiguration<T>` per entity (15 files, 10–25 lines each) under `Kernel/Shared/Persistence/Configurations/`, grouped Work vs Link vs inbox/outbox. Preserve every index, check constraint, and the `InList` constant SQL verbatim — then `dotnet ef migrations add` must produce an **empty** migration (proof the model didn't shift); if it does, fix the config, don't accept a diff.
6. **Move persistence facades into Persistence** — `IngressStore` (currently living inside `Kernel/Ingress/IngressProcessor.cs:33-160`), `LinkLedger`, `LinkService`, `LinkEnrollmentStore` keep their exact logic/names but land under the Shared/Persistence folder in Step 4's regroup.

## Step 4 — Kernel feature regroup + god-file splits (move-only where marked)

New top-level layout under `src/facilitator/Kernel/` — three features, one shared kernel. Namespaces follow folders: `Facilitator.Kernel.Work`, `Facilitator.Kernel.Link`, `Facilitator.Kernel.Shared`.

```
Kernel/
  Work/        # Work lifecycle: endpoints, use-cases, state machine, coordination store
  Link/        # workspace-link server: enroll, hub, deliveries, receipts, ledger
  Shared/      # not a feature: ingress plumbing, dispatch, persistence, auth, metrics, cli
```

Exact file mapping (behavior unchanged unless noted):

| From | To | Change |
|---|---|---|
| `Ingress/KernelEndpoints.cs` | `Work/WorkEndpoints.cs` | rename class `KernelEndpoints`→`WorkEndpoints`, `MapKernelApi`→`MapWorkApi`; drop the two `/api/dev` lambdas (they move to Link's admin endpoints, Step 3.4) |
| `Ingress/IngressProcessor.cs` | `Work/IngressProcessor.cs` + `Work/CloudEventIngress.cs` + `Work/WorkDto.cs` | split: `WorkDto` (:20) and `IngressStore` (:33-160) leave the file; the 110-line `IngestEventAsync` CloudEvent pipeline (:284-392) becomes `CloudEventIngress` (class `IngressProcessor` keeps `CreateWorkAsync`/`TransitionAsync`/`SteerAsync`); the steer result-switch duplicated at :253-268 and :349-368 collapses into one `HandleSteerResult` |
| `Domain/WorkState.cs`, `Domain/EnvelopeFactory.cs` | `Work/` | move only |
| `Persistence/EfCoordinationStore.cs`, `Persistence/ICoordinationStore.cs` | `Work/` | move — the coordination store IS the Work feature's persistence |
| `Link/*` | `Link/` | keep; plus the splits below |
| `Auth/BootstrapTokenMiddleware.cs` | split → `Shared/Auth/TokenCheck.cs`, `Shared/Auth/BootstrapTokenValidator.cs`, `Shared/Auth/BootstrapTokenMiddleware.cs`, `Shared/Auth/StartupChecks.cs` | 4 types in one file today; `LinkEnrollmentStartupCheck` (from `Link/LinkAuth.cs:62-82`) joins `StartupChecks.cs` — same fail-closed dev-gate pattern |
| `Link/LinkAuth.cs` | split → `Shared/Auth/ILinkTokenValidator.cs`, `Link/LinkEnrollmentSecret.cs`, `Link/LinkAuthMiddleware.cs`, `Link/LinkBearerHandler.cs` | 5 types in one file |
| `Ingress/ProblemErrors.cs` | `Shared/ProblemErrors.cs` | move |
| `Dispatch/*` | `Shared/Dispatch/` | move; `KernelActivity` (:258-266) gets its own file (it's referenced from 4 files, two via fully-qualified names — those qualifyings drop after the move) |
| `Persistence/{Entities,FacilitatorDbContext,FacilitatorDbContextFactory,DatabaseInitializer}.cs` | `Shared/Persistence/` | move (+ Step 3.5 configurations) |
| `Cli/CliRunner.cs` | `Shared/Cli/` | move (+ Step 2.10) |
| `KernelServiceCollection.cs` | `KernelServiceCollection.cs` (Kernel root) | restructure into `builder.AddSharedKernel()` + `builder.AddWorkFeature()` + `builder.AddLinkFeature()` extension methods, one per folder, so future features are one line each |

Link splits (same folder):
- `LinkEndpoints.cs` (374): break the 268-line `MapLinkApi` into `MapEnroll`, `MapEvents`, `MapDeliveries` (long-poll + `ClaimWindowAsync` stay together), `MapReceipt`, `MapCapabilities`, `MapAdmin` (the two `/api` endpoints, now thin over `OutboxAdminStore`). Shared body helpers go to `Shared/RequestBodies.cs` — `ReadBodyAsync`/`Body`/`catch (BadHttpRequestException)` are currently a line-for-line duplicate between `LinkEndpoints.cs:337-360` and `KernelEndpoints.cs:93-121`; delete both copies, keep one.
- New `Link/LinkReceipts.cs`: `CommitAsync(principal, deliveryId, receiptJson, ct)` = ownership check → `ledger.AckAsync` → `pump.PumpEndpointAsync` → `registry.Wake`; `LinkHub.AckAsync` (:229-247) and the HTTP receipt path (:181-192) both call it (parity is an explicit design goal maintained twice today).
- New `Link/AdmissionGate.cs`: the validation prefix of `LinkIngress.AdmitAsync` (:59-131: size → parse → receipt-class → envelope validate → known-command → assign-forbidden) returns a nullable admission verdict; `LinkIngress` keeps dedupe + dispatch.
- New `Shared/EnvelopeReader.cs`: `SourceOf`/`EventIdOf`/`WorkIdOf` are verbatim duplicates of `IngressProcessor.cs:394-420`; one static used by both ingress paths.
- `LinkHub.cs:14-40`: wire frames (`WelcomeFrame`, `GoodbyeFrame`, `FlowControlFrame`, `EnvelopeAck`) move to `Link/LinkDtos.cs` where the other DTOs live.

**Generated-code exemption (explicit):** `protocol/dotnet/Ofac.Protocol/Generated/WireTypes.cs` (1898 ln) and `src/protocol/src/generated/types.ts` stay whole-file. They're codegen output (quicktype, single `-o`, shared-type dedup), already analyzer-exempt via `.editorconfig` `generated_code`, never hand-edited, and CI has a regen drift gate. Splitting would duplicate shared types across outputs for zero reading-surface benefit.

Caller updates after all moves: `Program.cs`, `KernelServiceCollection.cs`, and ~9 `using Facilitator.Kernel.*` lines in tests (grep `using Facilitator.Kernel` — that is the complete set; tests drive HTTP otherwise).

Gate after Steps 2–4: `dotnet build && dotnet test` — 127 tests still pass, zero wire/behavior change.

## Step 5 — Test regroup, typed probe, parallelization

### 5a. Feature folders (mirror src), namespaces follow folders (`Facilitator.Tests.<Folder>`)

```
tests/Facilitator.Tests/
  Infrastructure/  PostgresFixture, PostgresCollection, KernelTestFactory, KernelOps, LinkTestHarness, TestDbProbe(new)
  Work/            WorkLifecycleHttp, IllegalTransition, ConcurrentSteer, StateGuard
  Ingress/         IngressValidation, DuplicateIngress, HealthEndpoint
  Link/            LinkIngress, Enrollment, Handshake, Heartbeat, DeliveryFlow, FlowControl,
                   ReplayOnReconnect, ReceiptLoss, HttpFallbackParity
  Dispatch/        DeadLetter, DeadLetterEndpoint, LeaseRecovery, RestartSurvival
  Persistence/     Migration, MigrationAndAuthBoundary
  Protocol/        ControlFrameValidator
  Cli/             CliSmoke
```

### 5b. Typed probe over raw SQL

16 of 29 files interpolate GUIDs into SQL strings via `PostgresFixture.Execute/Count/TextListAsync` (~45 sites). Add `Infrastructure/TestDbProbe.cs`: one class over `NpgsqlDataSource` with parameterized, feature-named query methods covering every current site — at minimum `OutboxCountBy(workId, state)`, `OutboxTotal(state)`, `InboxState(eventId)`, `TimelineCount(workId)`, `SessionEpoch(endpointId)`, `SessionLeaseState(endpointId)`, `DeliveryState(deliveryId, state)` returns bool, `MarkDeliveryState(deliveryId, state)`, `ExpireLinkLeases()`, `ReclaimOutboxLease(workId)`, `SchemaHasTable(name)`, `DeadLetterCount(workId)`. Convert all call sites (method call replaces interpolated string). `PostgresFixture` keeps only container lifecycle + fixture-file helpers.
Additionally fix `DeadLetterTests.cs:39`'s unscoped `UPDATE facilitator.link_sessions SET lease_until = ...` → route it through `TestDbProbe.ExpireLinkLeases()` which adds `WHERE lease_until > now()` (same semantics today, idempotent-safe).

### 5c. Parallelization (the <10 min lever)

Replace the single `"postgres"` collection with per-feature collection definitions — `"postgres:work"`, `"postgres:ingress"`, `"postgres:link"`, `"postgres:dispatch"`, `"postgres:persistence"` — each carrying an `ICollectionFixture<PostgresFixture>`. `PostgresFixture` gets a shared container: a `static Task<PostgreSqlContainer>` started lazily (first fixture created wins the race; migrations already serialize behind the existing advisory lock). One container spin-up, ~5 classes/instances now run in parallel across collections. Per-test data isolation is already the convention (GUID endpoint/work ids), and 5b killed the one global-write hazard; if cross-collection interference shows up as flakiness in `Dispatch` vs `Link` (global outbox claim), merge those two collection names — do not de-parallelize the rest.

### 5d. Keep (decisions, not deferrals)

- No factory-per-class reuse: host boots are ~0.5–1.5 s; parallelism buys more than dedup churn.
- `LinkDeliveryPump.SafetyPoll` 2 s stays; no production code bends for tests here.
- e2e tier stays `fileParallelism: false` (the per-file-Kestrel flakiness note in `vitest.e2e.config.ts` is correct); it's a separate CI job at ~2 min.
- `ReceiptLossTests`' honest ≥1.9 s long-poll assertion stays.
- Shrink two negative-proof windows: `AssertNoDeliverAsync` default 2 s → pass 500 ms at `FlowControlTests.cs:38` and `ReplayOnReconnectTests.cs:63`; `TryDisconnectAsync(1_000)` → 500 ms at `HandshakeTests.cs:82`.

### 5e. Vitest grouping (public surfaces)

- `src/workspace-link/test/` → feature folders: `test/client/` (client.test), `test/ledger/` (ledger.test), `test/transport/` (http-poll, envelope, mock-transport helper), `test/faults/` (faults.test), `test/package/` (fixtures.test, package.test), `test/e2e/` unchanged.
- Fix the two deep imports: `test/ledger.test.ts:8` imports internal `../src/core/ledger.js` for `dedupeKeyOf` — test dedupe through `SqliteLedger` behavior instead (insert twice, assert one row). `test/package.test.ts:26` imports `parseArgv`/`ConfigError` from `src/bin/linkd.js` — move both into `src/core/config.ts`, export them from `src/index.ts`, have `bin/linkd.ts` import from there.
- `vitest.config.ts` / `vitest.e2e.config.ts` `include` globs updated to the new folders.

## Step 6 — TypeScript package fixes

In `src/workspace-link` unless noted:

1. **`core/client.ts` split (694 → ~230-line facade + collaborators).** `LinkClient` keeps the state machine, wiring, and its exact public surface (`LinkClientOptions`, `LinkSession`, `state`/`session`/`stats`, `on()`, `start()`, `stop()`, `publishEvent()`, `publish()`, `counters`, `createLinkClient()`). Extract per responsibility cluster: `core/link-event-bus.ts` (listener map + emit, :91,134-153); `core/reconnect-supervisor.ts` (loop, backoff ladder, terminal error, :86-90,211-263); `core/handshaker.ts` (hello build + self-validate + welcome ingest, :274-319); `core/heartbeat-monitor.ts` (:335-360); `core/delivery-pipeline.ts` (validate → size gate → ledger insert → branch, :388-457); `core/command-dispatcher.ts` (dispatch + handler + outcome→receipt + ledger flip, :460-561); `core/outbox-drain.ts` (:572-625, serial send preserved — it's ordering law); `core/dispatch-queue.ts` (flow window + queue + pause/resume, :87-90,372-383,448-457,627-649). Token-refresh-on-401 (:322-332) folds into a small `core/token-rotation.ts`. Extract one `core/timers.ts` `unrefTimer()` helper killing all three `(x as unknown as { unref?: () => void }).unref?.()` casts (:352, :682-683).
2. **`node/http.ts`**: (a) deliveryIds interpolated into the receipt URL path (:106) → `encodeURIComponent`; (b) poll-loop error retry (:176-181) uses a hardcoded `sleep(1000)` — reuse a backoff ladder mirroring `DEFAULT_BACKOFF` from client.ts, reset on success; (c) validate each polled item against the existing `link-delivery.v1.json` schema (`validateAgainstSchemaId`, already registered in `src/protocol`) before fan-out instead of the raw `as LinkDeliveryV1[]` cast (:168) — malformed wrappers currently poison the ledger keyed by `undefined`; (d) delete `safeProblem` (:240-248) and use the single `parseProblem` exported from `core/enroll.ts` (the two are near-identical; keep the `detail: "non-JSON error body"` fallback variant).
3. **Frame-emitter dedup**: the triple-callback-list boilerplate (`deliverCbs`/`flowCbs`/`disconnectCbs` + push loops) is copy-pasted in `node/signalr.ts:40-42,138-148`, `node/http.ts:57-59,131-144`, `core/faults.ts:28-30,77-92`. One `core/frame-emitter.ts` helper used by all three + the client event bus. ~60 duplicated lines gone.
4. **Clean cutover**: delete `SessionSupersededError` (`core/errors.ts:32-43`) — never thrown anywhere; drop its assertion from `test/package.test.ts`. `LinkError.code` (free-form string) → string-union of the ~12 literal codes already in use.
5. **Rename** `core/faults.ts` → `core/fault-injector.ts` (it only contains `FaultInjector`).
6. **`bin/linkd.ts`**: link-token stderr print (:164) keeps (documented UX) but add `OFAC_ENROLL_SECRET` env fallback before `--secret` (argv secrets are visible in `ps`). `node/ledger.ts`: `chmod 0600` the SQLite ledger file on create.
7. **`pnpm-workspace.yaml`**: verify `@types/better-sqlite3@^9.6.0` actually matches better-sqlite3 v13's API; v13 ships its own types — if so, drop the `@types` catalog entry and dependency.

## Step 7 — Agent-facing documentation architecture (hierarchical AGENTS.md + drift gate)

Principle: **code is the source of truth; AGENTS.md files are signposts and module-local development rules.** Nested files are ≤ ~40-line indexes: 1-line purpose, invariants invisible in code, exact anchors to truth (files, schemas, commands), and how to develop in that area. Root AGENTS.md may run to ~90 lines because it carries the codebase-navigation walkthrough. NEVER restate schemas, signatures, or behavior text that code already states — link instead. Run after Steps 4–6 so all referenced paths are final.

### 7a. Root `AGENTS.md` — rewrite: ≤ 90 lines, navigation-heavy

Current 96 lines are rotting: line 7 claims "Status: greenfield. Nothing is implemented yet" (false — Plans 00–04 landed), and the layout map predates this plan's regroup. Rule driving the rewrite: **AGENTS.md states present facts, preferences, and instructions only — never implementation status, never forward-looking plan references.** Which slice lands what is roadmap content (`IMPLEMENTATION_ROADMAP_PLAN.md` owns it); docs that duplicate it rot on the next commit. New structure:

1. **What/why**: keep the current first paragraph, trimmed to ~4 lines, present tense, no status sentence.
2. **Truth pointers**: one block — specs (`docs/specs/`, normative for the *target* design; the code is authoritative for what *exists*), implementation status via `.omp/plans/IMPLEMENTATION_ROADMAP_PLAN.md` (link only, no plan list copied here).
3. **Commands block**: exact gate set — `make build`, `make test`, `make lint`, `pnpm -r --if-present typecheck`, `make docs-check`; e2e invocation with its env vars; the <10 min suite expectation; "zero warnings is a build requirement on both stacks".
4. **Progressive-disclosure map**: one table row per area → its nested AGENTS.md (`src/facilitator/`, `src/workspace-link/`, `src/protocol/` (covers `protocol/`), `tests/Facilitator.Tests/`, `reference/`, `features/`, `src/gateway`), plus one sentence: when a tool loads only the root file, open the nested AGENTS.md of every directory you touch — nearest file wins.
5. **Navigation walkthrough** (why the root file is allowed ~90 lines): a "follow the truth" section that teaches orientation in ~10 pointer-lines, present facts only — the request path (`Work/WorkEndpoints.cs` route table → processor → coordination store → `facilitator.outbox` → dispatcher/sinks, each hop one file path), the message path (link client → `Link/LinkHub.cs` / fallback endpoints → admission gate → ledger → pump), where a new endpoint / new table / new wire message type gets added (anchor files only, no behavior description), and how to run any single test slice (`dotnet test --filter`, `vitest run <file>`). Every line is a path or a command — zero prose that could rot.
6. **omp workspace layer**: compress the existing profiles/skills/rules/KB paragraphs; keep the skill-maintenance rule (skills describe implemented reality, updated in the same change) and the `.omp/plans/` project-artifact contract. Drop the layout tree and stack detail that now live in area AGENTS.md files. State the doc-sync rule: an AGENTS.md change rides in the same commit as the change it describes; `make docs-check` fails otherwise.

### 7b. Nested AGENTS.md — create 5 files, each ≤ 40 lines, one skeleton

Skeleton for every file: `Purpose` (1–2 sentences) / `Truth anchors` (paths to authoritative code, schemas, migrations) / `Develop here` (area-scoped build+test commands, conventions, pitfalls) / `Invariants` (rules NOT derivable from reading the code). Link to `.omp/skills/*` and `docs/*` instead of restating them. Mandated content per file:

|File|Truth anchors|Invariants/develop rules that must appear|
|---|---|---|
|`src/facilitator/AGENTS.md`|post-Step-4 tree (`Kernel/Work`, `Kernel/Link`, `Kernel/Shared`), `Kernel/KernelServiceCollection.cs` as the composition completeness check, `Migrations/`, `protocol/dotnet/Ofac.Protocol` as wire truth|raw-SQL law from Step 3 (raw SQL only inside `Shared/Persistence` classes, only for advisory locks / NOTIFY+LISTEN / SKIP LOCKED claims / version-fenced UPDATEs; no DbContext injected into endpoint lambdas); per-entity `IEntityTypeConfiguration` required for new entities; new services register via the feature-extension method of their folder; model edits must prove an empty `dotnet ef migrations add` diff; test classes live in `tests/Facilitator.Tests/<Feature>` mirroring `Kernel/<Feature>`|
|`src/workspace-link/AGENTS.md`|`src/core` = runtime-agnostic law, `src/node` = node-only IO; the `package.json` exports map is the public surface; payload schemas in `../protocol/schemas`|tests import only the two public entries (`../src/index.js`, `../src/node/index.js`) — no deep imports; unit tier has no containers, e2e tier needs `FACILITATOR_URL` + `FACILITATOR_PG` and boots its own server|
|`src/protocol/AGENTS.md` (one file covering both `src/protocol` and `protocol/` — referenced from the root map's protocol row)|`pnpm protocol:codegen` + `protocol/tools/codegen.mjs`; generated dirs `src/protocol/src/generated` and `protocol/dotnet/Ofac.Protocol/Generated` are read-only; `protocol/conformance-fixtures` is the dual-harness contract|never hand-edit generated dirs — regenerate, CI has a `git diff --exit-code` drift gate; a schema change = schemas + codegen + both validators + fixtures in ONE change|
|`tests/Facilitator.Tests/AGENTS.md`|`Infrastructure/` (collections, `TestDbProbe`, `KernelTestFactory`)|public-surface rule (HTTP/SignalR/CLI first; DB state asserted only via `TestDbProbe` — raw SQL strings forbidden in test files); feature folder mirrors `src/facilitator/Kernel/<Feature>`; a new Postgres-backed class joins an existing feature collection, never spawns a container|
|`reference/AGENTS.md` + `features/AGENTS.md`|`versions.env` pinning truth / Dev Container Features spec + `facilitator-workspace-packaging` skill|present facts only: "`referencectl` does not exist yet — `reference/` Compose files are spec §21–22 projections; do not invoke it"; new version pins must extend the CI `meta` job's key-completeness list. No plan numbers, no ETAs|

`src/gateway` ships no AGENTS.md now (a 2-line source file whose package.json exports map is the whole story); it enters 7c's `EXEMPT` set and loses the exemption whenever a file lands there.

### 7c. Sync mechanism — `docs-check` gate

New `protocol/tools/agents-docs-check.mjs` (~60 ln, zero deps, node `fs` only), run from repo root. Checks, each with a distinct failure message:
1. Every AGENTS.md (walk from root, skipping `node_modules`, `dist`, `bin`, `obj`, `docs`, `.git`) is ≤ 80 lines.
2. Every relative path referenced in an AGENTS.md — backticked `` `path/segments` `` tokens containing a `/` and markdown link targets — exists in the worktree. This is the anti-rot core: renames/moves break the gate.
3. Every first-level directory under `src/` and each of `tests/`, `reference/`, `features/`, `protocol/` has an AGENTS.md or an entry in `const EXEMPT = new Set(["src/gateway"])` in the script.
4. Root AGENTS.md does not contain the literal `Status: greenfield` (regression pin against the rot class found in this audit).
5. No AGENTS.md matches the regex `Plan [0-9]` — implementation-status/forward-looking plan references are banned from agent docs by rule, so enforce it mechanically; roadmap status lives only in `.omp/plans/`.
Wire-up: root `package.json` script `"docs-check": "node protocol/tools/agents-docs-check.mjs"`; `Makefile` target `docs-check:` invoking it; `.github/workflows/ci.yml` `meta` job step `- run: node protocol/tools/agents-docs-check.mjs`.

## Step 8 — CI polish

`.github/workflows/ci.yml`: add `-- --blame-hang-timeout 2m` to the `dotnet test` step (a hung suite must fail fast, not hit the job timeout). Everything else stays; the lint gate hardening from Step 1 flows through automatically.

## Critical files & anchors

- `src/facilitator/Kernel/KernelServiceCollection.cs` — composition root rewritten as three feature extensions; every moved type is registered here, so it's the completeness check for the regroup.
- `src/facilitator/Kernel/Link/LinkEndpoints.cs` — largest split + the two misplaced `/api` lambdas (:243-296) + duplicated body reader (:337-360).
- `tests/Facilitator.Tests/PostgresCollection.cs` + `PostgresFixture.cs` — single-collection → shared-container-per-feature-collection switch.
- `src/workspace-link/src/core/client.ts` — 694-line split; `test/client.test.ts` is the behavior oracle (must pass untouched).
- `tsconfig.base.json` + root `package.json` — the zero-warning gates.

## Verification

1. Gates: `pnpm lint && pnpm -r build && pnpm -r --if-present typecheck && dotnet build OpenFacilitator.slnx -c Release` → 0 warnings, 0 errors.
2. Model invariance: `dotnet ef migrations add ModelCheck -p src/facilitator -s src/facilitator` (after Step 3.5) generates an **empty** Up/Down; delete that migration file. Non-empty ⇒ a configuration shifted; fix before proceeding.
3. Wire invariance: `pnpm protocol:codegen && git diff --exit-code -- src/protocol/src/generated protocol/dotnet/Ofac.Protocol/Generated src/protocol/schemas`.
4. Behavior: `dotnet test OpenFacilitator.slnx -c Release` → all 127 pass, wall time **under ~60 s** (was 87 s serial); `pnpm -C src/workspace-link test`, `-C src/protocol test` green; `client.test.ts` unchanged-and-green proves the client split.
5. New behavior checks (concrete):
   - Dev gate: `ASPNETCORE_ENVIRONMENT=Production dotnet run --project src/facilitator` with `ConnectionStrings__Facilitator` + `Auth:BootstrapToken=x` → `GET /api/dev/deliveries` with `Authorization: Bearer x` returns **404** (in Development it still returns 200).
   - Query-token gate: SignalR negotiate POST with `?access_token=<link token>` and no bearer header → 401; a real WS upgrade carrying `?access_token=` still authenticates (covered by existing `HandshakeTests`/e2e, which use the WS path).
   - Parallel safety: run `dotnet test --filter "FullyQualifiedName~Dispatch|FullyQualifiedName~Link"` twice back-to-back → stable (proves the scoped-lease UPDATE + collection split).
6. e2e: with a Postgres up (e.g. `docker run -d --name ofac-pg -e POSTGRES_PASSWORD=postgres -p 5433:5432 postgres:17-alpine`), `FACILITATOR_URL=http://gate-only FACILITATOR_PG=postgresql://postgres:postgres@127.0.0.1:5433/postgres pnpm -C src/workspace-link test:e2e` → all 6 files pass (proves dev-endpoint gating didn't break the e2e harness's own seeding).
7. Enroll rate limit: `for i in $(seq 40); do curl -s -o /dev/null -w '%{http_code}\n' -X POST localhost:8088/enroll -H 'Content-Type: application/json' -d '{"endpointId":"e","instanceId":"i","trustMode":"gated"}'; done` against a dev server → first ~30 at 200/401, remainder 429.
8. Docs gate: `make docs-check` → exit 0. Self-test it once: temporarily add to `src/facilitator/AGENTS.md` a reference to `` `Kernel/Nope/ghost.cs` `` and the text "lands in Plan 09" → script must fail naming both violations → revert. Confirm root AGENTS.md ≤ 90 lines and every nested AGENTS.md ≤ 40 (the script's 80-line cap is the hard fail; the tighter budgets are the authored policy).
9. Total wall: the full CI set (dotnet + typescript + protocol + meta jobs, excluding e2e) completes well inside 10 min.

## Assumptions & contingencies

- **Nullable=enable in Ofac.Protocol** may surface flow-analysis warnings in the hand-written validators: fix properly (guards, not `!`); if a warning is generated-type-bound, add a targeted single-line `#nullable` annotation there, never a file-wide revert.
- **Cross-collection parallelism**: if `Dispatch` and `Link` collections interfere through the global outbox claim (dead-letter counts wobbling), merge those two collection names into `"postgres:dispatch-link"` — keep the others parallel.
- **`exactOptionalPropertyTypes` fallout**: if it flags > ~10 sites, skip that single flag (leave the other five) rather than mass-casting; record the skip in a comment in `tsconfig.base.json`.
- If `@types/better-sqlite3` is genuinely required by v13 (Step 6.7), keep both and store the pairing note in `pnpm-workspace.yaml`'s comment block.
