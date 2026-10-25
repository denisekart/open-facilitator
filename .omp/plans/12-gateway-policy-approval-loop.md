# Plan 12 — Gateway team policy engine and confirm→approval round-trip

**Status:** ⬜ not-started (🔵 replanning → 🟡 in-progress → 🟢 done via the Implementation Status table below)
**Phase:** C (closer). **Depends on:** 11 (approval lifecycle — the round-trip's other half) and transitively 09/10. This plan completes Phase C: after it, the full §12 loop (policy → confirm → approval.required → human responds → consume → execute) runs across kernel + Gateway.
**Branching:** work and commit **directly on `main`** (standing directive). Do not push.
**Size judgment:** kept as ONE plan — the evaluator is meaningless without the confirm flow that consumes it, and the flow is untestable without a policy that says `confirm`. Split candidate if execution drags: evaluator + config as a 12a, round-trip as 12b, decidable once the evaluator matrix is green.
**Questions may be asked during planning, but not during implementation.**

## Objective

Replace `DefaultPolicy`'s guts (keep the call-sites, exactly as its header comment promises) with the spec §12 typed allow/deny/confirm team policy engine in `@ofac/gateway`: a YAML policy file (typed JSON predicates, not a language), evaluated per command against caller provenance / actor claims / operation / resource / capability, with three effects — `allow`, `deny`, `confirm`. `confirm` runs the §12.1 lifecycle Gateway-side: emit `approval.required` (kernel-computed canonical `actionDigest`, eligible approvers, nonce, expiry), pause/track the action, on consumption re-evaluate current state and permissions, then execute. Deny-by-default: no policy row match ⇒ deny; cross-resource writes deny even when the kernel proposes a different capability.

## Expected state after

- `gateway.yaml` gains a `policy:` block (file inline or path); no policy file ⇒ current DefaultPolicy behavior with a boot warning (capability-advertence rules), preserving existing e2e tiers clean-cutover-style — the file replaces the seam guts, not the seam.
- Evaluator unit matrix: allow/deny/confirm per capability+resource+actorAnyOf (normalized `group:`/`role:`/`user:` terms matched against the actor claims carried by the command's `actorEvidenceRef` — from Plan 10's validated delegation when present, else the workload identity), conditions pass-through (`providerState` hints are advisory inputs, never authority).
- Confirm flow: on `confirm`, Gateway sends `approval.required` via the Link, holds the pending action in its execution ledger, forwards the kernel's approval state, and on a `kernel:`-evidenced `approval.respond` re-evaluates and executes once; revoked/stale ⇒ deny + audit event (`authorization.evaluated` mirror on the Gateway log side).
- Cross-team policy claim: a manifest/assignment referencing another team's capability denies even when the kernel routed it (kernel team check + Gateway policy = independent ceilings, §12).
- Deterministic worker harness example gains one `confirm` capability (e.g. `research.publish`) proving the loop in e2e; §26.1 item 8 passes.

## Known facts (verified 2026-10-25; re-verify, code wins)

1. Policy seam as-built: `src/gateway/src/policy.ts` — `PolicyAction = "command.forward" | "event.forward"`, `PolicyContext {commandType, capabilityId?, childWorkspaceId}`, `evaluate → "allow"|"deny"`; `DefaultPolicy` rules at `:26-38`; call-sites `runtime.ts:598-605` (assign; deny ⇒ receipt `rejected` `policy-denied: <capabilityId>`, no ledger row), `:688-697` (steer/cancel/status), `:757-760` (event forward); injectable `GatewayDeps.policy` (`:112`), default `new DefaultPolicy(new FleetCapabilities(this.fleet))` (`:196`). Header comment states this exact plan's contract: guts replaceable, call-sites untouched.
2. No policy config key today (`config.ts`); `team` slug at `config.ts:60-62,323`; union manifest via `ChildFleet.unionManifest(team)` (`child-link.ts:392-409`).
3. Approvals contract as-frozen + kernel lifecycle as-built from Plan 11 (record/respond/consume/revoke, `kernel:` evidence, `approval.required` response semantics) — read 11's Implementation Status + as-built endpoints before writing the Gateway side.
4. Canonicalization TS: `canonicalize@^2.1.0` via `@ofac/protocol` (`src/protocol/test/fixtures.test.ts:7,113-121` pattern); Gateway must import the shared digest helper (extend `@ofac/protocol`'s public surface — same package, no re-implementation).
5. Execution ledger + reconcile as-built from Plans 05/06 (`runtime.ts` reconcile, ledger rows, generation fencing) — the pending-approval state extends THAT ledger, not a parallel store.
6. E2E harness: `src/gateway/test/e2e/gateway-harness.ts` (`gatewayConfig()` YAML writer — gains a `policy` writer; ready regex, inboxRows probe); CI jobs `gateway-e2e` (also runs adapter worker e2e), `example-smoke`.
7. `approval.respond.v1` is a known command the Gateway can send on the Link surface (`KNOWN_COMMAND_TYPES`); it now carries a business effect kernel-side.

## Locked decisions (planning-approved)

- Policy DSL: typed JSON-predicates in YAML mirroring spec §12's example (`capability`, `resources[]`, `actorAnyOf[]`, `effect: allow|deny|confirm`, optional `conditions` as opaque key-values passed to connector-side checks, never evaluated by the engine itself). First-match-wins over an ordered list; implicit final deny. Schema ships as `protocol/json-schema/policy-file.v1.json` (Gateway-local artifact published in the contract tree for third-party gateway builders).
- Policy file revision is content-hash-addressed and included in every audit line ("policy ID/revision", §12.2); loading from a protected path is operator guidance, documented not enforced.
- Confirm implementation: pending action row in the execution ledger with state `awaiting-approval`; the Gateway re-evaluates policy + provider-state hints at consume time and executes with a fresh generation (stale-generation replay denies, §12.2). Crash-reconcile: on restart, pending actions reload and re-subscribe to approval state; no approval ⇒ stays pending until expiry.
- The deterministic worker's `research.publish` confirm capability is the e2e proof; the harness itself stays policy-agnostic (policy is Gateway-side, never child-side).
- Actor claims source order: validated evidence subject + groups (Plan 10 `actorEvidenceRef` on the command) > workload identity; unverified evidence ⇒ workload only (deny on any `actorAnyOf` row, matching kernel rule — no privilege from untrusted fields).

## Steps

0. **Research (mandatory start).** Spec §12 (all), appendix §7.3 revocation-before-apply; Plan 11 as-built approval endpoints/ledger + Plan 10 evidence-on-command shape; `policy.ts` call-sites; execution-ledger/reconcile code; `gateway-harness.ts` YAML writer; `@ofac/protocol` public-surface pattern.
1. `policy-file.v1.json` schema + loader + typed evaluator + matrix tests; `policy:` config key; boot-without-file warning path.
2. `confirm` effect plumbing: `PolicyEvaluator` third return value; call-sites gain the branch (assign path holds the action, emits `approval.required` with kernel-agreed canonical digest via the shared helper).
3. Pending-approval state in the execution ledger + reconcile + consume/revoke handling + re-evaluate-and-execute; Gateway audit lines (§12.2 field set, no long-lived tokens).
4. `research.publish` confirm capability in the deterministic worker + Gateway config in the example; e2e loop test (confirm → required-event recorded kernel-side → REST respond as eligible Keycloak user → Gateway consumes → execution runs once; replay/revoked/expired variants deny).
5. Fixtures for the policy schema + approval round-trip message pairs; drift gate green.
6. Docs + skills + roadmap bookkeeping (packaging skill policy section; example README).

## Testing requirements

- Evaluator matrix unit suite: allow/deny/confirm × actorAnyOf match/miss × resource match/miss × ordering; deny-by-default assertion; malformed policy file ⇒ boot failure with pointer (fail-closed).
- Cross-language digest: Gateway-emitted `actionDigest` == kernel-recomputed (the Step 2 shared-helper golden + the e2e kernel recording prove it end-to-end).
- E2E §26.1 item 8: confirm loop completes once and only once; revoked-before-apply denies; kernel-proposed cross-team capability denied by policy; stale execution generation after restart denies.
- Lint/typecheck gates: `eslint --max-warnings 0`, strict tsconfig — no `any` in policy types.

## Documentation requirements

- `docs/security.md`: team policy model + DSL reference + worked example; Gateway audit-line schema.
- `examples/slim/deterministic-research-worker` README: publish-confirm walkthrough.
- `.omp/skills/facilitator-workspace-packaging` policy section as-built (AGENTS.md maintenance rule).
- This file's Implementation Status table per step; roadmap Plan 12 row flip; Phase C closure note.

## Architecture fit

Spec §12 Gateway plane: team-owned policy + connectors' approval discipline, over the kernel truth from 11. Closes Phase C: identity (09), workload+evidence (10), kernel approvals (11), team policy + loop (12).

## Additional resources

Spec §12 + appendix §7; Plan 10/11 detail files + Implementation Status at their as-built state; YAML + JSON Schema 2020-12; existing `policy.test.ts` deny-matrix conventions.

## Implementation Status

|Step|State|
|---|---|
|0 Research|Not started|
|1 Policy schema + evaluator|Not started|
|2 Confirm plumbing|Not started|
|3 Pending-approval ledger + consume/revoke|Not started|
|4 Worker confirm capability + e2e loop|Not started|
|5 Fixtures|Not started|
|6 Docs + skills|Not started|

States: Not started / In progress / Partially implemented: <what>/ Fully implemented: <evidence>/ Blocked: <reason>.

## Assumptions & contingencies

- Replanning or optimization may be required before implementation: if Plan 11's approval-state delivery to the Gateway needs a Link push the frozen contract doesn't carry (e.g. approval transitions delivered like deliveries), the minimal delta is reusing the existing Deliver push with the `approval.*` events (already schema-frozen as events) — no new frame kind; verify at Step 0, and if a contract amendment is truly needed it rides this plan's fixture update.
- If holding pending actions in the ledger fights the 06 generation model, a Gateway-side side table keyed by approvalId is acceptable — the invariant is "one execution per consumed approval, re-evaluated at consume time", not the storage mechanism.
- `conditions` evaluation stays out of scope (connector-side checks, provider state arrives via 15/16); do NOT build a condition mini-language here.

## Verification

1. `pnpm -C src/gateway test` + `eslint --max-warnings 0` + typecheck green incl. the evaluator matrix.
2. New-behavior proof (the loop): `make gateway-e2e` — seeded confirm-capability assign ⇒ Gateway emits `approval.required` (kernel `facilitator.approvals` row via probe, digest recomputed-match), REST respond as `team-alpha` approver ⇒ child executes once, `assignment.completed` in inbox; second identical respond ⇒ denial, no second execution row.
3. `make example-slim-up` example walkthrough works per README.
4. Full gate: `make lint && make build && make test && make docs-check`; CI green = 🟢, Phase C closed.
