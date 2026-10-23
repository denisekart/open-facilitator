# Wire protocol (v1)

This document describes the implemented wire contract: the JSON Schemas under `protocol/`, the conformance fixtures, and the rules both endpoints MUST follow. It is the as-built companion to spec §5 (`docs/specs/open_facilitator_framework_architecture.md`) and appendix §16 (`docs/specs/open-facilitator-appendix.md`); where the spec proposes and this document records, this document reflects the shipped artifacts. The framework runtime that consumes the contract today is the Plan 02 transactional kernel (see Kernel ingress below); the Link transport (Plan 03) and the Workspace Link SDK (Plan 04) are the next consumers.

## Message classes and envelopes

Every domain message on the wire is one of four classes:

|Class|Wire form|Schema|
|---|---|---|
|command|CloudEvents 1.0 structured-JSON envelope|`protocol/cloudevents-schemas/envelope-command.json`|
|event|CloudEvents 1.0 structured-JSON envelope|`protocol/cloudevents-schemas/envelope-event.json`|
|receipt|plain JSON object (not a CloudEvent)|`protocol/json-schema/receipt.v1.json`|
|control frame|plain JSON object, `kind`-discriminated (`hello`, `welcome`, `heartbeat`, `flow_control`, `goodbye`)|`protocol/json-schema/control.v1.json`|

The base envelope (`protocol/cloudevents-schemas/envelope.json`) requires `specversion` (const `"1.0"`), `id`, `source` (`urn:` or `http(s):`), `type`, `time`, `datacontenttype` (const `application/json`), `dataschema`, and `subject`. Extension attributes on the envelope are lowercase per the CloudEvents 1.0 JSON format binding: `traceparent`, `correlationid`, `causationid` (spec §5.2 writes the latter two camelCase; the wire names are lowercase). The envelope root does NOT restrict additional properties, so future CloudEvents extensions do not break v1 readers; every `data` payload schema and the receipt/control schemas DO set `additionalProperties: false` — business payloads are closed.

Receipts carry `deliveryId` and `status` (`accepted|duplicate|rejected|unsupported`). `accepted`/`duplicate` MUST include `processedAt`; `rejected`/`unsupported` MUST include `reason`. Note the deliberate rename from the spec §5.2 example, which named the timestamp `acceptedAt`.

## Namespacing and versioning (spec §5.5)

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
|`org.facilitator.event.work.accepted.v1`|event|`event.work.accepted.v1.json`|`workId`, `assignmentId`, `acceptedAt`|
|`org.facilitator.event.work.progress.v1`|event|`event.work.progress.v1.json`|`workId`, `assignmentId`, `executionGeneration`, `progressNote`|
|`org.facilitator.event.work.input_required.v1`|event|`event.work.input_required.v1.json`|`workId`, `assignmentId`, `question`|
|`org.facilitator.event.assignment.completed.v1`|event|`event.assignment.completed.v1.json`|`workId`, `assignmentId`, `executionId`, `executionGeneration`, `resultManifest`|
|`org.facilitator.event.assignment.failed.v1`|event|`event.assignment.failed.v1.json`|`workId`, `assignmentId`, `executionGeneration`, `reason`, `retryable`|
|`org.facilitator.event.workspace.heartbeat.v1`|event|`event.workspace.heartbeat.v1.json`|`workspaceId`, `status` (`idle|busy|draining|error`)|
|`org.facilitator.event.artifact.available.v1`|event|`event.artifact.available.v1.json`|`artifactId`, `mediaType`, `size`, `digest`|
|`org.facilitator.event.memory.candidate.v1`|event|`event.memory.candidate.v1.json`|`scope`, `contentSummary`, `provenance`|
|`org.facilitator.event.approval.required.v1`|event|`event.approval.required.v1.json`|`approvalId`, `actionDigest`, `eligibleActors`, `nonce`, `expiresAt`, `createdAt`|
|`org.facilitator.event.approval.revoked.v1`|event|`event.approval.revoked.v1.json`|`approvalId`, `revokedBy`, `reason`|
|`org.facilitator.event.memory.used.v1`|event|`event.memory.used.v1.json`|`memoryId`, `consumer`, `justification`|

"Common five" = `commandId`, `workId`, `targetId`, `actorEvidenceRef`, `deadline`.

Supporting schemas, referenced by the types above and by the HTTP surface:

|Schema file|Purpose|
|---|---|
|`receipt.v1.json`|Delivery receipt for commands and events|
|`control.v1.json`|Handshake and session frames: `hello`, `welcome`, `heartbeat`, `flow_control`, `goodbye`|
|`result-manifest.v1.json`|`manifestId`, `outputs` (artifact refs); open model (`additionalProperties: true`) so Plan 09 extends it additively|
|`capability-manifest.v1.json`|Advertised capabilities (`apiVersion` `facilitator.framework/v1`), closed model; served by `GET /api/link/v1/capabilities`|
|`common.v1.json`|Shared `$defs` (`artifactRef`, `deliverable`, `evidenceRef`, `idToken`, `rfc3339DateTime`); no root validation|

Events carry identity in the envelope (`source` + `id` + `time`), so their `data` payloads do not repeat it.

## Payload limits and canonical serialization

- The serialized envelope MUST NOT exceed 262144 bytes. The single source of truth is `limits.maxEnvelopeBytes` in `protocol/conformance-fixtures/index.json`; both harnesses read it, and `protocol/openapi/link-v1.yaml` documents it in prose. Large artifacts travel by reference (`artifactRef`, appendix §8), never inline.
- Approval hashing uses RFC 8785 JSON canonicalization (appendix §16.1): sorted keys, shortest-form numbers, NFC strings, no insignificant whitespace. The golden payload `protocol/conformance-fixtures/canonical/approval-prehash.json` and its digest `approval-prehash.sha256` cross-check the two independent implementations (`canonicalize` in the TS harness, `Jcs.Net` in the .NET harness) against each other and the committed value.
- `date-time` strings are validated against RFC 3339 by both harnesses (fixture `invalid/event.bad-time.json` proves month `13` is rejected).

## Conformance fixtures

`protocol/conformance-fixtures/index.json` lists every case with its expectation (`valid` or `invalid` plus an `errorClass` from `envelope-violation | schema-violation | payload-too-large | unknown-command | no-common-version`). Fixture files are bare wire payloads, consumable by later plans without unwrapping. `unknown-command` and `no-common-version` cases name the expected response fixture, which the harnesses cross-validate. Two harnesses consume the same index:

- TypeScript: `pnpm --filter @ofac/protocol test` (vitest, `src/protocol/test/fixtures.test.ts`)
- .NET: `dotnet run --project protocol/tools/Validate.Fixtures -c Release`

## Adding a v2 type

1. Author the payload schema in `protocol/json-schema/` as a new file with a `v2` filename and `$id`. Decide explicitly: additive optional fields stay in v1; anything breaking gets the new major.
2. If the type introduces a new class, add a matching envelope in `protocol/cloudevents-schemas/`; otherwise the existing command/event envelopes already accept the new `type` pattern.
3. Add at least one valid fixture and one invalid fixture under `protocol/conformance-fixtures/`, registering each in `index.json` with its class, type, and error class.
4. Run `pnpm protocol:codegen` (or `make protocol`) and commit the regenerated trees (`src/protocol/src/generated/`, `protocol/dotnet/Ofac.Protocol/Generated/`, `src/protocol/schemas/`) — CI fails on drift.
5. Both harnesses MUST pass: `dotnet run --project protocol/tools/Validate.Fixtures -c Release` and `pnpm -r test`.

## Transport neutrality

The contract is transport-independent: the same fixtures MUST validate identically over the SignalR reference transport (Plan 03) and the HTTP long-poll fallback stubbed in `protocol/openapi/link-v1.yaml` (enroll, events, deliveries, receipts, capabilities). Consumers should use the `@ofac/protocol` package (`src/protocol/`): `schemaById` resolves a `dataschema` URI to its schema, `KNOWN_COMMAND_TYPES` is the fail-closed command table, and `validateEnvelope` / `validateReceipt` / `validateControlFrame` return the same `errorClass` taxonomy as the fixtures index.

## Kernel ingress (implemented, Plan 02)

The Facilitator kernel (`src/facilitator`, namespace `Facilitator.Kernel`) consumes the contract over HTTP today. All endpoints sit under `/api` and require `Authorization: Bearer <token>` (interim bootstrap token; see [`security.md`](security.md)). Errors are RFC 9457 `ProblemDetails` with `type` URIs under `https://errors.openfacilitator.org/v1/`.

### Endpoints

|Endpoint|Purpose|Success|
|---|---|---|
|`POST /api/works`|Create a Work (`{"objective": "...", "projectId": uuid?}`); participates in dedupe via a synthetic inbox row|`201` + `WorkDto`|
|`GET /api/works`|List newest 50 Works|`200` array|
|`GET /api/works/{id}`|Fetch one Work|`200` `WorkDto {workId, projectId, objective, state, version, createdAt, updatedAt}`|
|`POST /api/works/{id}/steer`|Steer (`{"directive", "commandId", "expectedVersion"}`)|`200` `WorkDto` (version bumped)|
|`POST /api/works/{id}/transitions`|Operator-driven state walk (`{"to", "expectedVersion", "reason?"}`), guarded by the §4.1 transition table|`200` `WorkDto`|
|`GET /api/works/{id}/timeline`|`work_events` rows, ascending|`200` array|
|`POST /api/ingress/events`|Raw CloudEvents 1.0 structured-JSON envelope (any type in the registry above)|`200 {"status":"accepted","inboxId":...}` or `200 {"status":"duplicate",...}`|
|`GET /api/dev/deliveries`|Outbox delivery ledger dump (Plan 12 replaces with admin views)|`200` array|

### Error contract

|Type URI suffix|HTTP|When|
|---|---|---|
|`invalid-state-transition`|`422`|Edge illegal per the §4.1 guard table; message names the illegal edge|
|`version-conflict`|`409`|`expectedVersion` mismatch (problem body carries `currentVersion`)|
|`unknown-command`|`422`|Command type outside the known-command table (fail closed, §5.5); the envelope is still persisted to `inbox` state `failed` as an audit record|
|`schema-violation`|`400`|Envelope well-formed, `data` payload violates its `dataschema`|
|`envelope-violation`|`400`|CloudEvents envelope violates `protocol/cloudevents-schemas/*`|
|`payload-too-large`|`413`|Serialized body exceeds 262144 bytes|
|`unauthorized`|`401`|Missing/wrong bearer token|
|`work-not-found`|`404`|Unknown Work id|

A duplicate admission is **not** an error: `200 {"status":"duplicate"}`.

### Dedupe semantics (spec §7.1, appendix §5.6)

Admission is idempotent on three independent keys, all enforced by PostgreSQL uniqueness in the `facilitator.inbox` table:

1. CloudEvents identity: `UNIQUE (event_source, event_id)`.
2. Business-command identity: `inbox.command_id` unique — a re-posted `commandId` returns the stored duplicate outcome without re-executing the command.
3. Provider webhooks: `UNIQUE (provider, provider_delivery_id)` where both are supplied (`provider_events` audit row records the full source).

A duplicate returns `200 {"status":"duplicate"}` with zero reprocessing — no second timeline row, no second outbox row. Never dedupe on textual content.

### Size limit

`POST /api/ingress/events` reads bodies with a 262144-byte cap, the same value as `limits.maxEnvelopeBytes` in `protocol/conformance-fixtures/index.json`; oversize bodies get `413` `payload-too-large` before any parsing.

### Validation at ingress

`POST /api/ingress/events` validates against the exact same schema files as the harnesses: `protocol/json-schema/*` and `protocol/cloudevents-schemas/*`, shipped as `EmbeddedResource`s of `Ofac.Protocol` (the container image can't read `protocol/` at runtime) and enforced by the shared `EnvelopeValidator` in `protocol/dotnet/Ofac.Protocol/Validation/EnvelopeValidator.cs`. It returns the same `errorClass` taxonomy as the fixture index (`envelope-violation | schema-violation | payload-too-large | unknown-command`). Commands outside the fail-closed 7-type known-command table (`Validation/KnownCommands.cs`, derived from the same list as `KNOWN_COMMAND_TYPES` in `src/protocol/src/validate.ts`) are rejected `422` and audited in the inbox. Of the seven known commands, `work.steer.v1` is the only one with a business effect this slice; the rest admit (`202`-path, inbox row `processed`) without side effects, typed rather than silent.

The kernel's own emission path builds outbox envelopes with the same validator before insert — the kernel emits conformant envelopes by construction. Kernel integration tests consume real files from `protocol/conformance-fixtures/` as live inputs to `/api/ingress/events` (e.g. `valid/command.work.steer.json` posted twice proves the duplicate path; `invalid/command.unknown.v1.json` proves fail-closed `unknown-command`; `invalid/envelope.specversion.json` proves `400 envelope-violation`), so the fixtures gate the runtime, not just the harnesses.
