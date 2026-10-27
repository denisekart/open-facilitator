# Plan 13 — Artifact store core: IArtifactStore, filesystem backend, upload flow, artifact.available wiring

**Status:** ⬜ Not started (🔵 replanning → 🟡 in-progress → 🟢 done via the Implementation Status table below).
**Phase:** D (first). **Depends on:** 03 (Link server, as-built); consumes Plan 09's role gate + Plan 10's workload/evidence plane + Plan 11's ledger pattern, all 🟢.
**Branching:** work and commit **directly on `main`** (standing directive from Plans 03–08). Do not push.
**Questions may be asked during planning, but not during implementation.** In implementation, prefer straightforward fixes consistent with this plan; if several iterations fail, step back and simplify rather than adding machinery.
**Vendor law (standing directive from 09):** the kernel never names a vendor; it speaks the S3 wire standard (S3-compatible) as a *later* backend — this plan ships the port plus the filesystem adapter only.
**Additional resources:** spec §15.3–15.5; appendix §8.3 manifest example (the shape 15 will promote to a golden); Plan 11's status table (amendment + ledger precedent, `.omp/plans/11-approval-lifecycle.md`); `protocol/json-schema/event.artifact.available.v1.json` + `common.v1.json`; Npgsql/EF Core migration conventions already in `src/facilitator/Migrations/`.
**Architecture fit:** spec §15.4 + appendix §8.1–8.2 — the kernel becomes the durable artifact authority at the heart of "durable typed outputs as the completion truth, decoupled from execution containers"; it is the data-services floor Phase D stands on and the store every later producer (15's manifests, 16's memory `fullTextRef`, the provider adapters' PR/evidence refs) writes through.

## Objective

Make the kernel the artifact authority (spec §15.4, appendix §8): a durable `facilitator.artifacts` metadata ledger + an `IArtifactStore` port (`Put/Get/List/Delete/VerifyDigest`) with a content-addressed filesystem adapter, an upload admission flow (`command.artifact.request-upload` over Link + a REST pair for humans/Gateway-relayed workers), digest verification at ingest (SHA-256 of received bytes must equal the declared digest), and real handling of the already-frozen `event.artifact.available` — recorded, same-tx outbox-linked, visible on the Work timeline. After this plan bytes uploaded through the kernel survive worker destruction and are retrievable by `artifactId` with authorization; S3 backends, signed URLs, and `artifact://` resolution are the next plans' scope.

## Expected state after

- `facilitator.artifacts` table (new migration): `artifactId` (PK), optional `workId`/`assignmentId`, `mediaType`, `size`, `digest` (`sha256:[0-9a-f]{64}`), `storageUri`, `state` (`pending→stored→deleted`), `createdBy` (effective principal form), `createdAt`, `storedAt`, `retentionPolicy`. Same-transaction audit rows for every state change.
- `IArtifactStore` in `Kernel/Shared/Storage/` + `FileSystemArtifactStore` (config `storage:filesystem:rootPath`, content-addressed `{root}/{digest-hex-prefix}/{digest-hex}` so dedupe is free); the store NEVER names S3/SeaweedFS.
- Link inbound arm `command.artifact.request-upload` (new schema, both command tables): admission-screened, allocates a `pending` artifact row + kernel-relative upload location, audited. REST `PUT /api/artifacts/{id}/content` (workload-or-human, mediaType + declared digest headers or body fields): kernel streams to store, recomputes digest, mismatch ⇒ 422 typed `artifact-digest-mismatch` + row `rejected`; match ⇒ `stored` + `artifact.available` event recorded from the same transaction (inbox-law dedupe on the command's `commandId`).
- `event.artifact.available` stops being a fall-through timeline note: the LinkIngress/CloudEventIngress arm records the artifact row (register-by-reference for externally-stored artifacts with an unverified `storageUrl` marked `unverified`), links `workId`, emits timeline + outbox.
- `GET /api/artifacts/{id}` (metadata) and `GET /api/artifacts/{id}/content` (bytes, FS-served) behind new gate bindings `artifacts.read` / `artifacts.write` in the role-gate config; cross-team read ⇒ 404 (non-disclosure, mirroring the approvals work-scope 404 precedent).
- No `Storage:*` S3 keys, no presigned URLs, no `artifact://` resolver yet — those are Plans 14/15; the `file://` worker interim stays untouched here.

## Known facts (verified 2026-10-27 against as-built; re-verify in Step 0, do not re-decide; code wins — divergence goes in the status table)

1. Frozen contract already carries the Phase D event half: `protocol/json-schema/event.artifact.available.v1.json` (required `artifactId, mediaType, size, digest` with `^sha256:[0-9a-f]{64}$`; optional `storageUrl`, `createdBy`, `createdAt`, `workId`), `common.v1.json#/$defs/artifactRef` (seven required fields), fixtures `valid/event.artifact.available.json`. There is NO `command.artifact.*` schema anywhere.
2. New inbound command = three synchronized touchpoints: schema file under `protocol/json-schema/` (TS known-command set derives from `$id` containing `/command.`, `src/protocol/src/validate.ts:53-55`), hand-maintained `protocol/dotnet/Ofac.Protocol/Validation/KnownCommands.cs` (currently 8 entries), registration in `src/protocol/src/schemas.ts` import list; then `pnpm protocol:codegen` + fixtures in `protocol/conformance-fixtures/index.json`, drift-gated by `make protocol`. Plan 07/11 precedent.
3. Link inbound law: `AdmissionGate.ScreenAsync` (size→parse→receipt-refusal→envelope-validate→known-command gate→assign-forbidden→evidence/effective principal + `authorization.evaluated:link` audit) then `LinkIngress.AdmitAsync` type-dispatch chain with inbox dedupe (`Kernel/Link/LinkIngress.cs:52-180`); approval arms (`Kernel/Work/ApprovalIngress.cs`) are the shared-arms template for mirroring into `CloudEventIngress`.
4. Endpoint-surface template: `Kernel/Work/ApprovalEndpoints.cs` (`app.MapGroup("/api")`, ProblemDetails via `ProblemErrors`) + `MapApprovalsApi()` line in `Program.cs:22-40` + `identity:authorization:operations` bindings in `appsettings.Development.json` (no `appsettings.json` exists). `Kernel/Shared/Auth/` = workload plane (`LinkAuthMiddleware`, `ILinkTokenValidator`); human plane OIDC + role gate middleware.
5. Ledger precedent (schema-only table → behavior + same-tx outbox + audit): `Kernel/Shared/Persistence/ApprovalLedger.cs` (`Append(db, record)` audit via `Kernel/Shared/Identity/AuditWriter.cs`, same-tx `db.Outbox.Add` for decided-push `link.{endpointId}`). Migrations live under `src/facilitator/Migrations/`, latest `20261026231203_AddApprovalPushAndConsume`.
6. 18 DbSets, no artifact/memory entities (`Entities.cs`); single `FacilitatorDbContext`, schema `facilitator`.
7. Test infra: xUnit + shared Testcontainers Postgres (`tests/Facilitator.Tests/Infrastructure/PostgresFixture.cs`), feature-folder convention (`Approvals/` newest), `KernelTestFactory.CreateAuthedClient()` mints OIDC tokens via `KeycloakFixture`; e2e harness `src/gateway/test/e2e/gateway-harness.ts` re-exports workspace-link's (`useFacilitator`, `workloadToken`, `psql`).
8. Digest law: artifact digests are plain SHA-256-of-bytes (schema-pattern-locked), NOT RFC 8785 (that is approvals only). The deterministic worker computes its own digest today (`src/adapters/harnesses/deterministic/src/harness.ts` onExecute) and publishes an inline manifest with a `file://` `storageUrl` — untouched by this plan.
9. `versions.env` already pins `SEAWEEDFS_TAG=4.48`; no storage config keys exist anywhere in kernel code/config.

## Locked decisions

- **D1 — Upload path (FS interim):** `request-upload` (Link command or `POST /api/artifacts/request-upload` REST twin) allocates a `pending` row and returns `{artifactId, uploadLocation, expiresAt}`; bytes are pushed to the kernel `PUT /api/artifacts/{id}/content`. Kernel-side proxying is the honest FS backend — "signed URL direct to object store" arrives with the S3 backend, not as a fake here. Link NEVER carries artifact bytes inline (appendix §8.1); the upload is plain HTTP to the kernel, not an envelope.
- **D2 — Digest verified at the kernel boundary**, not at the Gateway ("artifact digests are checked here, not at the Gateway", `adapter.v1.json:203` law stays). Single algorithm SHA-256; digest regex from the frozen schema.
- **D3 — Two write paths, one ledger:** kernel-stored (FS bytes, `storageUri` = `kernel-fs:` + path, `verified`) and externally-stored registration via `artifact.available` with a client-supplied `storageUrl` (`unverified`, no fetch — SSRF/verified-external rules are Plan 14's). `additionalProperties:false` on the frozen event means no new fields ride it; anything extra is a row-column, not a wire field.
- **D4 — Authorization:** artifact reads/writes are team-scoped by `createdBy`'s team (workload plane: `LinkPrincipal.TeamSlug`/effective principal; human plane: work-scope membership via the approvals 404-before-eligibility pattern). Cross-team ⇒ 404. New bindings `artifacts.read`/`artifacts.write`; dev-config defaults mirror `approvals.read/respond`.
- **D5 — No GC, no retention enforcement yet:** `retentionPolicy` column ships with `permanent` default so Plan 14's GC (S3 lifecycle) has its hook; no background janitor in this plan.
- **Split judgment:** kept as ONE plan — table+store+admission+verification+event-arm is one vertical; a store without an arm has no producer, an arm without a store has nowhere to point. Split candidate if execution drags: Step 1+2 (persistence + store + tests) as a 13a commit boundary.

## Steps

0. **Research (mandatory gate):** re-verify every Known-fact anchor against current `main`; confirm the request-upload request/response needs no envelope-schema change beyond the new command schema (payload: `mediaType`, `size`, `digest`, optional `workId`/`assignmentId`/`retentionPolicy`); check `AdmissionGate` payload-size limits so a 1 MiB envelope cap cannot accidentally become an artifact cap.
1. **Wire delta:** `command.artifact.request-upload.v1` schema + schemas.ts registration + `KnownCommands.cs` entry + codegen + valid/invalid fixtures (missing digest, bad digest pattern, unknown extra field).
2. **Persistence:** `ArtifactEntity` + configuration + migration `AddArtifactStore`; `IArtifactStore` + `FileSystemArtifactStore` + `ArtifactLedger` (allocation, verify-on-put, external registration, state machine `pending→stored/rejected→deleted`) with same-tx audit.
3. **Surfaces:** Link arm + REST surface (`request-upload`, `PUT content`, `GET {id}`, `GET {id}/content`) + gate bindings + `artifact.available` ingress arms in both ingresses; timeline entry on record.
4. **Tests** (`tests/Facilitator.Tests/Artifacts/`): happy vertical request→put→available→GET content digest-equal; digest-mismatch 422 + rejected row + audit; duplicate `commandId` ⇒ single row (inbox law); cross-team 404 read/write; human-plane OIDC read; external registration marks `unverified` and stores no bytes; Link-surface request-upload rides a delegated workload token. e2e: scripted fake-link client over the `link-e2e` harness requests an upload, PUTs bytes, posts `artifact.available`, fetches content back.
5. **Docs + skills:** `docs/protocol.md` artifacts section (request-upload command, REST routes, error rows, FS upload interim stated as fact, no plan prose); `docs/security.md` artifact verification + non-disclosure lines; `.omp/skills/facilitator-protocol` + `facilitator-architecture` updated same commit.

## Critical files & anchors

|File|Anchor|Why|
|---|---|---|
|`Kernel/Work/ApprovalIngress.cs`|shared arms pattern|mirror for artifact arms in both ingresses|
|`Kernel/Link/LinkIngress.cs`|`:52-180` dispatch chain|new `command.artifact.request-upload` arm site|
|`Kernel/Shared/Persistence/ApprovalLedger.cs`|conditional-UPDATE state machine, same-tx outbox/audit|`ArtifactLedger` template|
|`Kernel/Work/ApprovalEndpoints.cs`|`:23-26` group + ProblemDetails|REST pair template|
|`protocol/dotnet/Ofac.Protocol/Validation/KnownCommands.cs`|8-entry table|command registration, third touchpoint|
|`appsettings.Development.json`|`identity:authorization:operations`|gate bindings + new `storage:` block site|

## Verification

1. `make protocol` drift-clean with the new command + fixtures; `dotnet run --project protocol/tools/Validate.Fixtures` and `pnpm --filter @ofac/protocol test` green.
2. Scripted vertical against a dev kernel (Testcontainers Postgres + realm Keycloak): request-upload via Link with a workload token ⇒ pending row; PUT correct bytes ⇒ stored + `artifact.available` on the timeline + outbox row; tampered body re-request ⇒ 422 + no stored row; cross-team GET ⇒ 404.
3. `make link-e2e` green incl. the new artifact e2e; full gate `make lint && make build && make test && make docs-check`; CI green = 🟢, roadmap row flip.

## Implementation Status

|Step|State|
|---|---|
|0 Research|Not started|
|1 Wire delta|Not started|
|2 Persistence + store + ledger|Not started|
|3 Surfaces + event arms|Not started|
|4 Tests|Not started|
|5 Docs + skills|Not started|

States: Not started / In progress / Partially implemented: <what>/ Fully implemented: <evidence>/ Blocked: <reason>. Update this table in the same commit as each step; flip the roadmap Plan 13 row ⬜→🟡 at the first commit, 🟢 on CI green.

## Assumptions & contingencies

- **Envelope payload cap vs upload admission:** if `AdmissionGate`'s size limit rejects the new command's small payload, the fix is per-type limits, not a raised global cap — record which.
- **FS path traversal:** storage paths derive ONLY from the hex digest, never from client text; a client-controlled filename component is the bug class to avoid.
- **`artifactRef` seven-required-field mismatch:** the frozen `artifactRef` requires `storageUrl`; for kernel-FS artifacts that is the `GET /api/artifacts/{id}/content` absolute URL. If that reads as a lie (not durable storage origin), amendment precedent (additive optional) applies — decide at Step 1, record it.
- **If the external-registration path needs a verified flag on the wire**, it does NOT get one here (frozen `additionalProperties:false`); the row column is the arbiter and Plan 14's verified-external work owns the wire question.
- **Replanning caveat:** the first real S3-shaped producer (Plan 14) may show the upload-admission response shape is too thin (no signed-URL slot); that is an additive amendment in 14, not a re-do here.
