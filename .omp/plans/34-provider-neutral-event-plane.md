# Plan 34 — Provider-neutral event plane: purpose-bearing domain events in core, mapping in adapters

**Status:** 🔵 replanning — user-approved brief below; produce the full detail design at the gate, then flip 🟡.
**Depends on:** 22 + 23 as-committed (`af84222`). **Sequence: lands BEFORE 24 starts** — 24 consumes the connector seam and must not pin the vendor-shaped wire we are about to cut. Phase F leg 2.5.
**Supersedes:** the per-provider event schemas + per-provider arm cases introduced by 22/23 (clean cutover, no shims — roadmap no-forward-shims law). BOTH shipped providers (Mattermost, Forgejo) cut over in this one change.

## Why (locked user law, 2026-10-30)

Three sentences, each binding:

1. **No unknown types.** Every message type crossing a seam is enumerable and schema-validated by the core protocol. A provider name is never a wire-type segment, a generated core type name, or a kernel string comparison.
2. **Domain-specific, provider-neutral, purpose-bearing events live in protocol + kernel.** The core vocabulary is integration DOMAINS (chat, issues, change requests, pipelines), not vendors.
3. **Adapters are the mapping layer.** Inbound: they translate provider vocabularies into the central domain event that relays the PURPOSE. Outbound: they translate that purpose (reply/assign/op targets) into provider API instructions. Provider→purpose and purpose→provider mappings must live nowhere else.

Consequence for adoption: a new forge (GitLab, GitHub fast-follow) ships as **normalizer + connector policy + one `Providers:<name>` config section**. The core protocol changes only when a genuinely new domain or action purpose appears — an explicit enum widening, never a silent string.

## Locked design decisions (decided in the 2026-10-30 chat — do not relitigate, only detail)

- **Central closed type set (6 types):**
  `org.facilitator.event.provider.message.v1` · `.mention.v1` · `.reaction.v1` (chat family — the Mattermost adapter's `mention/reaction/message` hook vocabulary becomes in-package mapping data, exactly like forgejo's `issues/pull_request/status`)
  `org.facilitator.event.provider.issue.v1`
  `org.facilitator.event.provider.change_request.v1` (word locked: `change_request`, forge-neutral — GitLab `merge_request` / GitHub `pull_request` map in)
  `org.facilitator.event.provider.pipeline.v1` (word locked: `pipeline`)
- **Provider is data, not type.** `data.provider` (required `idToken`) names the provider token (`forgejo`, `mattermost`, later `gitlab`); `reference.provider` matches it. Registry lookup, tenant gate, and dedupe `(provider, provider_delivery_id)` all key off the data token. `ProviderEventClass`: prefix + closed type set + provider token read from envelope `data.provider`, never parsed from the type.
- **Action enums stay closed, per domain** (moved verbatim from the vendor schemas): issue `opened|labeled|commented|closed`; change_request `opened|synchronized|reopened|reviewed|commented|merged|closed`; pipeline `pending|success|failure|error`; chat family keeps its shapes (`text`, `emojiName`). `workTrigger`, `text`, `description`, `payload` (verbatim provider body) carry over per domain as today.
- **Sender binding law:** AdmissionGate requires the sending link endpoint's id == the resolved provider section's `EndpointId`, in addition to the tenant match. Wrong-claimed provider token or wrong-speaker endpoint ⇒ typed `unsupported` + failed audit row (existing fail-closed path).
- **Kernel arms switch on the 6 central types.** Behavior byte-identical to the as-committed arms: Work create + binding + interim assign on `issue.v1` `labeled`+`workTrigger`; input note on comments/pipeline verdicts; deferred steer on `change_request.v1 reviewed` with id `<provider>-review:<deliveryId>`; echo law. Domain judgment strings (`steered:`, `observe:unbound`, `observe:unhandled`) unchanged.
- **Kill every provider-name branch in the kernel.** `ResolveBotIdAsync`'s `options.Provider != ProviderOptions.Mattermost` special case becomes a per-section capability flag (`BotIdentityProbe: bool` — "resolve bot id via SiteUrl+BotToken REST once"); the `/link` unlinked-DM copy ("Your Mattermost account is not linked…", `ProviderEventStore.cs:433`) moves to the provider section as `LinkHintText` (mattermost overlay carries the current string). `ProviderOptions.Mattermost`, `MattermostPrefix`, `SectionName="Providers:Mattermost"` constants die — the section name survives only as DATA (`Providers:mattermost`). The kernel keeps zero vendor vocabulary.
- **`Providers:<name>` sections keep** `Enabled/EndpointId/Tenant/SiteUrl/BotUserId/BotToken/File/Assign*` + the new `BotIdentityProbe`/`LinkHintText`; section name lowercased = provider token; fail-closed per section unchanged.

## Mattermost cutover checklist (same law as forgejo — no half-vendor left)

- Adapter (`src/adapters/communication/mattermost/`): `normalize.ts` emits the three chat-domain types with `data.provider:"mattermost"`; local event-kind constants + `ProviderMattermost*Data` imports die; dataschema URLs point at the central chat schemas; README wire section rewritten as "our mapping table".
- Fixtures: the `event.provider.mattermost.{mention,message,reaction}.*` family re-types/renames to the chat-domain files; every existing trap (no-delivery-id, reference-extra-field, bad-conversation-kind) preserved under the central schema.
- Tests: 22's `ProviderIngressTests` envelopes re-typed; the misaddressing regression (completion → `link.mm-adapter`) and duplicate-judgment tests keep their assertions, new types only.
- Docs/skills: `facilitator-reference-env` + `facilitator-architecture` + `docs/advanced.md` + `docs/security.md` reworded to the domain vocabulary; both adapters described symmetrically as mapping layers.
- Config keeps vendor names where they are data: `Providers:mattermost`, `Providers:forgejo`, tenant/siteUrl values, connector capability ids (open question 1). Data names a product; the wire does not.

## Open questions to resolve at the gate (with recommendation)

1. **Connector capability ids** (`forgejo.pr.create`, `forgejo.issue.read`, …): capability ids are team-side ADVERTISEMENTS riding `work.assign` data — not core wire types — so vendor names there do not violate the law. **Recommend:** keep vendor-scoped ids for this cut (policy files already key them; 25's capability routing is the neutralization point), record neutral naming (`vcs.change_request.create`-style) as a 25 replanning input.
2. **Dataschema URL for provider events:** single `https://schemas.openfacilitator.org/v1/event.provider.<domain>.v1.json` per central schema (adapters emit it); fixture filenames + `index.json` rows follow.

## Edit surface (inventory-verified against `af84222`)

- **protocol/**: delete 6 vendor schemas + `src/protocol/schemas/` mirrors; ~31 fixtures renamed/re-pointed to 6 domain schemas (keep every trap); `index.json` re-types; `schemas.ts` registry; regen `WireTypes.cs` + `src/protocol/src/generated/types.ts` (new `ProviderIssueEventV1Data` etc.; `ProviderForgejo*`/`ProviderMattermost*` GONE).
- **kernel/**: `ProviderOptions.cs` (registry keyed by data token, probe flag + hint text options, constants die), `ProviderEventStore.cs` (type switch on the 6 central types; provider token from data; bot-id probe genericized; link-hint from config), `AdmissionGate.cs` (endpoint-id match), `ProviderEnvelopes.cs` + completion hook unchanged except judgment-id provider prefix.
- **adapters/**: BOTH `normalize.ts` files emit central types + `data.provider` (local vendor types stay in-package — that IS the mapping layer); dataschema lines; README wire sections.
- **tests/**: `ProviderIngressTests.cs` + `ForgejoIngressTests.cs` envelopes re-typed. Gateway e2e connector tests unchanged (`work.assign` data untouched, capability ids kept).
- **docs/skills/roadmap**: advanced.md + security.md event wording → domain vocabulary; both facilitator skills; the integration-placement cross-plan rule reworded (GitLab fast-follow = normalizer + policy + config section; a protocol enum widening is a deliberate act, never a silent string); 23's status table gets a D-note pointing here.

## Acceptance

- Both fixture harnesses green with the renamed central fixtures.
- **Zero vendor names in core:** `grep -ri "forgejo\|mattermost" protocol/json-schema protocol/dotnet/Ofac.Protocol/Generated src/protocol/src/generated src/facilitator/Kernel` ⇒ no hits.
- Replay/judgment, echo, steer, completion-comment behavior unchanged: full xUnit suite + `make provider-e2e` 13/13 on the live full stack + slim `verify` from clean, all green.
- Live proof delta: both adapters now publish domain types with `data.provider` — the existing 13-step chain is the witness, no new steps needed.
- Clean cutover: no per-provider types, schemas, constants, or dual-path code left anywhere central.
