# Wire protocol (v1)

This document describes the implemented wire contract: the JSON Schemas under `protocol/`, the conformance fixtures, and the rules both endpoints MUST follow. It is the as-built companion to the target design in `docs/specs/`; where that design proposes and this document records, this document reflects the shipped artifacts. The framework runtime consumes the contract on both sides: the transactional kernel (Kernel ingress below), the Link server (Link transport below), and the Workspace Link SDK (client half below).

## Message classes and envelopes

Every domain message on the wire is one of four classes:

|Class|Wire form|Schema|
|---|---|---|
|command|CloudEvents 1.0 structured-JSON envelope|`protocol/cloudevents-schemas/envelope-command.json`|
|event|CloudEvents 1.0 structured-JSON envelope|`protocol/cloudevents-schemas/envelope-event.json`|
|receipt|plain JSON object (not a CloudEvent)|`protocol/json-schema/receipt.v1.json`|
|control frame|plain JSON object, `kind`-discriminated (`hello`, `welcome`, `heartbeat`, `flow_control`, `goodbye`)|`protocol/json-schema/control.v1.json`|

The base envelope (`protocol/cloudevents-schemas/envelope.json`) requires `specversion` (const `"1.0"`), `id`, `source` (`urn:` or `http(s):`), `type`, `time`, `datacontenttype` (const `application/json`), `dataschema`, and `subject`. Extension attributes on the envelope are lowercase per the CloudEvents 1.0 JSON format binding: `traceparent`, `correlationid`, `causationid` (early drafts wrote the latter two camelCase; the wire names are lowercase). The envelope root does NOT restrict additional properties, so future CloudEvents extensions do not break v1 readers; every `data` payload schema and the receipt/control schemas DO set `additionalProperties: false` — business payloads are closed.

Receipts carry `deliveryId` and `status` (`accepted|duplicate|rejected|unsupported`). `accepted`/`duplicate` MUST include `processedAt`; `rejected`/`unsupported` MUST include `reason`. The timestamp is `processedAt`; an early example named it `acceptedAt` — that name never shipped.

## Namespacing and versioning

- Type strings are `org.facilitator.<class>.<type>.v<N>` (e.g. `org.facilitator.command.work.assign.v1`). They identify semantic major versions, not vendors.
- Every schema's `$id` is `https://schemas.openfacilitator.org/v1/<filename>`; the envelope's `dataschema` MUST equal the `$id` of the payload schema. Validators resolve `dataschema` to the registry by filename map — never by HTTP fetch.
- New optional fields are additive and non-breaking within a major version. Breaking schema or semantics changes require a new major version: a new file (`command.work.assign.v2.json`, new `$id` under `/v1/` filenames per the registry convention), not an in-place edit.
- Unknown **command** types MUST fail closed with a typed `unsupported` receipt (`valid/receipt.unsupported.json`). Unknown **event** types MAY be retained or quarantined for compatible future adapters.
- Handshake negotiation: a `hello` offering no mutually supported `protocolVersions` ends in a `goodbye` frame with reason `no-compatible-protocol-version`.

## Type registry

All payload schemas live in `protocol/json-schema/`; `$id` = `https://schemas.openfacilitator.org/v1/<filename>`.

|Type string|Class|Schema file|Required `data` fields|
|---|---|---|---|
|`org.facilitator.command.work.assign.v1`|command|`command.work.assign.v1.json`|`commandId`, `workId`, `targetId`, `actorEvidenceRef`, `deadline`, `assignmentId`, `capabilityId`, `expectedContextVersion`, `inputs`, `expectedDeliverables`|
|`org.facilitator.command.work.steer.v1`|command|`command.work.steer.v1.json`|common five + `assignmentId`, `directive`, `expectedContextVersion`|
|`org.facilitator.command.work.cancel.v1`|command|`command.work.cancel.v1.json`|common five + `assignmentId`, `reason`|
|`org.facilitator.command.work.status.request.v1`|command|`command.work.status.request.v1.json`|common five + `assignmentId`|
|`org.facilitator.command.work.nudge.v1`|command|`command.work.nudge.v1.json`|common five + `assignmentId`|
|`org.facilitator.command.context.fetch.v1`|command|`command.context.fetch.v1.json`|common five + `contextRefs`|
|`org.facilitator.command.approval.respond.v1`|command|`command.approval.respond.v1.json`|`commandId`, `approvalId`, `decision`, `actorEvidenceRef` (the approval record owns routing and expiry, so no `targetId`/`deadline`)|
|`org.facilitator.command.approval.consume.v1`|command|`command.approval.consume.v1.json`|`commandId`, `approvalId`, `actorEvidenceRef` (the recording Gateway's own `workload:` ref; the approval row owns routing, so no `workId`)|
|`org.facilitator.command.assignment.input.v1`|command|`command.assignment.input.v1.json`|`commandId`, `requestId` (the `input.required` request it answers), `answer` (≤16384), `actorEvidenceRef`. Routes to the Gateway bound to the assignment, which delivers a `respond` child message to the live run; the kernel moves the assignment `input_required → running`|
|`org.facilitator.command.artifact.request_upload.v1`|command|`command.artifact.request_upload.v1.json`|`commandId`, `mediaType`, `size`, `digest`, `actorEvidenceRef` (+ optional `workId`, `assignmentId`, `retentionPolicy` — `"permanent"` \| `"archive"` \| `Nd` (`^[1-9][0-9]{0,3}d$`; absent = `permanent`), `comment` ≤1024; `digest` = `^sha256:[0-9a-f]{64}$` — lowercase hex is a schema pattern, uppercase is a `schema-violation`)|
|`org.facilitator.command.artifact.confirm_upload.v1`|command|`command.artifact.confirm_upload.v1.json`|`commandId`, `artifactId` (the ticket's kernel GUID; Link-plane only, the link principal is the actor proof — no `actorEvidenceRef`) + optional `comment` ≤1024. Settles a direct-to-store upload: the kernel HEADs + re-hashes the declared key|
|`org.facilitator.command.memory.retain.v1`|command|`command.memory.retain.v1.json`|`commandId`, `actorEvidenceRef`, `scopeSelector`, `type` (`observed_fact|experience|decision|lesson_candidate|lesson_verified|hypothesis|preference`), `content` (≤16384), `provenance` (`{source, evidenceRef, createdBy, createdAt, workId?}`) + optional `mediaType`, `contentSummary`, `policyLabel`, `classification`, `observedAt`. Receipt `{entryId, status, duplicate}` rides the ack `data`; a workload retain lands `candidate`|
|`org.facilitator.command.memory.recall.v1`|command|`command.memory.recall.v1.json`|`commandId`, `actorEvidenceRef`, `query` (≤1024), `purpose` (`assignment.context|requirements.clarification|report.synthesis`) + optional `workId`, `scopeSelectors` (≤8), `maxItems` (1–20, default 8), `maxTokens` (≥256), `includeSuperseded`. The recall response `{requestId, providerProfile, entries, truncated, asOf}` rides the ack `data`; a duplicate `commandId` honestly re-runs the read|
|`org.facilitator.event.work.accepted.v1`|event|`event.work.accepted.v1.json`|`workId`, `assignmentId`, `acceptedAt`|
|`org.facilitator.event.work.progress.v1`|event|`event.work.progress.v1.json`|`workId`, `assignmentId`, `executionGeneration`, `progressNote`|
|`org.facilitator.event.assignment.input.required.v1`|event|`event.assignment.input.required.v1.json`|`workId`, `assignmentId`, `executionId`, `requestId`, `executionGeneration`, `question` (≤16384), `createdAt` + optional `choices` (≤8, each ≤256), `contextRef`, `deadline` (advisory display only). An adapter parks a live run: the kernel moves the assignment `running → input_required` and fans the question out (work's integration → team default endpoint → decision provider → timeline-only)|
|`org.facilitator.event.assignment.completed.v1`|event|`event.assignment.completed.v1.json`|`workId`, `assignmentId`, `executionId`, `executionGeneration`, `resultManifest` (inline doc, **deprecated carrier** — kept for the backward leg, `outputs: []` on the artifact path) + optional 1.1: `resultManifestRef` (`format: uri`, `^artifact://` — the uploaded manifest document's logical URI) and `resultDigest` (`^sha256:[0-9a-f]{64}$`); if/then couples them: a ref without a digest is a `schema-violation`|
|`org.facilitator.event.assignment.failed.v1`|event|`event.assignment.failed.v1.json`|`workId`, `assignmentId`, `executionGeneration`, `reason`, `retryable`|
|`org.facilitator.event.workspace.heartbeat.v1`|event|`event.workspace.heartbeat.v1.json`|`workspaceId`, `status` (`idle|busy|draining|error`)|
|`org.facilitator.event.artifact.available.v1`|event|`event.artifact.available.v1.json`|`artifactId`, `mediaType`, `size`, `digest` (+ optional `storageUrl` — an external declaration's location, screened never fetched; `createdBy`, `createdAt`, `workId`)|
|`org.facilitator.event.memory.candidate.v1`|event|`event.memory.candidate.v1.json`|`scope`, `contentSummary`, `provenance` (+ optional `policyLabel`). Lands a `candidate` entry owned by the workload's team (Link plane) or the named Work's Project→Team lineage (ungated CE plane); no derivable owner ⇒ audited deny + swallow, never a refusal; timeline `memory.candidate` when a live Work is named|
|`org.facilitator.event.approval.required.v1`|event|`event.approval.required.v1.json`|`approvalId`, `actionDigest`, `eligibleActors`, `nonce`, `expiresAt`, `createdAt`; optional 1.1: `action` (the exact payload `actionDigest` covers — the kernel re-digests it), `workId` (the Work the approval belongs to, when one exists)|
|`org.facilitator.event.approval.revoked.v1`|event|`event.approval.revoked.v1.json`|`approvalId`, `revokedBy`, `reason`|
|`org.facilitator.event.approval.decided.v1`|event|`event.approval.decided.v1.json`|`approvalId`, `decision` (`approved|denied|expired|revoked`), `decidedBy` (canonical actor, `""` for lazy expiry), `evidenceRef` (`kernel:` receipt on approve, else `""`), `expiresAt`, `createdAt` (binds the push to the exact digest record); optional `workId`. Kernel→Gateway decided-push on the recorder's `link.<endpointId>` channel|
|`org.facilitator.event.memory.used.v1`|event|`event.memory.used.v1.json`|`memoryId`, `consumer`, `justification`. An external read-then-decide attestation: records the same `memory_usage` row an in-kernel recall writes when the consumer's team may read the entry; otherwise audited deny + swallow (append-only usage law — the event never refuses)|
|`org.facilitator.event.provider.message.v1`|event|`event.provider.message.v1.json`|`reference` (`conversationReference`), `provider` (provider token, MUST equal `reference.provider`), `providerDeliveryId`, `actorProviderAccountId`, `text` (1..8192) + optional `payload` (verbatim provider body, open). Chat-domain family: a plain message a chat adapter deemed worth relaying|
|`org.facilitator.event.provider.mention.v1`|event|`event.provider.mention.v1.json`|same as `message.v1`. Chat-domain family: an @-mention of the bot identity — the kernel's identity-gated Work-creation arm|
|`org.facilitator.event.provider.reaction.v1`|event|`event.provider.reaction.v1.json`|`reference`, `provider`, `providerDeliveryId`, `actorProviderAccountId`, `emojiName` (1..64) + optional `payload`. Chat-domain family: emoji reaction; audits only, never consent|
|`org.facilitator.event.provider.issue.v1`|event|`event.provider.issue.v1.json`|`reference`, `provider`, `providerDeliveryId`, `actorProviderAccountId`, `action` (`opened|labeled|commented|closed`), `workTrigger` (bool, set by the adapter from its label config) + optional `text`, `payload`. Issue-tracker domain|
|`org.facilitator.event.provider.change_request.v1`|event|`event.provider.change_request.v1.json`|`reference`, `provider`, `providerDeliveryId`, `actorProviderAccountId`, `action` (`opened|synchronized|reopened|reviewed|commented|merged|closed`) + optional `text`, `payload`. Change-request domain (forge-neutral: merge/pull requests map in adapter-side)|
|`org.facilitator.event.provider.pipeline.v1`|event|`event.provider.pipeline.v1.json`|`reference`, `provider`, `providerDeliveryId`, `actorProviderAccountId`, `action` (`pending|success|failure|error`) + optional `description`, `payload`. Pipeline domain (CI/commit-status verdicts map in adapter-side)|
|`org.facilitator.event.provider.review.v1`|event|`event.provider.review.v1.json`|`reference`, `provider`, `providerDeliveryId`, `actorProviderAccountId`, `action` (`submitted|changed|dismissed`), `state` (`approved|changes-requested`), `text` + optional `commentId`, `headRef`, `payload`. Review domain (a change-request review verdict; the steer-on-review path consumes it)|
|`org.facilitator.event.provider.degraded.v1`|event|`event.provider.degraded.v1.json`|`provider`, `status` (the observed provider HTTP status, 100–599), `observedAt`, `retryable` + optional `reason`, `endpointId`, `team`. A provider adapter's own health signal (rate limit, auth failure); routes to operators, never to a Work|
|`org.facilitator.command.communication.reply.v1`|command|`command.communication.reply.v1.json`|`commandId`, `target` (`conversationTarget`: provider + conversation reference), `text` (≤8192), `idempotencyKey` + optional `artifacts` (attachments by `artifactRef`), `buttons` (1–3). A communication adapter executes it on the provider (thread reply when the target names a thread)|
|`org.facilitator.command.communication.post.v1`|command|`command.communication.post.v1.json`|same as `reply.v1`, but the target names a channel/space rather than a message — a new top-level post|
|`org.facilitator.command.communication.react.v1`|command|`command.communication.react.v1.json`|`commandId`, `target`, `emojiName` (1..64), `idempotencyKey`. An ack/reaction on a provider object|
|`org.facilitator.command.communication.update.v1`|command|`command.communication.update.v1.json`|`commandId`, `target`, `text`, `idempotencyKey`. Edit a post — adapters may execute it only for objects their own ledger created|
|`org.facilitator.command.communication.acknowledge.v1`|command|`command.communication.acknowledge.v1.json`|`commandId`, `target`, `idempotencyKey`. Delivery acknowledgment; adapters may answer `accepted` as a no-op|
|`org.facilitator.command.provider.webhook.v1`|command|`command.provider.webhook.v1.json`|`commandId`, `provider` (the routing token from the sink path), `contentType`, `body` (raw bytes, verbatim), `headers` + optional `deliveryId`. Kernel-sink → team adapter hop for webhook-ingress providers: the kernel receives and forwards, the adapter verifies the provider HMAC|
|`org.facilitator.command.provision.start.v1`|command|`command.provision.start.v1.json`|`commandId`, `operationId`, `workloadId`, `team`, `actorEvidenceRef`, `deadline`, `spec` (image + optional entrypoint/network/env — a container spec) + optional `reason`. Kernel → provisioning adapter: start an executor (scale-from-zero)|
|`org.facilitator.command.provision.stop.v1`|command|`command.provision.stop.v1.json`|`commandId`, `operationId`, `workloadId`, `team`, `actorEvidenceRef`, `deadline` + optional `reason`. Kernel → provisioning adapter: retire an executor|
|`org.facilitator.event.routing.requested.v1`|event|`event.routing.requested.v1.json`|`intentId`, `workId`, `capabilityId`, `commandIdBase`, `targetId`, `objective`, `actorEvidenceRef` + optional `team`, `effectKey`, `preferenceEndpointId`, `referenceJson`, `extraInputsJson`, `executionGeneration`, `dependsOnJson`. Kernel-internal routing intent (every Work-creating arm emits one); consumed by the dispatcher, never leaves the kernel's own channels|

"Common five" = `commandId`, `workId`, `targetId`, `actorEvidenceRef`, `deadline`.

Supporting schemas, referenced by the types above and by the HTTP surface:

|Schema file|Purpose|
|---|---|
|`receipt.v1.json`|Delivery receipt for commands and events|
|`control.v1.json`|Handshake and session frames: `hello`, `welcome`, `heartbeat`, `flow_control`, `goodbye`|
|`result-manifest.v1.json`|`manifestId`, `outputs` (`artifactRef` items, widened additively: optional `kind`/`classification` producer taxonomy labels, opaque to the kernel); optional declared vocabulary `workId`/`assignmentId`/`capability`/`summary`/`structuredResult`/`externalRefs`/`sourceRefs`/`notes`/`createdAt`/`revisionOf`; open model (`additionalProperties: true`) so future producers extend it additively|
|`capability-manifest.v1.json`|Advertised capabilities (`apiVersion` `facilitator.framework/v1`), closed model; served by `GET /api/link/v1/capabilities`|
|`common.v1.json`|Shared `$defs` (`artifactRef`, `deliverable`, `evidenceRef`, `idToken`, `rfc3339DateTime`); no root validation|

Events carry identity in the envelope (`source` + `id` + `time`), so their `data` payloads do not repeat it.

## Payload limits and canonical serialization

- The serialized envelope MUST NOT exceed 262144 bytes. The single source of truth is `limits.maxEnvelopeBytes` in `protocol/conformance-fixtures/index.json`; both harnesses read it, and `protocol/openapi/link-v1.yaml` documents it in prose. Large artifacts travel by reference (`artifactRef`), never inline.
- `date-time` strings are validated against RFC 3339 by both harnesses (fixture `invalid/event.bad-time.json` proves month `13` is rejected).
- Approval hashing uses RFC 8785 JSON canonicalization: sorted keys, shortest-form numbers, NFC strings, no insignificant whitespace. Two goldens in `protocol/conformance-fixtures/canonical/` cross-check the independent implementations (`canonicalize` in the TS harness, `Jcs.Net` in the .NET harness + kernel `ApprovalDigest`) against each other and the committed digests: `approval-prehash.json` (raw canonicalization) and `approval-record-1.json` + `.sha256` (the full record digest over `{approvalId, action, eligibleActors, expiresAt, createdAt}` — the value the ledger stores as `action_digest`; both harnesses also digest key-reordered and NFD-variant clones to the same digest). The shared helpers: kernel `ApprovalDigest.Compute`, `computeActionDigest` in `@ofac/protocol`.

## Conformance fixtures

`protocol/conformance-fixtures/index.json` lists every case with its expectation (`valid` or `invalid` plus an `errorClass` from `envelope-violation | schema-violation | payload-too-large | unknown-command | no-common-version`). Fixture files are bare wire payloads, consumable by any language runtime without unwrapping. `unknown-command` and `no-common-version` cases name the expected response fixture, which the harnesses cross-validate. Two harnesses consume the same index:

- TypeScript: `pnpm --filter @ofac/protocol test` (vitest, `src/protocol/test/fixtures.test.ts`)
- .NET: `dotnet run --project protocol/tools/Validate.Fixtures -c Release`

## Adding a v2 type

1. Author the payload schema in `protocol/json-schema/` as a new file with a `v2` filename and `$id`. Decide explicitly: additive optional fields stay in v1; anything breaking gets the new major.
2. If the type introduces a new class, add a matching envelope in `protocol/cloudevents-schemas/`; otherwise the existing command/event envelopes already accept the new `type` pattern.
3. Add at least one valid fixture and one invalid fixture under `protocol/conformance-fixtures/`, registering each in `index.json` with its class, type, and error class.
4. Run `pnpm protocol:codegen` (or `make protocol`) and commit the regenerated trees (`src/protocol/src/generated/`, `protocol/dotnet/Ofac.Protocol/Generated/`, `src/protocol/schemas/`) — CI fails on drift.
5. Both harnesses MUST pass: `dotnet run --project protocol/tools/Validate.Fixtures -c Release` and `pnpm -r test`.

## Transport neutrality

The contract is transport-independent: the same fixtures MUST validate identically over the SignalR reference transport and the HTTP long-poll fallback, both implemented in the Facilitator (Link transport below; `protocol/openapi/link-v1.yaml` 1.0.2 is the frozen HTTP surface — enroll, events, deliveries, receipts, capabilities GET+POST). Consumers should use the `@ofac/protocol` package (`src/protocol/`): `schemaById` resolves a `dataschema` URI to its schema, `KNOWN_COMMAND_TYPES` is the fail-closed command table, and `validateEnvelope` / `validateReceipt` / `validateControlFrame` return the same `errorClass` taxonomy as the fixtures index.

## Kernel ingress

The Facilitator kernel (`src/facilitator`, namespace `Facilitator.Kernel`) consumes the contract over HTTP today. All endpoints sit under `/api` and require `Authorization: Bearer <token>` (interim bootstrap token; see [`security.md`](security.md)). Errors are RFC 9457 `ProblemDetails` with `type` URIs under `https://errors.openfacilitator.org/v1/`.

### Endpoints

|Endpoint|Purpose|Success|
|---|---|---|
|`POST /api/works`|Create a Work (`{"objective": "...", "projectId": uuid?}`); participates in dedupe via a synthetic inbox row|`201` + `WorkDto`|
|`GET /api/works`|List newest 50 Works|`200` array|
|`GET /api/works/{id}`|Fetch one Work|`200` `WorkDto {workId, projectId, objective, state, version, createdAt, updatedAt}`|
|`POST /api/works/{id}/steer`|Steer (`{"directive", "commandId", "expectedVersion"}`)|`200` `WorkDto` (version bumped)|
|`POST /api/works/{id}/transitions`|Operator-driven state walk (`{"to", "expectedVersion", "reason?"}`), guarded by the Work transition table|`200` `WorkDto`|
|`GET /api/works/{id}/timeline`|`work_events` rows, ascending|`200` array|
|`GET /api/works/{id}/manifests`|Work's recorded result-manifest chain, newest revision first (gated `manifests.read`; work-scope 404 law)|`200` array of `{manifestId, workId, assignmentId, executionId, revision, revisionOf, digest, structuredDigest, artifactId, resultState, state, recordedAt}`|
|`POST /api/ingress/events`|Raw CloudEvents 1.0 structured-JSON envelope (any type in the registry above)|`200 {"status":"accepted","inboxId":...}` or `200 {"status":"duplicate",...}`|
|`GET /api/works/{id}/approvals`|Work's approval ledger rows (lazy expiry applied to each row read; gated `approvals.read`)|`200` array of `ApprovalDto`|
|`POST /api/works/{id}/approvals/{approvalId}/respond`|Human decision (`{"decision":"approved"|"denied", "comment"?: ≤1024}`; gated `approvals.respond`); a minted consumed `kernel:` evidence receipt rides the approve response as `evidenceRef`|`200` `{status:"decided", approvalId, state, evidenceRef?}`|
|`POST /api/artifacts/request-upload`|Human-plane upload allocation (the REST twin of `command.artifact.request_upload` — same fields minus the envelope, gated `artifacts.write`); team = the actor's first `/teams/<slug>` group|`201` ticket `{artifactId, uploadLocation, method, expiresAt, uploadUrl?}` (Location `/api/artifacts/{id}`; `uploadUrl` + `method:"PUT"` ride only when the configured adapter mints presigned URLs — callers prefer `uploadUrl` when present; otherwise the proxied push to `uploadLocation` is the only byte path)|
|`PUT /api/artifacts/{id}/content`|Human-plane byte push (gated `artifacts.write`; team-scoped in the ledger, foreign-team row answers `404`); the kernel counts + SHA-256-hashes the received stream|`200` `{artifactId, digest, size, state:"stored"}`; re-PUT of a stored row is idempotent|
|`GET /api/artifacts/{id}`|Artifact metadata (gated `artifacts.read`; team-scoped, `404` when foreign/missing — non-disclosure)|`200` `{artifactId, workId, assignmentId, mediaType, size, digest, state, verification, origin, createdBy, createdAt, storedAt, retentionPolicy}` (never `storageUri`)|
|`GET /api/artifacts/{id}/content`|Artifact bytes: object-store adapters answer `302` to a presigned GET (retrieval bytes bypass the kernel); the filesystem adapter streams from the store (gated `artifacts.read`; readable only when `stored` through an adapter the kernel config `Owns`s — foreign, pending, or external rows answer `404`)|`302` + Location, or `200` bytes, row's `Content-Type` + digest `ETag`|
|`POST /api/memory/retain`|Human-plane retain (REST twin of `command.memory.retain` — same fields minus the envelope, gated `memory.write`); a deliberate human write lands `active`|`201` `{entryId, status, duplicate}` (Location `/api/memory/{id}`; same-team same-hash same-scope re-retain returns the stored identity with `duplicate:true`)|
|`POST /api/memory/recall`|FTS recall (`{"query", "purpose", "scopeSelectors"?, "maxItems"?, "maxTokens"?, "includeSuperseded"?}`; gated `memory.read`); ACL-before-search, one `memory_usage` row per returned entry|`200` `{requestId, providerProfile, entries[{…, provenance, policyLabel, accessControl, retrievalReason}], truncated, asOf}`|
|`GET /api/memory/export?scope=…`|NDJSON export of one readable scope's live entries (gated `memory.read`)|`200` NDJSON|
|`GET /api/memory/{id}`|Entry view + live shares + usage count (gated `memory.read`; non-disclosure `404` for foreign/tombstoned/missing)|`200` entry DTO|
|`POST /api/memory/{id}/promote` · `POST /api/memory/{id}/correct` · `POST /api/memory/{id}/shares` · `DELETE /api/memory/{id}/shares/{shareId}` · `DELETE /api/memory/{id}`|Owner-team curation (gated `memory.curate`): candidate→active (optional `targetScope` = own team selector only, else the typed `memory-scope-invalid` 422), revision chain (`supersedesEntryId`, the new revision lands `active`), share grant/revoke (grantee must exist; private unsharable), delete = tombstone + content drop + share revocation in one tx|`200` `{entryId, …}` (promote carries `status:"active"`, correct `superseded:true`, shares the `granteeTeam`), `204` on share-revoke and delete|
|`/api/admin/*`|Operator surface: summary roll-up, universal search, project/work detail + timeline, links, merged deliveries, metrics, config GET/PATCH (If-Match + `X-Change-Reason` law, bounded history), dead-letter replay, manifest re-mark. Gated `admin.read` (GETs) / `admin.mutate` (PATCH/POST) via the config bindings — contract mirror `protocol/openapi/admin-v1.yaml`|see `admin-v1.yaml`|

### Error contract

|Type URI suffix|HTTP|When|
|---|---|---|
|`invalid-state-transition`|`422`|Edge illegal per the Work guard table; message names the illegal edge|
|`version-conflict`|`409`|`expectedVersion` mismatch (problem body carries `currentVersion`)|
|`unknown-command`|`422`|Command type outside the known-command table (fail closed); the envelope is still persisted to `inbox` state `failed` as an audit record|
|`schema-violation`|`400`|Envelope well-formed, `data` payload violates its `dataschema`|
|`envelope-violation`|`400`|CloudEvents envelope violates `protocol/cloudevents-schemas/*`|
|`payload-too-large`|`413`|Serialized body exceeds 262144 bytes|
|`unauthorized`|`401`|Missing/wrong bearer token|
|`work-not-found`|`404`|Unknown Work id|
|`approval-digest-mismatch`|`422`|`approval.required` carried an `action`: the kernel's own RFC 8785 recompute of `{approvalId, action, eligibleActors, expiresAt, createdAt}` differs from the declared `actionDigest` (inbox row `failed`)|
|`approval-id-invalid`|`422`|`approvalId` is not a GUID — the ledger keys on `uuid`|
|`approval-not-found`|`404`|Unknown approval, or (on the work-scoped surface) another work's approval — no cross-work existence leak|
|`approval-already-consumed`|`409`|Replay: the row is already `approved`/`consumed`/`denied` — atomic single use, denied even for byte-identical command JSON|
|`approval-expired`|`409`|Past `expiresAt`; the lazy state flip applied first, so the row reads `expired` afterwards|
|`approval-revoked`|`409`|`approval.revoked` landed before the respond/consume (revocation-before-apply)|
|`approval-not-pending`|`409`|Consume attempted against a still-`pending` row|
|`approval-actor-ineligible`|`403`|The deciding principal does not intersect `eligibleActors` (`group:` — raw path or `/teams/`-stripped, case-sensitive — then `user:` display name, then `oidc:` canonical actor); audited `deny`|
|`approval-actor-unknown`|`403`|Respond arrived with no verified human behind it (non-repudiation)|
|`artifact-not-found`|`404`|Unknown artifact, foreign-team row, pending slot on the content surface, or a spent (rejected) upload slot — one absent answer everywhere, no existence leak|
|`artifact-digest-mismatch`|`422`|PUT bytes hashed elsewhere than the declared digest; row flips `pending → rejected` (fresh allocation required), deny-audited `artifact.rejected`|
|`artifact-size-mismatch`|`422`|PUT byte count ≠ declared `size`; same reject path|
|`artifact-declaration-mismatch`|`422`|`artifact.available` re-declared a kernel-allocated artifactId with contradicting mediaType/size/digest (first writer owns the row); deny-audited `artifact.reregistration-denied`|
|`artifact-not-pending`|`409`|Confirm against a slot already spent — `rejected`, lapsed, or `deleted` (a fresh allocation is required); confirm of an object-store slot whose bytes never landed answers `404`, never this|
|`artifact-not-confirmable`|`409`|Confirm against a `pending` row on a plane that cannot have received direct bytes: the configured adapter mints no URLs (filesystem), or the row's scheme no configured adapter owns (inert row)|
|`manifest-not-found`|`404`|Completed event carried `resultManifestRef`: unknown work, foreign-team work, non-GUID ref last segment, or a missing/unverified/foreign manifest artifact — one absent answer everywhere, no existence leak|
|`manifest-digest-mismatch`|`422`|Stored manifest bytes are not byte-equal their own RFC 8785 canonical form, or the kernel's recompute ≠ the declared `resultDigest`; zero rows written|
|`manifest-output-unresolvable`|`422`|A manifest `outputs[].artifactId` does not resolve to a verified, digest-matching, same-team artifact; zero rows written|
|`memory-not-found`|`404`|Unknown entry, foreign-team row, tombstoned row, a denied promote/correct/share/delete target, or an unknown share — one absent answer everywhere, no existence leak|
|`memory-scope-invalid`|`422`|The selector is unparseable, unresolvable, or names a foreign team (write law); retain content over `Memory:Quota:MaxContentChars` (defense in depth behind the schema `maxLength`); or a promote `targetScope` that contradicts the owner-team law while the entry itself is real and owned|
|`memory-quota-exceeded`|`422`|The team's UTC-day retain count is at `Memory:Quota:DailyRetainPerTeam`; deny-audited `memory.quota-exceeded` (`daily-retain-budget`)|
|`memory-share-denied`|`422`|Share attempted on a `private` entry (unsharable by construction) or toward a team the roster doesn't know|

A duplicate admission is **not** an error: `200 {"status":"duplicate"}`.

### Dedupe semantics

Admission is idempotent on three independent keys, all enforced by PostgreSQL uniqueness in the `facilitator.inbox` table:

1. CloudEvents identity: `UNIQUE (event_source, event_id)`.
2. Business-command identity: `inbox.command_id` unique — a re-posted `commandId` returns the stored duplicate outcome without re-executing the command.
3. Provider webhooks: `UNIQUE (provider, provider_delivery_id)` where both are supplied (`provider_events` audit row records the full source).

A duplicate returns `200 {"status":"duplicate"}` with zero reprocessing — no second timeline row, no second outbox row. Never dedupe on textual content.

### Size limit

`POST /api/ingress/events` reads bodies with a 262144-byte cap, the same value as `limits.maxEnvelopeBytes` in `protocol/conformance-fixtures/index.json`; oversize bodies get `413` `payload-too-large` before any parsing.

### Validation at ingress

`POST /api/ingress/events` validates against the exact same schema files as the harnesses: `protocol/json-schema/*` and `protocol/cloudevents-schemas/*`, shipped as `EmbeddedResource`s of `Ofac.Protocol` (the container image can't read `protocol/` at runtime) and enforced by the shared `EnvelopeValidator` in `protocol/dotnet/Ofac.Protocol/Validation/EnvelopeValidator.cs`. It returns the same `errorClass` taxonomy as the fixture index (`envelope-violation | schema-violation | payload-too-large | unknown-command`). Commands outside the fail-closed 21-type known-command table (`Validation/KnownCommands.cs`, derived from the same schema registry as `KNOWN_COMMAND_TYPES` in `src/protocol/src/validate.ts`) are rejected `422` and audited in the inbox. Of the known commands, `work.steer.v1`, `approval.respond.v1`, `approval.consume.v1`, `artifact.request_upload.v1`, `artifact.confirm_upload.v1`, `memory.retain.v1`, and `memory.recall.v1` have business effects (`approval.consume.v1` decides the ledger row and only rides the Link plane: the caller must be the gateway enrollment that recorded the approval; `artifact.request_upload.v1` allocates an upload slot and `artifact.confirm_upload.v1` settles a direct-to-store upload, both riding the Link plane plus a REST twin — on the raw CE plane they admit on the generic typed-note path; the two memory commands ride both planes and their command result — retain receipt / recall response — rides the ack `data` field exactly like the upload ticket, the CE plane resolving actor authority from the validated `actorEvidenceRef`); of the event types, `approval.required.v1` records a ledger row (kernel digest recompute when `action` is present, `digest-unverified` otherwise; the Link plane also stores the recording endpoint for the decided-push), `approval.revoked.v1` marks one revoked, `artifact.available.v1` registers an external declaration or confirms a kernel-stored one (both ingresses, identical arms), `memory.candidate.v1` lands a promotable candidate entry and `memory.used.v1` records a usage attestation (both ingresses; the memory event arms never refuse); the rest admit (`202`-path, inbox row `processed`) without side effects, typed rather than silent.

The kernel's own emission path builds outbox envelopes with the same validator before insert — the kernel emits conformant envelopes by construction. Kernel integration tests consume real files from `protocol/conformance-fixtures/` as live inputs to `/api/ingress/events` (e.g. `valid/command.work.steer.json` posted twice proves the duplicate path; `invalid/command.unknown.v1.json` proves fail-closed `unknown-command`; `invalid/envelope.specversion.json` proves `400 envelope-violation`), so the fixtures gate the runtime, not just the harnesses.

## Link transport

The Facilitator serves the Workspace Link contract over two transports with identical application semantics (`src/facilitator/Kernel/Link/`): the SignalR hub at `/hubs/link` (reference transport) and the HTTP surface frozen in `protocol/openapi/link-v1.yaml` 1.0.2 (fallback, every language first-class). Outbox rows addressed `link.<endpointId>` are materialized into the durable delivery ledger `facilitator.link_deliveries` and offered to the endpoint over whichever transport it is speaking.

### Enrollment: dual modes

`POST /api/link/v1/enroll` presents its credential in the request body
(`token`), not a bearer header, and the kernel tells the two modes apart by
the credential's shape:

- **Mode (a) shared secret**: `token` = `Link:EnrollmentToken` (env
  `Link__EnrollmentToken`); unset + Development accepts the literal
  `enroll-dev`; unset + non-Development refuses startup (fail closed). The
  body MUST name `team` — a roster slug (`Link:Teams`, seeded at startup);
  absent or unknown ⇒ `403 team-unknown`, no on-demand default. `role`
  (`gateway` | `child`) is optional, default `gateway`.
- **Mode (b) workload OIDC**: `token` = an access token whose `aud` contains
  `Link:Evidence:Audience` (default `facilitator-link`), verified against the
  realm JWKS. The `Link:WorkloadIdentities` binding matching `(azp,
  endpointId)` fixes team, role, and delegation acceptance; no binding ⇒ `403
  workload-not-registered`; a body `role` contradicting the binding ⇒ `403
  role-mismatch`.

Both modes: a `gw.`-prefixed `endpointId` requires `role: gateway` (`403
role-mismatch`); every other `/api/link/v1/*` request and hub connection
carries `Authorization: Bearer <linkToken>`; invalid/expired/revoked tokens
yield `401 unauthorized` with no oracle for whether an enrollment exists.
All resolution flows through the `ILinkTokenValidator` seam
(`Kernel/Shared/Auth/ILinkTokenValidator.cs`).

Enrollment is an upsert by `endpointId` and **rotates the token**: the
previous `linkToken` stops validating immediately; **role is immutable
across rotation** (re-role = revoke + re-enroll, else `403 role-mismatch`).
The response always includes the additive 1.0.1 fields — `linkToken` (opaque
256-bit base64url) and `expiresAt` (now + `Link:TokenLifetimeHours`, default
720 h). `trustMode` (`shared-runtime` | `separate-process-same-host` |
`isolated-executor`) is echoed and MUST be logged at enrollment and at every
`hello`. The enrollment record (endpoint, team, role, workload
subject, token hash, manifest + digest, timestamps) is stored server-side.

**Delegation evidence**: commands may carry `actorEvidenceRef` (schema
`common.v1.json#evidenceRef`, ≤ 2048 chars — amended in 1.0.2 so an
`oidc:<jwt>` fits). Kernel-checked schemes (`oidc`, `kernel`) that FAIL
validation deny the admission `422 evidence-invalid`; any other scheme is
recorded non-authoritative. Valid evidence extends the effective principal
only when the enrollment accepts delegated claims. See
[`security.md`](security.md) (Workload plane) for the full algorithm and
audit shape.

### Handshake

`hello` → `welcome` (fixture pair `valid/control.hello.json` / `valid/control.welcome.json` — the normative handshake examples):

```json
{ "kind": "hello", "protocolVersions": ["v1"], "supportedTransports": ["signalr", "websocket", "http-poll"], "endpointId": "gw-team-alpha", "instanceId": "inst-01", "capabilityManifestDigest": "sha256:abab…ab", "lastKnownAck": "del-100" }
```

The server validates the frame against `control.v1.json`, requires `endpointId` to equal the authenticated principal, intersects `protocolVersions` with its own `["v1"]`, bumps the session epoch (`session_epoch = existing + 1`, lease `now + heartbeatSeconds*3` clamped ≥ 15 s, `replica_id` from `Kernel:ReplicaId`), supersedes any older live session, then **immediately pumps pending deliveries from the DB ledger** before answering:

```json
{ "kind": "welcome", "sessionId": "9f2c…", "leaseEpoch": 1, "maxInFlight": 16, "heartbeatSeconds": 30, "protocolVersion": "v1" }
```

**The resume cursor is a hint, the receipt ledger is truth**: `lastKnownAck` is accepted, logged, and never trusted as proof — replay always recomputes the un-acked set from `link_deliveries`.

**Version mismatch (null-return convention):** an empty intersection pushes the client method `Disconnect` carrying the frozen goodbye frame (fixture pair `invalid/control.hello.v9-only.json` → `valid/control.goodbye.incompatible.json`) and `Hello` **returns `null`** instead of a `WelcomeFrame` — the SignalR invocation simply has no result payload; no `link_sessions` row is written (the ephemeral `sessionId` in the goodbye is minted for the frame only):

```json
{ "kind": "goodbye", "sessionId": "7a41…", "reason": "no-compatible-protocol-version" }
```

**Supersede + epoch fencing:** a second `hello` for a live endpoint bumps the epoch and pushes `Disconnect` `{reason:"session-superseded"}` to the old connection on this replica; every other replica's socket is fenced by the DB epoch — a `heartbeat` carrying a stale `leaseEpoch` is refused (hub: `Disconnect` push + `false`; the reserved HTTP shape is 409 `session-superseded`), and delivery claims always charge the epoch of the current live `link_sessions` row, never a client-claimed one. Restarted endpoints re-use the logical `endpointId` and simply get a new session and epoch.

**Lease = liveness:** `lease_until = hello/heartbeat + max(15 s, heartbeatSeconds×3)`. The pump's sweeper deletes expired `link_sessions` rows and pushes `Disconnect` `{reason:"lease-expired"}` to tracked sockets — lease expiry, not TCP, ends a session. A client-initiated `goodbye` (hub method) revokes the session row and acks nothing.

### Hub method set

Server methods `Hello`, `Envelope`, `Receipt`, `Heartbeat`, `Goodbye`; client methods `Welcome` (Hello's return value), `Deliver`, `FlowControl`, `Disconnect` (payloads are the frozen control frames / `link-delivery.v1.json`). The hub is thin transport — no work execution. `Envelope` and `Receipt` share one code path with the HTTP equivalents (`LinkIngress.AdmitAsync`, `LinkLedger.AckAsync`), so both transports answer identically — including the ack's additive optional `data` field (an in-band command payload: a `command.artifact.request_upload` ack carries its upload ticket `{artifactId, uploadLocation, method, expiresAt, uploadUrl?}` on BOTH `accepted` and still-live `duplicate` acks). Frame validation is the embedded-schema `ControlFrameValidator` (`protocol/dotnet/Ofac.Protocol/Validation/`) — the same schemas the harnesses read.

**Deliver frame** — the client-method payload is the SAME JSON object as the OpenAPI `Delivery` component (`protocol/cloudevents-schemas/link-delivery.v1.json`, fixture `valid/link.delivery.json`); durable delivery metadata is `deliveryId` + `deliveredAt`, and receipts reference the durable `deliveryId`, never the socket:

```json
{ "deliveryId": "01JDELIVERY01", "envelope": { "specversion": "1.0", "…": "full CloudEvent" }, "deliveredAt": "2026-10-22T18:00:00Z" }
```

Over SignalR the receipt posts via the `Receipt` method with the full `receipt.v1.json` frame:

```json
{ "deliveryId": "01JDELIVERY01", "status": "accepted", "processedAt": "2026-10-22T18:00:05Z" }
```

**FlowControl pause/resume:** when a session's in-flight count reaches `maxInFlight` the server pushes `{ "kind": "flow_control", "sessionId": "…", "maxInFlight": 0 }` (0 = pause, per the frozen schema); when a receipt frees capacity it pushes `maxInFlight` restored. The frames are advisory pacing hints — the claim-side window (`min(limit, maxInFlight − inFlight)`) is what actually enforces the cap.

**Redelivery:** an `in_flight` row whose `redeliver_after` (= claim + `Link:InFlightLeaseSeconds`, default 120 s) has passed is claimable again — that IS the dropped-receipt mechanism. Delivery identity = the outbox row id, so a receipt arriving for an already-`acked` delivery answers the latest attempt idempotently and the dropped-receipt loop terminates. A `duplicate` receipt terminates redelivery exactly like `accepted` — the client saw the command.

### HTTP fallback surface (link-v1.yaml 1.0.2)

|Endpoint|Auth|Success|Errors|
|---|---|---|---|
|`POST /api/link/v1/enroll`|enrollment secret or workload OIDC token in body|`201` `EnrollResponse {enrollmentId, trustMode, linkToken, expiresAt}`|`400 schema-violation` (missing field, bad JSON), `401 enrollment-secret-invalid` / `workload-token-invalid`, `403 workload-not-registered` / `role-mismatch` / `team-unknown`, `413`|
|`POST /api/link/v1/artifacts/{id}/confirm`|link bearer|`200` `{artifactId, digest, size, state:"stored"}` (settle a direct-to-store upload: the kernel HEADs the digest-derived key, size-checks, re-hashes the bytes itself — provider ETags are never trusted)|`404 artifact-not-found` (foreign-team, unknown, or bytes-never-landed slot), `422 artifact-digest-mismatch` / `artifact-size-mismatch` (slot spent, deny-audited), `409 artifact-not-pending` / `artifact-not-confirmable`|
|`PUT /api/link/v1/artifacts/{id}/content`|link bearer|`200` `{artifactId, digest, size, state:"stored"}` (byte push for a link-originated ticket; the kernel counts + SHA-256-hashes the stream; idempotent re-PUT of a stored row)|`404 artifact-not-found` (foreign-team, unknown, or spent row), `422 artifact-digest-mismatch` / `artifact-size-mismatch`, `413` applies to the envelope cap only — never the artifact bytes (see Artifacts)|
|`GET /api/link/v1/deliveries?wait=0..60&limit=1..64`|link bearer|`200` array of `Delivery` (empty on poll timeout)|`404 session-not-found`, `400 schema-violation` (bad `wait`/`limit`)|
|`POST /api/link/v1/deliveries/{deliveryId}/receipt`|link bearer|`204` (idempotent re-receipt answers `204` again, overwriting the stored receipt jsonb)|`404 delivery-not-found` (unknown id or another endpoint's delivery), `400 schema-violation`, `413`|
|`GET /api/link/v1/capabilities`|link bearer|`200` stored CapabilityManifest|`404 capabilities-not-found`|
|`POST /api/link/v1/capabilities`|link bearer|`204` (stores manifest + SHA-256 digest)|`400 schema-violation`, `403 team-mismatch` (`ownerTeam` ≠ principal's team)|

`wait` long-polls (per-endpoint wake channel + 1 s polling safety net + inline materialize/claim; NOTIFY `facilitator_outbox` is a wake hint only) and `limit` is additionally bounded by `maxInFlight − inFlight`. HTTP principals get no `WelcomeFrame`: enrollment + bearer token IS the session bootstrap, **but a live `link_sessions` row is still REQUIRED for the delivery read** (404 `session-not-found` — enroll-then-poll without a handshake gets the 404; as-built that row is minted by a hub `hello`). Every successful `GET /deliveries` refreshes that row's lease (long-poll IS the heartbeat for a socketless principal). `/capabilities` (GET and POST) needs no session at all. Body size gate (413 at 262144) and dedupe keys are identical to Kernel ingress above; a link-originated `work.steer` runs the same `ApplySteerAsync` path as kernel ingress.

### Error URIs (extends the Kernel ingress table, same ProblemDetails shape)

|Type URI suffix|HTTP|When|
|---|---|---|
|`unsupported`|`422`|Command class outside the v1 contract or `work.assign` FROM a link (fail closed; envelope audited in `inbox` state `failed`)|
|`session-not-found`|`404`|Delivery read with no live `link_sessions` row|
|`delivery-not-found`|`404`|Unknown receipt `deliveryId` or a delivery of another endpoint|
|`capabilities-not-found`|`404`|Enrollment never advertised a manifest|
|`team-mismatch`|`403`|Manifest `ownerTeam` ≠ principal's team|
|`enrollment-secret-invalid`|`401`|Mode-(a) enroll body `token` ≠ `Link:EnrollmentToken`|
|`workload-token-invalid`|`401`|Mode-(b) enroll credential fails JWKS/iss/aud/exp validation|
|`workload-not-registered`|`403`|No `Link:WorkloadIdentities` binding for the token's `(azp, endpointId)`|
|`role-mismatch`|`403`|Body `role` contradicts the binding, a `gw.` endpoint enrolled non-gateway, or rotation tried to re-role|
|`team-unknown`|`403`|Mode-(a) body `team` absent or not in the `Link:Teams` roster|
|`evidence-invalid`|`422`|Command `actorEvidenceRef` of a kernel-checked scheme (`oidc`, `kernel`) failed validation (deny, audited)|
|`session-superseded`|`409`|Reserved HTTP shape; as-built supersede surfaces as a SignalR `Disconnect` goodbye (heartbeat is hub-only)|
|`no-common-version`|`409`|Reserved HTTP shape of the no-compatible-protocol-version goodbye|

The receipt timestamp is the frozen `processedAt` — an early example's `acceptedAt` is superseded by the schema (see Receipts above).

### Liveness, placement, observability

- The pump (`LinkDeliveryPump : BackgroundService`) materializes `link.<endpointId>` outbox rows into the ledger **only while a live session exists**, then claims within the window and pushes `Deliver` — the claim phase runs ONLY for endpoints with a socket tracked on this replica; without one the pass is materialize-only. It runs after every `hello`/receipt, on every `NOTIFY facilitator_outbox`, on a 2 s safety poll, and inline (materialize-only) before an HTTP claim, which `GET /deliveries` then performs itself. Un-acked rows stay non-`delivered` in the outbox while the link is down — no outbox row is ever tied to a dead socket.
- `LinkSink` (`Channel = "link"`) is a routing liveness probe only: live session ⇒ `true` (the pump owns the row via the DB); no session anywhere ⇒ `false` ⇒ the no-route requeue-with-backoff, unchanged.
- The connection registry tracks THIS replica's sockets only; the DB lease is the cross-replica authority.
- No secrets in WebSocket query strings for framework clients: the bearer header rides both the negotiate request and the WebSocket upgrade; `?access_token=` is accepted ONLY as the documented fallback for non-TypeScript clients that cannot set headers on the upgrade.
- Observability: span `link.handshake` on activity source `Facilitator.Kernel` (tags `facilitator.endpoint_id`, `facilitator.session_epoch`, `facilitator.protocol_version`); counter `facilitator.link.received`; gauge `facilitator.link.sessions`; link claims reuse `facilitator.delivery.attempts`.

### Config keys (defaults live in code, `LinkOptions.FromConfiguration`)

|Key|Default|Meaning|
|---|---|---|
|`Link:EnrollmentToken`|dev: `enroll-dev`; else required|Bootstrap enrollment secret (fail-closed startup gate when unset outside Development)|
|`Link:TokenLifetimeHours`|720|Link-token expiry issued at enroll|
|`Link:MaxInFlight`|16|Per-session in-flight window (≥1); advertised in `welcome`, enforced at claim|
|`Link:HeartbeatSeconds`|30 (5..600)|Client heartbeat cadence in `welcome`; SignalR keepalive = this, client timeout = 2×|
|`Link:LeaseSweeperEnabled`|true|Replica's link pump sweeps expired session leases (closes tracked sockets with a typed goodbye); fake-clock test hosts disable it|
|`Link:InFlightLeaseSeconds`|120|Redelivery deadline after each claim|
|`Link:Teams`|`[]`|Team roster slugs seeded at startup; mode-(a) enrollment must name one|
|`Link:WorkloadIdentities`|`[]`|Mode-(b) pre-registrations: `{clientId, endpointId, team, role, acceptsDelegatedClaims}`|
|`Link:Evidence:Audience`|`facilitator-link`|Required `aud` of `oidc:` evidence and mode-(b) enrollment credentials|
|`Link:Evidence:ClockSkewSeconds`|30|Tolerated skew on evidence lifetime checks|

## Client half — Workspace Link SDK

`src/workspace-link` (`@ofac/workspace-link`) is the reference client of the surface above. Entry points: package root (stack-agnostic core: `createLinkClient`, `enroll`, handler/transport interfaces, `FaultInjector`), `/node` (`SignalrTransport`, `HttpPollTransport`, `SqliteLedger`, `postCapabilities`), and the `ofac-linkd` bin (headless supervisor: enroll → capabilities → connect → serve; exit codes 0 clean, 2 config, 3 protocol mismatch, 4 enrollment rejected).

Delivery pipeline law (enforced in `src/core/client.ts`): an inbound delivery is validated against the frozen schemas BEFORE persisting — an unknown COMMAND type enters the ledger and answers `unsupported`, any other violation answers `rejected` with no ledger row; a fresh, valid delivery is persisted to the durable ledger BEFORE its receipt leaves the process, and a failed receipt send never suppresses the handler result (redelivery converges on `duplicate` without re-running the handler). Outbound events sent while disconnected buffer in the same ledger (`ctx.publishEvent` resolves `accepted`; the buffer drains on the next `ready`).

As-built asymmetry on the HTTP fallback: the server gives HTTP principals no WelcomeFrame and no lease row of its own — enrollment plus bearer token is the whole bootstrap, and every deliveries GET implicitly refreshes the session lease. The SDK's `HttpPollTransport` therefore maintains a hidden SignalR "session keeper" (connect + hello) to hold the `link_sessions` row; a `404 session-not-found` re-runs keeper connect + hello before the next poll. Because the keeper socket is a tracked hub connection, the server may push `Deliver` to it (the pump claims for any live socket), so the transport forwards keeper frames into the same client pipeline as poll results — the ledger dedupes either arrival. `linkd --transport http-poll` wires this automatically.

The e2e tier (`src/workspace-link/test/e2e/`, env-gated on `FACILITATOR_URL` + `FACILITATOR_PG`) boots its own Facilitator per file and proves handshake/supersede, delivery+dedupe, receipt-loss redelivery (`duplicate` termination), partition buffering + drain, HTTP-poll keeper revival, and linkd crash replay (SIGSTOP/SIGKILL, un-acked-only replay). CI runs it as the `link-e2e` job; locally `make link-e2e`.

## Artifacts (kernel artifact store)

The kernel is the durable artifact authority: the `facilitator.artifacts` metadata ledger + the `IArtifactStore` byte port (`Kernel/Shared/Storage/`, config-selected adapter: filesystem, blobs under `{root}/`; or s3, any S3-wire endpoint, presigned uploads/downloads — both adapters land blobs at `{hex[0..2]}/{hex}`, paths derived ONLY from hex digests). The upload flow, both planes:

1. **Allocate** — `command.artifact.request_upload.v1` over the Link plane, or its REST twin `POST /api/artifacts/request-upload` for humans. The row lands `pending` (TTL 15 min) and the ticket `{artifactId, uploadLocation, method, expiresAt, uploadUrl?}` returns IN-BAND: the Link ack's `data` field (hub `EnvelopeAck.data` / HTTP 202 body `data`), the REST twin's `201` body. `uploadLocation` is a kernel-relative URL path — `/api/link/v1/artifacts/{id}/content` for link-originated tickets, `/api/artifacts/{id}/content` for REST-originated — so the producer composes it against the base URL it already speaks and authenticates with the token it already holds. When the configured adapter mints presigned URLs (s3), the ticket also carries `uploadUrl` (an absolute presigned PUT to the digest-derived key, expiring with the same 15-min allocation clock) and `method:"PUT"` — callers prefer it; bytes then flow producer→store with zero kernel hops and zero bearer tokens. Re-allocating the same live `commandId` returns the existing ticket (never a second row); a duplicate-envelope replay re-attaches the still-live ticket on the `duplicate` ack.

2. **Push bytes** — either proxied: `PUT {uploadLocation}` with the raw stream; or direct (s3): `PUT {uploadUrl}` straight at the object store, then `POST …/confirm` (Link plane command `command.artifact.confirm_upload.v1` or REST twin `POST /api/artifacts/{id}/confirm`). Both paths land on one law: the kernel counts and SHA-256-hashes the bytes itself — never a provider ETag (multipart ETags are composite) — AT THE BOUNDARY: count ≠ declared `size` or hash ≠ declared `digest` flips the row `pending → rejected` (422, deny-audited, slot spent — a fresh allocation is required); match flips it `pending → stored` + `verified` at the digest-derived key (`{scheme}:{hex[0..2]}/{hex}` — every adapter stores at the declared digest's own key, so a presigned URL can address a blob that doesn't exist yet), content-addressed (identical bytes dedupe across works for free), `artifact.stored` audit + Work-timeline entry same-tx. Re-PUT/re-confirm of a `stored` row answers the idempotent 200. The 262144-byte envelope cap is IRRELEVANT to artifact bytes: they travel by `PUT` to the store, never in the envelope — the cap bounds only the small JSON command.

3. **Declare** — `event.artifact.available.v1` (both ingresses, identical arms): a non-GUID `artifactId` registers an `external` row (`stored`, `storageUri = external:<url>`, the kernel NEVER fetches the bytes); `verification` is the external screen's verdict (below), never a blanket `unverified`. A kernel-allocated GUID confirms — the row is untouched, the timeline notes `artifact.available`; a contradicting re-declaration of a kernel id is 422 `artifact-declaration-mismatch` (first writer owns it).

**External screen**: a declared `storageUrl` is `verified` only when an operator binding (`Storage:ExternalBindings: [{Id, UrlPrefix}]`, prefix match) waives it, or it is absolute http(s) with no userinfo and its host resolves to ONLY public addresses (loopback/private/link-local/unique-local ⇒ `unverified`; DNS failure likewise). The screen never refuses a registration — a miss downgrades and the reason rides the `artifact.registered` / `artifact.external-screened` audit detail.

**Retention GC** (`RetentionGcService`, advisory-lock serialized, default 60 s): rows with `Nd` policies collect once `stored_at + Nd` lapses AND nothing pins them: no live Work references the row (the row's `work_id` is NULL or that Work is terminal) AND no non-superseded `result_manifests` row names it (as `artifact_id` or inside the flattened `output_artifact_ids` — a recorded manifest pins its document AND every declared output regardless of Work state; supersession releases); a delete tombstones the row `deleted` + audit `artifact.gc` (+ timeline note when Work-tied), then removes the blob post-commit — idempotent, a crash between the two re-collects next pass. `permanent` never ages out; `archive` is held (kernel-side hold; tiering is an ops concern). Expired pending slots sweep with no backend call (`reason=expired-slot`). Batch 100/pass.


Reading: `GET /api/artifacts/{id}` (metadata) and `GET /api/artifacts/{id}/content` — object-store adapters answer `302` to a presigned GET (same 15-min TTL law; the redirect carries a `response-content-type` override so the row's media type survives a producer that PUT without one), the filesystem adapter streams with the row's media type + digest `ETag`; gated `artifacts.read` + team-scoped in the ledger — foreign-team and missing answer the same 404 (non-disclosure, the approvals precedent). Port note: an `IArtifactStore.List` method is deliberately absent in the reference implementation — listing queries ride the metadata ledger (a second FS-walk source of truth would duplicate the DB).

Config: `Storage:Provider` (`filesystem` | `s3`; s3 requires `Storage:S3:{Endpoint,Bucket,Region,AccessKey,SecretKey}`, fail-closed at startup) + `Storage:FileSystem:RootPath`; a stored row's scheme decides every later read/confirm/GC action — provider switch mid-life leaves foreign-scheme rows readable-but-inert. Artifact timeline kinds (`artifact.stored`, `artifact.registered`, `artifact.available`, `artifact.gc`) are legal `work_events.kind` values; the gate roles include `team-member` (any `/groups` claim), which is what lets a foreign-team human reach the ledger's 404 instead of the gate's 403 on reads.

## Result manifests

A completed event's completion truth is an immutable, digest-verifiable **Result Manifest document** stored as an artifact — decoupled from the execution container. The event (registry row above) carries `resultManifestRef` + `resultDigest`; the inline `resultManifest` stays as the deprecated carrier so pre-1.1 producers keep working (ref absent ⇒ the kernel records nothing new).

**Four output classes → manifest fields**: structured result → `structuredResult` (compact JSON object, digested on its own as `structured_digest`); document/binary artifact → an `outputs[]` `artifactRef` (`artifactId`, `mediaType`, `size`, `digest`, `uri`, optional producer-taxonomy `kind`/`classification`); external resource → `externalRefs[]` (opaque URIs — recorded, never dereferenced, the SSRF law); human-readable summary → `summary` (bounded narrative, communication only). The vocabulary is declared on `result-manifest.v1.json`; the schema stays open (`additionalProperties: true`) so producers extend it additively. The golden `protocol/conformance-fixtures/canonical/result-manifest-1.json` (+ `.sha256`) is the canonical example of the design's manifest shape — byte-canonical, cross-verified by both harnesses; read it there rather than restating it here.

**Recording** (`ResultManifestLedger`, both-plane arm in `ResultIngress`): a completed event with a ref resolves the ref's LAST path segment as the kernel artifact GUID (the `artifact://` logical-URI law: `artifact://{workId}/{assignmentId}/{artifactId}`), fetches the stored bytes, and requires, in one tx: bytes byte-equal their own RFC 8785 canonical form (the producer stores the document ALREADY canonical — non-canonical bytes are rejected, the kernel never rewrites a manifest); recomputed SHA-256-of-canonical-bytes == the wire `resultDigest` == the artifact row's kernel-verified digest; the document's `manifestId` matches the wire declaration; every `outputs[].artifactId` resolves to a verified, digest-matching, same-team artifact row. Any mismatch ⇒ 422 (`manifest-digest-mismatch` / `manifest-output-unresolvable` / `manifest-not-found`), zero rows, deny audit. Match ⇒ `facilitator.result_manifests` row (revision 1 for a fresh assignment) + `manifest.recorded` timeline + audit. The two digest laws stay distinct: manifest digest = SHA-256 over canonical JSON bytes; artifact digests remain SHA-256-of-bytes.

**Revision law:** the kernel chains revisions per assignment — a second completed event for the same assignment records revision N+1 with `revision_of` = the chain tip, flips the tip `superseded` (+ `manifest.superseded` timeline), and releases the superseded manifest's artifact pins. Rows are immutable; `UNIQUE(assignment_id, revision)`. `structuredResult` (when present) is additionally digested on its own (`structured_digest`) for future output-schema validation.

**Pull surface:** `GET /api/works/{id:guid}/manifests` — the work's chain, newest revision first; gated `manifests.read` (binding mirrors `artifacts.read`, includes `team-member`) + work-scope 404 non-disclosure. The manifest document itself rides the artifact content path: `GET /api/artifacts/{ref-last-segment}/content`. Timeline kinds `manifest.recorded`/`manifest.superseded` are legal `work_events.kind` values. Metrics: `facilitator.manifest.recorded` (tag `origin: link|rest`), `.superseded`, `.digest_mismatch`.
