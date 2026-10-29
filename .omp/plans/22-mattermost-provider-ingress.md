# Plan 22 — Provider ingest plane + Mattermost adapter: threads, reactions, echo suppression

**Status:** ⬜ not-started (flips 🔵 at the replanning gate, then 🟡)
**Depends on:** 19 as-built (acceptance run, `referencectl verify`, full-overlay boot proof from 17). 20/21 surfaces optional consumers. **Phase F leg 1 of 9.** Chain: 22 → 23 → 24 → 25 → 26; 27 → 28 → 29; 30 last.
**On approval:** this file is the working detail design; keep the Implementation Status table current.
**Granularity:** single plan, no split — one new kernel ingest seam + one adapter package; the ingest plane is deliberately provider-neutral because 23/26 are its next two consumers. If the account-linking challenge grows its own UX surface, split it out.
**Questions may be asked during planning, but not during implementation** — decisions are locked below; when reality diverges, infer from code/spec per the contingencies section and prefer the simpler robust option.

## Context
Spec §13 (`docs/specs/open_facilitator_framework_architecture.md:680`) defines thin provider adapters with threaded, provider-faithful replies, echo suppression (§13.4, `:721`), reaction-never-consent (§13.3, `:717), and the `CommunicationAdapter` op set (§13.5, `:725`). §26.1 item 9 (echo suppression + thread replies) and §21.2's chat-first demo step on this leg. As-built the kernel has the provider **schema** but zero provider **behavior**: `provider_events` and `communication_bindings` are written by nothing, and there is no `/api/integrations/*` route family. This plan lands the provider-ingest plane (verification → `provider_events` dedupe → normalized inbox → business effect) and the first full adapter (Mattermost TE, already pinned and booted by the full overlay), turning the stored-intent `notifications` config into real behavior.

## Known facts (as-verified 2026-10-29; code wins, divergence goes in the status table)
- **`provider_events` schema-only:** entity `ProviderEventEntity` (provider, provider_delivery_id, body_sha256, body jsonb, verified_reference, received_at) at `src/facilitator/Kernel/Shared/Persistence/Entities.cs:224-241`; unique index `(provider, provider_delivery_id)` partial-filtered at `Configurations/ProviderEventEntityConfiguration.cs:23-25`; in the InitialCreate baseline. Only reference today is the DbSet (`FacilitatorDbContext.cs:39`).
- **`communication_bindings` schema-only:** entity at `Entities.cs:365-387` (provider, channel, root_message_id, parent_message_id, work_id, idempotency_key), config at `Configurations/CommunicationBindingEntityConfiguration.cs:14-17`; its doc-comment cites "Plan 13 behaviour" — stale, this plan owns it.
- **Inbox is provider-ready:** inbox rows carry `provider`/`provider_delivery_id`/`payload_sha256` (`Entities.cs:136-146`) with a unique partial `(provider, provider_delivery_id)` index (`InboxEntityConfiguration.cs:35-37`) — ready but unused. Insert-or-duplicate primitive: `IngressStore.InsertInboxAsync` (`IngressStore.cs:11-31`); duplicate arm `CloudEventIngress.cs:79-85`.
- **Ingress today:** `POST /api/ingress/events` (`Kernel/Work/WorkEndpoints.cs:80` → `CloudEventIngress.cs:14-34`), type-switch arms at `:89-127`; link plane `POST /api/link/v1/events` + SignalR `Envelope` → `LinkIngress.AdmitAsync` (`LinkIngress.cs:35-59`). Zero `/api/integrations/*` routes.
- **Full overlay boots the service, nothing consumes it:** `reference/compose.full.yaml:20` mattermost (pin `MATTERMOST_TAG=11.11.1`, `reference/versions.env`); mattermost DB already created by `reference/postgres-init/01-databases.sql` (Plan 17 step 3). Plan 17 proof: "mattermost boots after fixes" (`17-slim-compose-images.md` step 6).
- **Workload identity + role gate exist:** 09/10's auth plane (`Kernel/Shared/Auth`, `IngressRoleGateMiddleware`); new inbound surfaces ride it with new binding rows — no new auth mechanism.
- **`notifications` config is stored intent:** `{replyVsReaction∈reply|reaction, progressVerbosity∈minimal|normal|verbose}` (`AdminConfigStore.cs:~219`, confirmed at Plan 21 step 0); nothing reads it (`Entities.cs:642-646` comment). This plan is its first consumer.
- **Approvals are digest-bound (§12):** approval round-trip frozen since 11/12; a reaction must never feed the approval path — `approval.respond` arrives only via the authenticated human REST pair (`Kernel/Work/ApprovalEndpoints.cs`).
- **`src/adapters/` today:** `harnesses/deterministic/` + `adapter-sdk/` (harness-side `@ofac/adapter`) only; provider adapters are a new sibling family. `examples/full/*` are README stubs.
- **Test tiers:** e2e precedent = `make link-e2e`/`gateway-e2e`/`worker-e2e` (`Makefile:41-64`); ref-env hosted CI jobs are commented out (free-tier law, `ci.yml:86-90`) — live full-stack proof is the documented local gate.

## Locked decisions
1. **Adapter owns provider trust, kernel owns truth.** The Mattermost adapter (new pnpm package `src/adapters/mattermost`, TS, runs as a full-compose sidecar container on the node image) verifies the provider's request signature/token, then posts **one normalized CloudEvent per inbound provider event** to a new kernel endpoint `POST /api/integrations/{provider}/events` (workload-auth per 10, new role binding `provider.ingest`). The kernel writes `provider_events` (dedupe on `(provider, provider_delivery_id)`, body_sha256 = SHA-256 of the canonical event body) and the inbox in the **same transaction**, then queues business effects via the outbox — the exact `IngressStore` pattern, new arm in the ingress type-switch. Offsets/webhook-ids are delivery metadata, never business completion (§7.5 law).
2. **Normalized event types:** additive protocol schemas per the Plan 11/16 precedent (additive amendment, both harnesses same commit): `provider.mattermost.mention.v1`, `provider.mattermost.reaction.v1`, `provider.mattermost.message.v1` under `protocol/json-schema/` + fixtures. Kernel arms: mention → create-or-link Work via `communication_bindings` (binding lookup on `(provider, channel, root_message_id)`, insert with `idempotency_key` = provider post id); reaction → audit + `notifications.replyVsReaction`-driven ack only, **never** an approval transition (deny-listed in the arm with a test).
3. **Outbound path = adapter as link client.** The adapter connects to the kernel's link plane as a workspace-link consumer (reuses 04 SDK + 06 gateway-less standalone mode): receives `command.communication.reply|react|update|acknowledge` deliveries (additive `communication.*` command schemas) and executes them against the Mattermost REST v4 API (bot account, thread replies under `root_id`). This keeps provider writes behind the same delivery/receipt ledger as everything else. Capability flags advertise the supported §13.5 subset.
4. **Echo suppression:** exact-match on the bot's own `user_id` (fetched at startup, pinned in config `adapters.mattermost.botUserId` fallback: adapter reports it at enrollment and the kernel filters on its own outbound post-id ledger recorded in `communication_bindings.parent_message_id`). Kernel drops bot-authored inbound events before inbox; adapter pre-filters to save the round trip.
5. **Account linking (§11, single-use challenge):** `/link` mention mints a single-use code (kernel record + audit, TTL 15 min), redeem via reply in DM; links `(provider, providerUserId)` → human identity `(iss,sub)` — never email equality. Unlinked actors get a typed reply, no Work.
6. **Work interim:** Work creation from a mention uses the existing assign interim (capability targeting via seed-lib's SQL pattern until routing, Plan 25) — record the divergence in verify output, no new interim mechanism.
7. **E2E law:** live full-Compose proof is a **local gate** (free-tier CI law): new `make provider-e2e` target scripted like `checks/verify.sh` steps (own step file `reference/checks/verify-full.sh`, `referencectl verify --full` flag lands in 30); fast CI tier = fixture/unit only.

## Steps
0. **Research (mandatory, no code).** Read spec §13 + appendix §10; Mattermost REST v4 docs (webhooks/outgoing hooks, bot accounts, thread replies, reactions API); current bot-token auth + CSRF behavior on 11.11.1; `CloudEventIngress.cs` arms; `IngressStore`/`AdmissionGate` seams; role-gate bindings config format; 04 SDK standalone mode; 06's gateway-less mode expectations. Re-verify every anchor above.
1. **Kernel ingest plane.** New `Kernel/Integrations/ProviderIngressEndpoints.cs` (`POST /api/integrations/{provider}/events`), provider-event + inbox same-tx write (extend `IngressStore`), typed provider arms (mention/reaction/message), `communication_bindings` store, audit rows, outbox effects. xUnit + Testcontainers suite (dedupe, unverified-provider reject, binding idempotency, reaction-never-approval).
2. **Protocol additions.** Schemas + fixtures for `provider.mattermost.*` + `command.communication.*`; registry regen; both fixture harnesses green.
3. **Adapter package.** `src/adapters/mattermost`: webhook server (signature verify), normalizer, link-client consumer loop, Mattermost REST v4 client (reply/react/edit/ack), echo filter, account-link challenge flow, config env (`OFAC_FACILITATOR_URL`, `OFAC_ENROLL_SECRET`, `MATTERMOST_*`). Vitest contract tests against recorded webhook fixtures (stored under `protocol/conformance-fixtures/` provider section per cross-plan fixtures-first rule).
4. **Compose + images.** `compose.full.yaml`: `mattermost-adapter` service (node image, command override), env wiring, depends_on healthy mattermost+facilitator; `Dockerfile.node` `pnpm deploy` filter list += `@ofac/mattermost-adapter`; bot pre-provisioning via mattermost CLI init container or config token.
5. **Live proof (local gate).** `make provider-e2e`: boot full stack clean → `@facilitator` mention in a thread → Work created + binding row → completion reply lands in same thread → adapter's own posts produce zero kernel rows → reaction produces the configured ack only → link challenge round-trip. Log transcripts in the status table.
6. **Docs + skills.** `docs/advanced.md` adapter-authoring walkthrough (first worked example, §13.5 ops table as-built); `docs/security.md` provider-ingress row; `reference/README.md` full-profile section; `.omp/skills/facilitator-architecture` + `facilitator-reference-env` updates.

## State after
A `@facilitator` mention in a Mattermost thread creates/links a Work and the completion reply lands in the same thread; Facilitator's own posts never re-trigger processing; reactions ack, never consent. §26.1 item 9 basis lands (full-profile half).

## Testing
- xUnit: provider ingest dedupe (`(provider, delivery_id)` race), same-tx rollback, binding idempotency, reaction→no-transition, unlinked-actor path.
- Vitest: adapter contract tests on recorded fixtures (valid/invalid webhook pairs).
- Local gate: `make provider-e2e` green on a clean full stack; unchanged `referencectl verify` 11/11 slim regression.

## Documentation
`docs/advanced.md` worked adapter example; security + reference-env docs; adapter package README (config, running standalone); skills synced same-change (AGENTS.md rule).

## Architecture fit
§13 — thin provider adapter at the kernel's edge; §7.1 inbox dedupe extended to providers; §11 account linking. The ingest plane this plan carves is reused verbatim by 23 (Forgejo) and 26 (Kafka); the published-op extract becomes the Phase G adapter SDK.

## Additional resources
Spec §13 + appendix §10; Mattermost REST API v4 docs; outgoing-webhook signature docs; 23 will contrast Forgejo webhooks.

## Critical files & anchors
- `Kernel/Shared/Persistence/Entities.cs:224-241,365-387` — the two schema-only tables this plan wakes.
- `Kernel/Work/CloudEventIngress.cs` + `IngressStore.cs` — the arm pattern to extend, not fork.
- `reference/compose.full.yaml` + `Dockerfile.node` — adapter service + image filter.
- `AdminConfigStore.cs` notifications validator — the stored intent this plan consumes.

## Verification
`make build`/`lint`/`test`/`docs-check` green; fixture harnesses cover the new schemas both sides; `make provider-e2e` transcript + slim `referencectl verify` 11/11 recorded in the status table.

## Assumptions & contingencies
- If Mattermost 11.11 webhook signing proves insufficient for the inbound path, fall back to the bot-WebSocket event listener inside the adapter (still one normalized ingest POST per event) — the kernel seam is unchanged.
- If outgoing-webhook delivery to a not-yet-healthy adapter loses early events, the adapter replays via the posts API on startup (provider-events ledger dedupes); do not add kernel-side webhook buffering.
- If bot pre-provisioning in compose proves flaky, seed the bot via the mattermost CLI in an init command of the same service; record divergence.
- If reality contradicts a "known fact", the code wins; record in the Implementation Status table. **Replanning or optimization may be required before implementation begins** — this design is a plan, not a contract.

## Implementation Status
| Step | Status | Notes |
|---|---|---|
| 0 Research | Not started | |
| 1 Kernel ingest plane | Not started | |
| 2 Protocol additions | Not started | |
| 3 Adapter package | Not started | |
| 4 Compose + images | Not started | |
| 5 Live proof | Not started | |
| 6 Docs + skills | Not started | |

Status vocabulary: Not started → In progress → Partially implemented → Fully implemented, or Blocked (with reason). Record deviations from locked decisions with a one-line reason.
