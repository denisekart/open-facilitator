# Router teams: deterministic sign-over + delivery-scoped grants (waves 1–5)

**Source idea:** `.omp/ideas/team-signover-routing.md` — read it first; its §2 laws (transfer vs. rent) and §4 validator law are the acceptance bar.

## Context

One globally-hosted bot (a router team) must serve N teams without becoming a master team that owns everyone's content at rest. A link enrollment belongs to exactly one team, inbound media is born on the adapter's enrollment team, and a cross-team artifact read answers 404 by law (`Kernel/Link/LinkEndpoints.Enroll.cs:255-296`). Works already move cross-team (`work-create`'s `team`); content does not. This plan adds **sign-over** (atomic, audited transfer of artifact + binding ownership to a guard-resolved team — inbound) and **delivery-scoped read grants** (rental of one foreign-team artifact's bytes for one reply, read-once, receipt-revoked — outbound). End state: after the router flow runs, the router team owns nothing of another team's; the bot reads team bytes exactly once per delivery; the ordering guarantee is a validator rule, not a convention.

## Locked decisions — do not re-open

1. **Scope:** waves 1–5 — transfer primitive, grants, `sign-over` step + validator + gallery pair, born-team-from-binding ingest, copy-on-forward history proof.
2. **Who may sign over:** the flow step **and** an admin mutation, one implementation, identical audit shape.
3. **Grant lifetime:** TTL ceiling + receipt floor. The read itself consumes (rent exactly once); the adapter's receipt revokes a never-read grant; expiry is enforced lazily at read time — **no new background sweeper**.
4. **Ledger posture:** `provider_events` rows never move; sign-over appends an ownership stamp to the row's existing `verified_reference` (see exact format below).
5. **Shared threads:** per-work bindings now — `communication_bindings` gains a nullable `team` column and the work grain, so one conversation can carry two teams' works.
6. **Verification bar:** xUnit against the Postgres fixture + one executed two-team gallery walk. No live-Telegram leg.

## Step order

Sequential spine; the tree stays green (`dotnet build OpenFacilitator.slnx -c Release`, 0 warnings is law, plus the filtered suites named per step) after each step.

1. **Schema + entities + one migration.**
2. **Transfer primitive** (`ArtifactSignOver`) + admin mutation.
3. **Grants** (`ArtifactGrantStore`) + content-route branch + receipt consumption.
4. **Reply-command mint** in `EfCoordinationStore`.
5. **`sign-over` palette step**: schema → codegen → runner → validator rule → console mirror.
6. **Born-team-from-binding ingest** + gallery pair (`router-ingress`/`team-home`) + the executed walk.
7. **Copy-on-forward history** proven + the `routerLedger` provenance field.
8. **Router-team hygiene WARN** (boot-time, independent; can land any time after 2).

## 1. Schema (one migration: `AddRouterTeamSignOver`)

Mechanism: EF Core, project `src/facilitator`, folder `src/facilitator/Migrations`, schema `facilitator`, one `IEntityTypeConfiguration<T>` per entity in `Kernel/Shared/Persistence/Configurations/`, DbSet in `Kernel/Shared/Persistence/FacilitatorDbContext.cs`. Generate with `dotnet ef migrations add AddRouterTeamSignOver --project src/facilitator` (design-time factory `Kernel/Shared/Persistence/FacilitatorDbContextFactory.cs` needs no DB for `add`); applied automatically at boot by `Kernel/Shared/Persistence/DatabaseInitializer.cs:51`. Table-example to copy: `Migrations/20261028071000_AddMemoryBroker.cs:108,170`.

**`communication_bindings`** (`CommunicationBindingEntity` `Kernel/Shared/Persistence/Entities.cs:914`, config `CommunicationBindingEntityConfiguration.cs`):
- Add `public string? TeamSlug { get; set; }` → column `team` (text, nullable).
- Per-work grain: replace the existing unique `communication_bindings_conversation_unique` on `(provider, channel, root_message_id)` with **two filtered uniques** (the filtered-index precedent is in the same file, `communication_bindings_idempotency_unique`):
  - `communication_bindings_conversation_unique` → `HasFilter("work_id IS NULL")` (one teamless binding per conversation),
  - new `communication_bindings_conversation_work_unique` on `(provider, channel, root_message_id, work_id)` `HasFilter("work_id IS NOT NULL")`.
- The migration drops/recreates that index (`migrationBuilder.DropIndex` + two `CreateIndex`) and backfills nothing (all rows stay `team = NULL` = router-or-sole-team posture, exactly today's semantics).
- Every write site must keep compiling and gaining the new column where the raw SQL lists columns: raw `INSERT INTO facilitator.communication_bindings` at `Kernel/Integrations/ProviderEventStore.cs:832`, `Kernel/Work/ResultIngress.cs:308`, `Kernel/Workflows/WorkflowRunner.cs:2376` — add `team` with a `NULL` value (no behavior change at this step); EF `Add(new CommunicationBindingEntity …)` at `Kernel/Ai/CommandApplier.cs:462`, `Kernel/Work/WorkCreate.cs:148` need no change.

**`facilitator.artifact_grants`** — new `ArtifactGrantEntity` (`Entities.cs`, next to `MemoryShareEntity` `:1220-1228`, the structural template) + `Configurations/ArtifactGrantEntityConfiguration.cs`:

| column | type | law |
| --- | --- | --- |
| `grant_id` | uuid PK, `ValueGeneratedNever`, client-side `Guid.NewGuid()` | |
| `artifact_id` | uuid, FK → `artifacts` | |
| `grantee_team_slug` | text, required | the renting (router) team |
| `grantee_endpoint_id` | text, required | the exact enrollment endpoint allowed to read |
| `command_id` | text, required | the delivery it was minted for (`communication-reply:…`), unique together with `artifact_id` |
| `status` | text, required, CHECK `IN ('active','consumed','revoked','expired')` | |
| `minted_by` | text, required | `kernel:reply` \| `admin:<actor>` |
| `reason` | text, nullable | why (rule id / audit join) |
| `created_at` | timestamptz, required | |
| `expires_at` | timestamptz, required | TTL ceiling |
| `consumed_at` | timestamptz, nullable | set by read, receipt, or expiry |
| `consume_reason` | text, nullable, CHECK `IN ('read','receipt','expiry')` | |

Index: `artifact_grants_open_idx` on `(artifact_id, grantee_endpoint_id)` `HasFilter("status = 'active'")`. The `(artifact_id, command_id)` pair is unique — that index is the replay guard for a re-appended reply command.

## 2. Transfer primitive

New `Kernel/Shared/Storage/ArtifactSignOver.cs`, registered in `AddStorageFeature` (`Kernel/KernelServiceCollection.cs` — the new-service law of `src/facilitator/AGENTS.md`):

```csharp
public sealed record SignOverRequest(
    IReadOnlyList<Guid> ArtifactIds, string ToTeamSlug, string FromTeamSlug,
    string Actor, string Rule, string? ProviderEventId = null,
    ThreadRef? Thread = null);                      // (Provider, Channel, RootMessageId)
public sealed record SignOverOutcome(
    IReadOnlyList<Guid> Transferred, IReadOnlyList<string> Skipped, Guid AuditId);
public async Task<SignOverOutcome> SignOverAsync(SignOverRequest r, CancellationToken ct);
```

One transaction (own `IDbContextFactory<FacilitatorDbContext>`), in order:
1. Load the rows `FROM UPDATE` on `facilitator.artifacts` (raw SQL is legal inside `Kernel/Shared/Persistence` for `FOR UPDATE`). Rows missing, or whose `team_slug != r.FromTeamSlug`, go to `Skipped` with a reason (`not-found` / `foreign-owner` / `already-owned`) — never moved.
2. **Digest re-verify**: stream each row's blob (`IArtifactStore.GetAsync(ArtifactKeys.StoreKeyOf(row.StorageUri))`) through SHA256 and compare with `row.Digest` (`sha256:<hex>` lowercase, the form built at `ProviderEventStore.cs:212`). Mismatch or missing blob ⇒ `Skipped("digest-mismatch")` + a `deny` audit row; the rest still transfers.
3. `UPDATE … SET team_slug = @to`.
4. Timeline: for a row with `work_id`, stage a `WorkEventEntity` with `Kind = "artifact.signed-over"`, `Actor = r.Actor`, `Detail = {artifactId, digest, mediaType, fromTeam, toTeam, rule}`, `CorrelationId = artifactId` — the exact shape of the private `ArtifactLedger.AppendTimelineAsync` (`Kernel/Shared/Storage/ArtifactLedger.cs:630-660`); copy the work `Version` lookup as that helper does.
5. Ledger stamp (locked decision 4), only when `r.ProviderEventId` is set: `UPDATE facilitator.provider_events SET verified_reference = COALESCE(verified_reference,'') || '|owner:' || @to WHERE provider_event_id = @id`. The suffix is inert for every reader: `JudgmentWorkId` (`ProviderEventStore.cs:1069-1071`) only inspects a `work/` prefix at position 0, and duplicate detection compares bodies.
6. Binding repoint (locked decision 5), only when `r.Thread` is set: `UPDATE facilitator.communication_bindings SET team = @to WHERE provider = @p AND channel = @c AND root_message_id = @r AND work_id IS NULL`.
7. One audit row through `AuditWriter` (`Kernel/Shared/Identity/AuditWriter.cs:19` record, `.Append(db, record)` — the `MemoryBroker` promote precedent at `Kernel/Shared/Memory/MemoryBroker.cs:380`): `Action = "artifact.sign-over"`, `Resource = "artifact/<guids, comma-joined>"`, `Decision = "allow"`/`"deny"`, `Reason = "signed-over"`/skip reason, `PolicyVersion = "artifact-v1"`, `Detail = {fromTeam, toTeam, actor, rule, transferred[], skipped[]}`, `CorrelationId = audit id` (the returned `AuditId`).

Admin leg — `POST /api/admin/artifacts/sign-over` added to `Kernel/Admin/AdminEndpoints.cs` (admin plane posture: middleware-gated, no `.RequireAuthorization` — see the comment at `AdminEndpoints.cs:19`; mutation-store precedent `Kernel/Admin/AdminMutationStore.cs:26-41`). Body `{ "artifactIds": ["<guid>"], "fromTeam": "router", "toTeam": "team-alpha", "reason": "mis-owned thread" }` → `SignOverAsync` with `Actor = "admin:<caller>"`, `Rule = "admin:" + auditId`, no thread/ledger legs. Unknown team slug ⇒ 400 via the existing `ProblemErrors` shape; empty transfer ⇒ 200 `{transferred: 0, skipped: [...]}`. `Kernel/Admin/AdminEndpoints.cs` and `protocol/openapi/admin-v1.yaml` both change, then `make protocol` regenerates.

## 3. Grants

New `Kernel/Shared/Storage/ArtifactGrantStore.cs` (registered in `AddStorageFeature`):

```csharp
public static readonly TimeSpan DefaultTtl = TimeSpan.FromMinutes(15);   // a code law, like ArtifactTicket.AllocationTtl
public void Mint(FacilitatorDbContext db, Guid artifactId, string granteeTeamSlug,
                 string granteeEndpointId, string commandId, string mintedBy, string? reason,
                 DateTime now);                                        // Add() on the caller's context
public Task<bool> TryConsumeOnReadAsync(Guid artifactId, string endpointId,
                 string teamSlug, CancellationToken ct);                // true ⇒ bytes may be served
public Task RevokeByCommandAsync(string commandId, string endpointId, CancellationToken ct);
```

- `TryConsumeOnReadAsync` matches `artifact_id + grantee_endpoint_id + status='active' + expires_at > now`, versioned-free (single-writer: a duplicate read loses the UPDATE and gets `false`). Active-but-past-expiry stamps `status='expired'`, `consumed_at=now`, `consume_reason='expiry'` and returns `false`. On success: `status='consumed'`, `consume_reason='read'`, then **two audit rows** (`artifact.grant.read` then `artifact.grant.consume`, `PolicyVersion = "artifact-v1"`, `Detail` carries `grantId`, `commandId`, `endpointId`, `artifactId`). Audit before `SaveChangesAsync` on the same context so a read is atomic with its trail.
- `RevokeByCommandAsync`: any `active` rows for `(command_id, grantee_endpoint_id)` ⇒ `status='revoked'`, `consumed_at=now`, `consume_reason='receipt'`, one `artifact.grant.consume` audit row. Zero rows ⇒ no audit (replay-answers-stored-receipt, the `LinkLedger.AckAsync:263-266` posture).
- Only `active` rows are consultable and there is no read-by-listing route — a grant is neither queryable nor enumerable from the link plane.

**Content-route branch.** `IArtifactLedger.GetForWorkloadAsync` (`ArtifactLedger.cs:565-569`, today strict `row.TeamSlug == workload.TeamSlug`) gains an optional parameter: `GetForWorkloadAsync(LinkPrincipal workload, Guid artifactId, CancellationToken ct, bool allowGrant = false)` (interface `:152`). When `allowGrant` and the team check fails, `TryConsumeOnReadAsync` decides. **The only caller passing `true` is the workload content GET** (`Kernel/Link/LinkEndpoints.Enroll.cs:269-296`); the content PUT (`:245-266`) and confirm (`:300-312`) keep strict equality — a rented principal may read, never write. The 404 stays `ProblemErrors.ArtifactNotFound` (non-disclosure preserved). If the optional-parameter churn proves awkward, the fallback is a separate `GetReadableForWorkloadAsync` (three call sites total, all in `LinkEndpoints.Enroll.cs`).

**Receipt leg.** In `Kernel/Link/LinkReceipts.CommitAsync` (`:25`), after a successful `ledger.AckAsync` and before the pump/wake calls: resolve the delivery's command id — `facilitator.link_deliveries` PK is the outbox id (`Configurations/LinkDeliveryEntityConfiguration.cs:15-26`), so read `command_id` from `facilitator.outbox` by `outbox_id = deliveryId` (`Configurations/OutboxEntityConfiguration.cs:21`) — then `RevokeByCommandAsync(commandId, principal.EndpointId, ct)`. Both transports (HTTP `LinkEndpoints.Deliveries.cs:61` and hub `LinkHub.cs:176`) inherit it for free (the receipt-parity invariant, `src/facilitator/AGENTS.md`).

## 4. Mint at reply-append

In `Kernel/Work/EfCoordinationStore.EmitBindingOutcomeAsync` (`:766`), immediately after `mediaArtifacts` is selected by `SelectCompletionMediaAsync` (`:800-815`) and **before both** delivery paths (the `notifications.EmitAsync` return at `:833` and the direct outbox append at `:847`): for every artifact id in `mediaArtifacts` whose row `team_slug` differs from the receiving enrollment's team (`db.LinkEnrollments` by `endpoint`), call `grants.Mint(db, id, enrollmentTeam, endpoint, commandId, "kernel:reply", rule: "reply-media", now)`. Both paths carry the same deterministic `commandId` (`communication-reply:<transitionEventId>[:bN]`, `:791-793`), so a replay is absorbed by the unique `(artifact_id, command_id)` index — the idempotency law already in this file. Reply targets the router enrollment exactly as today; the bot stays team-blind (`ConversationTarget` built at `:788`).

## 5. The `sign-over` palette step

1. **Schema** `protocol/json-schema/workflow-definition.v1.json`: add `{"required": ["sign-over"]}` to the `$defs.step` `oneOf` block (after the `work-create` entry, `:252-254`), and a `$defs["sign-over"]` payload object, `additionalProperties: false`, `required: ["team","refs"]`:
   - `team`: `$defs/idToken` — destination team slug (a branch-provided template value, the `work-create.team` posture).
   - `refs`: array (1–64) of `$defs/ref` — each resolves to a guid, or to a JSON array whose items carry `artifactRef` (the normalized `sourceRefs` shape, `ProviderEventStore.cs:1017-1027`); the executor takes every `artifactRef`.
   - `thread`: object `{provider, channel, rootMessageId}` of `$defs/ref` — optional; when present the binding repoint runs.
   - `eventRef`: `$defs/ref` — optional; resolves to the `provider_event_id` whose ledger row is stamped.
   - `actor`: `$defs/evidenceRef` — optional timeline actor, absent ⇒ `workflow:kernel`.
2. **Mirrors:** `pnpm protocol:codegen` regenerates `src/protocol/schemas/workflow-definition.v1.json`, `src/protocol/src/generated/types.ts` (the `"work-create"?: WorkCreate` line at `:1350` is the pattern), and `protocol/dotnet/Ofac.Protocol/Generated/WireTypes.cs` (`StepElement` `:2714`, the `[JsonPropertyName("work-create")]` attribute `:2845`, `class WorkCreate` `:3320`). Then `make protocol` must exit with a clean `git diff --exit-code`.
3. **Runner:** add `"sign-over"` to `ExecutableKinds` (`Kernel/Workflows/WorkflowDefinitionValidator.cs:39-42`) and an arm `"sign-over" => ExecuteSignOverAsync(db, instance, stepId, payload, context, envelope, now, ct, shadowMode)` to the `kind switch` (`Kernel/Workflows/WorkflowRunner.cs:272-290`). Model the executor on `ExecuteWorkCreateAsync` (`:875-1014`): resolve templates with the same `ResolveString`/`ResolveTemplate` helpers, resolve the team exactly as `work-create`'s team law does (`:912-940`: declared slug must be rostered, else `LegFailed`), honor `shadowMode` with a `wouldDo` output row (`:985-995`), call `ArtifactSignOver.SignOverAsync`, and return `StepOutcome.Done(new JsonObject { ["effect"] = "sign-over", ["transferred"] = n, ["skipped"] = … }, effectCommandId, note: "flow effect: sign-over N → <team>")` so the leg writer stages the `workflow.stage` timeline row (`:326-329`, `AppendStageTimeline` `:1824-1850`). Deterministic effect id: `WorkflowIdentity.SignOverSeed(instanceId, stepId) => Uuid5(WorkCreateNamespace, $"{instanceId:N}:{stepId}:sign-over")` added to `Kernel/Workflows/WorkflowIdentity.cs` (the `WorkCreateWorkId` precedent, `:87`). `LegFailed` when `transferred == 0` and nothing was shadowed.
4. **Validator rule** `content-not-signed-over`: a loop-level pass in `WorkflowDefinitionValidator.Validate`, inserted after the per-step loop and before the cycle pass (`:183-190`). Law, exactly: for every dispatch step (`emit`, `work-create`, `fork-work`, `invoke`) that declares a `team` value differing from the definition's own team, **a `sign-over` step must exist at a lower index in the `steps` array**; otherwise emit `new("content-not-signed-over", $"step '{stepId}' dispatches to team '<team>' with no preceding sign-over")`. Index order is the ordering law (no dataflow inference). DB-backed enable-time checks (`Kernel/Workflows/WorkflowDefinitionStore.cs:294 EnableChecks`) stay unchanged.
5. **Console mirror:** `"sign-over"` in the kind lists/labels of `src/facilitator/ui/src/components/StepNode.svelte:46` (reuse an already-imported lucide icon — `ArrowRightLeft` if imported, else reuse the `fork-work` icon), `src/facilitator/ui/src/lib/flow-layout.ts:59` (kind list) and `:160` (lane map: `"sign-over": "act"`), and extend the palette expectations in `src/facilitator/ui/src/components/FlowEditor.test.ts` / `flow-layout.test.ts`.

## 6. Born-team-from-binding ingest + the gallery pair

**Ingest leg** (`Kernel/Integrations/ProviderEventStore`): after the ledger insert (`:226-237`) and before the arm dispatch (`:382-391`), resolve

```csharp
var ingestTeam = (await db.CommunicationBindings.AsNoTracking()
        .Where(b => b.Provider == reference.Provider && b.Channel == reference.Channel
                 && b.RootMessageId == reference.Root && b.TeamSlug != null)
        .OrderBy(b => b.CreatedAt).FirstOrDefaultAsync(ct))?.TeamSlug ?? workload.TeamSlug;
```

and pass `ingestTeam` to the arms that take `workload.TeamSlug` today: the reaction policy lookup (`:382`), `MentionArmAsync` (`:385`), `IssueArmAsync` (`:387`), `MessageArmAsync` (`:391`). The **ledger row keeps `workload.TeamSlug`** (`:234`) — ledger ≠ content; the ownership stamp arrives with sign-over. Effect: a thread whose router flow already signed over resolves to the owning team at ingest, so second-and-later messages are born team-owned and the router leg runs once per thread. With per-work bindings, all bindings of the conversation are consulted and the oldest `team`-bearing row wins (one conversation, one content home in v1 of the grain; the `no-owner` branch remains for unresolved chats).

**Gallery pair** — two new files in `Kernel/Workflows/Gallery/` (`router-ingress.json`, `team-home.json`). They are auto-embedded by the csproj glob (`src/facilitator/Facilitator.csproj:35`) and seeded by `Kernel/Workflows/WorkflowGallerySeeder.cs` (enabled=false until an operator enables; update the "six built-in definitions" comment at `:15` to eight).
- `router-ingress`: `on.event` = `"org.facilitator.event.provider.mention.v1"` (verified against `Gallery/agentic-research.json:8`; the seven central types are in `ProviderOptions.cs:450`), `on.where` = `{"data.reference.provider": "telegram"}`; template refs use the gallery's `${…}` syntax (`${event.data.text}`); steps in index order. The guard `decide` is **binary** — a hit continues along the array-adjacent chain, a miss takes `fallback` (`WorkflowRunner.ExecuteDecideAsync:786-819`); there is no multi-way branch key. So the channel→team resolver is a chain of guards, one per team:
   1. `alpha?` = `decide {source: "guard", guard: {field: "event.data.reference.channelOrResourceId", in: ["alpha-channel"]}, fallback: "beta?"}` — hit falls through to step 2.
   2. `alpha` = `sign-over {team: "team-alpha", refs: ["${event.data.sourceRefs}"], thread: {provider: "${event.data.reference.provider}", channel: "${event.data.reference.channelOrResourceId}", rootMessageId: "${event.data.reference.rootThreadId}"}, eventRef: "${event.data.providerDeliveryId}"}`.
   3. `assign-alpha` = `work-create {objectiveRef: "${event.data.text}", capability: "research.default", team: "team-alpha"}`.
   4. `beta?` = the mirror guard with `in: ["beta-channel"]`, `fallback: "park"`.
   5. `beta` = `sign-over` with `team-beta`; 6. `assign-beta` = `work-create` with `team-beta`.
   7. `park` = `escalate`.
   (Each team's branch is its own sign-over + work-create pair — one guard per team is the v1 resolver law; a `source: "model"` decide with per-option `branch` targets is the documented richer variant but never the one that moves ownership.) `on.where` additionally gates `{"data.reference.provider": "telegram"}`. Both flows must pass `content-not-signed-over` — that is their documentation value.
- `team-home`: a minimal team-owned flow (single `emit` leg on `org.facilitator.event.approval.decided.v1`) showing the destination plane; it must also validate.
- Seeded definitions are `enabled=false`; the walk enables them explicitly.

**Walk:** `tests/Facilitator.ScenarioWalks/RouterSignOverWalkTests.cs`, mirroring `WalkKit.cs` and `AssignRulesSwapWalkTests.cs` in `WalkCollection`: seed both flows → ingest a mention on `alpha-channel` with one inbound artifact → assert the artifact's `team_slug` flipped to `team-alpha`, the binding's `team` is `team-alpha`, the `artifact.sign-over` audit names `workflow:router-ingress@1` and the guard rule, the ledger row's `verified_reference` ends with `|owner:team-alpha` → complete the created work → assert one active grant for the reply's command id → GET content as the router principal (200 once, 404 twice) → post the receipt → assert the grant is `consumed`/`receipt` and the audit has one read + one consume line → re-ingest a second message on the same thread and assert it is born on `team-alpha` with zero new `sign-over` audits.

## 7. Copy-on-forward history (proof + one field)

The mechanism already exists: `Kernel/Work/ContextIngress.HandleFetchedAsync` (`:159-215`) stores the adapter's answered window as a `context.history` artifact owned by **the work's team** (`TeamSlug = teamSlug` from the work, `:186`), retention-gated, with the verbatim answer in the `context.fetched` audit (`Detail: data.GetRawText()`), and the fetch command routes to the provider's registered endpoint (`:100-135`) — which is the router bot. For a signed-over thread that is exactly copy-on-forward: the team asks the router for the window and receives its own new row. The v1 delta is two things, no new fetch path:
- The `context.fetched` audit `Detail` gains `"routerLedger": {"provider": <p>, "eventIds": [...], "digests": [...]}` when the window came from a conversation whose binding carries a `team` — provenance pointing at the router ledger rows.
- One xUnit case (in `tests/Facilitator.Tests/Interaction/` or `Work/`, mirroring the existing context-ingress tests — locate by `grep -l ContextIngress tests/`): a router-born thread + an alpha work answers a fetch ⇒ the stored artifact has `team_slug='team-alpha'`, `kind='context.history'`, and the audit carries the `routerLedger` field.

## 8. Router-team hygiene WARN

New `Kernel/Shared/Storage/RouterTeamHygiene.cs`: an `IHostedService` registered beside the gallery seeder in `Kernel/KernelServiceCollection.cs`, `StartAsync` runs one query and at most one `LogWarning`: teams with ≥1 `link_enrollments` row, zero enabled `workflow_definitions`, and ≥1 artifact older than one hour with `team_slug` = that team and `work_id IS NULL`. Message, exactly: `team '{slug}' has enrollments and no enabled flows with {count} aged artifacts — a router team by neglect`. Never fatal, no state. Skips entirely when `Database:AutoMigrate=false` (the seeder's posture, `WorkflowGallerySeeder.cs:41`).

## Critical files & anchors

- `src/facilitator/Kernel/Shared/Storage/ArtifactLedger.cs:565-569` (+ interface `:152`) — the single team-scoping read; the grant branch and its `allowGrant` flag land here.
- `src/facilitator/Kernel/Work/EfCoordinationStore.cs:766-860` — reply append (`commandId` `:791`, media selection `:800`, both delivery paths `:833`/`:847`): the mint site, inside the existing transaction.
- `src/facilitator/Kernel/Workflows/WorkflowRunner.cs:272-290` + `:875-1014` — step dispatch and the `work-create` executor to copy for `ExecuteSignOverAsync`.
- `src/facilitator/Kernel/Workflows/WorkflowDefinitionValidator.cs:39-42,104-190` — `ExecutableKinds` and the loop-level pass site for `content-not-signed-over`.
- `src/facilitator/Kernel/Shared/Persistence/Configurations/CommunicationBindingEntityConfiguration.cs` — the filtered-unique precedent and the index swap.

## Verification

Docker must be running (Testcontainers Postgres; `tests/Facilitator.Tests/Infrastructure/PostgresFixture.cs` + `PostgresCollections.cs`). Run gates per the `of-gate-runs` / `ofac-verification-gate-pitfalls` skills (pnpm store lock, vitest timeouts, codegen/docker races).

1. `dotnet ef migrations add AddRouterTeamSignOver --project src/facilitator`, then re-run the same command and require an **empty diff** (the model-truth law, `src/facilitator/AGENTS.md:66`).
2. `dotnet build OpenFacilitator.slnx -c Release` — 0 warnings.
3. `dotnet test tests/Facilitator.Tests/Facilitator.Tests.csproj -c Release --no-build --filter "FullyQualifiedName~Artifacts|FullyQualifiedName~Link|FullyQualifiedName~Workflows|FullyQualifiedName~Interaction"` — the new `Artifacts/ArtifactSignOverTests.cs`, `Link/ArtifactGrantRouteTests.cs` (on `LinkTestHarness.cs`), `Workflows/SignOverStepTests.cs`. Concrete expectations: foreign team 404 after transfer; new owner 200 with `ETag: "sha256:…"`; `POST /api/admin/artifacts/sign-over {"artifactIds":["<guid>"],"fromTeam":"router","toTeam":"team-alpha","reason":"mis-owned thread"}` → 200 `{"transferred":1,"skipped":[]}`; a router principal's GET of an alpha artifact with an active grant → 200 once then 404 with exactly two audit lines; a router principal's **PUT** of the same row → 404; an expired grant → 404 + an `expired`/`expiry` stamp; a receipt for a never-read grant → `revoked` + one consume audit line, receipt replay → zero new rows. Validator: a flow whose `work-create` names another team with no earlier `sign-over` → error code exactly `content-not-signed-over`; the `router-ingress` gallery pair → no errors.
4. `make scenario-walks` (or the filtered `dotnet test tests/Facilitator.ScenarioWalks/…`) — `RouterSignOverWalkTests` green end to end.
5. `pnpm protocol:codegen && make protocol` — clean diff on generated protocol mirrors.
6. `pnpm -r test && make lint && make docs-check` — the console mirrors and any new doc. `docs-check` (`node protocol/tools/agents-docs-check.mjs`) enforces an 80-line cap on every `AGENTS.md` and that every cited path exists: `src/facilitator/AGENTS.md` is **exactly at 80 lines**, so a new signpost line must replace an existing one.

## Assumptions & contingencies

- **Envelope literals are verified:** mention type `org.facilitator.event.provider.mention.v1`, and `data.reference.{provider,channelOrResourceId,rootThreadId}` / `data.providerDeliveryId` / `data.sourceRefs` as read by `ProviderEventStore.ReferenceOf:1628-1637` and `:207`. If the live telegram payload differs from the gallery's expectations when the walk runs, fix the `on.where`/guard field paths in the gallery JSON only — the step law and validator rule do not change.
- **Receipt → command id**: if `link_deliveries`/`outbox` do not join in one read as assumed (`LinkDeliveryEntityConfiguration.cs:26` names the FK), fall back to matching the grant by `(grantee_endpoint_id, artifact ids present in the delivery's envelope)` read from the outbox `envelope` jsonb.
- **Filtered unique swap**: if EF refuses to model the two filtered uniques side by side, keep the original named index (with `HasFilter("work_id IS NULL")`) and add the work-grain unique as raw SQL in the migration only, with a comment — the runtime law is the same.
- **`allowGrant` parameter churn**: if the interface change ripples, split into `GetReadableForWorkloadAsync` (only three call sites, all in `LinkEndpoints.Enroll.cs`).
- **Team template from a guard branch**: if the runner cannot feed a branch-selected team into `sign-over.team`, keep the gallery's one-step-per-team shape (already the plan) and record the limitation in the doc rather than inventing a template mechanism.
- If `make scenario-walks` reveals the seeded pair needs a work team other than `team-alpha`, change the fixture's roster, not the flow law.
