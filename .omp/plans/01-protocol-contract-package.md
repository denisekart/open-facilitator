# Plan 01 — Protocol contract package: schemas, envelope, conformance fixtures (refined detail design)

**Status:** 🟡 in-progress — all artifacts landed on branch `plan-00-skeleton` (stacked; Plan 00 unmerged) with every local gate green (40/40 fixtures both harnesses, 42 vitest, drift-clean codegen, OpenAPI validates, lint/format clean). 🟢 flips when this branch's CI is green on `main`.
**Depends on:** 00 (🟡 in-progress on `plan-00-skeleton`; root props, slnx, pnpm workspace, CI spine must be on `main` first — if Plan 00 has not merged, block).
**Size judgment:** kept as ONE plan. The schemas, fixtures, validators, and codegen are one frozen artifact set — splitting would ship a half-frozen contract. Steps below are independently verifiable checkpoints.

> Questions may be asked during planning, but not during implementation. Every design decision is locked below; the implementer executes top-to-bottom. Missing detail → infer from `docs/specs/open_facilitator_framework_architecture.md` (the "spec"), `docs/specs/open-facilitator-appendix.md` (the "appendix"), and existing code — do not stall.

## Context

Plan 00 landed the buildable monorepo (`OpenFacilitator.slnx`, `Directory.Build.props`/`Directory.Packages.props` CPM, pnpm workspace with `catalog:`, CI in `.github/workflows/ci.yml`; `protocol/` holds only README stubs). Plan 01 publishes the normative wire contract as machine-readable artifacts before any runtime code, so Plans 02+ (kernel, Link server, Link SDK) reference versioned schemas and typed DTOs instead of inventing wire shapes. End state: CI validates every fixture against every schema on both .NET and TypeScript sides; spec §26.1 item 1 (schema validation, payload limits, major-version negotiation, unknown-command rejection) has its fixture basis.

## Step 0 — Mandatory inspection before writing any file

Execute all of these; record findings in the commit message body:

1. Read spec §5.2 (message classes + envelope example), §5.3 (handshake shapes), §5.4 (idempotency/backpressure/limits), §5.5 (namespacing/versioning); appendix §5 (outbox/inbox), §7 (approval hashing), §8.3 (manifest example), §16.1–16.3 (RFC 8785 rules + canonical approval example + envelope example). Line anchors: spec lines ~224–310, appendix lines ~116–160, ~196–255, ~417–456.
2. Read `.omp/skills/facilitator-protocol/SKILL.md` — the 10 hard rules are the design gate; no schema may violate one.
3. Inspect Plan 00 output to copy conventions, never a second convention: `Directory.Packages.props` (CPM — new `PackageVersion` items, strip `Version=` from csproj), `pnpm-workspace.yaml` `catalog:`, `src/gateway/package.json` (exports/tsup/publishConfig shape to mirror in `@ofac/protocol`), `OpenFacilitator.slnx` (add projects via `dotnet sln add`), `.github/workflows/ci.yml` (extend jobs, don't fork new files unless a new concern), `.editorconfig`.
4. Read `docs/specs/...§15.6` (CapabilityManifest YAML example — source for the capability schema) and appendix §6.1/§6.2 (memory entry + `memory.used`) and §7 (approval records — source for `approval.required`/`approval.revoked`).
5. Consult the official docs (already verified current, Oct 2026 — re-verify flags, don't re-decide): quicktype 26 (`github.com/glideapps/quicktype/releases` — generated-code defaults table, CLI target names and the System.Text.Json option for C#); ajv v8 JSON Schema 2020-12 usage (`ajv.github.io` / repo README, `ajv-formats`); JsonSchema.Net (`github.com/json-everything/json-everything` README); RFC 8785 test vectors (the `bennettfoyles/rfc-8785-test-suite` gist or equivalent — search "RFC 8785 test suite" if the gist moved). If a doc lookup changes a locked decision below (e.g. a quicktype flag name), adapt mechanically; the decision itself stands.
6. Check previously implemented plans for integration points: `.omp/plans/02-transactional-kernel.md` (its inbox/outbox tables key on `(source,id)` + `commandId` — envelope schema must make those fields required), `03-link-server.md` (hello/welcome field set must match its scope list verbatim), `04-workspace-link-sdk.md` (consumes `@ofac/protocol` generated types without remapping — TS types must be exported from the package root).

## Locked decisions (no implementer choice)

- **Source of truth = JSON Schema** (draft 2020-12, `$schema: "https://json-schema.org/draft/2020-12/schema"`). Generated C#/TS are build artifacts committed to the repo; regeneration + `git diff --exit-code` is the drift gate.
- **Codegen tool = quicktype 26** pinned as a root devDependency (`pnpm add -Dw quicktype@^26`), run via root script `protocol:codegen`. TS target: `typescript` (`--just-types`, interfaces only — runtime validation is ajv's job, not generated code). C# target: `csharp` with the System.Text.Json attribute flavor available in quicktype 26 (confirm exact flag from Step 0.5; historically `--csharp-system-json`; namespace `--namespace Ofac.Protocol`).
- **Message size limit = 262144 bytes** (256 KiB) for the full serialized CloudEvents envelope on the wire (spec §5.4 "payload limits"; artifacts never travel inline — appendix §8.1). Single source: the fixture index `limits.maxEnvelopeBytes` (below); both harnesses read it, and the OpenAPI stub documents it in prose.
- **Include `approval.required`, `approval.revoked`, `memory.used` now** (mandated by appendix §7.3 / §6.2 but missing from the roadmap's type list). 25 wire types total.
- **CloudEvents extension attributes are lowercase** per the CloudEvents 1.0 JSON format binding: `traceparent`, `correlationid`, `causationid` on the envelope (spec §5.2 writes them camelCase; the wire names are lowercase — document the mapping in `docs/protocol.md`).
- **Receipts and control frames are plain JSON payloads, not CloudEvents** (spec §5.2's receipt example; §5.3's hello/welcome). They get their own schemas, consumed by Plan 03/04 framing.
- **JSON Schema `additionalProperties: false`** on every `data` payload schema and on receipt/control frame schemas (fail-closed for business payloads); `true` (default) only on the envelope's extension-attribute layer, so future CloudEvents extensions don't break v1 envelopes.

## Step 1 — JSON Schemas (`protocol/json-schema/`)

Files, exact names (one per type; class-prefixed to keep the directory flat and greppable):

- `common.v1.json` — shared `$defs` (no root validation): `artifactRef` = appendix §8.3 manifest shape (`artifactId`, `mediaType`, `size` int≥0, `digest` pattern `^sha256:[0-9a-f]{64}$`, `storageUrl` uri, `createdBy`, `createdAt`); `deliverable` = `{kind: string, mediaType: string}` both required; `evidenceRef` = string pattern `^[a-z0-9-]+:.{1,256}$` (used by `actorEvidenceRef`); `contextVersion` = integer ≥ 0; `rfc3339DateTime` = `{ "type": "string", "format": "date-time" }`; `idToken` = string 1–128 chars.
- Commands (7): `command.work.assign.v1.json`, `command.work.steer.v1.json`, `command.work.cancel.v1.json`, `command.work.status.request.v1.json`, `command.work.nudge.v1.json`, `command.context.fetch.v1.json`, `command.approval.respond.v1.json`.
  - Every command `data` REQUIRES: `commandId` (idToken), `workId` (idToken), `targetId` (idToken), `actorEvidenceRef` (evidenceRef), `deadline` (rfc3339DateTime). Per-type additions:
    - `work.assign`: + `assignmentId`, `capabilityId`, `expectedContextVersion`, `inputs` (object, closed-free: `{ "type": "object" }`), `expectedDeliverables` (deliverable[], minItems 1) — mirror spec §5.2 example.
    - `work.steer`: + `assignmentId`, `directive` (string 1–16384), `expectedContextVersion`.
    - `work.cancel`: + `assignmentId`, `reason` (string ≤1024).
    - `work.status.request`: + `assignmentId`.
    - `work.nudge`: + `assignmentId`, optional `stallEpisode` (idToken) — dedupe tuple §6.4.
    - `context.fetch`: + `contextRefs` (uri[], minItems 1).
    - `approval.respond`: requires `commandId`, `approvalId` (idToken), `decision` (enum `approved|denied`), `actorEvidenceRef` (no `targetId`/`deadline` — the approval record owns routing/expiry); optional `comment` ≤1024.
- Events (11): `event.work.accepted.v1.json`, `event.work.progress.v1.json`, `event.work.input_required.v1.json`, `event.assignment.completed.v1.json`, `event.assignment.failed.v1.json`, `event.workspace.heartbeat.v1.json`, `event.artifact.available.v1.json`, `event.memory.candidate.v1.json`, `event.approval.required.v1.json`, `event.approval.revoked.v1.json`, `event.memory.used.v1.json`.
  - Events carry identity in the envelope (`source`+`id`+`time`); `data` REQUIRED fields:
    - `work.accepted`: `workId`, `assignmentId`, `acceptedAt`.
    - `work.progress`: `workId`, `assignmentId`, `executionGeneration` (int ≥1), `progressNote` (string 1–4096).
    - `work.input_required`: `workId`, `assignmentId`, `question` (string 1–16384).
    - `assignment.completed`: `workId`, `assignmentId`, `executionId`, `executionGeneration` (int ≥1 — fencing, §7.4), `resultManifest` (see below).
    - `assignment.failed`: + `reason` (1–4096), `retryable` (boolean).
    - `workspace.heartbeat`: `workspaceId`, `status` (enum `idle|busy|draining|error`), optional `activeAssignments` (int ≥0).
    - `artifact.available`: `artifactRef` fields inline (require `artifactId`, `mediaType`, `size`, `digest`; `storageUrl` optional on the wire — storage-internal), optional `workId`.
    - `memory.candidate`: `scope` (enum `project|team|workspace|private`), `contentSummary` (1–4096), `provenance` (`{source, evidenceRef, createdBy, createdAt}` all required, appendix §6.1), optional `policyLabel` (enum `private|team-only|facilitator-visible|evidence-only`).
    - `approval.required`: `approvalId`, `actionDigest` (pattern `^sha256:[0-9a-f]{64}$`), `eligibleActors` (idToken[], minItems 1), `nonce` (string 16–128), `expiresAt`, `createdAt`.
    - `approval.revoked`: `approvalId`, `revokedBy` (idToken), `reason` (1–1024).
    - `memory.used`: `memoryId`, `consumer` (idToken), `justification` (1–1024) — appendix §6.2.
- `receipt.v1.json`: required `deliveryId` (idToken), `status` (enum `accepted|duplicate|rejected|unsupported`); allOf conditionals: `accepted|duplicate` ⇒ require `processedAt` (rfc3339DateTime) (spec example names it `acceptedAt` — lock `processedAt`; record the rename in `docs/protocol.md`); `rejected|unsupported` ⇒ require `reason` (1–4096); optional `detail` (object). `additionalProperties: false`.
- `control.v1.json`: `{ "oneOf": [hello, welcome, heartbeat, flow_control, goodbye] }` frames, each `{kind: const}` discriminated, `additionalProperties: false`:
  - `hello`: `protocolVersions` (string[], minItems 1, items pattern `^v[0-9]+$`), `supportedTransports` (enum-members array: `signalr|websocket|http-poll`, minItems 1), `endpointId`, `instanceId`, `capabilityManifestDigest` (sha256 pattern), optional `lastKnownAck` (idToken).
  - `welcome`: `sessionId`, `leaseEpoch` (int ≥1), `maxInFlight` (int ≥1), `heartbeatSeconds` (int 5–600), `protocolVersion` (pattern `^v[0-9]+$`).
  - `heartbeat`: `sessionId`, `leaseEpoch`.
  - `flow_control`: `sessionId`, `maxInFlight` (int ≥0).
  - `goodbye`: `sessionId`, `reason` (1–1024).
- `result-manifest.v1.json`: required `manifestId` (idToken), `outputs` (artifactRef[], minItems 0); optional `revisionOf` (idToken), `notes` (string ≤4096); `additionalProperties: true` (Plan 09 extends additively within v1; §15.3 fullness lands there).
- `capability-manifest.v1.json`: required `apiVersion` (const `facilitator.framework/v1`), `kind` (const `CapabilityManifest`), `ownerTeam` (idToken), `capabilities` (array minItems 1 of `{ id: idToken, version: int≥1, execution: enum synchronous|asynchronous }` + optional `inputSchema` (uri), `acceptedContexts` (enum `uri-reference|inline-text|artifact-reference`[]), `outputKinds` (string[]), `outputMediaTypes` (string[]), `supportsSteering|supportsCancellation|supportsNudge` (boolean)); `additionalProperties: false`. Source: spec §15.6.

`$id` rule for every schema: `https://schemas.openfacilitator.org/v1/<filename>` (e.g. `https://schemas.openfacilitator.org/v1/command.work.assign.v1.json`). Envelope `dataschema` equals the `$id` of the data schema — validators resolve `dataschema` → registry by filename map, never by HTTP fetch. Versioning per §5.5: new major → new filename with `v2`; optional fields are additive within a major.

## Step 2 — Envelope schema (`protocol/cloudevents-schemas/`)

- `envelope.json`, `$id` `https://schemas.openfacilitator.org/v1/envelope.json`. Properties: `specversion` (const `"1.0"`), `id`, `source` (uri-reference, pattern `^urn:` or `^https?:`), `type` (pattern `^org\.facilitator\.(command|event)\.[a-z0-9.]+\.v[0-9]+$`), `subject`, `time`, `datacontenttype` (const `"application/json"`), `dataschema` (uri, pattern `^https://schemas\.openfacilitator\.org/v[0-9]+/.+\.json$`), `data` (type object), extensions `traceparent` (W3C 32hex-16hex pattern), `correlationid`, `causationid` (idToken). Required: `specversion`, `id`, `source`, `type`, `time`, `datacontenttype`, `dataschema`, `subject`. No `additionalProperties` restriction on the envelope root (extension tolerance).
- `envelope-command.json` / `envelope-event.json`: `$ref` envelope + tighten `type` pattern to the class. These two are what the harness validates against (class known from the fixture registry).
- Update the 4 README stubs under `protocol/*/` from "Lands with Plan 01" to as-built descriptions.

## Step 3 — Conformance fixtures (`protocol/conformance-fixtures/`)

Layout: fixture files are **bare wire payloads** (a CloudEvent, receipt, or control frame — directly consumable by Plans 02–04 without unwrapping). Expectations live in one index, `protocol/conformance-fixtures/index.json`:

```json
{
  "version": 1,
  "limits": { "maxEnvelopeBytes": 262144 },
  "cases": [ { "file": "valid/command.work.assign.json", "expect": "valid", "class": "command", "type": "org.facilitator.command.work.assign.v1" } ]
}
```

`expect`: `"valid"` | `"invalid"`. `errorClass` (required when invalid): enum `envelope-violation | schema-violation | payload-too-large | unknown-command | no-common-version`. For `unknown-command`/`no-common-version` cases the index entry also names `receipt` (expected response fixture, below).

Case set (file `valid/…json`, `invalid/…json`):
- Valid: one per wire type — 7 commands, 11 events, 4 receipts, 5 control frames (27 files). Content: reuse spec §5.2/appendix §16.3 values for `work.assign` (byte-identical `data` to spec example plus `subject`), so docs and fixtures agree.
- Valid payload-limit: `valid/command.work.assign.large.json` — `inputs` blob sized to ~250 KiB, total serialized ≤ `maxEnvelopeBytes`.
- Invalid (≥ 9): `invalid/envelope.specversion.json` (`specversion: "1.1"`); `invalid/envelope.missing-subject.json`; `invalid/command.assign.no-command-id.json`; `invalid/command.assign.no-evidence.json` (drops `actorEvidenceRef`); `invalid/command.assign.unknown-field.json` (extra key in `data` → closed-model violation); `invalid/event.progress.zero-generation.json` (`executionGeneration: 0`); `invalid/receipt.bad-status.json` (`status: "maybe"`); `invalid/receipt.unsupported-no-reason.json`; `invalid/payload.too-large.json` (~300 KiB, index entry carries `"errorClass": "payload-too-large"`).
- Behavior fixtures: `invalid/command.unknown.v1.json` — envelope with `type: org.facilitator.command.novel.thing.v1`, index `expect: invalid`, `errorClass: unknown-command`, `receipt: valid/receipt.unsupported.json`; `invalid/control.hello.v9-only.json` — hello offering `["v9"]`, `errorClass: no-common-version`, `receipt: valid/control.goodbye.incompatible.json` (goodbye frame, reason `no-compatible-protocol-version`).
- Canonicalization goldens: `canonical/approval-prehash.json` (appendix §16.2 payload verbatim) + `canonical/approval-prehash.sha256` (single line `sha256:<hex>`) + `canonical/approval-record.json` (appendix §7.2 shape). Digest computed once at implementation (Step 5 harness computes it; commit the value) — both harnesses must reproduce it, cross-checking the two RFC 8785 implementations against each other AND the golden.

## Step 4 — TypeScript package `@ofac/protocol` (`src/protocol/`)

New package mirroring `src/gateway/package.json` conventions (type module, tsup esm+cjs+dts, `publishConfig` GitHub Packages, version `0.0.0`, private). Add `- "src/protocol"` to `pnpm-workspace.yaml` `packages:`.

- Deps (add to pnpm `catalog:`): `ajv@^8` (+ `ajv-formats@^3`), `canonicalize@^2` (RFC 8785; verified active Aug 2026) — devDependencies of the package are `quicktype` (root-ws) for codegen.
- `src/schemas.ts`: every schema JSON imported via `fs.readFileSync` + `JSON.parse` at build-less runtime is wrong for a published pkg — instead ship schemas as `.json` files included in `files:` and load with `import … with { type: "json" }` (Node 22 import attributes; tsup passes through). Registry export `schemaById(id: string): object | undefined` mapping `$id` → schema.
- `src/validate.ts`: `validateEnvelope(env: unknown): { ok: true } | { ok: false; errorClass: "envelope-violation" | "schema-violation"; message: string }` — ajv (2020-12 + formats) compiles envelope-command/event + all data schemas once per process; class picked from `type` segment; data schema resolved via `schemaById(env.dataschema)`, unknown `$id` ⇒ `schema-violation`. Plus `validateReceipt(v)`, `validateControlFrame(v)` (both against their schemas + `errorClass: "schema-violation"`). Plus `enforceSizeLimit(bytes: number, max: number): boolean` — pure check, harnesses do byte counting.
- `src/generated/index.ts` + per-type interfaces from quicktype (Step 5), re-exported from `src/index.ts` root entry (Plan 04 consumes root exports only). Root exports also: validators, registry, and `KNOWN_COMMAND_TYPES: readonly string[]` derived from the registry (Plan 03's fail-closed table).
- `test/fixtures.test.ts` (vitest): reads `../../protocol/conformance-fixtures/index.json` (path via `new URL`), for each case: serialize fixture (JSON.stringify, no prettify), enforce size, run validator, assert `expect` + `errorClass`; for `unknown-command`/`no-common-version` also validate the named response fixture as a valid receipt/control frame. Canonicalization test: load `canonical/approval-prehash.json`, `canonicalize()` → sha256 hex → must equal golden file.
- `package.json` scripts: `build`, `test` per gateway pattern; `files` includes `dist` + `schemas`.

## Step 5 — .NET side: `Ofac.Protocol` classlib + `Validate.Fixtures` CLI

- `protocol/dotnet/Ofac.Protocol/Ofac.Protocol.csproj`: `Microsoft.NET.Sdk`, net10.0 from `Directory.Build.props`, no deps for DTOs. Add to slnx.
- `protocol/tools/Validate.Fixtures/Validate.Fixtures.csproj`: `Microsoft.NET.Sdk` console; refs: `Ofac.Protocol`, `JsonSchema.Net` (add `<PackageVersion>` to `Directory.Packages.props` with the version resolved at implementation — never guessed; same procedure Plan 00 used). Canonicalization: try `Jcs.Net` (nuget, RFC 8785, zero-dep, v0.1.x); if it fails the RFC test vectors, hand-roll a ~100-line canonical serializer in `Validate.Fixtures/CanonicalJson.cs` (sorted keys, NFC, round-trip-shortest doubles via `double.ToString("R")` invariant, UTF-8 no BOM) and validate against the RFC 8785 test vectors found in Step 0.5. Contingency pre-decided: hand-roll is the fallback, not a question.
- CLI (`dotnet run --project protocol/tools/Validate.Fixtures -- [fixturesDir])`, default `protocol/conformance-fixtures`): load `index.json` (same semantics as vitest suite, same assertions incl. canonical golden + response-fixture cross-validation); print one line per case `[PASS|FAIL] <file> <expect>/<errorClass>`; exit non-zero on any FAIL. This is spec §26.1 item 1's .NET harness and Plan 02's inbox fixture source.
- Generated C# DTOs land in `protocol/dotnet/Ofac.Protocol/Generated/*.cs` (Step 6). DTO round-trip check inside the CLI: every valid envelope fixture deserializes to the generated envelope/DTO types and re-serializes with `JsonSerializerOptions` (camelCase OFF — wire is lowercase as-is; verify each fixture property survives, assert via `JsonNode.DeepEquals` on the two DOMs).
- Behavioral smoke beyond build: `dotnet run --project protocol/tools/Validate.Fixtures` green AND a deliberate corruption (truncate one fixture into a tmp copy via `--fixturesDir /tmp/...`) fails — run this once at implementation.

## Step 6 — Codegen + drift gate

Root `package.json` script:
```
"protocol:codegen": "node protocol/tools/codegen.mjs"
```
`protocol/tools/codegen.mjs` (new, ~60 lines): globs `protocol/json-schema/*.json` + `protocol/cloudevents-schemas/*.json`; invokes the pinned quicktype CLI (via `npx quicktype` / imported `QuickType.*` API — decide by the tool's current CLI surface from Step 0.5) twice, targets `typescript --just-types` → per-schema `.ts` into `src/protocol/src/generated/` (filename-derived type names: `command.work.assign.v1.json` → `export interface WorkAssignCommandV1Data`), and `csharp` (System.Text.Json flavor, namespace `Ofac.Protocol`) → `protocol/dotnet/Ofac.Protocol/Generated/`. Deterministic output ordering: sort the schema list. Run prettier over generated TS afterward.
- Envelope DTOs: generate from `envelope-command.json`/`envelope-event.json` only (not per-type envelope subclasses — `data` stays `JsonElement` / `object` on the C#/TS envelope DTO; class-specific DTOs are for the payloads).
- Drift gate (CI): regenerate + `git diff --exit-code -- src/protocol/src/generated protocol/dotnet/Ofac.Protocol/Generated`.
- Generated-code hygiene: `.editorconfig` append `[**/Generated/**.cs]` `generated_code = true` (dotnet format skips) and add `src/protocol/src/generated` + `protocol/dotnet/Ofac.Protocol/Generated` to `.eslintignore` equivalents (`eslint.config.mjs` ignores list) + `.prettierignore` NOT (prettier formats generated TS as part of codegen).
- `make` targets: root `Makefile` gains `protocol: pnpm protocol:codegen` and hooks drift into `lint` (`pnpm protocol:codegen && git diff --exit-code -- …`).

## Step 7 — OpenAPI stub (`protocol/openapi/link-v1.yaml`)

OpenAPI 3.1. Paths (spec §5.3 verbatim surface): `POST /api/link/v1/enroll` (body `{token, endpointId, instanceId, trustMode enum shared-runtime|separate-process-same-host|isolated-executor}` → 201 `{enrollmentId, trustMode}`), `POST /api/link/v1/events` (body = envelope schema by `$ref` to `../cloudevents-schemas/envelope.json` via `$ref` file path — OpenAPI 3.1 supports JSON Schema refs; responses 202 `receipt.v1.json` ref), `GET /api/link/v1/deliveries` (query `wait` int 0–60, `limit` int 1–64 → 200 array of `{deliveryId, envelope, deliveredAt}`), `POST /api/link/v1/deliveries/{deliveryId}/receipt` (body receipt minus deliveryId → 204), `GET /api/link/v1/capabilities` (→ `capability-manifest.v1.json` ref). Components `$ref` the Step 1 schema files. Prose: 256 KiB limit, bearer auth placeholder securityScheme, "implemented in Plan 03; Plan 12 extends with admin surface". Stub validated in CI: add `pnpm add -Dw @readme/openapi-parser`? — NO, boring choice: validate via `npx @apidevtools/swagger-cli@^4 validate protocol/openapi/link-v1.yaml` (pin exact resolved version as devDependency) in the `meta` CI job (yamllint job already exists).

## Step 8 — CI wiring (extend `.github/workflows/ci.yml`)

- `dotnet` job: append step `dotnet run --project protocol/tools/Validate.Fixtures -c Release` after tests.
- `typescript` job: nothing new — `pnpm -r build/test` picks up `@ofac/protocol` (its vitest IS the TS fixture harness).
- New `protocol` job (or extend `meta`): checkout → pnpm setup (reuse typescript job steps) → `pnpm install --frozen-lockfile` → `pnpm protocol:codegen` → `git diff --exit-code -- src/protocol/src/generated protocol/dotnet/Ofac.Protocol/Generated` → `npx swagger-cli validate protocol/openapi/link-v1.yaml`. Keep actionlint/yamllint green (workflow-file changes are linted by the existing meta job).

## Step 9 — Docs, skill, and status bookkeeping

- `docs/protocol.md` (new): envelope + attribute table incl. lowercase extension mapping and the `processedAt` receipt rename note; 25-type table (class, type string, `$id`, required-field summary); namespacing/versioning discipline per §5.5 ("adding a v2 type" walkthrough: new filename → additive-or-new-major decision → regenerate → fixtures); handshake + receipt + control frame JSON (appendix §16.3 example promoted to fixture reference); 256 KiB limit + artifact-reference rule; how Plans 02+ consume (`schemaById`, `KNOWN_COMMAND_TYPES`, fixture index).
- `.omp/skills/facilitator-protocol/SKILL.md`: replace spec-proposal wording where now implemented — point at `protocol/` paths, `$id` rule, index.json format, error-class enum, size limit (AGENTS.md maintenance rule, same change).
- `.omp/plans/01-protocol-contract-package.md`: replace entirely with this approved plan's content + filled **Implementation Status** (below) at 🟢. `IMPLEMENTATION_ROADMAP_PLAN.md` Plan 01 entry → 🟢. Roadmap's 03 gate references `hello`/`welcome` shapes: unchanged (locked identical).
- `README.md`: mark `protocol/` row as populated. `docs/contributing.md`: add "changing the wire contract" checklist (edit schema → `make protocol` → add fixtures → CI drift+both harnesses green).

## Implementation Status

Updated by the implementer in-place as steps complete (states: Not started | In progress | Partially implemented | Fully implemented | Blocked — reason). Initial:

|Step|State|
|---|---|
|0 Inspection|Not started|
|1 json-schema|Not started|
|2 envelope|Not started|
|3 fixtures|Not started|
|4 @ofac/protocol (TS)|Not started|
|5 Ofac.Protocol + Validate.Fixtures (.NET)|Not started|
|6 codegen + drift|Not started|
|7 openapi stub|Not started|
|8 CI|Not started|
|9 docs/skill/status|Not started|

## Critical files & anchors

|File / anchor|Why|
|---|---|
|`docs/specs/open_facilitator_framework_architecture.md:240-310`|§5.2–5.5: type list, envelope example, receipt form, fail-closed rule — every schema traces here|
|`docs/specs/open-facilitator-appendix.md:417-456`|§16: canonical rules, approval pre-hash golden, envelope example|
|`Directory.Packages.props` + `pnpm-workspace.yaml`|CPM + catalog: the only places versions may be introduced|
|`src/gateway/package.json`|shape to copy for `src/protocol/package.json`|
|`.github/workflows/ci.yml`|extension point for Step 8|

## Verification

Run at repo root, in order (each gates its step):

1. `pnpm install && pnpm protocol:codegen && git status --porcelain src/protocol/src/generated protocol/dotnet/Ofac.Protocol/Generated` — codegen deterministic; rerun → empty diff (drift gate locally).
2. `pnpm --filter @ofac/protocol test` — vitest suite: all 27+ valid fixtures validate; each invalid fixture fails with its exact `errorClass`; `invalid/command.unknown.v1.json` cross-validates the `unsupported` receipt; the `v9`-only hello cross-validates the goodbye frame; canonical golden matches (`sha256:` hex equality). Concrete input: `invalid/command.assign.no-command-id.json` → fail with `schema-violation`.
3. `dotnet build OpenFacilitator.slnx -c Release && dotnet test OpenFacilitator.slnx -c Release --no-build` — classlib compiles, existing health test still green.
4. `dotnet run --project protocol/tools/Validate.Fixtures -c Release` — exit 0, ≥ 40 `[PASS]` lines. Then corrupt: copy fixtures to a temp dir, delete `commandId` from the copy of `valid/command.work.steer.json`, `dotnet run … -- /tmp/fixtures` → non-zero exit, `[FAIL] valid/command.work.steer.json`.
5. `npx swagger-cli validate protocol/openapi/link-v1.yaml` → valid.
6. `make lint && make build && make test` — full spine green incl. new `.editorconfig`/eslint ignores for generated code.
7. Push PR: CI `dotnet`, `typescript`, `protocol` jobs green — that is the slice exit for §26.1 item 1's fixture basis.

## Assumptions & contingencies

- Plan 00 is merged to `main` (its CI spine + props are hard prerequisites). If not yet merged: block, do not recreate them locally.
- quicktype 26 C# System.Text.Json flag differs from `--csharp-system-json`: adapt the codegen script flag only; committed-output drift gate unchanged.
- `Jcs.Net` fails RFC 8785 vectors → hand-rolled `CanonicalJson.cs` (decision locked in Step 5), cross-checked by the shared golden digest file.
- `canonicalize` npm pkg conflicts with Node 22 import attributes or ESM: pin/fork-in `src/protocol/src/jcs.ts` (own 80-line impl) as fallback; golden digest still arbitrates.
- If pnpm catalog resolution rejects a new dep version, adjust the catalog entry to the resolvable one (never an in-package `version` literal) — record resolved versions in the commit message, per Plan 00 precedent.
- If fixture byte-size tests flake from JSON.stringify spacing, the harness compares `Buffer.byteLength(JSON.stringify(payload))` — no pretty-printing anywhere in the fixture pipeline.
- Schemas use `format: "date-time"` — enforced via `ajv-formats` on TS and JsonSchema.Net's format vocabulary on .NET; both MUST fail `"2026-13-01T00:00:00Z"` (add as 10th invalid fixture `invalid/event.bad-time.json`, `errorClass: schema-violation`).
