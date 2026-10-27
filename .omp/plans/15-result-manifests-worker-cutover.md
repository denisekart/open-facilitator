# Plan 15 — Result manifests, typed outputs, worker artifact cutover, `@ofac/adapter` artifact client

**Status:** ⬜ Not started (🔵 replanning → 🟡 in-progress → 🟢 done via the Implementation Status table below).
**Phase:** D (third). **Depends on:** 13 + 14 (artifact store as-built). Cuts over Plan 07's `file://` interim.
**Branching:** work and commit **directly on `main`** (standing directive from Plans 03–08). Do not push.
**Questions may be asked during planning, but not during implementation.** In implementation, prefer straightforward fixes consistent with this plan; if several iterations fail, step back and simplify rather than adding machinery.
**Architecture fit:** spec §15.3 + §15.5 + appendix §8.3 — the completion truth becomes an immutable, digest-verifiable, revision-chained manifest decoupled from the execution container; the `@ofac/adapter` client makes artifacts a capability every harness — first-party or third-party — gets without touching wire details (the Phase G adoption thesis earning its keep early).
**Additional resources:** spec §15.3's example completed-event + manifest JSON (the amendment target shape); appendix §8.1/§8.3; spec §15.6 output-schema discovery (advertise-only, no behavior here); Plan 11's 1.1 amendment precedent + digest goldens (`.omp/plans/11-approval-lifecycle.md` status rows); `protocol/json-schema/result-manifest.v1.json` + `adapter.v1.json` if/then structure; deterministic harness as-built (`src/adapters/harnesses/deterministic/src/harness.ts`).

## Objective

Close the artifact half of Phase D at the execution edge (spec §15.3, appendix §8.3): immutable Result Manifests as kernel records with revision chains, the four output classes representable on the wire, and the deterministic worker's completion path cut over from the inline-manifest/`file://`-URL interim to the real flow — request-upload → PUT to signed URL → `artifact.completed` carrying manifest ref + digest. The `@ofac/adapter` SDK grows the artifact client surface (upload helper + manifest builder) so no harness adapter touches wire details. Kernel stores manifest metadata + bytes-as-artifact; correction creates a linked revision; the Work timeline shows the revision; review semantics (§15.5 resultState) get their first durable hook.

## Expected state after

- `facilitator.result_manifests` table: `manifestId` (PK), `workId`, `assignmentId`, `executionId`, `revision` (int, chain), `revisionOf` (nullable self-FK), `digest` (RFC 8785 over the canonical manifest document — approvals digest machinery reused), `artifactUri` (the manifest document itself stored as an artifact), `structuredDigest?`, `recordedAt`, `state` (`recorded|superseded`). Immutability: no UPDATE except `state` on supersede; corrections insert `revision+1` + `revisionOf`.
- Inbound `event.result.recorded` (new schema, additive event class) OR — decide at Step 0 against the frozen `event.assignment.completed.v1` payload path — extend the completed event's data with `resultManifestRef` + `resultDigest` (spec §15.3's example shape) via the sanctioned additive amendment; whichever wins, the inline `data.resultManifest` becomes optional-deprecated-in-fact and the kernel recomputes/validates the manifest digest from stored bytes before accepting the completion.
- Worker completion vertical: deterministic harness calls the new SDK client (`requestUpload(bytes, mediaType) → artifactId`, `buildResultManifest({...outputs, structuredResult, summary}) → canonical doc + digest`, then completes with ref+digest); kernel verifies, records manifest + outputs rows, links to Work/Assignment, timeline entry, outbox push. Kill the `file://` interim: the example README's verification line and worker-e2e's on-disk assertion move to kernel-GET-by-artifactId.
- `@ofac/adapter` exports the artifact client (from `@ofac/workspace-link`'s publish/event law, over the observe-relay event frame `adapter.v1.json:202-206` for child→kernel surfaces + kernel REST for the byte PUT via Gateway-passthrough or direct kernel HTTP with a delegated token — Step 0 pins which, given the child-wire has no request/response channel, Known fact 6/2026-10-27 scout report).
- GET pair: `GET /api/works/{id}/manifests` (revisions visible) + manifest document retrievable by artifactId through the Plan 13/14 content path.

## Known facts (verified 2026-10-27 against as-built; re-verify in Step 0; code wins — divergence goes in the status table)

1. `protocol/json-schema/result-manifest.v1.json` exists frozen with `additionalProperties:true` (extensible without amendment): required `manifestId` + `outputs[]` of `artifactRef`; optional `revisionOf`, `notes`. Spec §15.3's richer example (`kind`, `classification`, `structuredResult`, `externalRefs`, `sourceRefs`, `revision`, `summary`) therefore needs NO relaxation — additive fields only.
2. The frozen completed-event path: `adapter.v1.json` if/then `$ref` into `event.assignment.completed.v1.json` (skipped from DTO codegen via CODEGEN_SKIP); an amendment there re-touches adapter schema + fixtures + both harnesses. Plan 11's 1.1 optional-field amendment is the precedent for the completed event itself.
3. Worker today: `src/adapters/harnesses/deterministic/src/harness.ts` `onExecute` writes Markdown locally, self-computes `sha256:`, publishes `assignment.completed` with inline `data.resultManifest` and `storageUrl: pathToFileURL(...)`; worker-e2e asserts `digest == sha256sum` of the on-disk file. `@ofac/adapter` (`src/adapters/harnesses/adapter-sdk/`) exports lifecycle only — no client helpers.
4. Child→Gateway upstream is the unmodified-envelope event relay (`adapter.v1.json:202-206`); Gateway→child frames are `start|steer|status|stop` only (`childwire.ts:17-29`, MAX_CHILD_FRAME_BYTES 262 144). Bytes > frame cap can never ride the wire; the byte PUT path must be plain HTTP.
5. RFC 8785 machinery is dual-language and golden-tested: .NET `Kernel/Shared/ApprovalDigest.cs` (Jcs.Net) + TS `src/protocol/src/digest.ts` (`computeActionDigest`, canonicalize exported) + `protocol/conformance-fixtures/canonical/` goldens; artifact digests stay SHA-256-of-bytes, manifest canonical digests reuse the approvals algorithm — the two laws must not be conflated in code or docs.
6. Plan 13/14 as-built anchors: `ArtifactLedger`/`IArtifactStore`/`storage:provider`, request-upload response with `uploadUrl` (s3) or FS upload location, gate bindings `artifacts.read|write`. Status tables: `13-artifact-store-kernel-core.md`, `14-s3-artifact-backend-gc.md`.
7. Digest-of-manifest validation hook point: the ingress arm that processes completion already runs inbox dedupe + audit in one tx (ApprovalLedger pattern) — manifest verification joins that transaction (verify stored bytes ⇒ recompute ⇒ compare ⇒ insert rows or 422).

## Locked decisions

- **D1 — Manifest doc is itself an artifact** (canonical JSON, `application/json`, stored via the ordinary store path, `storageUri` per provider); the kernel record stores `artifactUri` + recomputed digest. `artifact://` logical URIs from spec §15.3 resolve through `GET /api/artifacts/{id}/content`; no new URI scheme on the wire beyond what Plans 13/14 established.
- **D2 — One canonical digest law:** manifest canonicalization = RFC 8785 + NFC (the approvals law verbatim), new golden fixture `canonical/result-manifest-1.json|.sha256` cross-verified by both harnesses; artifact digests inside the manifest stay SHA-256-of-bytes. The kernel ALWAYS recomputes; a client-supplied manifest digest that disagrees ⇒ 422.
- **D3 — Amendment over new event** (default, re-checkable at Step 0): additive optional `resultManifestRef`/`resultDigest` on `event.assignment.completed.v1` (the spec's own example shape) + new `command.result.record-manifest` for corrections/standalone manifests riding the Link surface; if Step 0 finds the amendment path blocks on the adapter if/then structure, fall back to `event.result.recorded` as sibling event — decide once, record it.
- **D4 — Byte path for workers:** upload PUT goes direct kernel HTTP with the Plan 10 delegated workload token (the same token the Link session uses for evidence), NOT a new child-wire frame kind — the relay already carries the small envelopes; inventing a byte channel violates "Links never carry artifacts inline".
- **D5 — Review-state hook, no review UI:** `state` on manifests + `resultState` vocabulary lands as kernel columns/enum only (spec §15.5 taxonomy); marking `needs_revision` via API is deferred to the admin-surface plan. Do not build approval interplay here.
- **Split judgment:** ONE plan (contract amendment + kernel recording + SDK + worker cutover is one vertical — any piece alone ships a broken producer). If it drags, the natural checkpoint is kernel-side (Steps 1-2) landing before SDK/worker (Steps 3-4), producers still green via the inline path until 4.

## Steps

0. **Research:** re-verify 13/14 as-built; decide D3's amendment-vs-sibling against the actual `adapter.v1.json` if/then + fixture dispatch; pin the worker byte-PUT auth flow end-to-end (token availability inside `RunContext` today vs what D4 needs — may need `createAdapter` to surface the delegated token, an SDK-visible change); confirm quicktype output sanity for the amended completed event (CODEGEN_SKIP interactions).
1. **Wire delta:** completed-event amendment (+ `command.result.record-manifest`), `canonical/result-manifest-1` golden, fixtures (valid ref+digest, bad digest pattern, missing-manifest-for-required-output), codegen, both harnesses.
2. **Kernel:** `result_manifests` entity/config/migration `AddResultManifests`; manifest verifier (fetch artifact bytes → canonicalize → digest → compare → immutable insert with `revisionOf` chain + same-tx audit/outbox/timeline); GET pair + gate binding `manifests.read` (work-scope 404 law); supersede-on-correction.
3. **SDK:** `@ofac/adapter` exports `createArtifactClient` (request-upload, PUT bytes, manifest builder + canonicalizer reusing `@ofac/protocol` digest exports, completion-payload builder); lifecycle kit gains a mock-kernel surface so `@ofac/adapter/testing` covers it without a live kernel.
4. **Worker + e2e cutover:** deterministic harness uses the client; delete inline-manifest and `file://` code paths (clean cutover, no flag); worker-e2e asserts kernel-side: completed event carries ref+digest, `GET content` digest-equal, timeline shows revision 1; a correction scenario records revision 2 with `revisionOf`; example README verification line rewritten to the kernel-GET check.
5. **Docs + skills:** `docs/protocol.md` result-manifest section (four output classes, revision law, digest law, amendment rows), appendix §8.3 example promoted to a fixture (already a golden from Step 1 — reference it), skills + roadmap flip.

## Verification

1. `make protocol` drift-clean; new golden cross-verifies in .NET and TS; fixture suites green.
2. Scripted vertical (dev kernel + SeaweedFS + realm, 14's harness): fake-worker completes an assignment via the SDK client ⇒ manifest row revision 1, artifact bytes digest-matched, timeline entry; correction ⇒ revision 2 chain; tampered manifest ⇒ 422 + no row.
3. `make gateway-e2e`/`worker-e2e` green with the cut-over producer (no inline path remains — grep the SDK/harness for `file://`/`pathToFileURL` returns zero production hits); full gate + CI green = 🟢, roadmap flip.

## Implementation Status

|Step|State|
|---|---|
|0 Research|Not started|
|1 Wire delta + golden + fixtures|Not started|
|2 Kernel manifest recording|Not started|
|3 @ofac/adapter artifact client|Not started|
|4 Worker + e2e cutover|Not started|
|5 Docs + skills|Not started|

States: Not started / In progress / Partially implemented: <what>/ Fully implemented: <evidence>/ Blocked: <reason>. Update this table in the same commit as each step; flip the roadmap Plan 15 row ⬜→🟡 at the first commit, 🟢 on CI green.

## Assumptions & contingencies

- **Delegated token inside RunContext:** if the adapter SDK today gives the harness no kernel-usable credential, Step 3 extends `CreateAdapterOptions`/`RunContext` — additive, adapters opt in; deterministic harness is the worked consumer. Do NOT route bytes through the Gateway child wire as a fallback.
- **Manifest canonicalization of a received (already-canonical) document:** the kernel stores the bytes it verified; the golden defines canonical form, and the builder emits canonical — a non-canonical submission is rejected rather than re-canonicalized-then-accepted (record the digest of what was actually stored).
- **If D3's amendment hits the adapter-schema codegen wall** (quicktype junk on the amended union), the `CODEGEN_SKIP` precedent applies to the touched schema with hand-rolled types — ugly but established.
- **Two producers, one law:** the scripted fake-link clients in existing e2e tiers that publish `assignment.completed` inline keep validating (optional field); only the deterministic harness is cut over. Any other producer found at Step 0 joins the cutover — grep for `resultManifest` publishers first.
- **Replanning caveat:** Plan 16 (memory) references manifests as `sourceRef` targets; if its needs expose a manifest-field gap, the extensible schema absorbs it additively — no rework here.
