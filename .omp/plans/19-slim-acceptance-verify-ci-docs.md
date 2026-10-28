# Plan 19 — Slim reference environment, leg 3: acceptance run, `referencectl verify`, CI, getting-started
**Status:** ⬜ not-started (flips 🔵 at the replanning gate, then 🟡)
**Depends on:** 17, 18
**Phase E leg 3 of 5.** Closes the reference-env chain (17→18→19); slice-6 exit test.

## Objective
Turn the booted slim stack into the spec's *tested acceptance run*: implement
`referencectl verify` executing §21.1's acceptance script end-to-end, wire the
locked CI tier (PR: bring-up; main + nightly: full verify, arm64+amd64), and
complete `docs/getting-started.md` to spec §28's exactness bar. After this plan
"fresh clone → `referencectl up slim` → acceptance passes unattended" is a CI
fact, not a hope. §26.1 item 15 (slim half) lands.

## Known facts (as-built anchors; code wins, divergence goes in the status table)
- Acceptance script (§21.1, `docs/specs/…architecture.md:1436`): login via Keycloak → create Work (CLI/UI) → dispatch over outbound SignalR link → Gateway admits + launches deterministic harness → progress + Markdown artifact → SeaweedFS + Postgres registration → inspector shows completion + searchable memory candidate → Aspire trace correlation → **stop/restart worker and Facilitator mid-task, verify retry/resume**.
- Inspector half of that claim cannot be UI-checked until the admin surface (Plan 21) exists; `verify` asserts via API read models (timeline, artifacts, memory recall) — the UI walk-through lands in the Plan 21 docs pass.
- Every hop already has a programmatic probe: Keycloak token via form POST (e2e pattern), `POST /api/works`, timeline `GET /api/works/{id}/timeline`, artifact `GET /api/artifacts/{id}(/content)`, manifests `GET /api/works/{id}/manifests`, memory `recall` (`MemoryEndpoints.cs`), dead-letters, gateway `/status` (incl. `awaitingApproval` bucket), link sessions via `facilitator.link.sessions` metric or `link_sessions` table through a kernel endpoint if needed.
- Aspire standalone dashboard exposes query APIs over its in-memory store (OTLP ingest on the configured port); checking "spans exist for traceId" is an HTTP query against the dashboard container from the host.
- CI today: no compose job beyond Plan 17's bring-up; dual-arch precedent exists (`features` job matrix `ubuntu-latest` + `ubuntu-24.04-arm`); runner Compose+BuildKit available on both.
- Mid-task restart semantics are implemented and e2e-proven at process level (Plan 04 SDK reconcile, Plan 03 redelivery, ledger law); container-level injection = `docker compose restart facilitator` / `restart gateway` at a checkpointed moment.
- Policy/approval: deterministic worker demo flow is allow-policy (no approval) — approval loop proven separately (Plan 12 e2e); `verify` may assert the `awaitingApproval` bucket exists but must not require a human round-trip unless seeded policy demands it.

## Locked decisions (from planning)
- **CI tier:** PR = bring-up + healthchecks only (Plan 17 job). Full `referencectl verify` on **main-push + nightly**, matrix `linux/arm64` + `linux/amd64`.
- `referencectl verify` is self-contained (mints its own tokens, creates its own demo Work, cleans up), exit code 0 iff every step passed, per-step machine-readable output (`--json`) — Plan 24's conformance CLI consumes the same result shape.
- Restart injection points: after `work.progress` observed, before completion — worker restart first, kernel restart second (order pinned at the gate if evidence suggests otherwise).
- `getting-started.md` rewritten to §28 shape: prerequisites, `referencectl up slim`, OIDC login, run a Work, view result/artifact, timeline/OTLP, connect an existing agent by adding one Feature, cleanup, port map, resource budget. Exact commands + expected output. The `up full`/Mattermost leg of §28 stays unbuilt (full-profile providers are Phase F plans) and the doc MUST state that honestly, not fake it.

## Steps
0. **Research (mandatory):** Aspire dashboard query API surface for trace lookup (version 13.4.2); compose `restart` behavior vs healthcheck states; Work-timeline event-kind vocabulary (which kinds prove each hop); re-read §21.1/§28 wording to fix the verify step list verbatim.
1. `reference/checks/`: verify script (step table → probes → assertions → report), replacing the stub README.
2. `referencectl verify` wiring (`--json`, per-step timing, non-zero on first-failure-continue-policy decided at gate).
3. Restart injection: worker-restart and facilitator-restart probes with resume assertions (no lost work, no duplicate side effects — assert exactly one result manifest revision chain and one completion).
4. OTLP correlation probe: capture `traceId` from a kernel response header or timeline correlationId, assert spans visible in the dashboard API.
5. CI jobs: `ref-env-verify` on main+nightly dual-arch (build images from tree, compose up, bootstrap, verify, teardown on failure with log capture); keep PR bring-up job green.
6. `docs/getting-started.md` full §28 rewrite + README quickstart flip; expected-output transcripts generated from a real run, not handwritten.
7. §26.1 item-15 bookkeeping: append the slim-half row to the roadmap conformance ledger (this file's status table), fixtures untouched unless a wire assertion needs one.

## State after
Main-push/nightly CI proves: fresh clone → `.env` → `referencectl up slim` → `referencectl verify` green, including mid-task kernel and worker restarts, on both arches; Aspire shows the trace; getting-started executes verbatim.

## Testing
- `referencectl verify` IS the deliverable's test harness; the CI jobs are its proof.
- One negative probe per critical hop (e.g. verify fails loudly if the artifact blob is missing from the bucket) so the verify script itself can't pass vacuously.
- No new unit-test surface expected beyond the verify script's own probe units if it ships as TS with vitest-coverable functions.

## Documentation
- `docs/getting-started.md` to §28; `README.md` quickstart updated; `reference/README.md` verify section; `.omp/skills/facilitator-reference-env` final pass; `docs/contributing.md` e2e-tier table extended with the compose tier.

## Architecture fit
§21.1 slice-6 exit; §26.1 item 15; §28 learning path. The reference environment becomes the regression harness every later phase (full profile, multi-replica) layers onto.

## Assumptions & contingencies (replanning caveat)
- If the Aspire 13.4.2 dashboard API is too unstable to assert against, downgrade the probe to "OTLP export egress observed at the dashboard container" + record the deviation; do NOT drop trace correlation.
- Dual-arch nightly may need arm64 self-hosted labeling if hosted arm64 queues stall; the tiering (not the runner) is the locked part.
- If restart-injection races make one assertion flaky, pin the checkpoint on an explicit timeline event kind, never on wall-clock sleeps.
- Replanning or optimization may be required before implementation begins; implementation begins with additional research (Step 0).
- **Questions may be asked during planning, but not during implementation.**

## Implementation Status
| Step | Status | Notes |
|---|---|---|
| 0 Research | Not started | |
| 1 checks/ verify script | Not started | |
| 2 referencectl verify | Not started | |
| 3 Restart probes | Not started | |
| 4 OTLP correlation | Not started | |
| 5 CI verify jobs | Not started | |
| 6 getting-started §28 | Not started | |
| 7 Conformance ledger | Not started | |

## Verification
`ref-env-verify` green on both arches for one main push + one nightly; a deliberate seeded break (stop SeaweedFS before verify) makes verify fail loudly.
