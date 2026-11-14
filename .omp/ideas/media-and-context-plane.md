# Media and constructed context: first-class bytes between any source and any destination

**Status:** idea, pre-plan. Everything cited below was read from the tree
(2026-11-14); anchors are file paths, not memory.
**Deployment constraint that shapes everything:** the only public,
internet-facing interface we support is the **Facilitator API**. S3 may
exist when deployed somewhere that has it, but no user may be required
to expose additional endpoints or S3-like behavior (presigned URLs) for
any leg to work. Every byte move must have a kernel-proxied path
through the facilitator API; presigned redirects are an optimization
where the operator opted in, never a requirement.

## 1. Why

The kernel moves *text* first-classly and *bytes* second-classly. The
contract and the broker are actually further along than the product
feels:

- `common.v1.json#/$defs/artifactRef` is a complete, digest-verified
  media reference (`artifactId/mediaType/size/digest/storageUrl/createdBy/createdAt`,
  `kind` free-tag) and `event.provider.mention.v1` already carries
  `sourceRefs: artifactRef[]` ("Inbound media attached to this message,
  already stored as kernel artifacts; carried verbatim into
  provider_events.body").
- `command.communication.post.v1` / `command.communication.reply.v1`
  already carry `artifacts: artifactRef[]` (≤8) — "Outbound media the

 adapter should post alongside the reply text (the kernel emits these
 for harness-produced artifacts; the adapter consumes them)."
- The broker exists and is portable by design: `ArtifactEndpoints.cs`
  hands out tickets whose `uploadUrl` (presigned) is "absent, not null"
  when the store can't mint one — the fallback is
  `PUT /api/artifacts/{id}/content` through the kernel; retrieval is
  `GET /api/artifacts/{id}/content`, which **streams the bytes itself**
  when the store isn't an `IStoreSignedUrlSource` (the
  `FileSystemArtifactStore` path), and 302s to a presigned GET when it
  is. `FileSystemArtifactStore` + `S3ArtifactStore` +
  `RetentionGcService` are all real (`Kernel/Shared/Storage/`).
- The Telegram adapter has the **only complete round trip**:
  `telegram/src/media.ts` runs download → sha256 → upload-via-link-plane
  → `ArtifactRef` construction, with a `files` ledger deduping on
  `file_unique_id` ("an image forwarded twice costs one upload and one
  ref"), size-cap skip rules that never fail the event, and the GUID-free
  logical URI `artifact://provider/<fileUniqueId>/<artifactId>`.
  Outbound, `handler.ts` consumes `data.artifacts` with the
  sendPhoto/sendDocument caption law and `fetchArtifactContent`
  (link-plane GET, follows the 302 when the kernel redirects).

What is *missing* is the middle and the rest of the fan-out:

1. **Nobody constructs outbound media.** `grep artifacts` across the
   kernel's command emitters is empty: no arm, no plane step, no
   ResultIngress path ever populates `communication.post.artifacts`.
   The harness's `result-manifest.v1` can list artifacts, and the
   fixture `event.assignment.completed.v1` carries `resultManifest` —
   but the "reply on Telegram with the new image **and** the text" hop
   does not exist as law. The reply schema *describes* it ("the kernel
   emits these for harness-produced artifacts"); the emitter is absent.
2. **One adapter of many.** forgejo, gitlab, github, mattermost (and
   every *-connector) have zero media handling — their event schemas
   carry `sourceRefs` in the shared `provider-event` family, but no
   forgejo adapter uploads an issue's PNG, and no forgejo comment path
   posts one back. Telegram is the prototype nobody extracted.
3. **Context is whatever the envelope happens to contain.** A harness
   gets one envelope. `command.context.fetch.v1` is in
   `KnownCommands.cs` and its schema takes `contextRefs: uri[]` — but
   the only sanctioned consumer today is the harness SDK's *memory*
   client (`adapter-sdk/README.md:108`); there is no kernel-side
   expansion of the interesting URIs (thread history, artifact bytes,
   timeline), no fetch-more semantics, and no way for an operator to
   say "this route needs the last 20 messages and the file posted
   yesterday" — that knowledge lives in each arm's C#.

The idea: promote what Telegram already does privately into a **media
plane** (adapter capability + policy + shared law), and give routes a
declarative **context builder** so the facilitator assembles
"everything this communication needs" before the harness sees it —
both as data riding the mechanisms we already have (broker, outbox,
link plane, workflow palette), not new subsystems.

## 2. The worked example (the round trip to design around)

A user posts an image + "look at this screenshot" in a Telegram group;
the routed harness analyzes it and answers with text **plus** an
annotated image; the reply must carry both, on every channel, degrading
honestly where it can't.

1. **Inbound (exists, per-adapter):** adapter sees
   `message.photo` → largest variant → `getFile` → download →
   digest → `POST /artifacts/request-upload` (link-token) →
   `PUT …/content` (kernel-proxied bytes, no S3 dependency) →
   `confirm` → `sourceRefs=[artifactRef]` on the `provider.mention`
   envelope before publish. Deduped via the adapter's `files` ledger.
   *This is `telegram/src/media.ts` verbatim, today.*
2. **Route + construct (new):** routing creates the Work as today;
   the route's `context.build` block (§5) is expanded by the arm/plane
   at dispatch time: envelope + configured history (fetch-more via the
   source adapter, §4) + `sourceRefs` (+ optionally brokered content
   URLs) + work/project metadata + memory recall. The dispatch envelope
   the harness receives is a **context document**, not a bare event —
   refs and digests only; bytes stay in the store.
3. **Harness produces (exists):** uploads its output image via the
   link plane (same ticket law, workId/assignmentId bound), files a
   `result.manifest` whose `artifacts[]` include the new image and the
   text report.
4. **Outbound media reply (new — the missing hop):** the completion
   path (arm today, a flow leg in the target shape) maps manifest
   artifacts → `communication.reply.v1.artifacts`, respecting the
   target channel's **media capability** (§3): Telegram gets
   `sendPhoto` (caption = the text, photo = the new image); a
   text-only channel gets the text plus a brokered link (`/api/artifacts/{id}/content`
   — the only public interface, working by construction); a forgejo
   comment embeds the markdown link and posts the upload as an issue
   attachment.
5. **Degrade honestly:** the capability descriptor of the endpoint
   (link manifest, §3) declares `media: {kinds, maxCount, maxBytes}`;
   the emitter never silently drops — when it degrades, the text says
   so ("image attached: <link>"), and the act is audited like every
   other media act.

Every hop above is either existing law (1, 3) or an emitter + policy +
one fetch handler (2, 4). No new store, no new transport, no new
endpoint family.

## 3. Media plane: capability, taxonomy, policy

**A capability, declared, not assumed.** The link enrollment's
`capability-manifest.v1` gains a `media` facet per endpoint:
`{ directions: [in|out], kinds: [image/*, application/pdf, ...],
maxCount, maxBytesPerItem, semantics: {caption: true|false,
attachmentUpload: true|false} }`. Adapters that predate it declare
`{directions: []}` (text-only), which is exactly what forgejo/gitlab/
github/mattermost mean today (verified: no media code in any of their
`src/`). The capability drives construction, emission, and degradation;
the UI and docs read the same declaration instead of prose.

**Taxonomy: MIME + policy, not a new type system.** `mediaType` is
already free-form in `artifactRef`; the plane adds a closed *class* tag
on the artifact `kind` namespace (`chat.attachment`,
`result.report`, `result.image`, … — the `kind` field exists on the
wire refs already) plus policy: per-team and per-endpoint size/count
caps (Telegram's cap pattern — `media.ts` step 3 — generalized to
config), and a per-route allow-list of MIME classes ("PDFs yes,
executables never").

**Laws to preserve, unchanged:**
- content-addressed digests, verified at store **and** read
  (`ArtifactLedger` DigestMismatch posture; the ETag on
  `…/content` is the digest itself);
- brokered, time-boxed access: harnesses and adapters reach bytes only
  through kernel routes with their own plane token (link bearer /
  work-scoped ticket), never ambient credentials;
- **refs ride the wire, bytes live in the store** — envelopes stay
  bounded; the 32 KB envelope reality never changes because of a
  40 MB video;
- idempotent uploads: dedup on content identity (the Telegram
  `files` ledger pattern is right; extract it to shared adapter law —
  digest-known ⇒ reuse artifactId, one upload);
- audit on every media act (request-upload/confirm/read already
  speak audit; a media *reply* and a media *skip/degrade* must land a
  line each — "why did my image vanish" must be a queryable answer).

**Per-adapter inventory (verified today):**

| adapter | in | out | notes |
| --- | --- | --- | --- |
| telegram | ✅ full pipeline (`media.ts`) | ✅ sendPhoto/sendDocument caption law (`handler.ts:104`) | the prototype; extract, don't rewrite |
| mattermost | ✗ | ✗ | platform supports file API; nothing implemented |
| forgejo / forgejo-connector | ✗ | ✗ | issue-attachment API exists upstream; comment markdown links are the cheap first leg |
| gitlab / gitlab-connector | ✗ | ✗ | same shape as forgejo |
| github / github-connector | ✗ | ✗ | same shape |

## 4. History and fetch-more

`command.context.fetch.v1` exists (schema: `workId`, `targetId`,
`actorEvidenceRef`, `deadline`, `contextRefs: uri[]`) and is in
`KnownCommands`, but the sanctioned consumer is only the SDK's memory
client (`adapter-sdk/README.md:108`). What it **can** do today: prove
the fetch-ask shape and carry memory intent. What it **cannot**:
expand history/artifact URIs, paginate, or time-box a source call.
Design:

- **Kernel-side expansion with source round-trips.** `context.fetch`
  becomes a real arm/handler: each `contextRef` URI resolves to a
  *source handler* — `memory://<scope>?q=…` (today's path),
  `artifact://…` (broker metadata + optionally a short-lived
  content route), `thread://<provider>/<channel>/<root>?before=<msg>&limit=N`
  (**routed to the source adapter** over the link plane — the
  adapter's own credentials touch the platform; the kernel proxies,
  never bypassing the federation boundary).
- **Adapter-side history ledger first.** Telegram's `chat-ledger.ts`
  already persists `messages`/`topics`/`kv`; a bounded
  `history.get` on the adapter (its own ledger + `getChatHistory`)
  covers "last 20 messages of this thread" without a platform round
  trip for the common window, falling back to a platform call for
  fetch-more. The ledger becomes the pattern (§3) for other adapters
  as their media legs land.
- **Budget and pagination are the caller's problem in data:** the
  builder block (§5) declares counts and byte budgets; the expansion
  enforces them (truncation recorded, never silent).
- **Async shape:** history fetch is a source round trip → the fetch
  answers as a command *response* (existing command/response law), and
  context construction is either inline (arm path: one hop, deadline
  bound) or a flow step (§5, plane path) — never a hidden synchronous
  platform call inside routing.

## 5. `context.build`: assembly as data

The workflow plane's closed-palette law already says "rich behavior,
declared shape". Context assembly is the same move — a
definition-level (or route-level, on the arm path) block, YAML all the
way down:

```yaml
context:
  build:
    - envelope: required
    - history: { thread: $event.reference, last: 20, order: asc }
    - media: { from: [$event.data.sourceRefs], content: brokered, maxBytes: 8388608 }
    - work: { fields: [objective, state, requirements] }
    - timeline: { last: 30 }
    - memory: { recall: "$project.tags", scopes: [team:$work.team, project:$work.projectId], limit: 6 }
    - budget: { totalBytes: 32768, onOverflow: truncate-note }
```

- **Validator-law:** closed keys, refs resolved against the same
  template law as `emit.inputs`, `memory.scoped` enforced at
  construction (scope selectors must be within the actor's readable
  scopes), `event.memory.used` appended per recalled entry
  (existing schema: `memoryId/consumer/justification` — the
  justification is the builder block's own path).
- **Where it runs:** the dispatch executor (arm path today) reads the
  route's block, expands sources it can answer locally (envelope,
  work, timeline, memory), and issues one batched `context.fetch` for
  source-side items (history, brokered content), then assembles the
  dispatch context document. In the plane, Phase-6's `invoke`
  `inputs` already template over context — a `context` step (or
  an executor fold into dispatch) keeps the palette honest:
  `context { build: <block> }` would be the nineteenth palette
  entry; decide in planning whether it's a step or a dispatch-time
  fold (lean: **fold** — every dispatch gets a context; steps order
  time, assembly doesn't need to).
- **Determinism + audit:** the constructed context document is
  digest-stamped and stored as an artifact (kind `context.dispatch`)
  attached to the assignment — "what exactly did the harness see"
  becomes evidence, not folklore.
- **Arm-path parity:** parity-law again — the strangler pattern this
  repo knows: build the block behind the arm first, shadow, swap.
  Routes without a block get today's envelope verbatim, forever.

## 6. Privacy, scope, federation

- History and memory are the two leak vectors; both already have
  plane-law answers to extend rather than replace: memory scopes
  (`MemoryEndpoints` scope validation) and per-team link planes bound
  what any construction can reach.
- **Forwarding is a policy, per team, per destination kind:**
  a `media-forwarding` policy entry (the policy-file matcher law —
  same shape operators already write): "team-alpha allows
  `history.thread→harness` up to 20 messages; forbids
  `artifact.kind=chat.attachment` forwarding to team-beta endpoints".
  Construction consults it before crossing a team boundary; federation
  law restated: **a team's history/media never leaves its plane
  unless that team's policy says so, and the audit line names the rule**
  (same attribution law as consent tickets).
- Retention: constructed context artifacts ride the existing
  retention-GC (`RetentionGcService`, `Storage:Gc`), with a shorter
  default than user uploads (context is derived, disposable).

## 7. What is explicitly NOT new

- No second blob store: broker only (`IArtifactStore`, both backends,
  GC included).
- No new wire transport: media refs ride existing commands/events;
  the fetch uses the existing command/response law over the link
  plane.
- No presigned-requirement: `uploadUrl`/302-GET stay conditional
  optimizations (`IStoreSignedUrlSource`); the portable path (kernel
  PUT/stream GET) is the contract. Byte-size caps exist precisely
  because the proxied path is the honest default — cap enforcement is
  per-endpoint policy (§3), stated at request-upload time, not a
  surprise at PUT time.
- No new authoring surface: `context.build` is YAML in the same
  definition document the workflow plane already validates, stores,
  versions, and renders.

## 8. Wave sketch

1. **Extract the prototype** — lift `telegram/src/media.ts` + the
   `files` dedup ledger + caption law into `@ofac/adapter` shared law;
   add the `media` facet to capability manifests; Telegram declares
   itself (no behavior change). Wire the **outbound hop**: manifest
   artifacts → `communication.reply.artifacts` for the harness-completion
   path (arm, behind config), with capability-aware degradation +
   audit. This closes the worked example end-to-end, still one channel.
2. **Second + third media legs** — mattermost (platform file API,
   chat semantics closest to Telegram) then forgejo (attachment upload
   + markdown-link degradation). Each leg: shared law + declared
   capability + a conformance fixture (`*.with-media.json` family grows
   per provider) + a walk.
3. **Fetch expansion** — `context.fetch` handler with `memory://`
   (delegate), `artifact://` (broker), and `thread://` (link-plane
   adapter call, Telegram history ledger first); bounded, paginated,
   audited.
4. **`context.build`** — schema block + validator + dispatch-time
   fold + context artifact; one route shadow-compared against
   today's envelope before any arm change. Then a flow-visible shape.
5. **Policy + GC** — media-forwarding policy entries, per-class caps,
   context-artifact retention defaults.

## 9. Open questions

- **Store verbatim vs. derive** (thumbnails, PDF→text): lean hard to
  *verbatim only* (digest law makes derivation tempting but every
  derivative is a second artifact with its own lifecycle); if
  derivations are wanted, they're separate artifacts + `work.linked`
  style relations, produced by an arm/harness, never by the broker.
- **Outbound emitter identity:** who composes the media reply —
  ResultIngress extension, a kernel-side "notice builder", or the
  first media flow leg (`emit` with `inputs` templating manifest
  artifacts — the plane may already be enough: `emit
  {command: …communication.reply, inputs: {artifacts:
  $steps.complete.output.manifest.artifacts}}`)? Lean: prove it as a
  **flow leg** (no new mechanism), keep an arm shim only where parity
  demands.
- History fetch **cost ceilings**: per-team budgets on source
  round-trips, and whether the adapter ledger or the kernel caches
  windows (cache invalidation vs. staleness honesty).
- **Media GC/retention policy**: user-posted attachments today live
  under the adapter's enrollment team with whatever retention the
  row carries; is there a per-kind retention policy surface, or does
  `kind` + existing per-team retention suffice?
- **Which adapter first after Telegram** — mattermost (symmetry) vs.
  forgejo (bigger payoff for code-review-with-screenshot flows).
- `maxCount ≤ 8` is currently a wire-law constant
  (`communication.post.artifacts maxItems`): keep wire-fixed (simple,
  honest) or relax into capability?
- Channel semantics beyond photo/file: voice notes (Telegram
  `sendVoice` vs `sendDocument`), Telegram-only albums (grouping N
  photos as one message), and whether the capability facet needs a
  `groups: albums` flag or whether the emitter just sends N captions.

## 10. Research anchors (verified 2026-11-14)

- `protocol/json-schema/common.v1.json` — `artifactRef` (7 required
  fields), `evidenceRef`.
- `protocol/json-schema/event.provider.mention.v1.json` —
  `sourceRefs` + its verbatim law comment;
  `protocol/conformance-fixtures/valid/event.provider.mention.with-media.json`
  — the full inbound shape incl. `kind: chat.attachment` and
  `artifact://provider/…` storageUri.
- `command.communication.post.v1.json` / `…reply.v1.json` —
  `artifacts: artifactRef[] (≤8)` + the "kernel emits / adapter
  consumes" comment that is currently a promise without an emitter.
- `command.context.fetch.v1.json` (+ `KnownCommands.cs:20`) — the
  fetch ask shape; only consumer today: `adapter-sdk/README.md:108`
  (memory client).
- `command.memory.recall.v1.json` / `retain.v1.json`,
  `event.memory.used.v1.json`, `Kernel/Work/MemoryEndpoints.cs`,
  `MemoryIngress.cs` — recall/retain/usage law, scope validation.
- `Kernel/Shared/Storage/`: `ArtifactLedger.cs` (ticket law:
  `UploadLocation` + conditional `UploadUrl`, allocation TTL,
  digest-mismatch posture), `IArtifactStore.cs`,
  `FileSystemArtifactStore.cs`, `S3ArtifactStore.cs`,
  `IStoreSignedUrlSource.cs`, `RetentionGcService.cs`,
  `ArtifactOptions.cs` (`Storage:Gc`).
- `Kernel/Work/ArtifactEndpoints.cs` — request-upload /
  `PUT …/content` / `GET …/content` (digest ETag; 302 only when the
  store implements `IStoreSignedUrlSource`) / confirm;
  `ArtifactIngress.cs` — the arm shared by both planes.
- `src/adapters/communication/telegram/src/media.ts` (inbound
  pipeline + dedup + cap-skip + ref construction law +
  `fetchArtifactContent`), `chat-ledger.ts` (`files` ledger,
  `messages`/`topics` — history seed), `handler.ts:104,322-366`
  (outbound caption/sendPhoto/sendDocument law consuming
  `data.artifacts`).
- Empty greps that are themselves evidence: no media code in
  `src/adapters/communication/{forgejo,gitlab,github,mattermost}/src`;
  no kernel emitter fills `communication.*.artifacts`.
- Workflow plane anchors: `protocol/json-schema/workflow-definition.v1.json`
  (closed palette, `emit.inputs` refs, `join`/`memory` stored-not-executed),
  `.omp/ideas/embedded-workflow-engine.md` (parity + strangler law this
  idea inherits).
