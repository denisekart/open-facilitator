# Plan 25 — Kernel routing: deterministic capability matching + the decision-provider seam

**Status:** ⬜ not-started (flips 🔵 at the replanning gate, then 🟡)
**Depends on:** 24 as-built (its provider-created Works make the seeded-assign interim untenable). **Phase F leg 4 of 9.**
**On approval:** this file is the working detail design; keep the Implementation Status table current.
**Granularity:** single plan — one kernel subsystem (routing) + one two-implementation port (decision provider, deterministic impl now, model impl in 26). Split candidate if it inflates: routing engine vs routing-defaults/config activation.
**Questions may be asked during planning, but not during implementation** — decisions are locked below; when reality diverges, infer from code/spec per the contingencies section and prefer the simpler robust option.

## Context
Spec §10.1 defines routing evaluation; §10.2 the advisory decision provider. As-built the kernel **never picks an assignment target**: Production-mode assign is raw outbox SQL in `reference/bootstrap/seed-lib.sh:93-138` addressed to the hardcoded `link.gw.team-alpha` ("Until the routing slice assigns works itself, this SQL insert is the Production-mode assign path"), capabilityId is accepted-then-ignored (`:68-69`), and the only routing-ish artifact is the stored-intent `routing-defaults` admin config (`AdminConfigStore.cs:78,229-231`, validator = one string field `provider`). Capability advertisement is fully live (`POST/GET /api/link/v1/capabilities` → enrollment jsonb + SHA-256 digest, `LinkEnrollmentStore.cs:152-155`; digest echoed to `link_sessions.capabilities_digest` on Hello, `LinkHub.cs:100-103`) — the candidate set exists, its evaluation doesn't. Provider legs (22–24) create Works that must route to whichever workspace advertises the capability, so the routing engine + seed-interim cutover lands now, before the Kafka/Ollama plan adopts it.

## Known facts (as-verified 2026-10-29; code wins, divergence goes in the status table)
- **Dispatch plane:** outbox rows carry channel-addressed envelopes; sinks keyed by channel prefix (`ISink.cs:16-19`; `KernelLogSink` channel `kernel`, `LinkSink` channel `link`, registered `KernelServiceCollection.cs:91-92,169`); `LinkSink` is a liveness probe — online session ⇒ deliver, else no-route requeue-with-backoff, never dead-letter (`DbEventDispatcher.cs:117-124,157-165`).
- **Candidate data:** capability manifests stored per enrollment (jsonb + digest); `link_sessions` rows give live endpoint + team + `capabilities_digest` (`Entities.cs:245-256`); team roster/roles from 10.
- **Work/assignment creation:** `POST /api/works` + ingress state machines (02); assignment rows exist at create-or-dispatch time (`Entities.cs:59-84`, has `Capability` column); nothing evaluates `Capability` against advertisements.
- **Config plane:** `routing-defaults` resource exists as stored intent (never read by behavior); 20's `If-Match` PATCH law + audit apply to any new config consumed here.
- **Metrics:** `KernelMetrics.cs` meter `facilitator` — new routing counters follow the existing counter-naming pattern.
- **Seed interim:** `seed-lib.sh` `seed_work`/dispatch primitives (Plan 19's `reference/checks/verify.sh` steps ride them); slim `verify` currently seeds a Work then SQL-inserts the assign row.

## Locked decisions
1. **Routing is a deterministic kernel step in the event loop.** A `RoutingProcessor` consumes a `work.routing.requested`-style outbox effect from the Work-creation arm: select candidate endpoints = live sessions whose stored manifest advertises the Work's required capability, filter by team scope + role, order deterministically (capability specificity, then oldest-online, then endpointId tiebreak). Zero AI on this path (§10.1). Placement: new arm in the dispatcher pipeline (step 0 picks exact seam: sibling processor to `DbEventDispatcher`'s effect handling, not a new hosted loop unless the seam resists).
2. **Ambiguity → decision-provider seam.** When >1 candidate qualifies and `routing-defaults.provider` names a decision provider, the kernel consults `IDecisionProvider` **advisory-only**: it returns a preference ordering over the kernel-computed allowed set; a choice outside the set is rejected → deterministic order stands. Result + allowed-set + provider reference land in the routing audit row + decision audit (§10.2 law: model output advisory). Deterministic in-process implementation (`deterministic`) ships here; Ollama implementation is 26 — the seam must be two implementations at 26 only.
3. **Assignment creation moves into routing.** Routing creates the assignment row (capability, team, endpoint), writes the `work.assign` outbox envelope addressed to the chosen endpoint, timeline row, `facilitator.routing.decisions` counter + audit. The seed-lib SQL insert dies (clean cutover, cross-plan no-shim rule): Production-mode `seed_work` posts a normal `POST /api/works` with a capability and lets routing assign.
4. **Config consumption:** `routing-defaults` gains fields (additive validator change): `{provider: "deterministic"|"model"|"", preferTeam?: string}` — first behavior ever to read admin config; missing row ⇒ deterministic-only default (stored-intent comment updated).
5. **No-route law:** no candidate ⇒ Work stays routable: requeue routing with backoff (LinkSink's pattern), admin-visible state (routing lane in deliveries view), never silent drop.
6. **Nudge interaction deferred:** nudge-consumption is 27 (it needs generation semantics); routing only records `lastRoutingAttemptAt`-style state needed for the 27 handoff.

## Steps
0. **Research (mandatory, no code).** Spec §10 fully (esp. 10.1 evaluation steps), `DbEventDispatcher` effect handling seams, Work-creation arm in `CloudEventIngress`/`EfCoordinationStore`, capability manifest jsonb shape (gateway union manifest), `AdminConfigStore` validator pattern, metrics + audit patterns. Re-verify anchors.
1. **Routing engine + `RoutingProcessor`** with deterministic candidate evaluation; xUnit + Testcontainers: single-candidate route creates assignment + link-addressed outbox row; two-candidate tie broken deterministically; no-live-candidate requeue + backoff; team-scope filter excludes cross-team endpoint advertising the same capability.
2. **`IDecisionProvider` seam + deterministic impl + audit.** DI port, allowed-set law enforced, decision audit row, unit tests for out-of-set rejection + provider-throw fallback (advisory ⇒ never blocks routing).
3. **Config activation:** extended `routing-defaults` validator + behavior read-through (cached, version-aware); admin API unchanged; SPA `routing-defaults` form field additions (`config-shapes.ts` mirror).
4. **Seed-interim cutover:** seed-lib dispatch primitives rewritten to capability + `POST /api/works`; delete the hardcoded `link.gw.team-alpha` insert; slim `verify` steps re-pass on the new path (dispatch-link step semantics unchanged from the outside).
5. **Live proof:** slim stack clean → `referencectl verify` 11/11 with routing-based assign; full stack → 22's mention-Work routes to the right workspace by capability, admin links/deliveries show the decision; transcript in status table.
6. **Docs + skills:** `docs/protocol.md`/advanced routing section (§10.1 as-built), metrics row, `facilitator-architecture` skill routing section, `facilitator-reference-env` seed-lib note.

## State after
The kernel assigns Works itself: capability match → deterministic pick (advisory model optional), assignment + addressed outbox row + audit, in every mode; the seeded-assign interim is gone; `routing-defaults` is live behavior. §10.1 exists; §10.2 has its seam + first implementation.

## Testing
xUnit routing suite above (CI tier); audit-law tests; slim `verify` 11/11 regression (its dispatch-link step now proves routing); provider-e2e re-run proves provider-Work routing.

## Documentation
Routing section in docs, metrics/audit inventory updates, skills same-change, getting-started untouched (behavior externally identical).

## Architecture fit
§10 (kernel-first routing, advisory model), §7 (event-loop placement), §19/§20 (DB authority — routing state is rows). Removes the last hardcoded-address interim from the reference flow.

## Additional resources
Spec §10/§6.4; 26's Ollama docs (interface anticipation only); as-built capability/manifest storage.

## Critical files & anchors
- `reference/bootstrap/seed-lib.sh:93-138` — the interim this plan deletes.
- `Kernel/Shared/Dispatch/DbEventDispatcher.cs` — placement host.
- `Kernel/Admin/AdminConfigStore.cs:78,229-231` — routing-defaults validator to extend.
- `LinkEnrollmentStore.cs:152-155` + `link_sessions` config — candidate data.

## Verification
build/lint/test/docs-check; routing xUnit suite green; slim verify 11/11 on clean stack with no seed SQL assign rows (grep proves interim gone); provider-e2e mention still completes end-to-end.

## Assumptions & contingencies
- If the dispatcher seam can't host a routing step cleanly, routing runs as a small dedicated `DbEventDispatcher`-pattern processor on its own outbox channel prefix — same DB laws, no new in-memory state.
- If capability-manifest parsing proves under-typed (jsonb free-form), add a validation schema arm at enrollment in this plan (additive) rather than loose parsing in routing.
- If a second real candidate never exists in slim, the two-candidate test runs against two scripted fake sessions (Testcontainers-seeded), not a production fake.
- **Replanning or optimization may be required before implementation begins.**

## Implementation Status
| Step | Status | Notes |
|---|---|---|
| 0 Research | Not started | |
| 1 Routing engine | Not started | |
| 2 Decision-provider seam | Not started | |
| 3 Config activation | Not started | |
| 4 Seed-interim cutover | Not started | |
| 5 Live proof | Not started | |
| 6 Docs + skills | Not started | |

Status vocabulary: Not started → In progress → Partially implemented → Fully implemented, or Blocked (with reason). Record deviations from locked decisions with a one-line reason.
