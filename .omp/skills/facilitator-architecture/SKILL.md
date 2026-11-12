---
name: facilitator-architecture
description: Domain model, ownership boundaries, state machines, durable coordination (Postgres outbox/inbox, SKIP LOCKED leases, session placement), and design-gate questions for the Open Facilitator Framework.
---

> **Status:** the transactional kernel is IMPLEMENTED and the Link server is IMPLEMENTED — see "Kernel (as-built)" and "Link server (as-built)" below. The approval lifecycle is IMPLEMENTED, and so is its decided-push + Link-plane consume wire delta. The artifact store is IMPLEMENTED with its s3 backend, signed URLs, external screen, and retention GC — see "Artifacts". Result manifests are the kernel's completion truth — see "Result manifests". The memory broker is IMPLEMENTED (`postgres-fts`) — see "Memory broker" below. The AI plane (AiDeck: decode, decision ladder, admin chat) is IMPLEMENTED — see "AI plane (AiDeck)" below. Update this skill the moment a slice lands so implemented reality replaces the older design notes.

# Facilitator Architecture

Normative source: `docs/specs/open_facilitator_framework_architecture.md`. Read the relevant chapter before changing a boundary — component ownership is prescriptive, not stylistic.

## Kernel (as-built)

Kernel code lives in `src/facilitator` (single deployable), grouped by feature: `Kernel/Work` (lifecycle + its coordination store), `Kernel/Link` (workspace-link server), `Kernel/Ai` (AiDeck — AI plane, below), `Kernel/Shared` (Auth, Identity, Dispatch, Persistence, CLI, metrics — not a feature). Namespace root `Facilitator.Kernel.*` follows folders. Composition: `KernelServiceCollection` exposes one extension method per folder (`AddSharedKernel`/`AddIdentityFeature`/`AddWorkFeature`/`AddLinkFeature`/`AddAiFeature`); new services register in their folder's method. Raw SQL lives only inside `Kernel/Shared/Persistence` classes and only for advisory locks, `pg_notify`/`LISTEN`, `FOR UPDATE SKIP LOCKED` claims, and version-fenced UPDATEs; no endpoint lambda injects a DbContext.

- **Schema** (Postgres `facilitator` schema, EF Core migrations `InitialCreate` + `AddLinkServer` + `AddIdentityFoundation` + `AddWorkloadIdentity` + `AddApprovalLifecycle` + `AddArtifactStore` + `AddArtifactGcTimelineKind` + `AddResultManifests` + `AddMemoryBroker` + `AddAdminConfig` + `provider_identities` + `AddRoutingDecisions` + `AddDagNudges` + `AddAiDecoderChat` + `AddInteractionTimelineKinds` + `AddProviderAttachmentTimelineKind` + `AddTeamRosterAndScopedSwitches`, auto-applied on startup behind `pg_advisory_lock(420001)`): `teams`, `team_members`, `team_settings`, `projects`, `works`, `work_events`, `inbox`, `outbox`, `dead_letters`, `provider_events` (kernel core); `link_enrollments` and `link_deliveries` (Link server, below); `audit` and `identity_roles` (identity, below); `evidence` + `link_enrollments.role/-accepts-delegated/-workload-*` (workload identity, below); `approvals` is the lifecycle authority (approvals, below); `artifacts` (Artifacts, below); `result_manifests` (Result manifests, below); `memory_scopes`/`memory_entries`/`memory_shares`/`memory_usage` (Memory broker, below); `admin_config`/`admin_config_history` (Admin plane, below); `provider_identities`/`provider_link_challenges` (provider ingest, below); `ai_turns`/`actor_preferences`/`chat_sessions`/`chat_messages`/`chat_actions` (AI plane, below). `assignments`, `execution_attempts` exist schema-only, unused; `communication_bindings` is the thread-binding authority; `link_sessions` predates the Link server.
- **HTTP surface** (all under `/api`, bearer token, RFC 9457 ProblemErrors with type URIs `https://errors.openfacilitator.org/v1/<code>`; `/api/link/v1/*` is the exception — link-token auth, see the Link server section below): `POST /api/works` (201), `GET /api/works`, `GET /api/works/{id}`, `POST /api/works/{id}/steer`, `POST /api/works/{id}/transitions`, `GET /api/works/{id}/timeline`, `POST /api/ingress/events` (raw CloudEvents 1.0 envelope, ≤262144 bytes); `GET /api/teams`, `GET|PUT|DELETE /api/teams/{slug}/members/{actor}`, `GET|PUT /api/teams/{slug}/settings/{resource}`, `GET /api/teams/{slug}/effective/{resource}?channel=` (the team surface: roster + per-scope switches + the level-named precedence answer); the operator surface is `/api/admin/*` (Admin plane, below). Error codes: `invalid-state-transition` 422, `version-conflict` 409 (carries `currentVersion`), `unknown-command` 422, `schema-violation` 400, `envelope-violation` 400, `payload-too-large` 413, `unauthorized` 401, `work-not-found` 404; duplicate ⇒ 200 `{"status":"duplicate"}` (not an error). Endpoint detail: `docs/protocol.md` (Kernel ingress).
- **Dedupe**: `inbox UNIQUE (event_source, event_id)`, `inbox.command_id` unique for business commands, `provider_events UNIQUE (provider, provider_delivery_id)`. Duplicate ⇒ 200 duplicate, zero reprocessing.
- **Per-Work serialization**: `pg_advisory_xact_lock(hashtextextended(work_id::text, 0))` inside each processing transaction + optimistic `workVersion` fencing. Illegal transitions fail closed 422 naming the edge; the Work guard tables (`Kernel/Work/WorkState.cs`) are the single authority — no shortcuts.
- **Outbox dispatcher** (`OutboxDispatcher : BackgroundService`, in-process, no new container): state+outbox rows commit in one tx; claim `FOR UPDATE SKIP LOCKED`; `NOTIFY facilitator_outbox` is a wake hint only — correctness never depends on it; sinks invoked outside any tx; backoff `min(2^attempts*2s, 5m)`; `attempts >= 5` ⇒ `dead_letters` row (reason + full envelope) + status `dead_letter` (replay: `POST /api/admin/dead-letters/{id}/replay`, Admin plane below). Channel-prefix sinks: `kernel.log` (stub ledger, exposed via the merged `GET /api/admin/deliveries`); `link.*` resolves to `LinkSink` (Link server, below); `decode.*` resolves to `DecodeSink` (AI plane, below). Outbox state transitions are owned by `Shared/Persistence/OutboxRepository`; wake listeners share `PgWakeListener`, notifies share `PostgresNotifications`.
- **Ingress validation**: shared .NET `EnvelopeValidator` in `Ofac.Protocol` (schemas embedded as resources); fail-closed `KnownCommands` table (schema-derived set, mirrors the derivation in `src/protocol/src/validate.ts`); kernel tests consume `protocol/conformance-fixtures` files as live inputs.
- **Identity (human plane)**: OIDC bearer on every `/api/*` route except `/api/link/v1*` — JwtBearer scheme `"oidc"` (`Shared/Identity/IdentityJwt`), vendor-neutral (issuer/JWKS/audience, RS256/ES256). Identity = verified `(iss, sub)`; actor string `oidc:<iss>|<sub>`. Operator claim pointers (`identity:claims`, RFC 6901 + shape) map principal context; roles = runtime-mutable claim rules (`identity_roles`, config-seeded when empty, `RoleStore` snapshot ≤`rolesRefreshSeconds` stale, gated API `PUT/DELETE /api/identity/roles/{name}` rowVersion-guarded 409). `IngressRoleGateMiddleware` (after UseAuthorization) matches the resolved endpoint against `identity:authorization:operations`; no binding ⇒ any authenticated principal; matched ⇒ rolesAnyOf or 403 `forbidden`; every evaluation audits `facilitator.audit` (allow+deny, `AuditWriter`), audit failure on allow ⇒ 503 `auth-unavailable`. `IdentityStartupCheck` fail-closed boot on any invalid `identity:` block. Link plane untouched (`LinkBearerHandler`).
- **Approvals (lifecycle authority)**: `Shared/Persistence/ApprovalLedger.cs` (`IApprovalLedger`, scoped) owns `facilitator.approvals`: record on `approval.required` ingress (kernel recompute of the record digest via `Shared/ApprovalDigest.cs` when `action` present — mismatch denies 422, never records; declared-verbatim + `digest-unverified` audit without), decide/revoke/consume by single conditional `UPDATE ... WHERE state=...` (atomic single use: exactly one decider, replays 409 `approval-already-consumed`), lazy expiry on every touch (`pending AND expires_at<=now ⇒ expired`, audited, never resurrected), revocation-before-apply. Eligibility law D4: `group:` (case-sensitive, raw or `/teams/`-stripped) then `user:` displayName then `oidc:` canonical actor. Surfaces: work-scoped REST pair (`Kernel/Work/ApprovalEndpoints.cs`, gated `approvals.read`/`approvals.respond`), ingress event/command arms + Link arms (`Kernel/Work/ApprovalIngress.cs` shared; respond needs a verified human via bearer or `oidc:`/`kernel:` evidence, else 403 `approval-actor-unknown`); REST approve mints a consumed `kernel:` evidence receipt (audit + `approval.decided` timeline note). Decided-push delta: recording captures `requested_by_endpoint_id` from the Link caller; EVERY terminal flip appends `event.approval.decided.v1` to the `link.<endpointId>` outbox channel in the same transaction (outbox law, `commandId=approval-decided:<id>:<state>` dedupe) and the pump pushes it to the Gateway's session; the Gateway answers by sending `command.approval.consume.v1` over the Link plane (`HandleConsumeAsync`: row must exist, belong to the caller endpoint, caller enrolled as `gateway`, row `approved` ⇒ flips `consumed`). Wire/behavior detail: `docs/security.md` + `docs/protocol.md`.
- **CLI**: same assembly, rides the public HTTP API only — `dotnet run --project src/facilitator -- cli [--url URL] [--token TOKEN] work create|steer|get|timeline ...` (defaults `http://localhost:8088`, bearer REQUIRED from env `FACILITATOR_TOKEN` or `--token`, an OIDC access token).
- **Provider ingest (provider-neutral plane)**: `Kernel/Integrations/` (`ProviderOptions` — `ProviderRegistry` binds every `Providers:*` child section, one entry per provider, section key lowercased = the provider token; + `ProviderEventStore` + `ProviderIdentityEndpoints` + `WebhookIngressEndpoints`), composed per provider only when its section (e.g. `providers:mattermost`, `providers:forgejo`; `enabled: true`) exists — provider-less is the default (that provider's events ⇒ typed `unsupported` + audit, zero routes/rows; the webhook sink 404s an unconfigured provider). Webhook fan-out law: the facilitator is the only web-exposed point — `POST /api/ingress/webhooks/{provider}` (anonymous-gated, rate-limited, 1 MiB cap) stores the verbatim bytes as a `command.provider.webhook.v1` outbox row to the provider's link endpoint (effectKey `webhook:<provider>:<sha256(body)>` = provider-retry dedupe); the ADAPTER verifies the vendor signature (provider-local: Gitea-family bare-hex HMAC, GitHub `sha256=` prefix, GitLab constant-token) and normalizes. Arms switch on the six CENTRAL domain types (`org.facilitator.event.provider.{message,mention,reaction,issue,change_request,pipeline}.v1`) — the provider is `data.provider` (must equal `reference.provider`, sender must be the section's `EndpointId`; gate enforces), never the type. The kernel reads ONLY adapter-normalized fields (`headRef`, `labels`, `commentId`, `workTrigger`) — it never digs vendor `payload` shapes. Chat family: mention ⇒ Work + `communication_bindings` row + ack command; message ⇒ bound-thread input note or echo-record (bot's own posts judgment `echo:post-ledger`, zero arm effects); reaction ⇒ timeline audit + configured ack — **never consent**. Forge-domain arms (`ProviderEventStore`): `issue.v1` labeled ∩ adapter-flagged `workTrigger` ⇒ Work + binding; `change_request.v1` review ⇒ deferred steer (`ApplySteerAsync`, id `<provider>-review:<deliveryId>`, own tx post-commit — the steer-arm precedent); `pipeline.v1` ⇒ `provider.input` note. Judgment law: the stored `verified_reference` IS the decision; a duplicate delivery returns the saved judgment. Human linking: `/link` DM mints a challenge (message arm), redemption is REST (`POST /api/link-identities/redeem`, verified `(iss,sub)` stored). Completion hook in `EfCoordinationStore.ApplyTransitionAsync`: terminal transition of a bound Work appends ONE `communication.reply.v1` in the same tx, id `communication-reply:<transitionEventId>`. Adapters are the mapping layer and ride `@ofac/provider-core` (adapter face `runProviderAdapter` + webhook-receiver seam; connector face config-law/ledger/hooks/ops — single way to integrate): `src/adapters/communication/mattermost/` (inbound = bot WebSocket, D1) + the forge pairs `forgejo{,-connector}`, `github{,-connector}`, `gitlab{,-connector}` (inbound = kernel fan-out, signature verified adapter-side; connector = Gateway-child ops, `vcs.*` capability ids, forge token never leaves the team domain).
  - Echo-law exemption: a bot-authored `change_request.v1` `opened`/`reopened` skips echo suppression (kernel gate + forgejo normalizer) — it is resolver input (binds the parked `branch:<head>` marker to the minted PR number), not a conversational echo; no reply targets it, so no loop can form.
- **Config keys**: `ConnectionStrings:Facilitator`, `Database:AutoMigrate` (default true), `identity:*` (oidc/claims/roles/authorization, see docs/security.md), `Kernel:ReplicaId`, `Otel:Endpoint` (OTLP exporter when set), `Providers:<name>` per provider (optional; absent ⇒ that provider composed nowhere), `Ai:Endpoints` + `Ai:TemplateDirectory` (optional; absent section ⇒ AiDeck inert everywhere, no boot error; a present endpoint missing BaseUrl/Model throws at composition).
- **Observability**: meter `facilitator` — counters `facilitator.delivery.attempts`, `facilitator.inbox.processed`, `facilitator.ai.decode.total{basis}`, `facilitator.ai.gateway.errors{endpoint}`, `facilitator.ai.chat.messages{basis}`, `facilitator.ai.chat.actions{status}`; histograms `facilitator.delivery.latency`, `facilitator.ingress.processing.latency`; gauges `facilitator.outbox.queue_depth`, `facilitator.inbox.queue_depth`, `facilitator.delivery.dead_letter_count`, `facilitator.ai.chat.actions.pending`; activity source `Facilitator.Kernel` (spans `ingress.process`, `outbox.dispatch`, `ai.decode`, `ai.chat.turn`, `ai.action.execute` — AI spans tag endpoint + template id + token counts); log correlation `workId`/`deliveryId`.
- **Still target, not reality**: scope/group-level policy over evidence groups.

## Link server (as-built)

Link server code lives in `src/facilitator/Kernel/Link` (namespace `Facilitator.Kernel.Link`). Coordination/ownership rules for the transport edge:

- **DB lease = liveness truth.** `link_sessions.lease_until` (hello/heartbeat + max(15 s, heartbeatSeconds×3)) decides whether an endpoint is live — never socket state. The pump's sweeper deletes expired rows and closes tracked sockets; a link being down leaves its outbox rows non-`delivered` (nothing is tied to a dead socket).
- **The pump owns link delivery-state transitions** (`link_deliveries`: materialize pending → claim in_flight under the caller's epoch → redeliver past `redeliver_after`) **except the receipt ack**, which is its own transaction flipping ledger row to `acked` AND outbox row to `delivered` together — the dispatcher never double-charges a link row. `LinkSink` (`Channel="link"`) is only a routing liveness probe; ordering/redelivery/attempts live in the ledger, not the dispatcher.
- **The connection registry is per-replica sockets only**; cross-replica fencing is the DB `session_epoch` (stale epoch ⇒ refuse + supersede goodbye). NOTIFY `facilitator_outbox` remains hint-only for the link pump too (LISTEN + 2 s safety poll + inline pumps).
- **Ledger is truth, resume cursor is a hint**: replay recomputes un-acked rows from `link_deliveries`; `lastKnownAck` is logged, never trusted. Delivery identity = outbox id, so dropped-receipt → redelivery → duplicate terminates; a `duplicate` receipt ends redelivery like `accepted`.
- **Retryable-rejection law**: a `rejected` receipt whose reason is
  `child-busy`/`child-disconnected`/`child-offline` (the gateway's transient answers) is
  NOT terminal — the ledger keeps the receipt, holds the row `in_flight` behind
  `Link:RedeliverBackoffSeconds` (charging the window = flow control), and re-offers up to
  5 attempts, then settles like any receipt. The client ledger mirrors it: a `failed` row
  re-freshes on re-offer (the handler re-runs); `processed`/`unsupported` stay `duplicate`.
- **Enrollment/auth**: `link_enrollments` (token hash, unique per endpoint, rotation on re-enroll; team = roster row from `Link:Teams` seeded at startup by `TeamRosterSeeder` — no on-demand default) behind the `ILinkTokenValidator` seam (joins `teams` so principals carry team slug + role + accepts-delegated). Two enrollment modes split by credential shape: (a) shared secret `Link:EnrollmentToken` (fail-closed outside Development) + body-required `team`; (b) workload OIDC — a `facilitator-link`-audience token verified against the realm, `(azp, endpointId)` matched against `Link:WorkloadIdentities` bindings which fix team/role/accepts. `gw.` prefix ⇒ role gateway, enforced at enroll; role immutable across rotation. Commands carry optional `actorEvidenceRef`: scheme-split validation (`Shared/Evidence/EvidenceValidator`) — `oidc:`/`kernel:` failures DENY 422 `evidence-invalid`, other schemes record non-authoritative; valid + accepts extends the effective principal (`Shared/Evidence/EffectivePrincipal`), every command admission writes `audit` row `authorization.evaluated:link` (allow and deny; audit failure on allow ⇒ 503). `POST /api/link/v1/enroll` is rate-limited per remote IP (30/min fixed window, 429 on reject); `endpointId`/`instanceId` carry the schema's 128-char idToken bound server-side. Link-token query strings (`?access_token=`) are accepted only on WebSocket upgrade requests. Config: `Link:TokenLifetimeHours` 720, `Link:MaxInFlight` 16, `Link:HeartbeatSeconds` 30, `Link:InFlightLeaseSeconds` 120, `Link:RedeliverBackoffSeconds` 5, `Link:Teams`, `Link:WorkloadIdentities`, `Link:Evidence:Audience`/`:ClockSkewSeconds`.
- **Observability additions**: span `link.handshake`; counter `facilitator.link.received`; gauge `facilitator.link.sessions`; link claims reuse `facilitator.delivery.attempts`. Wire-level endpoint/error detail: `docs/protocol.md` (Link transport).

## Artifacts

`Kernel/Shared/Storage/` (namespace `Facilitator.Kernel.Shared.Storage`): `ArtifactEntity` (`Shared/Persistence/Entities.cs`, table `facilitator.artifacts` — the metadata ledger: state `pending|stored|rejected|deleted`, verification `verified|unverified`, origin `link|rest|external`, `retention_policy` verbatim wire text, team-scoped, TTL 15 min on unclaimed pending slots) + `ArtifactLedger.cs` (allocation with live-commandId dedupe returning the EXISTING ticket, the shared `ConfirmStoredAsync` law used by BOTH the proxied PUT and the confirm paths, external/confirm declaration arms with the `ExternalRefScreener`, team-scoped reads answering the non-disclosure 404, deny audits + same-tx Work-timeline rows) + `IArtifactStore` byte port (`StorageUriScheme`, `Owns(uri)` — a stored row's scheme, never live config, decides every later action) + two config-selected adapters (`Storage:Provider`): `FileSystemArtifactStore` (`kernel-fs`, streaming, no presign) and `S3ArtifactStore` (`s3`, any S3-wire endpoint via AWSSDK path-style, implements the optional `IStoreSignedUrlSource` — presigned PUT/GET minted ONLY for the digest-derived key the allocation fixed, 15-min TTL = ticket expiry). Both land blobs at `{hex[0..2]}/{hex}` (`ArtifactKeys`), paths ONLY from hex digests. Upload flow: request_upload allocates a ticket `{artifactId, uploadLocation, method, expiresAt, uploadUrl?}` in-band (ack `data` / 201 body; prefer `uploadUrl` when it rides); bytes go proxied (`PUT uploadLocation`, count+SHA256 tee) or direct (presigned `PUT uploadUrl` then confirm — kernel HEADs + re-hashes, ETags never trusted); mismatch flips `pending→rejected` (slot spent), match `pending→stored`+`verified`; `event.artifact.available` registers external declarations (screen: bindings waive the address screen, else every resolved address must be public — a miss downgrades to `unverified`, never refuses, never fetches) or confirms kernel-stored ones. `GET /api/artifacts/{id}` + `/content` gated `artifacts.read`, team-scoped 404; object-store rows answer the content GET with 302 to a presigned GET. `RetentionGcService : BackgroundService` (advisory lock 420002, `Storage:Gc:IntervalSeconds` default 60): `Nd` policies collect once lapsed + unreferenced (a live Work pins; a non-superseded result manifest pins its document AND every declared output regardless of Work state — see below), `permanent`/`archive` never, expired slots sweep without backend call, tombstone+audit in-tx then post-commit idempotent delete, batch 100. The 262144 envelope cap NEVER bounds artifact bytes. An `IArtifactStore.List` method is deliberately absent — listing queries ride the ledger. Metrics: `facilitator.artifact.allocated|stored|registered|rejected|confirmed|presigned|gc.collected|store.errors` + gauge `facilitator.artifact.gc.last_pass_age` + histogram `facilitator.artifact.store.latency`; span `artifact.upload` (attributes artifactId, outcome). Config: `Storage:Provider`, `Storage:FileSystem:RootPath`, `Storage:S3:*` (fail-closed `StorageStartupCheck`), `Storage:ExternalBindings`, `Storage:Gc:IntervalSeconds`. Tests ride the dual-provider matrix (`postgres:work` fs leg / `postgres:work:s3` leg against the `SeaweedFsFixture` container); matrix legs use leg-tagged link endpoint ids — both collections share one DB and a re-enroll fences the other leg's token.

## Result manifests

`Kernel/Shared/Storage/ResultManifestLedger.cs` + `Kernel/Work/ResultIngress.cs` + `Kernel/Work/ResultManifestEndpoints.cs`: a completed event carrying `resultManifestRef`+`resultDigest` records an immutable row in `facilitator.result_manifests` ONLY after the ledger refetches the stored bytes, requires byte-equal RFC 8785 canonical form, re-hashes to the wire digest AND the artifact row's verified digest, and re-resolves every `outputs[].artifactId` (same team, verified, digest-matching) — one tx, 422 + zero rows on any miss. The kernel never rewrites a manifest. Revisions chain per assignment (`revision_of`, UNIQUE(assignment_id, revision), tip flips `superseded`); supersession releases the GC pin. The GC reference check treats a non-superseded manifest (artifact_id ∪ output_artifact_ids) as a pin regardless of Work state. Pull: `GET /api/works/{id}/manifests` (gated `manifests.read`, work-scope 404); the document rides `GET /api/artifacts/{id}/content`. TS producers: `@ofac/adapter`'s `createArtifactClient`/`buildResultManifest` (canonical bytes on disk = the law). Wire detail: `docs/protocol.md` (Result manifests).

## Memory broker

`Kernel/Shared/Memory/` (`MemoryBroker.cs` + `IMemoryProvider` port + `PostgresFtsMemoryProvider` + `MemoryRetentionService` + `MemoryOptions`) + `Kernel/Work/MemoryEndpoints.cs` + `Kernel/Work/MemoryIngress.cs`: four tables (`memory_scopes` lazily provisioned owner+selector, `memory_entries` with the tsvector generated column, `memory_shares`, append-only `memory_usage`). The read law is ACL-before-search: the readable predicate (own team OR `facilitator-visible` OR live share; `private` never via another team's label) is embedded in the SQL WHERE — denial discloses nothing (`{entries: []}`, non-disclosure 404). Every recall that returns entries writes one usage row + audit `memory.used` per entry; external consumers attest through the frozen `event.memory.used` envelope (same rows). Link-plane writes land `candidate` + curation is REST-only (`memory.curate`: promote/correct/shares/delete; delete = tombstone + content drop + share revoke + usage retention, one tx). Commands ride both planes with results in the ack `data`; candidate events derive ownership from the workload team (Link) or the named Work's Project→Team lineage (CE plane) and never refuse routing. Quota/guards: per-team UTC-day retain budget + content cap (typed 422s), same-scope content-hash dedupe returns the stored identity. Retention: per-Project `projects.config` `memory` key, fallback `Memory:Retention:CandidateDays`; the GC loop (advisory lock 420003) tombstones lapsed candidates auditing `memory.gc`. Metrics: `facilitator.memory.*`. Error codes: `memory-not-found` 404, `memory-scope-invalid`/`memory-quota-exceeded`/`memory-share-denied` 422.

## AI plane (AiDeck) — IMPLEMENTED

`Kernel/Ai/` (composition `AddAiFeature`): the in-process decode plane calling an external LiteLLM proxy over HTTP. Two ports, one failure law — `IAiGateway` (OpenAI chat completions) and `IDecisionGateway` (LiteLLM `/v1/decisions`, JEV-style typed questions) throw ONLY `AiUnavailableException` on timeout/connect/5xx, and every caller treats that as "degrade", never as a business error: no AI flow may fail a kernel operation.

- **Layered availability**: file config `Ai:Endpoints` (named endpoints, fail-closed at composition when half-addressed; absent section ⇒ no endpoints) × admin_config `llm` row (hot policy; **absent row ⇒ AiDeck inert** — the NudgeSweeper precedent, `AiPolicyStore.Inert`). Both must be live for any model call; everything below degrades to today's deterministic behavior verbatim.
- **One command registry, two parsers**: `CommandRegistry` (code built-ins `/help` `/work` `/status` `/cancel` `/approve` `/link` + admin_config `commands` row custom verbs) is the single source of effects — the deterministic slash parser (`CommandParser`, synchronous in the provider-event arm, zero network) AND the LLM's command list rendered into the decode prompt both read it. The AI may only SELECT a registry verb, never invent an effect: out-of-registry verb, low confidence (<0.5), parse failure, or `unknown` ⇒ deterministic fallback (verbatim Work + hint reply). The `/link` check stays provider-plane code ahead of the parser.
- **Decode is async**: non-slash mention text with a live AiDeck appends outbox row `decode.kernel` (channel `decode`, `DecodeSink`) instead of a Work — no ingest tx/ack held open across LLM HTTP; deterministic commandId `decode:<deliveryId>` rides the dedupe law. Attempts exhausted ⇒ fallback path, never a DLQ for AI-down. Every AI touch (in and out, basis `slash|model|fallback`) ledgers to `facilitator.ai_turns` in the same tx as its effect; the context builder feeds prompts from that ledger + memory-broker recall + bound-Work facts, bounded.
- **Three-level decision ladder** (every decision point): (1) decisions API — one typed `ChoiceQuestion` over the closed candidate set, when the `decision` endpoint is configured and `decisionMode != "llm"`; (2) full chat model as a strict-JSON classifier (`decision-routing.scriban` / `decision-communication.scriban`); (3) deterministic kernel order. Any refusal/off-set/low-confidence/inert answer ⇒ null ⇒ caller's behavior stands verbatim. Consumers: `ModelDecisionProvider` (routing's stored-accepted `provider="model"`, advisory hint audited `model-fallback` by the routing sink) and `CommunicationDecision` (reply|react|silent at the completion reply site in `EfCoordinationStore` — the AI may suppress or soften an update, never swallow one it did not see).
- **Chat plane** (`/api/chat/**`, `ChatEndpoints`/`ChatProcessor`): stored sessions/messages; the model returns a reply plus ZERO OR MORE action proposals, each path checked against the code-defined `ActionCatalog` allow-list — one off-catalog path degrades the WHOLE turn to a plain reply. Nothing executes until an admin confirms: `ChatActionExecutor` replays the stored method/path/body as loopback HTTP against the kernel itself, forwarding the **confirming admin's own Authorization header** (and `X-Change-Reason`) — gate, RBAC, and audit fire exactly as if the admin clicked the UI; audit rows carry `aiInitiated: true` + session/action ids (attribution: admin requested, AI executed). Authorization is config bindings (`chat.use`/`chat.execute`/`chat.read-all`), role `ai-operator` — never auto-granted to framework-admin/kernel-operator. Verbosity per actor (`facilitator.actor_preferences` > `llm.verbosityDefault`); replies compose through `reply-composer.scriban` or fall to canned template text (`replyMode: "template"` or gateway down).
- **Prompts**: Scriban templates as embedded resources (`src/facilitator/Ai/Templates/*.scriban`), per-template override via `Ai:TemplateDirectory`; each template ends with its exact JSON output contract; templates are data (no host functions).
- **Simulation rule**: deterministic tests never construct a real gateway — the test host swaps both ports for fixture-replayed fakes unconditionally. Law text: `tests/Facilitator.Tests/AGENTS.md`.

## Team plane — IMPLEMENTED

`Kernel/Teams/` (`TeamRosterStore.cs` + `TeamSettingsStore.cs` + `TeamEndpoints.cs` +
`TeamScope.cs`): two tables (`facilitator.team_members` — team_id+actor PK, kind
`human|contributor`, capacity `member|operator`, `added_by` provenance;
`facilitator.team_settings` — per-scope switch rows, team/channel scope, jsonb
payload, rowVersion + author + reason). Config seeds both at boot
(`identity:roster`, TeamRosterSeeder — ON CONFLICT DO NOTHING, DB is truth
afterwards). Two code-declared built-in roles (`Shared/Identity/BuiltInRoles.cs`):
`operator` (derived from kernel-operator/framework-admin) and `team-operator`
(granted iff a roster row of capacity `operator` exists for one of the
principal's `/teams/<slug>` groups); the gate evaluates them in the same pass as
config rules, and `PUT/DELETE /api/identity/roles/{name}` refuses to remove a
built-in name (409 `role-builtin`). Roster authority: operator anywhere,
team-operator own team only; a foreign team answers 404 (`team-not-found`), a
member without capacity 403. Empty roster = unconfigured = allow; a populated
roster gates. Team-scoped reads: works list filters to the caller's teams,
foreign work 404s (unscoped callers — no group claim, or operator/admin roles —
see everything, the pre-roster posture). `EffectiveConfigResolver` is the single
cross-scope switch answer (system `admin_config` → team → channel, most specific
wins); `GET /api/teams/{slug}/effective/{resource}` serves it audited with the
winning level named. Registered admin resources grew: `team-behavior`,
`consent-policy`, `loop-budget`, `escalation`; `retry-policy` gained its first
reader (outbox requeue cap + dead-letter threshold in `DbEventDispatcher`, 5 s
cache, default 5 attempts/300 s).

## Admin plane

`Kernel/Admin/` (`AdminEndpoints.cs` + `AdminReadStores.cs` + `AdminConfigStore.cs` + `AdminMutationStore.cs`): the `/api/admin` operator surface — roll-up summary, universal search, project/work detail (timeline reuses `ICoordinationStore.TimelineAsync` verbatim), links (lease = online authority), merged deliveries (outbox+link+dead_letter superset, replacing `/api/dev/deliveries` and `GET /api/dead-letters`), audit read (`GET /api/admin/audit` — action/actor/since/limit, what the chat-history screen needs), metrics (SQL gauges + per-hour buckets + `KernelMetrics` snapshot), runtime config, dead-letter replay (`OutboxRepository.ReplayAsync`: byte-identical snapshot re-enqueue, ledger allows exactly one replay command per dead letter — re-replay 409), manifest re-mark (`available|validated|rejected` only). Config law (`facilitator.admin_config` + `admin_config_history`, newest 20 rows kept): registry-fixed resources (`notifications`, `retry-policy`, `nudge-policy`, `workload-scale`, `routing-defaults`, `llm`, `commands`), PATCH REQUIRES `If-Match` (`*` forces, quoted integer ETag fences; absent/malformed 428, stale 412 `config-version-conflict` carrying `currentVersion`) and `X-Change-Reason`; every stored write audits `admin.config.patch` + one history row in the same tx. Authorization is 100% config bindings (`admin.read`/`admin.mutate`) — no `RequireAuthorization` anywhere; a route without a binding admits every authenticated principal. Contract mirror: `protocol/openapi/admin-v1.yaml` (validated by `make protocol`); drift check = `tests/Facilitator.Tests/Admin/`.

## Admin SPA

`src/facilitator/ui/` (Svelte 5 + vite, workspace member `@ofac/ui`): the embedded inspector. `pnpm -r build` writes hashed assets to `src/facilitator/wwwroot/` (git-ignored pure output); the kernel serves it (`UseDefaultFiles`/`UseStaticFiles`/`MapFallbackToFile`, `Program.cs`) and exposes `GET /ui-config` (anonymous bootstrap: oidc authority/clientId). Auth: public client `facilitator-api`, code+PKCE via `oidc-client-ts`; roles derive from the **access token** payload (`realm_access.roles` + `groups`) — the same claims the kernel gate evaluates, so `canMutate` mirrors server authorization. As-built deltas: a Work's `CorrelationId` chip is the trace handle into the Aspire dashboard (no telemetry store); the config resources are stored intent until a feature reads through (`llm`/`commands` read through AiDeck, AI plane above); a gateway with a stale session lease renders offline (DB lease is the sole online authority); the AiDeck chat panel (session list + action cards + verbosity select) renders only for principals carrying `ai-operator`. Browser tier: `tests/Facilitator.E2E` (Playwright + `KernelWebHostFixture`, linked Admin-plane fixtures), CI job `admin-e2e`, locally `make admin-e2e`.

## When to use

- Placing new code: deciding Facilitator vs Gateway vs Workspace vs adapter responsibility.
- Designing schema/state changes, routing, memory, artifact, or approval features.
- Reviewing PRs that touch coordination or privilege placement.

## Ownership law

| Concern                                                       | Owner                                                                     |
| ------------------------------------------------------------- | ------------------------------------------------------------------------- |
| Normalize events, resolve Work, communicate externally        | Facilitator                                                               |
| Execute implementation/research/testing; pick models & skills | Workspace/harness                                                         |
| Authorize privileged writes (SCM/issues/deploy)               | Gateway (or workspace in direct mode) — Facilitator does admission only   |
| Authoritative work state                                      | Facilitator PostgreSQL                                                    |
| Team-private memory                                           | Team; Facilitator only when explicitly delegated                          |
| Start/stop workers                                            | Gateway provisioner / external scheduler; Facilitator records status only |

The Facilitator holds **no general SCM/issue/deploy credential** (D-10). It may hold only a narrow chat-bot identity for thread replies/reactions. Anything that writes outside the framework runs under team-owned credentials behind team policy.

## State machines — do not invent shortcuts

- Work: `open → planning → active → review → completed` (+ `blocked|cancelled|failed`); closure independent of execution completion.
- Assignment: `proposed → routed → accepted → running → input_required|completed|failed|cancelled|expired`.
- Execution: `starting → ready → working → stopping → stopped|failed|lost`.
- Result: `missing → submitted → validated|rejected → superseded`.
- Approval: `pending → approved|denied|expired|revoked|consumed`.
- Delivery: `pending → leased → sent → durably_acknowledged|rejected|dead_letter`.

Invariants: one _current_ execution generation per assignment; assignment-scoped events carry `workId`+`assignmentId`; completion event never publishes its artifact; parent Work closes only per its completion policy. Four output classes are distinct states: execution status, result state, review status, delivery/publication state — never conflate "agent finished" with "business accepted".

## Coordination pattern (PostgreSQL-first)

- Ingress: verify signature/origin → insert `provider_events`/inbox transactionally (dedupe = success, no processing).
- Claim due rows `FOR UPDATE SKIP LOCKED` with `leaseOwner/leaseUntil/attempt/nextAttemptAt`; serialize per-Work via row lock or advisory lock keyed by Work ID in a **short** transaction; persist new state + outbox rows **atomically**; call providers only after commit, outside the transaction.
- Session placement: `link_sessions(endpointId, connectionInstanceId, replicaId, sessionEpoch, leaseUntil)`; only the session owner (epoch match) may deliver; LISTEN/NOTIFY is a wake hint, never the event store. No Redis backplane, no Kafka, no Temporal in the slim reference — those are adapter seams (D-16).
- Memory default = `postgres-fts` (GIN/tsvector). Memory is a context supplement: never consulted for authn/authz/approval; recall budget-bounded; injection framed as untrusted reference material; missing authorization excludes the whole scope (no post-filter-by-LLM).

## Extension seams (implement one of these, or don't exist)

`LinkTransport`, `HarnessAdapter`, `CapabilityProvider`, `ExecutionProvisioner`, `MemoryProvider`/`IMemoryBroker`, `ArtifactStore`, `CommunicationAdapter` (receive/read/reply/react/discover — provider ops are OPTIONAL per adapter, discovered not assumed), `IDecisionProvider` (only the **authorization** evaluator may allow/deny, and it must be deterministic), `IWorkCoordinator` (Temporal/Orleans behind it, optional).

## Design gate for any new component

Answer all: which contract does it implement; does it change team onboarding; does it add an always-on container; what identity/data can it touch; does it degrade offline/local; version compatibility path; at-least-once/idempotency behavior; recovery with a transient workspace; would a thin provider adapter have sufficed.

## Capability routing — IMPLEMENTED

The funnel (`Kernel/Routing/RoutingSink.cs`, dispatcher channel `routing`) is the ONLY producer of `work.assign` rows. Writers append a `routing:<base>:<key>` intent row (`Kernel/Work/RoutingIntent.cs`; bases `work-assign`/`work-assign-coding`/`work-redispatch`, key = the N-format work id or steer command id) and the sink resolves it: candidates = live `link_sessions` whose enrollment manifest advertises the capability, team-filtered, ordered specificity → oldest-online → endpointId. The effect (`work.assign` envelope + assignment row + timeline event) keeps the intent's command id, so `work-assign*`/`work-redispatch:` outbox read-back (steer fan-out, PR-dispatch ledger) survives. Every decision writes a `facilitator.routing_decisions` audit row (allowed set, chosen, basis, provider name), unique per intent command id ⇒ replay-idempotent.

- No candidate ⇒ `RoutePendingException` requeues with the dispatcher's own backoff (never dead-letters); basis distinguishes `no-capacity` (advertised somewhere, no live session) from `no-candidate` — the scale-up trigger reads exactly this field.
- Provider rules are HINTS: `AssignRules` match contributes capability/inputs/commandIdBase, `EndpointId` (rule or global `AssignEndpointId`) rides as an optional preference, audited `preference-satisfied`/`preference-ignored` (outside allowed set ⇒ deterministic first stands). `POST /api/works` accepts the same intent fields (`capabilityId`, `targetId`, `preferenceEndpointId`, `actorEvidenceRef`) — seed/bootstrap post creates, no raw-SQL assigns.
- Decision seam: `Shared/IDecisionProvider.cs` (`EvaluateAsync(kind, facts, allowedCandidates)`); `DeterministicDecisionProvider` + `ModelDecisionProvider` (AiDeck, see "AI plane" above) ship. `routing-defaults` config `{provider: "deterministic"|"model"|"", preferTeam?}` — `"model"` is advisory (ladder: decisions API → LLM classifier → null); any null answer falls back deterministic audited `model-fallback`.
- Re-dispatch (`EfCoordinationStore.AppendSteerDeliveryAsync`, no live attempt) appends a fresh intent reusing the ORIGINAL envelope's capability/target/inputs (`steers[]` accumulates directives); preference = the original recipient endpoint.

## DAG coordination, generation fencing, bounded nudge — IMPLEMENTED

- **DAG gate** (`Kernel/Work/DependencyGate.cs`): `POST /api/works` accepts
  `assignments[].dependsOn = [{index, condition: completed|result_validated|approved}]`
  (sibling index → that sibling's deterministic assignment id). A create-time cycle /
  unknown index / unknown condition ⇒ 422 `dependency-violation`, nothing created
  (`DagLaw.ValidateBatch`). The funnel consults `UnmetAsync` per intent: an unmet edge
  audits `routing_decisions` row `pending`/`deps-unmet` and requeues — a gated intent
  NEVER dead-letters and its sibling assignment row is NOT created early. The WAKE LAW
  (`WakeGatedRoutingIntentsAsync`, run inside every terminal finalize) resets the still
  gated intents to `pending`/`wake` so the funnel re-evaluates immediately.
- **Generation fencing**: `assignments.current_execution_generation` is the
  fence key; the assign envelope carries it as `data.executionGeneration` (REQUIRED on
  the wire — gateways stamp it onto START frames, receipts, and every event an execution
  publishes; a Gateway's own `assignment.failed`/lost stamps ride the envelope/row
  generation, never the link epoch). `ExecutionLedger.FinalizeAsync` accepts a
  completed/failed event ONLY at the assignment's current generation: mismatch ⇒ 422
  `stale-generation`, inbox row settles `failed` (no manifest recorded). A
  terminal-attempt re-dispatch rides `current+1`. Attempt rows settle
  `stopped|failed` (the attempt vocabulary has no `completed`).
- **Bounded nudge** (`Kernel/Work/NudgeSweeper.cs`, periodic + `SweepOnceAsync` for
  tests): policy = admin_config `nudge-policy` `{"staleAfterMinutes":N}` — ABSENT ⇒
  fully inert. A live assignment (running, open attempt) silent past the window with a
  LIVE session lease on its owning endpoint gets `command.work.nudge.v1`
  (`work-nudge:<assignmentN>:<gen>:<episode>:<count>`, cap 3/episode, fresh progress
  starts a new episode); offline endpoint ⇒ silence. Ownership resolves from the latest
  assign-family outbox row (parse envelopes; jsonb `LIKE` does not translate). Ledger:
  `facilitator.nudges` (assignment, generation, episode, count); admin DTOs surface
  `nudgeCount` + `stalled` (cap spent) per assignment alongside `dependsOn` and
  `currentExecutionGeneration`.

## Provisioning continuity
- **Scale law** (`Kernel/Provisioning/ProvisioningProcessor.cs`, periodic +
  `SweepOnceAsync`): gate = admin_config `workload-scale` (ABSENT ⇒ manual
  mode, silent) + `Provision:Workloads` specs. Arms: min-shortfall/no-capacity
  scale-up, `idleShutdownMinutes` scale-down, worker-lost relaunch (a FAILED
  attempt at the assignment's current generation ⇒ START + re-dispatch intent
  at gen+1 — the SAME assignment, the fence law makes straggers 422). Escalation
  order nudge-capped ⇒ stalled ⇒ scale-up, never reverse. Ledger
  `facilitator.provisioning_operations` (op id = hash(work,assignment,gen,op),
  replay-safe); state mirrors the link lease (two-sweep demotion). Envelopes
  `command.provision.start|stop.v1` to `Provision:LauncherEndpoint`.
- **Session-loss law** (`Kernel/Work/SessionLossSweeper.cs`): a kill-9'd
  gateway posts nothing, so the kernel does — an open working attempt at its
  assignment's current generation whose owner holds no live lease gets marked
  on the first offline sweep and failed on the SECOND (hysteresis; a
  reconnect clears the mark). Synthesized `event.assignment.failed.v1`
  (`id=session-loss:<executionN>`, source kernel, `reason=session-lease-expired`,
  `retryable=true`) rides the inbox + ResultIngress finalize path; the inbox
  unique is the cross-replica dedupe. Same `workload-scale` gate as the scale
  law: manual-mode deployments keep worker failures manual.
- **Launcher** (`src/adapters/provisioning-oci`): link endpoint owning Docker
  Engine over a mounted socket; launches ONLY allowlisted images/networks,
  manages only its own `io.facilitator.managed`-labeled detached containers
  (never the compose project); reconcile-by-label read-before-act; workload
  env VALUES expand by NAME from the launcher's own env (secrets travel by
  name). Compose overlay `reference/compose.provisioning.yaml` pins
  `Link__HeartbeatSeconds=5`: lease 15 s + two 5-s sweeps fail a dead
  gateway's execution ~25 s after the kill.


## Scenario-gap-closure laws (2026-11)

- **Team layer** (`Kernel/Teams/`): `team_members` (humans + contributors;
  an EMPTY roster means unconfigured, a populated one gates),
  `team_settings` (version-fenced, reasoned switches at team/channel
  scope), `EffectiveConfigResolver` (the ONE cross-scope precedence:
  channel > team > system, never a side read). Built-in `operator` (global)
  and `operator:<team>` roles always exist and cannot be removed by config.
  Foreign-team reads answer 404, not 403.
- **Consent authority** (`ConsentTicketService.cs`): a held action rides
  the approval row (digest over the KERNEL-STORED envelope), consume
  re-authorization is lazy-expiry-correct, and the requester-side may not
  be the sole consent. Expiry flips approval + held action + parks the
  work in one transaction.
- **Notifications** (`Kernel/Notifications/`): emission writes
  `notifications` + outbox ATOMICALLY; `EgressResolver` walks the ladder
  bound-conversation → team channel → anonymized public post →
  undeliverable-WITH-REASON (never silence); a vanished conversation flips
  the row `address-obsolete` and the work endures.
- **Contributors** (`ContributorStore.cs`): automations as process members;
  handle unique across teams, every write needs a reason + audit;
  retirement reserves the name and moves open work to the team (accountability
  terminates at people); contributor-only capabilities refuse human evidence.
- **Review-event family** (`org.facilitator.event.provider.review.v1` +
  the `communication.post.v1` command + reply `buttons`): a review verdict
  is a decision with accountability (forks completed work, notes running
  work); the shared normalizer lives in `provider-core/normalize-shared.ts`
  (`reviewEvent`/`reviewStateFor` — anything not a formal verdict is never
  a fake review). `command.communication.post.v1` posts to a CHANNEL
  (the reply command stays thread-bound). Approval buttons ride replies;
  the callback rides `communication.reply.v1` with `buttonId`.
- **Loop budget** (`provider-core/loop-budget.ts`, telegram `loop-guard.ts`):
  per-conversation exchange cap shared by the chat adapters; over-cap
  messages drop before publish with ONE loud notice; a human reply resets.
- **Harness functionality list** (`adapter-sdk/src/capabilities.ts`):
  `fullHarnessCapabilities(omissions)` is the harness-plane twin of the
  comms command family — an omission MUST carry a reason; each harness
  exports its honest declaration (`OMP_CAPABILITIES` et al.); an
  unsupported command answers `unsupported` with a reason, never silence.
- **Scenario gate** (`tools/scenario-plane` + `tests/Facilitator.Tests/
  ScenarioGate/` + `docs/scenarios/scenario-bindings.json`): verdicts are
  review artifacts; the xUnit theory fails closed on evidence rot (a bound
  test renamed) or catalog drift; `make scenario-gate` regenerates
  `docs/scenarios/audit-results.md` and only DOWNGRADES verdicts a run
  observed failing (a filtered run proves nothing). The T4 lane
  (`docs/scenarios/manual-rubric.md`) records, never gates. The compose
  legs live in `docs/runbooks/manual-verification-tiers.md` — manual, and the law
  still binds changes to reference-env inputs.
