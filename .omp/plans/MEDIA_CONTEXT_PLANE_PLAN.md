# Media plane + constructed context: first-class bytes and a declarative context builder

## Context

The kernel moves text first-classly and bytes second-classly: the contract (`artifactRef`, `sourceRefs`, `communication.*.artifacts ≤8`), the broker (`ArtifactEndpoints` + link-plane artifact routes, both stores, digest law), and one complete Telegram round trip (`telegram/src/media.ts` + `handler.ts`) all exist — but nothing constructs outbound media (`communication.*.artifacts` has no kernel emitter), only Telegram speaks media, `command.context.fetch.v1` has no kernel handler, and dispatch envelopes are bare events. This plan: extract Telegram's media law into shared SDK law, add a declared media capability facet, emit manifest artifacts on completion replies (arm, behind per-provider config), give mattermost and forgejo full media legs, make `context.fetch` real (memory/artifact/thread handlers + an adapter answer-event), add a declarative `context.build` block folded into dispatch (stored as a digest-stamped `context.dispatch` artifact), and land media policy + retention as a new admin config resource. End state: an image posted on Telegram yields a harness reply carrying text + the annotated image on every capable channel, degrading honestly (brokered link + audit line) where not, and "what exactly did the harness see" is evidence.

## Wave 1 — shared media law, capability facet, outbound emitter (arm, gated)

### 1.1 Contract: capability facet + context schemas + codegen

- `protocol/json-schema/capability-manifest.v1.json`: the capability-descriptor object (items, `additionalProperties: false`) gains optional `media`:
  ```json
  "media": { "type": "object", "additionalProperties": false,
    "required": ["directions"],
    "properties": {
      "directions": { "type": "array", "minItems": 1, "items": { "enum": ["in", "out"] } },
      "kinds": { "type": "array", "items": { "type": "string", "pattern": "^[a-z]+/[a-z*+.\\-]+$" } },
      "maxCount": { "type": "integer", "minimum": 1, "maximum": 8 },
      "maxBytesPerItem": { "type": "integer", "minimum": 1 },
      "semantics": { "type": "object", "additionalProperties": false,
        "properties": { "caption": { "type": "boolean" }, "attachmentUpload": { "type": "boolean" } } } } }
  ```
  `maxCount` ceiling is 8 to match the wire-law `communication.*.artifacts maxItems: 8` — wire stays fixed, capability only narrows.
- New schema `protocol/json-schema/event.context.fetched.v1.json`: object, `additionalProperties: false`, required `[fetchId, workId, responder, items]`; `fetchId`/`workId`/`responder` `$ref common.v1.json#/$defs/idToken`, `items` array `minItems 1 maxItems 32` of `{ uri (uri), content (object), truncated (boolean) }` (`additionalProperties: false`), optional `deadline` (`rfc3339DateTime`), `note` string ≤512. Events are not in the fail-closed command table (verified: `EnvelopeValidator` validates events by dataschema only), so no KnownEvents change.
- New fixture `protocol/conformance-fixtures/valid/event.context.fetched.json` + a `cases` entry in `protocol/conformance-fixtures/index.json` (`expect: "valid"`, `class: "event"`, `type: "org.facilitator.event.context.fetched.v1"`), and a history-shaped `valid/command.context.fetch.history.json` (contextRefs `["thread://telegram/123/456?limit=20"]`) mirroring the existing `command.context.fetch.json` case.
- Run `make protocol` (`protocol` target: `pnpm protocol:codegen` + `git diff --exit-code -- src/protocol/src/generated protocol/dotnet/Ofac.Protocol/Generated src/protocol/schemas`) — regenerates `src/protocol/src/generated/types.ts` and the C# generated types. TS `KNOWN_COMMAND_TYPES` is schema-derived (verified `src/protocol/src/validate.ts:53`), so it picks up nothing new (no new commands); no KnownCommands.cs change either.

### 1.2 Shared media law in `@ofac/adapter` (lives at `src/adapters/harnesses/adapter-sdk`)

New file `src/adapters/harnesses/adapter-sdk/src/media-law.ts`, exported from `src/index.ts`:
- `ensureDigest(bytes: Uint8Array, digest: string): string` — moved from `telegram/src/media.ts:182` (verbatim semantics: recompute sha256, mismatch throws).
- `fetchArtifactContent(client: { baseUrl, token, fetchImpl? }, artifactId): Promise<Uint8Array>` — moved from `telegram/src/media.ts:190`; keeps the link-plane GET (`GET {base}/api/link/v1/artifacts/{id}/content`, bearer link token, follows 302 via native fetch redirect law).
- `createMediaLedger(db: DatabaseHandle): { known(uniqueId): string|undefined; remember(uniqueId, artifactId, digest): void }` — the `files` table (`file_unique_id → artifactId`) dedup ledger, generalized from `telegram/src/chat-ledger.ts` (the table stays owned by the adapter's schema; the helper just speaks SQL against it). better-sqlite3 is already a dep of the package that ships `@ofac/provider-core` law; add `better-sqlite3` (catalog:) as a peer dep of adapter-sdk only if the handle is passed in — pass `DatabaseSync` handle in, no new dep.
- `ingestMedia(deps): Promise<IngestOutcome>` where
  ```ts
  deps = { bytes: Uint8Array; mediaType: string; uniqueId: string;
           maxBytes: number; enabled: boolean; ledger: MediaLedger;
           client: ArtifactClient; logger; provenance?: { comment?: string } }
  ```
  runs the law extracted from `telegram/src/media.ts` `MediaPipeline.processMessage`: dedup on `uniqueId` (digest-known ⇒ reuse artifactId, zero uploads) → size-cap ⇒ `{ kind: "skipped", reason: "size-cap"|"disabled" }` (never throws: "media failures log and shrink the ref list, they never fail the event") → `client.upload(...)` (existing `createArtifactClient` — request_upload → kernel-proxied PUT → confirm) → `{ kind: "stored", ref: ArtifactRef }` with `storageUrl` = the logical URI pattern `artifact://provider/<uniqueId>/<artifactId>` (GUID-free logical URI law preserved) and `kind` set from the class taxonomy (1.5's helper, see below).
- Telegram (`telegram/src/media.ts`, `chat-ledger.ts`, `handler.ts`): refactor internals onto these exports, **no behavior change** — `MediaPipeline` keeps its public surface (`processMessage`, `refsByMessage`, `ctx.mediaArtifactsFor`), `fetchArtifactContent` re-exported from the old module path so `handler.ts` imports keep compiling. `telegram/test/media.test.ts` must pass unmodified except import paths.

### 1.3 Outbound emitter (the missing hop) — kernel arm

Gate: `Providers:<name>:SendManifestArtifacts` — new `bool` init-property on `ProviderOptions` (`src/facilitator/Kernel/Integrations/ProviderOptions.cs`, default `false`, next to `AssignRules`), read per binding provider.

`src/facilitator/Kernel/Integrations/ProviderEventStore.cs`:
- `ReplyEnvelope` / `PostEnvelope` (:1645/:1653) gain an optional trailing param `JsonArray? artifacts = null`; `TextTail(text, buttons, artifacts)` puts `tail["artifacts"] = artifacts` when non-null. Every existing caller passes nothing (compile-clean cutover — optional param, no other edits needed at the ~6 call sites found by `grep -n "ReplyEnvelope(\|PostEnvelope("` in `ProviderEventStore.cs`, `EfCoordinationStore.cs`, `WorkflowRunner.cs`, `NotificationLedger.cs`).
- New `internal static JsonArray ArtifactRefs(IEnumerable<(ArtifactEntity row, string kind)> rows)` — builds wire `artifactRef` JSON (7 required fields from the entity + `kind`), the single emitter-side builder so every hop stamps refs the same way.

`src/facilitator/Kernel/Notifications/NotificationLedger.cs`:
- `NotificationRequest` record (:16) gains `JsonArray? Artifacts = null`; `EmitAsync` threads it into the `PostEnvelope`/`ReplyEnvelope` call at :140-142.

`src/facilitator/Kernel/Work/EfCoordinationStore.cs` — `EmitBindingOutcomeAsync` (:770), the completion-reply seam (verified: builds the per-binding reply/reaction/silent decision, then either the notification-ledger path or the direct `ProviderEventStore.ReplyEnvelope` path):
1. After `action` resolves to a reply (not `"silent"`, not `"react"`) and `providers.Find(binding.Provider)` yields the endpoint: when `registry`'s provider options have `SendManifestArtifacts` and `to == WorkState` terminal-completed, load the manifest: `db.ResultManifests.AsNoTracking().Where(m => m.WorkId == workId && m.State == "recorded")` ordered by `Revision` desc, first row ⇒ `OutputArtifactIds` (string[] of lowercase guids, verified on `ResultManifestEntity` :1091); load matching `ArtifactEntity` rows `State == "stored" && Verification == "verified" && TeamSlug == <work team>`.
2. Read the endpoint's declared facet: `db.LinkEnrollments` row for `binding`'s endpoint (`LinkEnrollmentEntity`, :629; `CapabilityManifest` jsonb column :643) → parse the `media` facet of the `communication.reply.v1` capability descriptor. Absent facet or no `"out"` direction ⇒ empty facet `{ directions: ["out"], kinds: [] }` (text-only, exactly what forgejo/gitlab/github/mattermost mean today).
3. New `src/facilitator/Kernel/Work/MediaReplyPolicy.cs` — `public static class MediaReplyPolicy` with
   `public static (List<(ArtifactEntity, string)> Attach, List<string> DegradeReasons) Select(IReadOnlyList<ArtifactEntity> candidates, MediaFacet facet, long maxBytesPerItemDefault, int maxCountDefault = 8)` —
   filter by facet `kinds` (MIME glob match: `image/*` style), cap to `min(facet.maxCount ?? 8, 8)`, drop oversize (`facet.maxBytesPerItem ?? maxBytesPerItemDefault`), reason strings verbatim: `"kind-not-allowed"`, `"count-cap"`, `"size-cap"`. `kind` stamped by `MediaReplyPolicy.KindOf(mediaType)`: `image/*` ⇒ `"result.image"`, else `"result.report"` (wire `kind` is open extensibility, verified on `common.v1.json#/$defs/artifactRef`).
4. Attach: `artifacts = ProviderEventStore.ArtifactRefs(attach)`, text unchanged (adapters own the caption law — Telegram already sends photo+caption). Degrade: for every dropped/unsupported candidate, append to the reply text a line `\n\nimage attached: {PublicBaseUrl}/api/artifacts/{artifactId}/content` when the new static option `OFAC:PublicBaseUrl` is set (new init-property on `InteractionOptions`, `src/facilitator/Kernel/Work/InteractionIngress.cs` — no public-base-URL config exists today, verified empty grep); when unset, degrade note is `attachment available: artifact {artifactId}` and the audit reason is `no-public-base-url`. Never silently drop.
5. Audit: one `AuditEventEntity` per completion reply carrying media (pattern: `ProviderEventStore`'s existing `AuditEvents.Add` calls), `Action = "media.reply"` / `"media.degrade"`, subject `work/{workId}`, detail JSON naming the facet verdict per artifact — "why did my image vanish" must be a queryable answer (`GET /api/admin/audit`).

Both reply paths get it: the ledger path passes `Artifacts` into `NotificationRequest`; the direct path passes `artifacts` into `ReplyEnvelope`. The deterministic reply text `"Work reached {to.Wire()}."` stays the base; degrade lines append to it.

### 1.4 Adapters declare themselves (no behavior change)

- `telegram/src/handler.ts` `communicationManifest(team)`: the `CMD_REPLY`/`CMD_POST` descriptors gain `media: { directions: ["in","out"], kinds: ["image/*","video/*","application/pdf","text/*","application/*"], maxCount: 8, maxBytesPerItem: 52428800, semantics: { caption: true, attachmentUpload: false } }` (50 MB = the documented `DOCUMENT_MAX_BYTES` outbound law).
- mattermost/forgejo/gitlab/github (+ connector variants) manifests gain `media: { directions: ["in","out"], kinds: [] }` in wave 1 (honest text-only declaration); wave 2 widens them.

### 1.5 Verification gate for the wave

`make test` (build + `dotnet test tests/Facilitator.Tests -c Release --no-build` + `pnpm -r test` + `dotnet run --project protocol/tools/Validate.Fixtures`) green. New kernel test `tests/Facilitator.Tests/MediaReplyTests.cs`: a completed work with a recorded manifest naming a stored `image/png` artifact + an endpoint whose enrollment manifest declares `out` + `image/*` ⇒ the outbox `communication.reply.v1` row's `data.artifacts[0].kind == "result.image"`; same scenario against a `kinds: []` facet ⇒ no `artifacts` key, reply text contains `attachment available: artifact`, audit row `media.degrade` with reason `kind-not-allowed`.

## Wave 2 — mattermost and forgejo media legs (equal depth)

Both reuse 1.2 law + 1.1 facet; each leg = shared law + declared facet + adapter tests + a kernel-side fixture (the wire already has `event.provider.mention.with-media.json` and `command.communication.reply.with-artifact.json` — no new protocol surface).

### 2.1 Mattermost (`src/adapters/communication/mattermost`)

- `mm-client.ts`: extend `MattermostApi` with `uploadFile(channelId: string, filename: string, bytes: Uint8Array, mediaType: string): Promise<{ id: string }>` (`POST /api/v4/uploads?channel_id=…` multipart; rides the existing `private request<T>(call, method, path, body)` seam — add a sibling `requestRaw` for binary bodies + FormData, same error-shaping as `MmError`), `downloadFile(fileId: string): Promise<Uint8Array>` (`GET /api/v4/files/{fileId}`), and a post-create variant taking `fileIds: string[]` (`POST /api/v4/posts` with `file_ids`).
- Inbound (`normalize.ts` + a new `media.ts` mirroring telegram's): a post carrying `file_infos` ⇒ for each file (cap: config `maxBytes`, new config key alongside existing config-loader law in `config.ts`): `downloadFile` → `ingestMedia` (ledger table `files (file_unique_id TEXT PRIMARY KEY, artifact_id TEXT, digest TEXT, created_at INTEGER)` added to the adapter's `post-ledger.ts` schema, `uniqueId` = Mattermost file id) → refs attached to the normalized mention event's `sourceRefs`.
- Outbound (`handler.ts` `case CMD_REPLY` :157 / `case CMD_POST` :188): read `data.artifacts ?? []`; when non-empty and under caps, `uploadFile` per ref (bytes via `fetchArtifactContent` from 1.2) then post with `file_ids`; when over cap or upload fails, append `attachment available: <storageUrl-or-link>` to the post text (mirror of the kernel degrade law, local caps read from the adapter's own facet declaration).
- Facet: `communicationManifest` gains `media: { directions: ["in","out"], kinds: ["image/*","application/pdf","text/*","application/*"], maxCount: 5, maxBytesPerItem: 52428800, semantics: { caption: false, attachmentUpload: true } }` (Mattermost has no caption concept — text + files).
- Tests: `test/media.test.ts` (fetchImpl stub of the uploads/files API: ingest dedup on second sighting, size-cap skip never fails the event, outbound reply posts with `file_ids`, degrade path). Follow `telegram/test/media.test.ts` structure (vitest; `pnpm -r test` runs it).

### 2.2 Forgejo (`src/adapters/communication/forgejo`)

- `forgejo-client.ts`: add to `ForgejoApi` `uploadIssueAttachment(repo: string, number: number, filename: string, bytes: Uint8Array, mediaType: string): Promise<{ id: number; url: string; download_url: string }>` (`POST /repos/{repo}/issues/{number}/attachments`; needs a FormData/raw-body sibling of `private request<T>`, same `ForgejoError` shaping) and `downloadAsset(url: string): Promise<Uint8Array>` (GET on an absolute URL with the same token header).
- Inbound (`normalize.ts` + new `media.ts`): webhook issue payload `attachments[]` ⇒ `downloadAsset(full_url)` → `ingestMedia` (uniqueId = attachment id; a `files` ledger table added to `comment-ledger.ts`'s sqlite schema) → `sourceRefs` on the mention envelope.
- Outbound (`handler.ts`): when a command (`communication.reply.v1` comment path / issue-comment leg) carries `data.artifacts` and the issue is resolvable, upload as issue attachment and append `![name](download_url)` markdown to the comment body (forgejo's comment markdown link is the native media semantic); no issue context (e.g. PR-review comment) ⇒ degrade to a public link line.
- Facet: `forgejoAdapterManifest` (`index.ts:97`) gains `media: { directions: ["in","out"], kinds: ["image/*","application/pdf","text/*"], maxCount: 8, maxBytesPerItem: 8388608, semantics: { caption: false, attachmentUpload: true } }`.
- Tests: `test/media.test.ts` mirroring 2.1 (fetchImpl stub: attachment upload folded into the comment, inbound refs carry `chat.attachment`, degrade when payload has no issue number).

## Wave 3 — `context.fetch` becomes real

### 3.1 Kernel-local handlers

New `src/facilitator/Kernel/Work/ContextFetchService.cs`:
- `public sealed class ContextFetchService(...)` with `Task<ContextFetchResult> ExpandAsync(IReadOnlyList<string> contextRefs, LinkPrincipal principal, Guid? workId, CancellationToken ct)`, resolving the URI classes:
  - `memory://<scope>?q=…` — delegate to the existing recall path (`src/facilitator/Kernel/Work/MemoryIngress.cs` — the same core the SDK's memory client hits; scope validation is already `MemoryEndpoints` law).
  - `artifact://…` — `IArtifactLedger.GetReadableAsync` metadata + brokered content URL `{PublicBaseUrl}/api/artifacts/{id}/content` (bytes stay in the store; refs + digests only, envelope-bounded law).
- Harness→kernel `context.fetch` on the link plane (`LinkIngress.cs`, the command-class router next to the memory recall/retain arms — verified today no `context.fetch` class exists there, the command is only in the fail-closed table): admit it, call `ExpandAsync`, return the items **inline** on the admission `Data` (`EnvelopeAck.Data` already carries handler data, verified `LinkDtos.cs:70` + `LinkHub.cs:133`) — no new synchronous event for the harness path.
- `thread://<provider>/<channel>/<root>?before=<id>&limit=N` is NOT kernel-local ⇒ returns `{ kind: "pending", fetchId }` (3.2). Budget law enforced centrally here: `contextRefs` ≤ 32, item content ≤ 32 KB, total answer ≤ 256 KB envelope reality — truncation recorded as `truncated: true` on the item, never silent.

### 3.2 `thread://` = routed to the source adapter

- Kernel→adapter: the command type `org.facilitator.command.context.fetch.v1` is already contract-legal in both directions (fail-closed table is direction-blind; `communication.reply` proves kernel→adapter commands). Kernel appends it to the source adapter's outbox (`RecipientEndpointId = "link.<endpointId>"`), one `uri` per command, `commandId = "context-fetch:<fetchId>"` (deterministic, ledger-idempotent like `communication-reply:<transitionEventId>`).
- Adapter answers **asynchronously** with the new `event.context.fetched.v1` (1.1) published on the same plane it already speaks (same publish seam telegram/mattermost use for normalized events). Kernel ingress handler: new class arm in `CloudEventIngress.cs`/`IngressProcessor.cs` — store the item set as an artifact (kind `context.history`, `retentionPolicy` per wave-5 policy, default `1d`), append `event.context.fetched.v1` verbatim to `facilitator.provider_events`-style audit, and resolve the pending fetch (3.3).
- Telegram answers `thread://` from the platform (`getChatHistory`, `limit` from the URI, capped 50): the ledger `messages` table stores only ids, not bodies (verified schema `chat-ledger.ts:28`), so a ledger-first window needs a body column the adapter doesn't have — answering from the platform bounded by `limit` is the honest v1; a `body` column on the ledger is a later optimization, not this plan. Unknown thread or platform error ⇒ item `{ uri, content: { error }, truncated: false }`, never a failed command.
- Deadline: the existing `deadline` field on `command.context.fetch.v1.json` is honored by the new sweeper behavior in the fetch-pending record (3.3): expired ⇒ answer with a `truncated` item carrying `{ error: "deadline" }`.

### 3.3 Pending-fetch record

No new table: reuse the routing-pending pattern. `RoutingSink` already parks a routing intent as `"pending"` with an audit basis and lets a later event requeue it (verified `RoutingSink.cs` pending path). `ContextBuilder` (wave 4) records pending thread items in the same intent-pending audit detail (JSON: `pendingFetches: [{uri, fetchId, deadline}]`); the 3.2 ingress arm, on a fetched answer, requeues the parked intent (same wake as the dependency-gate path: mark the outbox row available).

## Wave 4 — `context.build`: assembly as data, folded into dispatch

### 4.1 Authoring

The block lives on the route = `Providers:<name>:AssignRules[n]` (`src/facilitator/Kernel/Integrations/ProviderOptions.cs` `AssignRule`) plus a provider-level default `AssignContext`, mirroring how `AssignRules` already overrides provider defaults:

```jsonc
"context": [
  { "envelope": "required" },
  { "history": { "thread": "$event.reference", "last": 20, "order": "asc" } },
  { "media": { "from": "$event.data.sourceRefs", "content": "brokered", "maxBytes": 8388608 } },
  { "work": { "fields": ["objective", "state", "requirements"] } },
  { "timeline": { "last": 30 } },
  { "memory": { "recall": "$project.tags", "scopes": ["team:$work.team"], "limit": 6 } },
  { "budget": { "totalBytes": 32768, "onOverflow": "truncate-note" } }
]
```

C#: `public sealed class ContextSource { public string? Envelope {get;init;} … }` (one nullable property per closed key, `additionalProperties`-equivalent enforced by the validator below, not by the binder). `$…` refs resolve under the same template law as `emit.inputs` (copy the interpolation helper `WorkflowRunner` uses for `inputs`; rival pattern NOT to copy: the raw string `$"…"` templating in `NudgeSweeper`).

### 4.2 Validation at config load (fail-closed, ProviderRegistry precedent)

In `ProviderRegistry.Load` (fail-closed per-section law already established there): reject `context` blocks with unknown keys (`context-key-unknown`), `memory.scopes` selectors outside the rule/provider team (`memory-scope-outside-team`), `history.last`/`timeline.last` > 100 (`context-budget-exceeded`), `budget.totalBytes` outside 1 KB–256 KB. Composition error names the section, like the existing addressing failures.

### 4.3 Dispatch-time fold (arm path)

`RoutingSink.DeliverAsync`: after the candidate is chosen and before `ProviderEnvelopes.Assign` (:368), when the chosen rule/provider carries a `context` block:
1. `ContextBuilder.BuildAsync(db, block, intent, work, principal, ct)` (new, same file-folder as 3.1: `src/facilitator/Kernel/Work/ContextBuilder.cs`) — expands local sources (envelope, work fields, timeline from `WorkEvents`, memory via `ContextFetchService`), calls `ContextFetchService.ExpandAsync` for the rest; a pending `thread://` ⇒ park as 3.3 and return "not-ready" (intent stays pending, requeues like the dependency gate).
2. Ready ⇒ the context document `{"schema": "context.dispatch.v1", "sources": [...in block order, each with the resolved content], "digest": "sha256:…"}`; store it as an artifact (kind `context.dispatch`, `retentionPolicy` default `7d` — derived, disposable) bound to the assignment via the `IArtifactLedger` allocation path (same law the link-plane request-upload uses, `Origin = "kernel"`).
3. Enrich `extraInputs` (already flowing into `ProviderEnvelopes.Assign`'s `inputs`): `inputs["context"] = { "artifactId": …, "digest": …, "document": <doc when under budget, else refs-only> }`. `command.work.assign.v1.inputs` is `"type": "object"` with no closed keys (verified) ⇒ **no protocol change**; harnesses that don't know `context` ignore it. Routes without a block get today's envelope verbatim, forever (parity law: no block ⇒ zero behavior change).
4. Audit: `ContextBuilder` emits an `event.memory.used.v1` per recalled entry (existing schema: `memoryId/consumer/justification`; justification = the builder block's index path, e.g. `context[5]`) and one audit row `context.dispatch` naming doc digest + source count.
5. Workflow-plane visibility: **fold only** — no new palette entry. Plane dispatches reach the same builder when a gallery/scoped definition emits a routing intent; `invoke` `inputs` templating already reaches `steps.*.output.context`. A dedicated `context` palette step is explicitly out of scope for this plan (one line, at the palette file: `protocol/json-schema/workflow-definition.v1.json` + `WorkflowDefinitionValidator.cs` untouched).

### 4.4 SDK surface for harnesses

`src/adapters/harnesses/adapter-sdk`: `types.ts` `RunContext` gains `context?: ContextDocument` (parsed from `inputs.context.document` when present — `adapter.ts` assign-command parse site already reads `inputs`; one-line pass-through) and a `fetchContext(uris: string[]): Promise<ContextItem[]>` that sends `command.context.fetch.v1` and reads the inline `Data` from the ack (3.1). Conformance kit (`testing.ts`): assert a kit run with a context-bearing assign exposes `ctx.context.sources` (guarded by existing `interactionLeg`-style opt-in flag `contextLeg?: boolean`, default off so no existing instantiation changes).

## Wave 5 — policy, taxonomy persistence, retention

### 5.1 `media-policy` admin resource

`src/facilitator/Kernel/Admin/AdminConfigStore.cs`: add `"media-policy"` to `Resources` (:70-83) and a validator to the `[…]= payload => FirstError(…)` dictionary (:219+), payload shape:
```jsonc
{ "defaults": { "maxBytesPerItem": 8388608, "maxCount": 8,
                "retentionDays": { "chat.attachment": 30, "context.dispatch": 7, "context.history": 1 } },
  "teams": [ { "team": "team-alpha",
               "allowKinds": ["image/*", "application/pdf"],
               "forbidKinds": ["application/*"],
               "history": { "toHarnessMaxMessages": 20, "toForeignTeam": false } } ] }
```
Readers: `MediaReplyPolicy.Select` (wave 1) takes the resolved defaults/allow-list per work team (absent resource ⇒ today's wave-1 behavior: facet-only, defaults 8 MB / 8 items), `ContextBuilder` consults `teams[].history` before answering `thread://`/memory forwards, and a foreign-team destination check (`toForeignTeam: false` ⇒ refuse + audit `media-forwarding-denied`, naming the rule — attribution law). Federation line: a team's history/media never leaves its plane unless its policy entry says so.

### 5.2 Kind persisted, retention defaults

- `command.artifact.request_upload.v1.json`: optional `"kind": { "type": "string", "minLength": 1, "maxLength": 64 }` (same law as the wire `artifactRef.kind`). Re-run `make protocol`.
- `ArtifactEntity` (Entities.cs:1011): `public string? Kind { get; set; }`; EF migration `AddArtifactKind` (`make db-update` flow, `dotnet ef migrations add AddArtifactKind -p src/facilitator -s src/facilitator`), recorded on the request_upload arm. `ArtifactLedger` echo: the confirm/read views carry `kind` so `ArtifactRefs` (1.3) reads stored kind first, class-fallback second.
- `RetentionGcService` needs **no logic change**: the `^[1-9][0-9]{0,3}d$` policy sweep already exists (:221) and manifest/live-work pins already protect referenced blobs (:232-252). The only change: allocation paths stamp the 5.1 `retentionDays` lookup as `RetentionPolicy` (`"{n}d"`) instead of always `permanent` for new `chat.attachment` / `context.*` kinds.
- `context.fetch` history answers and dispatch context docs use those defaults ⇒ GC honesty is inherited, not re-invented.

## Critical files & anchors

| File | Anchor | Why |
|---|---|---|
| `src/facilitator/Kernel/Work/EfCoordinationStore.cs` | `AppendProviderCompletionReplyAsync` :724 / `EmitBindingOutcomeAsync` :770 | the only completion-reply seam; both the ledger and direct paths must carry artifacts + degrade + audit |
| `src/facilitator/Kernel/Integrations/ProviderEventStore.cs` | `ReplyEnvelope`/`PostEnvelope`/`TextTail` :1645-1679 | the artifact tail enters the wire here; all emitters flow through it |
| `src/facilitator/Kernel/Routing/RoutingSink.cs` | assign-envelope region :360-405 + pending path :116 | the dispatch fold point and the requeue precedent for pending thread fetches |
| `src/adapters/communication/telegram/src/media.ts` + `handler.ts` | `MediaPipeline`/`ensureDigest` :182/`fetchArtifactContent` :190; caption law :104-108, send :366, `communicationManifest` :111 | the prototype being extracted — move, don't rewrite; Telegram behavior must be byte-identical after 1.2 |
| `protocol/json-schema/capability-manifest.v1.json` | capability descriptor (items, `additionalProperties: false`) | the facet lands here; `make protocol` regenerates both language bindings |

## Verification

1. **Protocol**: `make protocol && make test` — includes `Validate.Fixtures` over the new `event.context.fetched.json` and history-fetch fixtures (each new fixture is a new-behavior check: a fetch envelope that could not exist before).
2. **Outbound hop (wave 1, kernel)**: `dotnet test tests/Facilitator.Tests -c Release --no-build --filter MediaReplyTests` — recorded manifest with `image/png` + out/image facet endpoint ⇒ outbox `communication.reply.v1` row contains `data.artifacts[0].kind == "result.image"` and text has no degrade line; `kinds: []` facet ⇒ no `artifacts`, text contains `attachment available: artifact`, audit `media.degrade` / `kind-not-allowed`. (Concrete input→observable output, both success and honest-degrade legs.)
3. **Adapter legs (wave 2)**: `pnpm -r test` — mattermost/forgejo `media.test.ts` with `fetchImpl` stubs assert: inbound file ⇒ `sourceRefs[0].digest == sha256(bytes)` and a second sighting of the same file id costs zero uploads (ledger dedup); outbound reply with two refs posts `file_ids`/attachment markdown; a 60 MB inbound file skips with a logged skip and the event still publishes.
4. **Fetch + build (waves 3-4, kernel)**: new `tests/Facilitator.Tests/ContextFetchTests.cs` + `ContextBuildTests.cs` — `contextRefs: ["artifact://<stored-id>"]` answer carries the brokered URL + digest and no bytes; a route block `[envelope, work]` ⇒ the `work.assign` row's `inputs.context.document.sources[1].fields.objective` equals the work objective and a `context.dispatch` artifact row exists with `RetentionPolicy == "7d"`; a `thread://` block ⇒ intent parks `pendingFetches`, the fabricated `event.context.fetched.v1` ingress requeues it and the second pass emits the assign with the history source.
5. **Policy/GC (wave 5)**: `PATCH /api/admin/config/media-policy` with `forbidKinds: ["image/*"]` for the test team ⇒ the same wave-1 scenario now degrades (reply text link, audit `kind-not-allowed`) — policy demonstrably consulted, not decorative; a `context.history` artifact aged past `1d` is collected by a forced `RunOnceAsync` sweep while a still-pinned manifest output is not.
6. **End-to-end (worked example)**: `make example-slim-up` then post (or walk) an image mention → harness completes with an artifact → the bound Telegram conversation shows photo + caption (`sendPhoto` with `caption == reply text`). Degrade leg: disable `SendManifestArtifacts` on a second provider ⇒ reply carries the link line and one `media.reply`/`media.degrade` audit line per media act.

## Assumptions & contingencies

- Manifests may predate the facet: every facet read defaults to text-only, so enabling `SendManifestArtifacts` on an endpoint whose stored enrollment manifest predates 1.1 attaches nothing (honest, audited). If operators need media pre-re-enroll, the facet default flips per provider later — no schema change.
- If `pnpm protocol:codegen` output for the manifest facet unions proves awkward for the C# side (descriptor with `additionalProperties: false` + nested semantics), fall back to a `Dictionary<string, JsonElement>` custom converter on the generated type rather than editing the generator; the JSON shape stays 1.1's verbatim either way.
- If the `ef` migration for `ArtifactEntity.Kind` drifts from `FacilitatorDbContextModelSnapshot.cs` (unverified: two migrations landed the same day as this plan), regenerate with `dotnet ef migrations add AddArtifactKind` fresh — never hand-edit the snapshot.
- Telegram outbound album/voice semantics and thumbnail derivation are out: verbatim-only store law (one artifact per real file, derivations would be separate artifacts, not this plan).
- `communication.*.artifacts maxItems: 8` stays wire-fixed (capability narrows, never widens).

## Wave 6 — provider parity (github + gitlab full legs; context arms everywhere)

Every provider speaks BOTH planes at equal depth.

- 6.1 GitHub (`src/adapters/communication/github`): media leg on the shared law (inbound = `![…](url)` extraction from webhook bodies, uniqueId = sha256(url)[0..16]; outbound = issue-image upload on the uploads host derived from the configured base, `![name](asset_url)` folded into the comment, `attachment available:` degrade) + `context.fetch` arm (`thread://forgejo`-twin `thread://github/<repo>/<number>`, window via issue-comments API). Facet: `image/* application/pdf text/*`, count 5, 5 MB (GitHub's upload cap), `attachmentUpload: true`.
- 6.2 GitLab (`src/adapters/communication/gitlab`): same shape — `/projects/{id}/uploads` multipart outbound (GitLab's own `markdown` line folded in), `/uploads/` markdown inbound, `thread://gitlab/<repo>/<iid>` via issue-notes API. Facet: count 10, 10 MB (GitLab instance defaults).
- 6.3 Forgejo + 6.4 Mattermost: the `context.fetch` answer arm (media legs already landed in wave 2); forgejo window = issue comments, mattermost = `GET /posts/{root}/thread`.
- Telegram already complete (waves 1 + 3). Facet law unchanged: capability narrows, wire stays ≤8.
- Verification: per-package `test|typecheck|build` + context/media test files (≥10 each new), then the repo gate.
