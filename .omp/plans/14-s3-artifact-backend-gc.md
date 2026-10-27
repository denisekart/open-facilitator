# Plan 14 — S3 artifact backend: SeaweedFS adapter, signed URLs, verified external refs, retention GC

**Status:** ⬜ Not started (🔵 replanning → 🟡 in-progress → 🟢 done via the Implementation Status table below).
**Phase:** D (second). **Depends on:** 13 (artifact store core, as-built). Config-plane consumer of Plan 09's role gate.
**Branching:** work and commit **directly on `main`** (standing directive from Plans 03–08). Do not push.
**Questions may be asked during planning, but not during implementation.** In implementation, prefer straightforward fixes consistent with this plan; if several iterations fail, step back and simplify rather than adding machinery.
**Architecture fit:** spec §15.4 (portable retrieval, external-ref trust) + appendix §8.2 (signed upload URLs, retention/GC) — the storage seam that proves "large bytes need not be hosted centrally" while the kernel keeps metadata, policy, and references; vendor neutrality is the adoption story's precondition (kernel speaks S3 wire, SeaweedFS is a container).
**Additional resources:** appendix §8.2 lifecycle + §9.1 (artifact metrics); AWS SDK for .NET S3 presigning/path-style docs; SeaweedFS `weed mini -s3` S3-API docs (EMT/multipart ETag semantics); Testcontainers.SeaweedFs package; Plan 13's detail design (`.omp/plans/13-artifact-store-kernel-core.md`) + the dispatcher's hosted-service shape (`src/facilitator/Kernel/Shared/Dispatch/`).
**Vendor law:** the kernel backend speaks the S3 wire standard; SeaweedFS exists only in test/dev infrastructure and docs examples.

## Objective

Replace the filesystem interim with the production-shaped storage backend (spec §15.4, appendix §8.2): an S3-compatible `IArtifactStore` adapter (AWS SDK for .NET S3 client against any S3 wire endpoint; reference target SeaweedFS `weed mini`, pinned `SEAWEEDFS_TAG=4.48`), presigned PUT/GET URLs so the upload flow stops proxying bytes through the kernel, verified-external-reference admission with SSRF guards, and the retention/GC pass appendix §8.2 demands. Clean cutover law: FS remains as the dev/test default **only where an S3 container is unavailable**, selected by config — no dual-write, no shim; the request-upload response gains a real signed-URL slot.

## Expected state after

- `S3ArtifactStore : IArtifactStore` selected by `storage:provider` (`filesystem|s3`); `storage:s3` config block (endpoint, bucket, region, credentials, forcePathStyle). The kernel emits S3-standard presigned URLs; nothing names SeaweedFS.
- `command.artifact.request-upload` / REST twin response now carries `{artifactId, uploadUrl, method:"PUT", expiresAt}` (additive amendment to the Plan 13 response shape, both fixtures same commit); `PUT content` remains for FS provider and small human-plane uploads, kernel 307/streams-to-store, and `GET {id}/content` becomes a 302 to a presigned GET for the s3 provider.
- Verified-external admission (spec §15.4 bullet 3): registering an `artifact.available` with an external `storageUrl` applies an allowlist of provider bindings + SSRF screen (reject non-http(s), userinfo, unsafe/private address ranges unless an operator binding names them); the row records `external` + `verified|unverified` + the binding id. The kernel NEVER fetches external bytes opportunistically.
- GC: background pass over `retentionPolicy` (`permanent|<N>d|archive`) + reference check (an artifact referenced by a non-superseded result-manifest row or a live Work is never collected; manifest table exists from Plan 15 — until this plan's own merge order, the reference check reads `artifacts.workId` liveness) → `stored→deleted` row + backend delete + audit. Bounded batch, idempotent, restart-safe.
- xUnit tier runs the s3 adapter against a SeaweedFS (or minio-equivalent) Testcontainer; the whole existing artifact suite passes against BOTH providers via a collection fixture parameter — provider behavior is a test matrix, not a fork.
- `file://` worker interim still stands (Plan 15 kills it); no manifest changes here.

## Known facts (verified 2026-10-27 against as-built; re-verify in Step 0; code wins — divergence goes in the status table)

1. Plan 13 ships `Kernel/Shared/Storage/IArtifactStore` + `FileSystemArtifactStore` + `ArtifactLedger` + `artifacts` table with `state`, `storageUri`, `retentionPolicy`; upload admission returns `uploadLocation`; status table: `13-artifact-store-kernel-core.md`.
2. `reference/versions.env:SEAWEEDFS_TAG=4.48` pinned, `SEAWEEDFS_DIGEST=` empty (CI `meta` job enforces key completeness, `.github/workflows/ci.yml` meta job); no compose files exist yet — the reference Compose env is a later plan, so this plan's container lives in Testcontainers only.
3. Testcontainers precedent: shared static-Lazy Postgres + Keycloak fixtures (`tests/Facilitator.Tests/Infrastructure/`); a per-collection SeaweedFS container follows the same pattern (`Testcontainers.SeaweedFs` exists in the Testcontainers contrib catalog; else a generic `GenericContainer` with `server -s3` — decide at Step 0, prefer contrib).
4. Background-service precedent: the outbox dispatcher (`Kernel/Shared/Dispatch/`) — `IHostedService` loop with lease/batch semantics; GC mirrors its shape, no new scheduler framework.
5. There is no `appsettings.json`; production-shaped config keys must have a Development-mode default + startup fail-closed check precedent (`identity:` block, Plan 09).
6. AWS SDK for .NET S3 (`Amazon.S3`) supports custom `ServiceURL` + `ForcePathStyle` + presigning without S3-specific account features (SigV4); SeaweedFS `weed mini -s3` serves that surface. No new vendor abstraction beyond the SDK client.

## Locked decisions

- **D1 — One port, two adapters, config-selected.** `storage:provider` is read once at startup; a runtime flip requires restart (no hot-swap machinery). FS stays the zero-dependency dev default so `make test` gains no container dependency for kernel-only unit tiers; the s3 matrix belongs to the integration collection.
- **D2 — Presigned PUT is the admission artifact:** URL TTL ≤ 15 min, single artifact key, no writable bucket-level URL (appendix §8.1: manifests never carry long-lived bearer secrets — presigned URL lives only in the admission response, never stored on the row; row keeps the canonical `s3://bucket/key` URI).
- **D3 — SSRF/verified-external is an operator-binding table** (config `storage:externalBindings`: id, urlPrefix, network ranges), not a code list; unbound external `storageUrl` registers `unverified` and is never fetched, ever.
- **D4 — GC correctness over cleverness:** single serialized pass (advisory lock, same precedent as dispatcher), reference-check before delete, delete-row-last (tombstone state `deleted`), crash between backend-delete and row-update ⇒ re-delete is idempotent. No distributed scheduling.
- **Split judgment:** ONE plan — adapter+presign+GC share one backend surface; splitting adapter from GC would ship an un-collectable store. Split candidate if it drags: GC as a 14b commit boundary after adapter+presign are green.

## Steps

0. **Research:** verify Plan 13 as-built shapes (response type, ledger API, config block); verify Testcontainers SeaweedFS contrib package + `weed mini` flags for CI (arm64 + amd64); verify SigV4 presign TTL semantics against the chosen container; re-check whether Plan 13's amendment contingency took the `artifactRef`-`storageUrl` option.
1. **Adapter + config:** `S3ArtifactStore` (Put/Get/List/Delete/VerifyDigest + `RequestUpload(key, ttl) → presigned`), DI selection, startup checks, `storage:*` config docs-shape; provider matrix wired into the existing artifact test collection (s3 collection against a SeaweedFS container).
2. **Upload-flow cutover:** request-upload response gains `uploadUrl` (additive schema amendment + fixtures + codegen, both harnesses); confirm-on-put path switches from kernel-received bytes to HEAD-object size check + server-side digest readback (or ETag/md5 caveat — verify SeaweedFS ETag semantics at Step 0; if non-multipart ETag==md5 != sha256, keep client-declared sha256 + kernel GET-and-verify on first confirm, and record the choice); 302 presigned GET.
3. **Verified-external + GC:** externalBindings screen on the `artifact.available` arm; GC hosted service + audit + metrics (appendix §9 names artifact counters — emit `artifact_gc_collected`, `artifact_store_errors` counters).
4. **Tests:** full provider matrix green; presign expiry 403-on-late-PUT + re-admission; SSRF matrix (private range, userinfo, non-http scheme, unbound host ⇒ unverified-not-fetched); GC respects a live-work reference and a `<N>d` window (FakeTimeProvider); restart-mid-GC idempotence.
5. **Docs + skills:** `docs/protocol.md` upload-flow update (signed URL flow as fact), `docs/security.md` SSRF/binding + retention rows, `docs/getting-started.md` untouched (no reference env yet); skills updated; roadmap row flip.

## Verification

1. `make protocol` drift-clean (response amendment + fixtures both harnesses); artifact suite green under BOTH providers (`dotnet test --filter FullyQualifiedName~Artifacts` with the s3 collection up).
2. Scripted vertical against dev kernel on s3 provider + SeaweedFS container: request-upload ⇒ presigned PUT from the harness process ⇒ confirm ⇒ 302 GET digest-equal; late PUT after TTL ⇒ 403 + fresh admission works.
3. `make lint && make build && make test && make docs-check`; CI gains the SeaweedFS service where the s3 matrix runs; CI green = 🟢, roadmap row flip.

## Implementation Status

|Step|State|
|---|---|
|0 Research|Not started|
|1 S3 adapter + config + provider matrix|Not started|
|2 Signed-URL upload cutover|Not started|
|3 Verified-external + GC|Not started|
|4 Tests|Not started|
|5 Docs + skills|Not started|

States: Not started / In progress / Partially implemented: <what>/ Fully implemented: <evidence>/ Blocked: <reason>. Update this table in the same commit as each step; flip the roadmap Plan 14 row ⬜→🟡 at the first commit, 🟢 on CI green.

## Assumptions & contingencies

- **SeaweedFS ETag ≠ sha256:** near-certain (md5 of parts). The fallback (kernel GET-and-verify on confirm) is already locked in D2's tail; if byte re-read costs prove bad, digest verification moves to first-read caching — out of scope, do not build it here.
- **arm64 CI image:** if the SeaweedFS image lacks arm64 variants at the pinned tag, the s3 test collection skips on arm64 with an explicit platform skip reason (NOT silent) and amd64 CI carries the matrix; record in status table.
- **Multipart presigned PUT limits:** object sizes here are bounded by admission-declared `size`; enforce the declared size at confirm (HEAD) so a presigned URL can't smuggle an unbounded object.
- **If Plan 15's manifest table merges BEFORE this plan's GC step,** the GC reference check points at manifests directly instead of workId liveness — re-read 15's schema at Step 0 and take the stronger check.
- **Replanning caveat:** the reference-Compose plan may relocate where the SeaweedFS container config lives; this plan's Testcontainers wiring is independent and survives that move.
