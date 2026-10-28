# Plan 16 — Memory broker: scopes, ACLs, retain/recall/reflect over PostgreSQL FTS, adapter memory client

**Status:** 🟢 done — executed against `16_MEMORY_BROKER_PLAN.md` (its Implementation Status table is the record of evidence + divergences); full gate green on `500ca19`+worktree: `make lint`/`make build`/`make test` (dotnet 254/254, vitest all packages, fixtures 88/88)/`make docs-check`/`pnpm -r typecheck` (detail design: this file).
**Phase:** D (fourth, closes Phase D — slice-5 memory half). **Depends on:** 09 + 10 (principals, evidence, audit — as-built); consumes Plan 15's artifacts as `sourceRef` targets. Independent of the artifact *storage* backend: safe to interleave with 13–15 after 09/10.
**Branching:** work and commit **directly on `main`** (standing directive from Plans 03–08). Do not push.
**Questions may be asked during planning, but not during implementation.** In implementation, prefer straightforward fixes consistent with this plan; if several iterations fail, step back and simplify rather than adding machinery.
**Architecture fit:** spec §14 + appendix §6 — lower-trust searchable knowledge that supplements context and is never an authorization source (§3.3); the broker (not a product) keeps the Hindsight seam optional while the reference `postgres-fts` profile stays zero-new-infrastructure, closing Phase D's slice-5 half.
**Additional resources:** spec §14.2–14.9 + §14.11's wire examples and `memory.yaml` profile; appendix §6.1–6.3; PostgreSQL FTS docs (tsvector triggers, `websearch_to_tsquery`, GIN); Hindsight repo + docs (mapping sketch only, §14.7 warning block); frozen `event.memory.candidate.v1.json`/`event.memory.used.v1.json` + Plan 13's arm recipe (`.omp/plans/13-artifact-store-kernel-core.md`).

## Objective

Implement the memory contract layer as a broker, not a product (spec §14.3 core ops, appendix §6): Postgres-backed entries with scope ownership + ACL intersection, the frozen `memory.candidate` lifecycle (candidate → review → promoted, §14.6), full-text `recall` with provenance metadata, the `memory.used` usage-event law on every automated read, per-Project retention, cross-team private block, and the injection audit (§14.9). `@ofac/adapter` grows the memory client surface on the same principle as the artifact client. Hindsight stays an optional seam — the provider port exists, the only provider is `postgres-fts`.

## Expected state after

- Tables (migration `AddMemoryBroker`): `memory_scopes` (id, kind `work|project|team|private`, owner team/project, retention policy) + `memory_entries` per appendix §6.1 + spec §14.4: stable `entryId`, `scopeId`, owner, `type` (the seven §14.4 classifications), content (+mediaType), `contentHash`, provenance (sourceRef/evidenceRef/workId/executionId/sourceDigest), `observedAt`/`effectiveFrom`, `classification`, `status` (`candidate|active|superseded|tombstoned`), `revision`, `supersedesEntryId`, `contradictsEntryIds`, retention, tsvector search column (trigger-maintained) + GIN index; `memory_shares` (entry/scope → granting principal → grantee team, revocable).
- Broker service (`Kernel/Shared/Memory/`): `Retain/Recall/Get/Promote/Correct/Revoke/Delete/Export` — `MemoryAccessContext` built server-side from verified principals/grants ONLY (never from agent JSON, §14.3); request `scopeSelectors` are hints intersected with grants; denial returns a scoped error that does not disclose existence (same non-disclosure law as artifacts/manifests).
- Surfaces: human-plane REST pair set (`POST /api/memory/retain`, `POST /api/memory/recall`, `GET /api/memory/{id}`, `POST /api/memory/{id}/promote|correct|revoke`, timeline-integrated) + Link command adapters (`command.memory.retain`, `command.memory.recall`) reusing the 13-arm pattern; new gate bindings `memory.read`/`memory.write`/`memory.curate`.
- Frozen-event arms land as behavior: `event.memory.candidate` (from Plan 07's worker, today a fall-through note) records a `candidate` entry (status `candidate`, shorter default retention); `event.memory.used` records a usage event row + audit — and the recall path itself emits `memory.used` per appendix §6.2 for every automated read, with `memoryId`, `consumer`, `justification`.
- Lifecycle: promote = owner review, inserts new scope linkage + audit (never auto-publish into another team's scope, §14.5); correct = revision + supersede older; delete/revoke = tombstone + FTS purge + propagation (no physical wipe of audit); retention enforcement pass reuses 14's GC service shape, per-Project policy honored (§6.3).
- `@ofac/adapter` memory client: `retain/reflect/recall` helpers + the `memory.candidate` builder; `reflect` returns typed `unsupported` without a reasoning provider (no LLM in this plan — provider seam only).
- Cross-team matrix provable: private/team-only never visible cross-team without a grant; `facilitator-visible` readable per §14.5 precedence order; search results carry `provenance` + `accessControl` (§6.3).

## Known facts (verified 2026-10-27 against as-built; re-verify in Step 0; code wins — divergence goes in the status table)

1. Frozen wire already has the event half: `event.memory.candidate.v1.json` (required `scope` enum `project|team|workspace|private`, `contentSummary`, `provenance{source,evidenceRef,createdBy,createdAt}`; optional `policyLabel`) and `event.memory.used.v1.json` (required `memoryId`, `consumer`, `justification`); both have valid fixtures. NO `command.memory.*` schemas, no retain-request/`memory.retained|rejected|failed` schemas — §14.6's async-request messaging has no contract; this plan uses synchronous command→receipt + event, and only adds `memory.retained|rejected` event schemas if a genuinely async path proves necessary (decide at Step 0, prefer fewer frozen surfaces).
2. The frozen candidate `scope` enum lacks `work:` (spec §14.11's examples use `work:` selectors) — `scopeSelectors` in *requests* are free-form hint strings (new request schemas, this plan authors), while the frozen entry `scope` enum stays as-is: map work-scope entries under `project` with a `workId` provenance field until an amendment proves needed. Record the mapping in docs rather than amend spec-frozen enums for cosmetics.
3. Command-class landing machinery (schema + schemas.ts + `KnownCommands.cs` + fixtures + codegen) is the Plan 13 recipe, three synchronized touchpoints, `make protocol` drift gate.
4. GC/background precedent (14): serialized advisory-locked hosted-service loop; retention pass copies it. Audit spine + `AuditWriter.Append` same-tx law applies to every broker op (§14.9 metadata: actor, scopes, purpose, workRef, result count, outcome — no confidential content in general traces).
5. Postgres FTS is in-stack already (shared Testcontainers Postgres 17): tsvector column + trigger + GIN + `websearch_to_tsquery` ranking; no pgvector, no new container — provider port `IMemoryProvider` keeps the Hindsight seam as a not-implemented adapter, per §14.7 profiles table (`postgres-fts` IS the reference profile).
6. Link arms + inbox dedupe + gate + evidence: 13's arm template applies verbatim; write ops carry idempotency keys per §14.11's retain law (`commandId` inbox law already gives this — reuse, no parallel scheme).
7. Worker today: deterministic harness emits `memory.candidate` only as a plan-07-era event when configured; no memory calls exist in `@ofac/adapter`. The client surface (like 15's artifact client) rides kernel HTTP with the delegated workload token; envelopes ride the observe relay.

## Locked decisions

- **D1 — Synchronous retain, no queue:** retain returns a receipt (accepted/duplicate/rejected) with the entry row committed in-transaction; §14.6's async-queue messaging is deferred (no extraction/embedding cost exists in the FTS provider — the queue would be theater). A later provider adds the async path additively.
- **D2 — ACL before search, always:** recall resolves the authorized scope set first, builds SQL with scope predicates (`WHERE scope_id IN (...)`), never post-filters rows (§14.8 #2). Response metadata reports only visible scopes; denial discloses nothing.
- **D3 — `memory.used` is emitted by the broker on every recall that returns entries** (per-entry rows, consumer = calling principal + purpose), plus an explicit REST arm so external consumers who read-then-decide can attest usage; kernel-internal consumers (later routing/clarify per §14.5) write the same rows.
- **D4 — Candidates from the Link plane land `candidate` + `team-only` default label**; `policyLabel` from the frozen event is honored but may only *narrow* (an external candidate can't self-declare `public`). Promotion requires `memory.curate` on the owning scope.
- **D5 — Deletion law:** tombstone + index purge + share revocation in one tx; physical content drop at tombstone (rows keep hash + metadata for audit); backup-retention promises stay prose in docs (§14.6 "no instantaneous erasure" honesty).
- **Split judgment:** larger than 13/14 but one coherent service — split candidate recorded NOW (not deferred): if execution drags, cut at 16a (tables+broker+REST+FTS+tests) / 16b (Link commands + candidate/promote lifecycle + adapter client + docs), each independently green.

## Steps

0. **Research:** re-verify 09/10/13/15 anchors (principals, arm template, gate config, artifact sourceRef shape); verify tsvector trigger + `websearch_to_tsenglish` ranking against the CI Postgres image/arm64; check §14.11's recall request/response against the frozen candidate schema for field-name alignment (`contentSummary`/`provenance` reuse verbatim); decide D1's event-surface tail (retain receipt sufficiency).
1. **Wire delta:** `command.memory.retain|recall` schemas (+ response shapes as command results per 13's precedent), fixtures (valid retain per §6.1 fields, invalid: missing provenance, bad policyLabel), codegen.
2. **Persistence + broker:** entities/config/migration `AddMemoryBroker` (scopes, entries + tsvector trigger, shares, usage events); broker service with `MemoryAccessContext` construction from `EffectivePrincipal`/OIDC principal; scope-grant intersection; audit ops.
3. **Surfaces:** REST set + Link arms + gate bindings; candidate-event arm (frozen event → entry); `memory.used` recording both paths; recall returns ranked entries with provenance/accessControl/revision/retrievalReason, `truncated`/`asOf` per §14.11.
4. **Lifecycle + retention:** promote/correct/revoke/delete per D4/D5 with supersede chains; GC-shaped retention pass (candidate default shorter than reviewed, §14.6 triggers + per-Project policy rows); write-rate/quota guard (§14.8 #3: per-scope retain quota, payload cap).
5. **SDK + cross-team proof:** `@ofac/adapter` memory client (retain/reflect-unsupported/candidate builder/recall); e2e in the gateway tier: team-A worker retains a candidate → team-B recall cannot see it (private/team-only) → share grant + promote makes it visible to B → cross-team denial discloses nothing.
6. **Docs + skills:** `docs/advanced.md` memory model + provider seam + `.facilitator/memory.yaml` profile shape (as fact, not plan prose); `docs/protocol.md` command/event/error rows; `docs/security.md` memory threat rows (§14.8 matrix condensed); skills (`facilitator-architecture` memory section, `facilitator-protocol` registry rows); roadmap flip + Phase D closeout note.

## Verification

1. `make protocol` drift-clean; fixture suites green both harnesses.
2. Scripted vertical: retain (OIDC human) ⇒ entry `candidate`; recall by owner sees it with provenance; cross-team recall: zero rows + no existence disclosure; share+promote ⇒ visible; correction ⇒ revision chain + superseded; delete ⇒ FTS purge (recall 0 rows) + tombstone + audit trail intact; a recall that returns entries has produced `memory.used` rows (assert via psql probe).
3. Link-plane: worker candidate event (via existing e2e harness) becomes a promotable entry end-to-end; quota guard trips at configured cap with typed error.
4. Full gate `make lint && make build && make test && make docs-check`; CI green = 🟢; Phase D section closeout.

## Implementation Status

|Step|State|
|---|---|
|0 Research|Not started|
|1 Wire delta + fixtures|Not started|
|2 Persistence + broker|Not started|
|3 Surfaces + event arms|Not started|
|4 Lifecycle + retention|Not started|
|5 SDK + cross-team e2e|Not started|
|6 Docs + skills|Not started|

States: Not started / In progress / Partially implemented: <what>/ Fully implemented: <evidence>/ Blocked: <reason>. Update this table in the same commit as each step; flip the roadmap Plan 16 row ⬜→🟡 at the first commit, 🟢 on CI green.

## Assumptions & contingencies

- **FTS ranking quality:** plain tsvector will feel dumb next to embeddings; that is the sanctioned `postgres-fts` profile (§14.7). Resist embedding-machinery creep — the provider seam is where a vector backend lands later.
- **`workspace` scope in the frozen enum vs §14.2's dimensions:** the enum came from the frozen schema; map `workspace` ≈ team-scoped-by-ownership now, record the mapping in `docs/advanced.md`; amend only if a real producer needs the distinction.
- **Retention GC collision with 14's artifact GC:** two hosted loops, one advisory-lock discipline; if both land serialized in the kernel process, a shared scheduler helper is allowed, not required.
- **`memory.used` on kernel-internal reads** has no kernel-internal consumers yet (§14.5's routing/clarify reads arrive with the decision-provider work) — the broker-side emission + the attest arm are the testable law now; do NOT fabricate an internal reader to justify it.
- **Replanning caveat:** 13–15 may land amendments that shift the arm/helper templates this plan copies; Step 0's anchor re-verification is the correction mechanism. If Phase D ordering changes (memory before manifests), `sourceRef` validation loosens to format-check + recorded-but-unverified — record, don't block.
