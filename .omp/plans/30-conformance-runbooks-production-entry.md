# Plan 30 — Operational hardening: conformance package, runbooks, production entry

**Status:** ♻️ superseded by plan 35 (Phase E of `35-production-shape-milestone.md`; its locked decisions carry over there)
**Depends on:** 21, 26, 29 (i.e. everything: the full profile, replicas, and the operator surface are in place). **Phase F leg 9 of 9 — closes the phase.**
**On approval:** this file is the working detail design; keep the Implementation Status table current.
**Granularity:** split-safety note — conformance package, runbook/drills, and release/SBOM are three faces of one gate; split into 30a (executable conformance CLI + gap closure) / 30b (runbooks + drills + SBOM/release) if the §26.1 gap list from step 0 comes back large.
**Questions may be asked during planning, but not during implementation** — decisions are locked below; when reality diverges, infer from code/spec per the contingencies section and prefer the simpler robust option.

## Context
Spec §26.1 (15-item conformance suite) + §26.2 (operational entry criteria) + §27 (runbooks) + §22.6 (SBOM, digest pinning). As-built: fixture basis exists from 01 onward (index.json + valid/invalid/canonical, dual-harness), each phase landed its behavior tests, but there is **no executable conformance package** ("run against any conformant implementation"), **no `docs/runbooks/`**, **no SBOM job** (comment-only in `release.yml:91-95`, `*_DIGEST` pins in `versions.env:17-26` are empty), dead-letter replay verified in 20/21 but never asserted through the §26.1 checklist lens, and the ref-env hosted CI tier is written-but-commented (free-tier law — locally the gate). This plan diffs §26.1's 15 items against all existing proofs, closes gaps, packages the suite as a CLI, executes the incident drills, and hardens the release path.

## Known facts (as-verified 2026-10-29; code wins, divergence goes in the status table)
- **Fixture harnesses dual-run today:** TS vitest (`src/protocol/test/fixtures.test.ts`) + .NET (`protocol/tools/Validate.Fixtures/Program.cs`) over `protocol/conformance-fixtures/index.json`; `make test` + CI wire both.
- **Behavior-proof inventory:** unit/integration xUnit (254+ tests), link/gateway/worker e2e tiers, admin Playwright tier, `referencectl verify` 11 steps (slim acceptance, locally-gated), 22–29 add provider legs, §23.1 walk, provisioning kill-9, replica race matrix.
- **verify hook:** Plan 19's status table already reserves the ledger line "26.1 item 15 (slim half)"; full half + executable conformance CLI were explicitly deferred **to this plan**.
- **CI reality:** live jobs `dotnet|typescript|protocol|meta|docker-build|link-e2e|gateway-e2e|admin-e2e|features|example-smoke`; `ref-env-bringup`/`ref-env-verify` commented out with free-tier rationale (`ci.yml:86-90`); `.github/workflows/README.md` carries the job table incl. "(CI-disabled)" rows.
- **Audit/config/dead-letter admin surface complete (20/21):** replay, re-mark, PATCH+ETag+history, metrics roll-up — 27's original "dead-letter UX verified here" is re-verification, not construction.
- **Spec anchors:** §26.1 items at `architecture.md:2005`, §26.2 at `:2023`, §27 runbook scope, appendix §9 (metrics/runbook snippets), §10.1 (must-have conformance list), §15 (MVP checklist).
- **Docs corpus:** getting-started/advanced/protocol/security/contributing exist and are as-built; runbooks absent; "Deferred to later plans" table in `security.md:381+` needs the sweep verdict.

## Locked decisions
1. **Gap-first sequencing.** Step 0 produces a 15×harness matrix (item → existing proof → gap). Each gap is assigned to: fixture (both harnesses), verify step, provider-e2e leg, replicas matrix, or new drill — closed inside this plan before anything is called done. The matrix file (`protocol/conformance-fixtures/README.md` section or sibling `COVERAGE.md`) is the durable artifact.
2. **Conformance CLI = executable package, implementation-agnostic.** `protocol/tools/Conformance.Runner` (or sibling): runs the fixture suite + a **behavioral** scenario set (handshake/replay/dedupe/approval-roundtrip sequences) against **any** conformant kernel endpoint + a conformant link client, driven by a published config (base URL, creds, roles); emits a machine-readable report (JSON: item, scenario, pass/fail, evidence refs). This repo's stack is just its first victim (run against full compose + a scripted foreign-shape link client built only on 04's public surface + 28-adjacent published docs).
3. **`referencectl verify --full`** = slim 11 + full-profile legs (provider legs, §23.1, kafka/ollama, provisioning, replicas matrix when overlay up) behind one gate command; free-tier CI law unchanged (local gate, jobs stay commented, workflow README keeps the honest table).
4. **Runbooks = executed, not written:** `docs/runbooks/` seeded from 28/29's notes + incident drills actually run on the full stack and logged (DB restart mid-drain, provider outage = mattermost/forgejo container stopped, invalid token replay, replica SIGKILL = 29's matrix re-run, dead-letter storm + replay). Each runbook: symptom → diagnosis query (admin API/SQL) → action → the drill transcript date.
5. **Release hardening:** SBOM generation (CycloneDX for .NET + `pnpm dlx cyclonedx-npm` or equivalent — pick at step 0, both stacks) attached to `release.yml`, digests computed and written into `reference/versions.env` `*_DIGEST` pins (the meta job then **enforces** non-empty pins — flip from comment-plan to law), image digest verification step in compose bring-up.
6. **Protocol version-negotiation test:** a simulated v2 link client (major-version mismatch hello) gets the typed rejection/negotiation behavior per §5.5 — fixture + live leg.
7. **Prose scrub executes here** (27's original scope): strip every plan-number/phase/stage reference outside `.omp/` + `docs/specs/` (code comments — e.g. the stale "Plan 13 behaviour" comment at `Entities.cs:365` — READMEs, docs, protocol descriptions, test comments), each sentence rewritten as fact; the cross-plan rule keeps it clean afterward. The live-doc references found by 17's note (`docs/protocol.md:125`, `docs/security.md:104`, `getting-started.md:130`, `verify.sh:156`) are in scope.

## Steps
0. **Research + 15-item matrix (mandatory, no code).** Walk §26.1/§26.2/§27/appendix §9/§10.1/§15 and every existing proof tier; produce item→evidence→gap matrix; assign gaps; **if the gap set is large, split 30a/30b now** (per granularity note). Re-verify anchors.
1. **Gap closure:** fixtures/steps/legs per the matrix, both harnesses same commit.
2. **Conformance CLI** + machine-readable report + foreign-shape client leg + v2 negotiation scenario.
3. **verify --full aggregation** + runbooks + executed drills (five named drills minimum, transcripts logged).
4. **Release/SBOM/digest law** + meta-job enforcement flip + compose digest verification.
5. **Prose scrub** + `security.md` deferred-table verdict + docs re-verification: clean-clone getting-started run word-for-word.
6. **Close-out:** §26.2 entry-criteria checklist recorded in this file's status table; skills full sweep (AGENTS.md rule); roadmap Phase F flipped complete.

## State after
`referencectl verify --full` passes from empty volumes on the full profile; a machine-readable conformance report proves all 15 §26.1 items against this implementation and via the CLI's foreign-client leg proves the contract is implementable independently; runbooks are backed by executed drills; release artifacts carry SBOMs and every pin is a digest; no plan-number prose survives outside the plan layer.

## Testing
The suite is the deliverable: gap-closure tests ride their native tiers; CLI self-test; drills logged; meta job enforces digest pins; clean-clone getting-started proof.

## Documentation
Runbook chapter final, security deferred-table resolved, getting-started re-verified, `.omp/skills/` full sweep, workflow README current.

## Architecture fit
§26 — the conformance gate turning "reference implementation" into "conformant framework"; §29.5's statement made testable.

## Additional resources
Spec §26.1/§26.2/§27/§28/§22.6, appendix §9/§10.1/§15; CycloneDX tooling docs.

## Critical files & anchors
- `protocol/conformance-fixtures/index.json` + both harness mains — the CLI's foundation.
- `release.yml:91-95` + `versions.env:17-26` — the comment-plan to make law.
- `reference/checks/verify.sh` + `verify-full`/`verify-replicas` — aggregation inputs.
- `docs/security.md:381+` — the deferred table to resolve.

## Verification
`referencectl verify --full` green from empty volumes; conformance JSON report 15/15; drills in runbooks dated; `make docs-check` + a fresh-clone getting-started walkthrough.

## Assumptions & contingencies
- If the gap matrix surfaces one huge missing behavior (e.g. an §26.1 item no tier covers), it may warrant its own numbered plan (30a/30b split handles two) — declare, don't absorb silently.
- If SBOM tooling can't digest the node-image layout, emit SBOMs at the package/dependency layer and document the image-layer gap honestly.
- If the foreign-shape client reveals a contract ambiguity, fix the docs + fixture in the same commit (appendix §16 canonicalization is the tie-breaker).
- **Replanning or optimization may be required before implementation begins.**

## Implementation Status
| Step | Status | Notes |
|---|---|---|
| 0 Matrix + gaps | Not started | |
| 1 Gap closure | Not started | |
| 2 Conformance CLI | Not started | |
| 3 verify --full + runbooks + drills | Not started | |
| 4 Release/SBOM/digests | Not started | |
| 5 Prose scrub + docs re-verify | Not started | |
| 6 Close-out | Not started | |

Status vocabulary: Not started → In progress → Partially implemented → Fully implemented, or Blocked (with reason). Record deviations from locked decisions with a one-line reason.
