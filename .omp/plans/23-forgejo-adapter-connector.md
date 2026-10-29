# Plan 23 — Forgejo adapter: webhooks in, team-owned connector out

**Status:** ⬜ not-started (flips 🔵 at the replanning gate, then 🟡)
**Depends on:** 22 as-built (provider ingest plane, `communication_bindings` law, node-image adapter family). **Phase F leg 2 of 9.**
**On approval:** this file is the working detail design; keep the Implementation Status table current.
**Granularity:** split-safety note — this plan is the Forgejo **read/webhook side + privileged write connector** only; the coding-worker harness and the §23.1 end-to-end sequence are Plan 24. If connector + webhook server exceed one reviewable unit, split connector from webhook adapter (they are separate deployables anyway: adapter = stateless webhook normalizer, connector = Gateway-side privileged executor).
**Questions may be asked during planning, but not during implementation** — decisions are locked below; when reality diverges, infer from code/spec per the contingencies section and prefer the simpler robust option.

## Context
Spec §13.6 categories + §9.2 (team-owned connectors execute privileged provider writes behind Gateway policy) + §23.1's coding sequence. The roadmap's original Plan 23 bundled adapter + connector + coding worker + steering; per the approved 9-way split this plan lands provider half (Forgejo issues/PR webhooks → normalized events via 22's ingest plane) and the Gateway-side Forgejo connector (issue read, branch push, PR create — policy-gated, approval-gated when privileged). Plan 24 completes §23.1 on top.

## Known facts (as-verified 2026-10-29; code wins, divergence goes in the status table)
- **Ingest plane:** 22's `POST /api/integrations/{provider}/events` + `provider_events` dedupe + role binding `provider.ingest`; provider event schemas + fixtures are the pattern (`provider.mattermost.*.v1`).
- **Forgejo boots:** `reference/compose.full.yaml:5` (`FORGEJO_TAG=16.0.7`, `versions.env:4`), :3000 confirmed 200 (Plan 17 step 6); no consumer code, no seeded repos (`examples/full/seed-repositories/` is a README stub).
- **Gateway policy/approval loop closed (12):** typed YAML policy at `src/gateway/src/policy.ts` (allow/deny/confirm + `command.forward` gated on child advertisement, `:38-43`); confirm→`approval.required`→consume→re-evaluate→execute proven e2e (`src/gateway/test/e2e/approval-loop.e2e.test.ts`). The connector is a Gateway-side operation riding this flow, not a new authority.
- **Connector idempotency law (appendix §5.4–5.5):** canonical payload hash + read-before-reconcile for create-style operations; ambiguous timeout → reconcile, never blind retry.
- **Artifact/manifest plane (13–15):** a PR URL bound to a Work rides the artifact reference + manifest-output machinery as-is (`@ofac/adapter` artifact client exists).
- **Gateway child transport (06):** connector can be a new child module of the gateway process or a standalone unit in the demo-workspace container — the units/config story exists at `reference/container/units`.

## Locked decisions
1. **Webhook adapter (`src/adapters/forgejo`, new pnpm package, compose sidecar):** Forgejo webhook HMAC verification → one normalized ingest POST per event on 22's plane. Additive schemas: `provider.forgejo.issue.v1`, `provider.forgejo.pr.v1`, `provider.forgejo.review.v1` (+fixtures). Kernel arms: issue-with-label → create Work + `communication_bindings` row (provider `forgejo`, channel = repo, root = issue id, idempotency = issue id + action digest); review-comment on a bound PR → steer routed to the live assignment (via existing `work.steer` path with `workVersion` fencing) or queue a re-dispatch intent when no live assignment (24 executes the relaunch).
2. **Connector = Gateway child, not kernel code.** New first-party child in `src/gateway` (or a sibling package the gateway loads — gate decides at step 0 against 06's child-loading surface): ops `issue.read`, `branch.push`, `pr.create`, `pr.comment`, advertised in the union capability manifest as `org.facilitator.forgejo.v1`; each op passes 12's policy engine; privileged ops (`branch.push`, `pr.create`) configured `confirm` → approval round-trip. The kernel never holds a Forgejo token (team-side execution authority, §3.3).
3. **PR-create idempotency:** connector hashes the canonical request (source head SHA + base + title-digest); on ambiguous timeout it reads open PRs by head-ref before retrying (read-before-reconcile, appendix §5.4). Connector records its own outbox-style intent ledger locally (SQLite, pattern of 04's delivery ledger).
4. **Issue→Work binding ownership:** `communication_bindings` (22's store) is the single binding table; no new Forgejo-specific binding table.
5. **Seed repositories:** `examples/full/seed-repositories/` becomes a real init story: a compose one-shot (forgejo CLI or API) creates an org + demo repo with a sample issue and a webhook pointing at the adapter, token via env. Repo-owned fixtures, no manual steps.
6. **Webhook URL topology:** adapter reachable from forgejo over the compose network (`http://forgejo-adapter:PORT/api/integrations/forgejo/webhook` local receive, then normalized POST to the kernel) — the adapter terminates the webhook, the kernel never does.
7. **E2E law:** same as 22 — live full-Compose proof is the local gate (`make provider-e2e` extends to a forgejo leg); fixture/unit tiers stay in CI.

## Steps
0. **Research (mandatory, no code).** Forgejo webhook payload shapes + HMAC scheme (16.0.7 docs), forgejo API for repo/PR creation, 06's child-loading surface + 12's policy config format, appendix §5.4–5.5, spec §9.2/§13.6/§23.1. Re-verify anchors.
1. **Protocol additions:** three provider schemas + fixtures + registry regen, both harnesses green.
2. **Webhook adapter package:** verify → normalize → ingest POST; vitest on recorded forgejo fixtures (valid/invalid HMAC, duplicate delivery ids).
3. **Kernel arms:** issue→Work+binding, review→steer/re-dispatch intent; xUnit suite (webhook dedupe race, label filter, review routed to live assignment's steer).
4. **Forgejo connector child:** ops + manifest + policy wiring + idempotent `pr.create` with reconcile; unit tests against a forgejo API fake; approval-gated privileged ops proven against the 12 loop.
5. **Compose + seeds:** `forgejo-adapter` + connector wiring in full overlay, `Dockerfile.node` filter += adapter, seed-repositories init one-shot.
6. **Live proof (local gate):** full stack → labeled issue creates a Work (visible in admin) → connector opens a PR after approval → review comment steers. Transcripts to the status table. §23.1 steps 2–5 half-proven (worker half = 24).
7. **Docs + skills:** provider-connector privilege ceilings (§9.2) in `docs/advanced.md`; seed-repo README; security doc row; skills.

## State after
Forgejo webhooks land as deduped normalized events; a labeled issue becomes a Work; a policy-approved connector PR opens against the seed repo; review comments steer bound works. Provider half of slice-7 done; §23.1 completes in 24.

## Testing
xUnit (webhook dedupe, arm routing, steer-vs-intent decision), vitest (fixture pairs), connector unit + 12-loop integration, live full-Compose script as local gate, slim `verify` regression unchanged.

## Documentation
`docs/advanced.md` privilege-ceiling section; `examples/full/seed-repositories/README.md`; adapter README; skills.

## Architecture fit
§9.2 (team-side privileged writes behind Gateway policy), §13 (adapter thinness), §7.1 (inbox dedupe). Second consumer of 22's ingest plane — proves provider-neutrality by reuse.

## Additional resources
Forgejo webhook/API docs (16.x); appendix §5.4–5.5; GitHub-apps prior art; 22's as-built ingest plane.

## Critical files & anchors
- 22's `Kernel/Integrations/ProviderIngressEndpoints.cs` + provider arms — extension point (new provider, no plane change).
- `src/gateway/src/policy.ts` + child-loading surface — connector seam.
- `reference/compose.full.yaml` + `reference/container/` — topology.

## Verification
build/lint/test/docs-check; fixture harnesses cover new schemas; forgejo leg of `make provider-e2e` green on a clean full stack; slim `verify` 11/11.

## Assumptions & contingencies
- If Forgejo HMAC webhooks lack a stable delivery id, the adapter synthesizes one from `(guid, action, created_at)` and documents it as the dedupe key.
- If the connector-as-child loading proves hostile to a first-party package, run it as a standalone link-side process using 04's SDK + 12's policy module import — still Gateway-side, no kernel token.
- If seed-repo one-shot ordering proves flaky, make it a long-running init loop with healthcheck-gated exit 0.
- If reality contradicts a "known fact", the code wins; record divergence. **Replanning or optimization may be required before implementation begins.**

## Implementation Status
| Step | Status | Notes |
|---|---|---|
| 0 Research | Not started | |
| 1 Protocol additions | Not started | |
| 2 Webhook adapter | Not started | |
| 3 Kernel arms | Not started | |
| 4 Forgejo connector | Not started | |
| 5 Compose + seeds | Not started | |
| 6 Live proof | Not started | |
| 7 Docs + skills | Not started | |

Status vocabulary: Not started → In progress → Partially implemented → Fully implemented, or Blocked (with reason). Record deviations from locked decisions with a one-line reason.
