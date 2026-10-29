# Plan 24 — Coding worker vertical slice: chat → Work → branch + PR → steer (§23.1 complete)

**Status:** ⬜ not-started (flips 🔵 at the replanning gate, then 🟡)
**Depends on:** 23 as-built (Forgejo events + connector). **Phase F leg 3 of 9.**
**On approval:** this file is the working detail design; keep the Implementation Status table current.
**Granularity:** single plan — one harness example + the glue that closes §23.1's sequence; deliberately small because the heavy machinery (policy, approvals, manifests, connector) is all as-built. If it grows a second harness, split per the Phase G sibling-adapter rule, not here.
**Questions may be asked during planning, but not during implementation** — decisions are locked below; when reality diverges, infer from code/spec per the contingencies section and prefer the simpler robust option.

## Context
Spec §23.1 (`docs/specs/open_facilitator_framework_architecture.md`) is the framework's demo paragraph: chat mention → Work → assignment → coding worker produces a feature branch + PR → a review comment steers the **same** Assignment (or relaunches a scaled-to-zero worker). As-built, the deterministic research worker proves the harness lifecycle (07), the provider/connector halves exist after 23, and steer has workVersion fencing (`EfCoordinationStore.ApplySteerAsync`, `EfCoordinationStore.cs:123-186`) but no execution-generation bump (Plan 27's scope). This plan delivers `examples/full/coding-worker`: a real harness that clones the seed repo, works on a branch, opens a PR through 23's connector, and is steerable mid-flight — the first end-to-end proof of the whole stack on the full profile. Worker stays long-lived once assigned; scale-to-zero is 28 (stated, per the replanning-gate decision).

## Known facts (as-verified 2026-10-29; code wins, divergence goes in the status table)
- **Harness contract frozen (07):** `@ofac/adapter` (`src/adapters/adapter-sdk/`: adapter.ts, artifacts.ts with `confirm_upload`, memory.ts, testing.ts lifecycle kit); deterministic worker at `src/adapters/harnesses/deterministic/` (src/harness.ts, config.ts, worker.e2e.test.ts, sample/capabilities.yaml) is the copy-me shape; `kernelUrl` required since 18's cutover.
- **Result manifests (15):** completion truth = kernel-recorded manifest with revision chain + RFC 8785 digest; PR URL lands as an artifact/reference via 13–15's surface; completed-event amendment is ref+digest (§15.3 shape).
- **Steer today:** both planes settle steer as timeline row + progress outbox with version fencing; `CurrentExecutionGeneration` (`Entities.cs:59-84`) exists but nothing bumps it; `ExecutionAttemptEntity.Generation` exists (`:86-99`). The coding worker consumes steer as in-band instructions (appended to its work context) — generation fencing is 27.
- **Child busy law:** demo-workspace child quota `maxInFlight: 1` (Plan 19 status: overlapping assign honestly answers `child-busy`); the full-profile coding workspace gets its own unit with its own quota.
1. **Harness = scripted deterministic coder, not an LLM.** `examples/full/coding-worker` (new pnpm workspace member under `src/adapters/harnesses/coding/` is wrong — it's an example; it lives at `examples/full/coding-worker/` as its own package): clones the bound repo over http + job token, implements a **templated** change (file append + deterministic test), pushes the branch, asks the connector for a PR, posts a manifest whose outputs are the commit SHA + PR URL artifact. Determinism keeps §23.1 provable; a real-LLM harness is a Phase G sibling (Plan 32's territory), not this plan's job.
- **Connector ops (23 as-built):** `issue.read`, `branch.push`, `pr.create`, `pr.comment` — the worker drives the connector through forwarded commands, holding no Forgejo token itself.
- **Memory broker (16):** retain/recall/reflect + `memory.used` law live; the coding worker's context re-hydration story uses artifacts + memory reads, no new seam.

## Locked decisions
1. **Harness = scripted deterministic coder, not an LLM.** `examples/full/coding-worker` (new pnpm workspace member under `src/adapters/harnesses/coding/` is wrong — it's an example; it lives at `examples/full/coding-worker/` as its own package): clones the bound repo over http + job token, implements a **templated** change (file append + deterministic test), pushes the branch, asks the connector for a PR, posts a manifest whose outputs are the commit SHA + PR URL artifact. Determinism keeps §23.1 provable; a real-LLM harness is a Phase G sibling (Plan 31's territory), not this plan's job.
2. **Capability name:** `org.facilitator.coding.v1`; advertised in the coding workspace's child manifest; policy allows `command.forward` for it + the connector ops under the coding team's policy file.
3. **Steering semantics (this plan's slice):** a review comment on the bound PR (23's review arm) reaches the running assignment as a steer envelope; the harness's steer handler appends it to the work context and, when the run is still active, the worker incorporates it before finishing (one extra amend+push cycle, PR updated via `pr.comment`/push — no second PR). When no live assignment exists, 23's re-dispatch intent creates a fresh attempt whose context re-hydrates from the PR-head branch + manifest lineage. Generation fencing stays 27's problem; this plan makes steering *observable*, not fenced.
4. **Binding:** PR → Work association is 23's `communication_bindings` row (root = issue, parent = PR message id pattern); the PR URL also registers as a Work artifact reference (13's surface).
5. **Compose:** new full-overlay service `coding-workspace` (node image, command override, its own units dir `reference/container/coding-units/` + policy file), seeded issue carries the trigger label; `make provider-e2e` gains the §23.1 leg; the demo-workspace research flow stays the slim reference flow untouched.
6. **No kernel behavior change** except what 23's review arm needs if it under-specified steer routing; any kernel growth here is a divergence to record.

## Steps
0. **Research (mandatory, no code).** Read 07's lifecycle kit expectations (`testing.ts`), deterministic harness as-built, 15's manifest recording path, 23's as-built review arm + connector ops, git-over-http auth options for forgejo job tokens, spec §23.1 + §8.3.
1. **Example package:** `examples/full/coding-worker/` (own package.json, depends on `@ofac/adapter` via workspace, capabilities.yaml, config env, harness main implementing accept/progress/steer/complete with manifest outputs).
2. **Git + connector leg:** clone/push with a scoped job token; PR create via connector command; idempotent re-run (branch exists → update, reconcile per 23's law).
3. **Lifecycle tests:** `@ofac/adapter/testing` kit suite + vitest unit tiers (steer-before-complete incorporated; steer-after-complete no-op; push conflict reconcile).
4. **Compose + seed wiring:** `coding-workspace` service, units/config payload, policy file allowing the coding capability + connector ops, seed issue labeled.
5. **Live §23.1 proof (local gate):** clean full stack → Mattermost mention (22) → Work → coding assignment → PR with deterministic change → review comment → same assignment amended (and the no-live-worker branch: stop the workspace, comment, re-dispatch completes from lineage). Transcript + manifest/PR links in the status table. This closes §21.2 demo steps 2–7.
6. **Docs + skills:** getting-started full-profile tour section (the §23.1 walk), advanced.md worker-lifecycle note (long-lived here, scale-to-zero owned by provisioning), example README as copy-me.

## State after
§23.1's sequence runs end-to-end in the full reference environment, steerable mid-flight; a second harness shape proves the adapter contract beyond the deterministic worker. Slice-7 coding half complete.

## Testing
Kit-proven lifecycle tests (CI tier), vitest units, live §23.1 script in `make provider-e2e` (local gate), slim `verify` unchanged, gateway-e2e regression (policy surface reused, not forked).

## Documentation
`examples/full/coding-worker/README.md`, getting-started tour extension, advanced.md steer-observable note, skills.

## Architecture fit
§23.1 (the demo), §8.3 (adapter contract), §9.2 (worker holds no provider token), §15.3 (manifest truth). Proves the harness seam by being an ordinary consumer of published packages.

## Additional resources
Spec §23.1/§8.3; 07/15/23 as-built; forgejo job-token docs.

## Critical files & anchors
- `src/adapters/adapter-sdk/` — the frozen contract the example consumes.
- 23's review arm + connector ops — routing counterparties.
- `reference/container/` + `compose.full.yaml` — deployment shape.

## Verification
build/lint/test/docs-check; kit suite green; §23.1 live transcript recorded; slim verify 11/11.

## Assumptions & contingencies
- If git-over-http auth against compose forgejo proves awkward, use the forgejo Actions-runner-style token or a repo-scoped bot token via env; either keeps the token team-side.
- If steer mid-run races the harness's completion, honest-first: record steer as a timeline event and amend via the re-dispatch leg; 27's generation fencing makes the race window explicit later.
- If the example must stay out of the pnpm workspace for publish-shape reasons, build it standalone with file: deps (example-smoke precedent); record divergence.
- **Replanning or optimization may be required before implementation begins.**

## Implementation Status
| Step | Status | Notes |
|---|---|---|
| 0 Research | Not started | |
| 1 Example package | Not started | |
| 2 Git + connector leg | Not started | |
| 3 Lifecycle tests | Not started | |
| 4 Compose + seed wiring | Not started | |
| 5 Live §23.1 proof | Not started | |
| 6 Docs + skills | Not started | |

Status vocabulary: Not started → In progress → Partially implemented → Fully implemented, or Blocked (with reason). Record deviations from locked decisions with a one-line reason.
