# Router teams: deterministic sign-over so one bot can serve every team

**Status:** idea, pre-plan. All anchors read from the tree (2026-11-14).
**One-sentence shape:** a team whose only power is a flow — ingest
everything, own nothing: every message and its artifacts are
deterministically **signed over** to the rightful owning team before
any harness sees them, and outbound media is **rented** (scoped grant),
never surrendered. Transfer inbound, grant outbound.

## 1. Why: the platform-bot question the current planes answer only halfway

A single globally-hosted Telegram bot (or a shared gateway+connector
fleet) is architecturally clean today precisely because of the star
topology: adapters and harnesses are dialers, both directions of
traffic ride the facilitator API, and the bot needs zero
team-awareness — reply commands carry the structured
`conversationTarget` (`common.v1.json#/$defs/conversationTarget`:
provider, `channelOrResourceId`, `rootThreadId`, `conversationKind`)
derived by the kernel from inbound provenance (`TargetOf(reference)`,
`ProviderEventStore.cs:1648` — how every existing reply, the `On it.`
ack and the fork notice alike, is addressed).

But team law forces a hard choice that the planes only half-resolve:

1. **A link enrollment is exactly one team.** `link_enrollments.team_id`
   is `IsRequired()` with a real FK
   (`LinkEnrollmentEntityConfiguration.cs:18,32`). The whole
   authorization plane — artifacts, consent, memory, audit principals —
   hangs off that column. There is no "platform/shared" enrollment
   type.
2. **Content born on the ingesting plane stays there.** Inbound media
   uploads ride the adapter's enrollment team
   (`telegram/src/media.ts` step 5: "the row lands on the adapter's
   enrollment team, which is what keeps the team-scoped link-plane PUT
   authorized"). The provider event ledger has **no team column at
   all** (`ProviderEventEntityConfiguration.cs` — provider, delivery
   id, body, sha256, verified_reference, received_at); the ingesting
   team is baked into `verified_reference` as a slug
   (`ProviderEventStore.cs:234`).
3. **Cross-team means 404, in both directions, deliberately.**
   `LinkEndpoints.Enroll.cs:250-253`: *"a cross-team row (e.g. a chat
   image stored by the communication adapter's team, routed to a Work
   on another team) answers 404 to that team's workload — honest, not a
   leak."* Mirror leg: the platform bot posting a team-owned image
   hits the same 404 reading the team's artifact to `sendPhoto`.
4. **Work, however, already moves cross-team fine.**
   `AssignRules[].Team` ("Team slug the rule-matched Work is created
   under (blank ⇒ the ingesting workload's team)",
   `ProviderOptions.cs:141-145`), and cross-team exclusion routes only
   to fleets whose manifest `ownerTeam` equals the **work's** owning
   team (`RoutingSink.cs:70-74,193-197`; workless works route
   team-agnostically). So the *work* reaches team-owned infra; the
   *content it is about* does not.

The naive fix — a big "platform" team that ingests and then *shares*
with everyone — inverts the trust model: a master team owns all
communication at rest and can intercept anything. The user-stated
requirement, which this idea takes as its law:

> **Common ownership during ingestion is fine as long as it does not
> remain that way.** A router team's flows assign all communication to
> other teams deterministically; after the flow's resolver runs, the
> router team owns nothing that belongs to someone else.

## 2. The two mechanisms, and why they are different

**Sign-over (transfer) — inbound.** Ownership of an existing row moves
from the router team to the resolver-chosen team, atomically, audited,
digest re-verified. This is cheap and exact because team ownership is
**a column, not a location**: `artifacts.team_slug` is metadata over
scheme-addressed bytes — `ArtifactKeys.cs`: "a row can migrate
providers by re-pointing only its StorageUri scheme". No bytes move, no
copy, no window where two teams can read it (single-owner rows, D2
posture preserved — this is a *move*, not the share table).

**Grant (rental) — outbound.** Team-alpha's harness produces an image;
the platform bot must read it to `sendPhoto`. Transferring it to the
router team would recreate the at-rest master-team property. So the
bot gets a **delivery-scoped read grant**: one artifact, one delivery,
minted kernel-side when the reply command is appended, revoked on the
adapter's receipt (or expiry). `memory_shares` is the structural
template (`Entities.cs:1165-1172`: entry, grantee, granted-by,
created/revoked) with two tightenings: short default TTL and a
consumption stamp, because this grants *content bytes*, not a search
hit.

> **Transfer changes ownership; grant rents access.** Inbound:
> transfer. Outbound: rent. A "share everything" platform team is the
> anti-pattern this law exists to forbid.

In-transit honesty: the platform process terminates the Telegram
connection, so it *sees* every message at ingestion regardless. The
guarantee being built is about **ownership at rest and reachability in
context** — after the resolver runs, the router team owns nothing of
yours, and no team can read another team's content. "The bot can never
intercept" is only true of the bot as notary; the bot-as-observer bound
is stated here so nobody oversells the model.

## 3. The worked deployment

Team `router` holds: one telegram enrollment (the bot), one workflow
(`router-ingress`), nothing else — no harnesses advertise against it,
it holds no memory, no consent grants name it.

```
telegram ──(router enrollment)──> provider.mention + sourceRefs
        born on team `router` (ingest-legal: it owns them for seconds)
   │
   ▼  flow router-ingress (on: provider.* — every provider event type
   │   the bot can emit; matcher law only, no AI):
   │  1. decide {source:"guard", …}         # the TEAM RESOLVER
   │       guard over event.data: labels (`ofac:team-alpha`),
   │       channel/chat-id map, reference.channelOrResourceId —
   │       closed option list, fallback = "no-owner" branch
   │  2. sign-over { team: $branch, refs: [$event.data.sourceRefs] }
   │  3. work-create { capability: <branch-provided>, team: $branch,
   │                  objective: … }         # = today's AssignRules[].Team path
   │  4. emit command.communication.post { to: $event.data.reference, … }
   ▼
team-alpha's plane: event's *content*, artifacts, work, bindings —
all team-alpha at the moment of first external read; routing excludes
non-alpha fleets as today; alpha's harness GETs its own bytes (200s,
not 404s).
   ▲
   │  reply: kernel appends communication.reply to link.<router-bot>
   │  with conversationTarget = TargetOf(origin reference) — the bot
   │  stays team-blind; AND a delivery-scoped read grant on each
   │  alpha-owned artifact riding the command's refs
   ▼
telegram: bot GETs …/content (grant-satisfied, digest ETag as usual),
sendPhoto + caption; receipt revokes the grant.
```

Determinism law: the resolver is a `decide {source: "guard"}`
(workflow-plane palette, `workflow-definition.v1.json`) — labels and
channel maps, the same matcher law as policy files. A model may
*propose* a team in a richer variant, but sign-over executes only on a
guard-decided branch; a 40% guess must never move content ownership.

**Follow-on messages.** After the first sign-over the thread's
`communication_bindings` row is repointed to the owning team in the
same atomic act (the table has no team column —
`CommunicationBindingEntityConfiguration.cs`: provider/channel/
root_message_id unique, work_id, idempotency key — so a nullable
`team` column is the whole schema change). Future inbound messages on
the thread resolve to team-alpha via the binding at ingest time and
are **born team-owned** — the router leg runs once per thread, not
once per message. Unknown new chat → router flow again; the binding
map is the accumulating, auditable routing table.

**History.** `provider_events` bodies live on the ingesting plane with
no team column. Law: **copy-on-forward** — when a team's context
construction (media-and-context-plane §4/§5) wants the thread window,
the kernel materializes the window as a team-owned row/artifact (same
digests, new rows, new owner, provenance points at the router ledger
row). The owning team gets the content; the router keeps only the
ledger (which it needs anyway for idempotency: the delivery-id UNIQUE
constraint). The router's raw ledger is not a second content plane —
retention-GC posture applies, and a router team may hold a
short-retention policy entry precisely because ledger ≠ content.

**Memory.** Already solved, no new mechanism: a pure router team holds
no memory entries, and cross-team reads are exactly `memory_shares`
explicit grants. Restate only: sign-over does **not** imply a memory
grant; if a harness should recall router-adjacent facts, that team's
policy must say so per the federation law.

## 4. Validator law (the guarantee, made mechanical)

The whole point is a checkable property, not a convention:

- **No-dispatch-before-sign-over:** a flow leg that dispatches
  (`work-create`, `emit`, `invoke`) with a context containing refs or
  history born on a team other than the target work's team fails
  enable-time with `content-not-signed-over`, *unless* a prior
  `sign-over` step names that branch. Statically: every `sign-over`
  branch must precede, in the `on:` graph, every dispatch step whose
  team resolves to a different value. This is the workflow-plane
  validator's referential-pass law applied to ownership.
- **Router-team minimalism is data, not code:** nothing special about
  the slug `router`; what makes a team a router is that it holds only
  enrollments + flows. A boot-time health check (WARN, never fatal)
  flags a team with enrollments but zero enabled flows whose content
  ages past a threshold — "a master team by neglect".
- **Grant hygiene:** grant rows must carry artifact id, grantee
  endpoint, delivery/command id, expiry, receipt state; the content
  route consults them only for link principals with an unconsumed
  grant matching the row; audit rows on mint/read/revoke/expiry. One
  read = one audit line; the receipt command consumes (idempotent:
  replay answers with the stored receipt, the adapter `commands`-ledger
  law already does this shape).

## 5. Touchpoints (small, and mostly columns + one table + one step)

| touchpoint | shape | precedent to copy |
| --- | --- | --- |
| `artifacts.team_slug` transfer | audited UPDATE + digest re-verify + timeline note | `MemoryBroker.PromoteAsync` (in-place transition, owner-gated, timeline note) |
| `communication_bindings.team` | nullable column, repointed atomically with sign-over | the fork's binding `ExecuteUpdateAsync` repoint |
| `artifact_grants` table | artifact, grantee endpoint, delivery id, expires, consumed; **not** a share: no read-by-listing | `memory_shares` (`Entities.cs:1165`) + link receipt law |
| content route | +1 branch: link principal with unconsumed matching grant reads foreign-team row once | `GetForWorkloadAsync` guard shape |
| `sign-over { team, refs }` step | flow palette addition, executable where `work-create` is | Phase-5 work-create extraction pattern (arm helpers → shared law) |
| reply-command mint | reply to router endpoint + grants appended in the same transaction | ResultIngress's dispatch block |
| `provider_events` | **unchanged** (ledger, not content) | — |

Explicitly **not** new: no second store, no new plane, no envelope
change (refs already ride commands; grants are addressed by the
delivery the command *is*), no change to link enrollment's
one-team law (the router team is a normal team), no share-table
generalization (this is transfer + rental, both tighter than sharing).

## 6. What this buys, priced honestly

- One bot / one gateway fleet for N teams, **zero master-team
  interception at rest**: after the resolver, a router-team principal
  gets 404 on your content (the existing non-disclosure law does the
  work — the guarantee is enforced by the code that exists today, the
  sign-over is what *gets* content out of the router's reach).
- The wrong-team accident becomes deterministic: a mis-labeled chat
  lands in the `no-owner` fallback branch (park + escalate — the
  palette has `escalate`), never "leaked to whichever team the bot was
  feeling like today".
- Team-owned infra gets full context: input media 200s for the
  rightful team's harness; reply media works via grants; history
  arrives by copy-on-forward into the team's own context construction.
- The router flow is a flow — visible in the flows console,
  versioned, auditable, editable via YAML — instead of a pile of
  provider options that only the kernel understands.

Costs: two small schema additions (binding team column, grants table),
one palette step + validator rule, one route branch, and the ordering
discipline. The real cost is conceptual: ownership becomes a *state
machine* (born-router → signed-over → [granted, consumed]), and every
future feature that touches content must ask "which state am I
reading?" — which is the same question the D-laws already ask, now
with a transit state named.

## 7. Wave sketch

1. **Transfer primitive** — artifact sign-over as a kernel act (API +
   audit + digest re-verify + binding team column + timeline rows),
   exercised by an admin call only; xUnit: foreign-team still 404s,
   new owner 200s, audit names the actor and the rule.
2. **Grants** — `artifact_grants` + route branch + mint/revoke on
   reply-command lifecycle; walk: team-alpha harness artifact posted
   by the router bot, receipt revokes, replay no-ops.
3. **`sign-over` step + router flow** — palette entry,
   no-dispatch-before-sign-over validator rule, the two-team gallery
   pair (`router-ingress` + a toy `team-home` flow) as the
   documentation, mirroring how Phase-6 ships `ensure-capacity` +
   `agentic-research` as data.
4. **Born-team-from-binding ingest** — resolver consults the bindings
   table before the router flow; second-and-later messages skip the
   router leg entirely.
5. **Copy-on-forward history into `context.build`** — lands together
   with the media-and-context-plane's history leg (shared machinery;
   this idea should not build a second fetch path).

## 8. Open questions

- **Who may sign over?** Flow-only (pure, recommended) vs. also an
  admin mutation (operator emergency for mis-owned threads)? If the
  latter, it must produce the identical audit shape.
- **Transfer of the event body itself:** keep the ledger-on-router
  posture (recommended: ledger ≠ content, copy-on-forward covers
  context) or add a body-copy step to `sign-over`? Middle ground:
  `sign-over` optionally stamps the ledger row's
  `verified_reference` with the new owner without moving bytes.
- **Group chats with two teams' works** (alpha work + beta work bound
  to one thread): bindings are per (provider, channel, root) — one
  team per thread is the current grain. Per-work bindings is a bigger
  schema conversation; does the fallback branch's park+escalate cover
  v1 honestly?
- **Grant expiry vs. receipt:** TTL alone (simple, risks a revoked-but-
  unsent reply) or receipt-command-carrying-consumption (exact, needs
  the adapter's receipt path to name the grant)? Lean: both — TTL is
  the ceiling, receipt is the floor.
- **Does a sign-over re-run when the resolver's next answer
  differs** (chat relabeled mid-thread)? Binding exists ⇒ resolver
  skipped ⇒ owner is sticky until an operator re-runs the resolver.
  Probably right; say it out loud in the doc that ships.
- **Multi-bot / per-team bot tokens later:** this idea is what makes a
  *future* per-team-enrollment model unnecessary for the common case;
  keep the multi-enrollment adapter capability anyway as the escape
  hatch, don't build it.
- **Deletion semantics:** a team cancelling a work never *un-signs*
  content (no return-to-router), but should there be a team-initiated
  **purge** that deletes router-born artifacts outright? (Retention-GC
  posture says yes, cheaply.)

## 9. Research anchors (verified 2026-11-14)

- `Kernel/Shared/Persistence/Configurations/LinkEnrollmentEntityConfiguration.cs:18,32`
  — enrollment team `IsRequired` + FK: one enrollment, one team.
- `protocol/json-schema/capability-manifest.v1.json:10,20` — required
  `ownerTeam`; `Kernel/Routing/RoutingSink.cs:24,70-74,193-197` —
  cross-team exclusion, work's team authoritative, teamless works
  agnostic.
- `Kernel/Integrations/ProviderOptions.cs:133-145` — AssignRules
  `Team` override + "owning team must match the endpoint's fleet"
  manifest-ledger reason.
- `Kernel/Link/LinkEndpoints.Enroll.cs:243-262` — workload content
  read guard; the cross-team-404 law comment verbatim.
- `Kernel/Shared/Persistence/Configurations/ProviderEventEntityConfiguration.cs`
  — no team column on the ledger; `ProviderEventStore.cs:234` —
  ingesting team baked into `verified_reference`.
- `telegram/src/media.ts` step 5 — uploads ride the adapter's
  enrollment team; `chat-ledger.ts` `commands` ledger — replay-answers-
  stored-receipt idempotency to copy for grants.
- `Kernel/Shared/Storage/ArtifactKeys.cs:7,14-18` — ownership is a
  scheme/column, not a location; `ArtifactLedger.cs:56,71,103,144` —
  team-scoped visibility posture (foreign ⇒ absent, non-disclosure).
- `Kernel/Shared/Persistence/Entities.cs:1161-1172` — `memory_shares`
  (explicit cross-team read grants, D2) as the grant-table template;
  `MemoryBroker.cs:341,449` — promote (in-place, owner-gated) as the
  transfer primitive's precedent, share as the counter-example.
- `common.v1.json#/$defs/conversationTarget` + `ProviderEventStore.cs:1648`
  (`TargetOf`) — reply addressing law, already team-blind.
- `CommunicationBindingEntityConfiguration.cs` — unique
  (provider, channel, root), no team column (the delta this idea
  adds); `ForkTerminalAsync`'s binding repoint as the atomic-move
  precedent.
- `OutboxEntityConfiguration.cs` — addressed by `recipient_endpoint_id`
  only: a kernel-emitted reply to a router endpoint needs no team law.
- `protocol/json-schema/workflow-definition.v1.json` +
  `.omp/plans/EMBEDDED_WORKFLOW_PLANE_PLAN.md` — `decide {source:
  "guard"}` determinism law, `work-create` (Phase 5), validator
  referential pass the ownership rule extends, gallery-pair-as-docs
  precedent (Phase 6).
- `.omp/ideas/media-and-context-plane.md` §6 — the forwarding-policy
  entry this idea supersedes for the router-team case (policy gating
  stays right for *other* cross-team forwards; the router case is
  transfer, which is stronger than policy-gated sharing).
