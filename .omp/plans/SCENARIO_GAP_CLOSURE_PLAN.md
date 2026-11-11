# Close the approved-scenario gaps: team standing, work record, consent, egress, contributors, ops, github/gitlab/telegram, then the scenario gate and docs

## Context

`docs/scenarios/approved-scenarios.md` (73 positives, 41 negatives, principles P1-P14) is the working
yardstick. Measured against the code, the kernel already carries the execution machinery - works,
assignments, generation fencing, the routing funnel, digest-bound approvals, artifacts, result
manifests, memory, provisioning - so the gap is the **human/team layer on top of it**: nothing records
who belongs to a team or in what capacity (P2, P4, C3, G5, G7, I2), `works` has no owner, team, links,
merge/redirect or cost (A7, A8, A11, D4, I6), a held action is not persisted so approval-before-execution
is convention (C2, C5, C6, N3, N6, N23), the system can only reply into the conversation it was spoken
to (A6, B4, D1, D5, F5, F6, I7), there is no non-human contributor identity (family K), and no
schedule/queue/triage surface (A10, A13, D2, F1, I5). The three in-scope integrations are thin:
`github`/`gitlab` adapters advertise only `CMD_REPLY` and their connectors can comment but cannot create
an issue, close it, or merge - so C7, D6, E1, E3, E4, K3-K6 cannot be honoured at all; `telegram` is the
most complete comms adapter and needs the fallback ladder plus one loop guard.

End state: the approved scenarios are expressible and observable end to end, proven by a fast
scenario-gate harness over a deterministic faked plane, with the heavy infra legs demoted to a
manually invoked plan, and the public surfaces widened additively (the system is unpublished, so wire and
API changes are allowed, but each one is additive and versioned).

## Ground rules for every step

- Gates after each commit: `make build` (`TreatWarningsAsErrors`), `make test`, `make lint`,
  `make docs-check`, `pnpm -r --if-present typecheck`. Migration generation: `make db-update`
  (root `Makefile:79`).
- EF law (copy, do not reinvent): entity in `src/facilitator/Kernel/Shared/Persistence/Entities.cs`,
  mapping + CHECK in `Configurations/<Name>Configuration.cs` using `CheckSql.InList`, then a migration.
- New wire type law: `protocol/json-schema/<type>.v1.json` -> `pnpm protocol:codegen` ->
  fixtures under `protocol/conformance-fixtures/valid|invalid` -> register in
  `protocol/conformance-fixtures/index.json` -> update `protocol/conformance-fixtures/COVERAGE.md`.
  Copy `protocol/json-schema/command.communication.reply.v1.json` as the shape precedent.
- Outbox law: every effect is a row written in the same transaction as the decision, addressed
  `<channel>.<endpoint>` and resolved by an `ISink` (`Kernel/Shared/Dispatch/ISink.cs`, `Channel` =
  text before the first dot). Internal channels (`kernel.log`, `decode.kernel`) bypass the envelope
  validator; every row that leaves the kernel is validator-gated - copy
  `ProviderEventStore.AppendCommand` / `AppendDecodeRow`.
- Actor law: canonical audit/timeline actor is `oidc:<iss>|<sub>` (`Kernel/Shared/Identity/FacilitatorPrincipal.cs`).
  New actor kinds extend the form; a display name never enters an actor string (P9).
- Public surface: all TS packages are still `publishConfig: null`, so exported types may change, but
  one shape per concern - extend `@ofac/provider-core`, never a second per-provider law.
- Scenario IDs (`P1`, `A7`, `C5`, `N29`) never appear in production source or `docs/` outside
  `docs/scenarios/`. Name a behaviour by what it is.
- New cross-cutting areas get an `AGENTS.md` signpost (`protocol/tools/agents-docs-check.mjs` requires one per
  `src/*` child, caps any AGENTS.md at 80 lines, bans backticked paths that do not exist and
  plan-status prose).
- Commit per functional step, only when green. Never push.

## Step 1 - Team roster, membership, built-in operator roles (P2, P4, C3, C10, G1, G5, G7, I2, I4)

- New table `facilitator.team_members`: `team_id uuid`, `actor text` (`oidc:iss|sub` or `contributor:<id>`),
  `kind text` CHECK `human|contributor`, `capacity text` CHECK `member|operator` (team-operator),
  `added_by text`, `created_at`; unique `(team_id, actor)`; entity + configuration + migration.
- Built-in roles that **cannot be removed by config**, evaluated by the same engine as
  config-mapped roles (`Kernel/Shared/Identity/RoleRules.cs`): `operator` (system operator) and
  `team-operator` (granted iff a `team_members` row with `capacity = operator` exists for the acting
  team). The claim-to-role mapping stays config (`identity:roles`); only these two names become code-declared.
- Roster seeding from config, DB is truth after the first write - copy the exact pattern of
  `Kernel/Shared/Identity/TeamRosterSeeder.cs`.
- Roster API: `GET /api/teams`, `GET` + `PUT` + `DELETE /api/teams/{slug}/members[/{actor}]`,
  `PUT`/`DELETE` requiring `operator`, or `team-operator` for own team. New
  `Kernel/Teams/TeamEndpoints.cs`; register beside the other `Map*Api` calls in `Program.cs`.
- Authorization binding for the new routes in the `identity:authorization:operations` list
  (`reference/config/facilitator.json`).
- **Team scoping now resolves.** Today nothing scopes a read. Every read path - works list/get/timeline,
  artifacts, memory, approvals, audit - filters to the caller's teams, and a foreign team's private
  resource answers **404, not 403** (G1).
- **Decision point:** the `team-member` role is granted today (`reference/config/facilitator.json` grants
  it on claim `/groups` exists) with no roster row to scope against. Chosen: an empty roster for a team
  means "unconfigured, allow"; once a team has rows, the roster gates. This keeps existing tests green
  while making the gate real the moment a roster exists.
- Per-scope switch resolution (P13, I2, N39): new `facilitator.team_settings(team_id, scope_kind CHECK
  `team|channel`, scope_id, resource, payload jsonb, row_version, author, reason, updated_at). One
  resolver `EffectiveConfig.Resolve(actorTeams, teamId, channel, resource)` = system -> team -> channel,
  most specific wins, every answer audited with the level it came from.
- `AdminConfigStore.Resources` (`Kernel/Admin/AdminConfigStore.cs`, the registered-resource list): add `team-behavior`,
  `consent-policy`, `loop-budget`, `escalation`; `retry-policy` gains a reader. `PATCH /api/admin/config/{resource}`
  requires `operator`; a `team-operator` may PATCH only `team_settings` for own team.
- Tests (the suite folder mirrors the feature): `tests/Facilitator.Tests/Teams/*` (roster CRUD +
  authority, cross-team 404, precedence, operator/team-operator split).
- Commit: `feat(teams): team roster, built-in operator roles, scoped effective config`.

## Step 2 - Work as the durable record (A7, A8, A11, A13, D4, I6, I7, L2, N10, N11, N30)

`WorkEntity` is seven columns; everything below is additive columns plus two tables and one operation,
so `WorkDto` and `GET /api/works` stay a superset.

- `works` gains: `team_id uuid null`, `owner_actor text null`, `origin text null` (first-delivered wire
  type), `active_where jsonb null` (`{provider,channel,root,kind}` - one row per work, not N bindings
  standing in for it), `cost jsonb null` (`{tokens, units, currency, source, at}`),
  `merged_into uuid null`, `close_reason text null`.
- New table `facilitator.work_links`: `work_id`, `ref_work_id`, `kind CHECK
  `successor|parent|child|merged-duplicate|context`, `note`, `created_at`, unique `(work_id, ref_work_id, kind)`.
- **Merge/redirect:** `POST /api/works/{id}/merge` body `{intoWorkId, reason}` - the loser keeps its
  history, gets a `merged-duplicate` link and `merged_into`, and `GET /api/works/{loserId}` answers **303
  redirecting to the survivor** (A11: original ids still resolve, history never squashed, P11).
- **Follow-up after completion** (A7/E4): completing a bound conversation and then receiving input never
  reopens. It creates a successor work + a `successor` link, moves the conversation binding onto the
  successor, and the reply names the new item (the fork lives in `AppendWorkInputAsync`, and arm
  judgments re-bind to the effective work so a re-delivery of the fork's message notes the successor).
  Provider-origin works carry `origin` (admitting wire type), `team_id` and `active_where`. Reaching a
  terminal work by transition or steer stays illegal (`StateGuard`), so the successor path is the only one.
- **Partial success** (D4): the `completed` transition accepts an optional `{successorWorkId, note,
  basis, closeReason}`; the link is required for the `partially-complete` basis, recorded in the same
  transaction as the transition. A stated `reason` doubles as the closure basis; a successor link written
  by the same call is itself evidence.
- **Misrouted ask** (A13): with no capability candidate, the work goes `blocked` with
  `routing_triage = 'triage'` on the decision row (implemented column name) and the bound conversation is
  told a human will pick it up - loud, not silent. Triage is written ONCE (the column is the ledger); the
  no-route law stands untouched (the intent keeps requeuing so a fleet that later advertises the
  capability still routes normally - the resume law then lifts the block, blocked->planning).
- **Closure evidence** (E1/E6, N38): a completion carrying no artifact, no manifest, no external
  `work_links` row and no `close_reason` is refused with a stable problem code; the reason is a first-class
  field, never free text in `detail`.
- `WorkDto` (+ `protocol/openapi/admin-v1.yaml` `AdminWorkRow`) gains the new fields; a merged work answers
  GET with **303 See Other** (a first-class `IResult`, not `Results.LocalRedirect` - that is 302);
  `POST /api/works/{id}/cost` stores the executor's cost jsonb verbatim.
- **Provider-project consolidation (decision):** provider-origin works land on the per-team synthetic
  project named by `ProviderEventStore.ProviderProjectName`. They now carry `team_id` too, and team scoping
  reads `works.team_id`, so those projects stay as the routing anchor without lying about ownership.
- Tests in `Work/WorkRecordTests.cs`, `Integrations/ProviderIngressTests.cs`,
  `Routing/RoutingFunnelTests.cs` (merge + 303 via a no-redirect client, successor-not-reopen, cost
  round-trip, unbacked-completion refusal, misroute-to-triage once + resume-on-candidate,
  N11 duplicate-noted with a renumbered delivery of one native message - the business identity strips
  delivery bookkeeping AND attribution fields, and only a prior judgment that bound a work can note).
- Commit: `feat(work): owner, team, links, merge-with-redirect, cost, closure evidence`.

## Step 3 - Held actions and consent authority (C2, C3, C5, C6, C7, C9, N5, N6, N23, N24, N25)

- New table `facilitator.held_actions`: `held_action_id uuid`, `approval_id uuid`, `work_id uuid null`,
  `capability_id text`, `endpoint_id text`, `envelope jsonb` (the command to deliver after consume),
  `action_digest text`, `state CHECK `pending|approved|denied|expired|revoked|delivered``, `attempts int`,
  `decided_by text null`, `nudge_at timestamptz null`, `created_at`, `updated_at`; one row per approval,
  FK `approvals`. Copy `Kernel/Shared/Persistence/ApprovalLedger.cs` - conditional UPDATE + lazy expiry, no janitor.
- **Persist-first, law D3** (F7): the held row is written before any notice is sent. Approve ->
  `approved` -> the gateway's existing consume command delivers the stored envelope and flips
  `delivered` -> exactly-once execution. C5's mutate-then-consent is already enforced by `ApprovalDigest`
  recomputation.
- **Consent authority** is one evaluator over the approval row, so a deny can never consume the held
  action (C3): a decision records only for an eligible actor; an outsider gets a refusal that still
  discloses the action exists (P6) and the row stays `pending`.
- `approvals` gains `requested_by text null`, `rule_id text null` (the rule that made this consent
  necessary - N3 needs the rule name), `nudged_at timestamptz null`.
- Rule flag `not-requester` denies the requester as sole consent (C9). The requesting endpoint id is
  already persisted as `approvals.requested_by_endpoint_id`; an endpoint-to-team lookup resolves it to a
  person for the rule check.
- **Approval nudge + expiry sweeper** - new `Kernel/Work/ApprovalSweeper.cs`, copy `Kernel/Work/NudgeSweeper.cs`
  (`PolicyResource = "consent-policy"`, `SweepOnceAsync()` public + in-proc). A nudge before expiry;
  expiry flips the approval to `expired` **and** the held action to `expired`, notifies requester + owner
  (step 4), and parks the work in `blocked` with reason `consent-expired` (C6: any restart is a human
  signal, P8). This removes today's lazy-expiry-only gap.
- **Chat consent = a ticket** (C7, the known-soft seam): an entitled chat decision creates the tracker
  record through the provider plane (`vcs.issue.create` + a `vcs.issue.comment` carrying decision,
  decider, digest) and a separate work item carrying that ticket. With no tracker bound it records the
  decision with the person's proven identity plus a timeline row naming the missing tracker, instead of
  silently doing nothing.
- **Approval attention** (N17/N23/N25): a held action whose eligible set is empty or whose conversation
  is obsolete fans out to the team roster, never to zero readers; `consent-policy` carries
  `{maxInFlightPerActor, groupWindowSeconds}`.
- New verb `deny` aliased to `approve <id> reject` in `Kernel/Ai/CommandRegistry.cs`.
- Tests in `Approvals/`: held row created before notice, mutate-then-approve denied, outsider deny does
  not consume, two responders -> one decided + the other told who decided, nudge-before-expiry, expiry
  parks the work, not-requester rule, chat-consent ticket effect.
- Commit: `feat(approvals): held-action store, consent authority, expiry sweeper with nudge`.

## Step 4 - Notification ledger and the egress fallback ladder (A6, B4, B5, D1, D5, F5, I7, N8, N10)

Today the kernel can emit `command.communication.{reply,react,update,acknowledge}` only back into the
conversation that spoke to it, addressed `link.<provider-adapter endpoint>` taken from
`Providers:<provider>:EndpointId` (`ProviderEventStore.AppendCommand`). Anything beyond escalation is
impossible, so this step replaces the ad-hoc egress with a ledger.

- New table `facilitator.notifications`: `notification_id uuid`, `work_id uuid null`, `audience text CHECK
  `actor|team|operators``, `team_id uuid null`, `actor text null`, `level text` (`info|notice|warning|critical`),
  `text text`, `target jsonb` (a `conversationTarget`), `fallback jsonb` (ordered ladder), `state CHECK
  `pending|sent|fell-back|undeliverable|obsolete``, `reason text null` (P5: a visible abandonment reason),
  `correction_window_ends_at timestamptz null`, `created_at`, `updated_at`.
- New resolver `Kernel/Notifications/EgressResolver.cs`: an ordered ladder - bound conversation -> the
  team's channel for that provider -> an anonymized public post in the team channel (P5) ->
  `undeliverable` with a reason row. DM requires a **linked** provider identity
  (`provider_identities`), else it is skipped, never guessed (P4).
- Single emission path: `NotificationLedger.EmitAsync(...)` writes the row + outbox row in one
  transaction. Retrofit every existing producer to call it: the ack/reply in
  `Kernel/Integrations/ProviderEventStore.cs`, the fan-out in `Kernel/Work/InteractionIngress.cs`,
  `Kernel/Ai/CommandApplier.cs` and `Kernel/Ai/DecodeSink.cs`. The wire commands are unchanged, so no
  adapter protocol change is needed here.
- **Vanished conversation** (F5, N14): a delivery receipt that reports an unmappable target flips the
  notification to `obsolete` and records an `address-obsolete` timeline row naming the provider and
  channel. The work and its history are untouched (I7: old history renders as text, not as an error).
- **Per-team + per-channel cadence** (D1, N39): `admin_config` `notifications` grows to
  `{default: {...}, teams: {<slug>: {...}}, channels: {<provider>/<channel>: {...}}}`, resolved through
  step 1's `EffectiveConfig`. The existing reader in `Kernel/Ai/DecodeSink.cs` (reply-vs-reaction) reads
  through the same resolver so one config truth serves both.
- **One status reply** (D1): the `status` verb in `Kernel/Ai/CommandApplier.cs` answers exactly one
  current-state message built from `work` + assignments + held actions + links + cost.
- **Outcome where the request came from** (D5, N8): on completion, notify every binding of the work
  (`communication_bindings` by `work_id`) - tracker comment on the origin + a courtesy reply in the chat
  thread, both naming the same item.
- **Ambiguous name** (B5): name resolution across the roster returns every match and asks; a silent pick
  is a defect (N4).
- **Provider 403 vs 429** (F6): an adapter receiving 403 emits a `provider.degraded` event (new
  `protocol/json-schema/event.provider.degraded.v1.json`, fields `provider`, `reason`, `status`,
  `endpointId`, `observedAt`, `retryable`) -> a team-scoped notification for the team operators; 429
  stalls only that endpoint's deliveries through the existing outbox backoff and notifies the system
  `operators` audience. One provider stalling never blocks another (F6).
- New tests in a new `tests/Facilitator.Tests/Notifications/` folder mirroring the feature area:
  unlinked DM target -> anonymized public notice naming the cause; deleted channel ->
  `obsolete` + history intact; quiet team -> outcome without chatter; 403 vs 429 -> different
  audiences, other integrations still flowing.
- Commit: `feat(notifications): delivery ledger with a fallback ladder and per-team cadence`.

## Step 5 - Non-human contributors (P9, K1-K7, K9, K10, L2, N13, N19, N20, N22, N32, N33)

- New table `facilitator.contributors`: `contributor_id uuid`, `team_id uuid`, `handle text` (unique),
  `display_name text`, `remit jsonb` (`{capabilityIds: [], operations: []}`), `status CHECK
  `active|retired``, `created_at`, `retired_at`, `changed_by`, `change_reason`; a `team_members` row of
  kind `contributor` is inserted alongside, so the contributor is a first-class actor in one place.
- Canonical actor `contributor:<handle>`; an `actorEvidenceRef` naming a contributor is only mintable by
  that contributor's link enrollment (`Kernel/Link/LinkEndpoints.Enroll.cs`).
- **Not silently a human** (N13, P9): a `work.assign` whose actor evidence resolves to a human subject is
  rejected when the target is a contributor-only capability. That single rule is the enforcement - no
  per-adapter policing.
- **Assignable + team-owned on removal** (K2, K10): a contributor appears as an assignee/owner of a work
  (step 2's `owner_actor` accepts `contributor:<handle>`); retirement moves its open works to
  `owner_actor = null` + the team attention view, and past records keep naming it (B3's law reused).
- **Attribution and evidence** (K3, K4, K6): a change request opened by a contributor is recorded with
  `provider`, the contributor handle, the URL and the human **accountable person** = the work owner
  (L2). A human approval observed on that change request is recorded as a human decision on the item
  timeline (`review.decision`, added to the `work_events_kind_check` list in
  `Configurations/WorkEventEntityConfiguration.cs` + a migration); a contributor's own
  "checks passed" is recorded as an unverified claim.
- **Remit changes are operator actions** (K7/C10): a `PUT` on the remit requires `operator` or
  `team-operator` for own team, writes an audit entry with author + reason + date.
- **Scale** (K9): several link enrollments may share one contributor id - `link_enrollments` gains a
  nullable `contributor_id`; all their work attributes to the one identity.
- Tests in a new `Contributors/` folder: named team-owned directory, assignability, evidence refusal
  against a human subject, retirement moves open work and leaves history, remit change audit.
- Commit: `feat(contributors): named team-owned non-human actors with enforced attribution`.

## Step 6 - Operating the machine (A8, A10, D2, F1, F3, H2, H3, I2, I5, I6, N7, N16, N18, N27, N28)

- **Schedules** (A10): new table `facilitator.schedules` (`schedule_id`, `team_id`, `objective`,
  `cadence` (`interval:<n>m` | `daily:HH:mm` | `cron:<expr>`), `timezone`, `next_run_at`, `last_run_at`,
  `enabled`, `owner_actor`, `created_by`) + `Kernel/Work/ScheduleSweeper.cs` (copy `NudgeSweeper`:
  `SweepOnceAsync()` public, periodic + an admin trigger). Each firing creates a work **and** a
  team-visible notification naming the accountable owner (A10: no silent item).
- **Queue and starvation** (D2, I5, N28): `GET /api/works` gains `?state=&team=` and paging (today it is
  a fixed recent list). New read `GET /api/queue?team=` = pending intents with age, plus oldest-wait age
  **metrics** in `KernelMetrics.cs` (metrics, not a new table), exposed on the admin read models so
  "waiting long" is a query. Arrival order stays as it is (`available_at` FIFO under `SKIP LOCKED`) and
  **no system-side per-team concurrency knob is introduced** (I5).
- **Capacity is observed, never owned** (D2, P13): an assignment may carry the advisory reason
  `waiting-for-capacity` (settable by a gateway status report) and the read model renders it distinctly
  from `running`.
- **Executor offline** (F1): `WorkDto` gains assignments with attempt count, generation, endpoint id and
  last-error, so a queued work with no live endpoint is visible on the work itself, not only by joining
  `/api/admin/links`. Reuse `Kernel/Link/LinkConnectionRegistry.cs` liveness; no new state.
- **Repeated failure** (F3): when an attempt count crosses a threshold, one human-facing notification
  naming the attempt count. No new retry policy in the system.
- **Cancellation discloses effects** (F4): the cancel path appends `effects-disclosed` (new timeline
  kind) listing irreversible effects from `result_manifests`/`work_links` when non-empty; the cancel
  command already reaches offline executors through the existing delivery pump.
- **Switch triggers** (I2): `POST /api/admin/sweeps/{nudge|session-loss|schedules|approvals}` so operators
  force a pass without waiting.
- **Audit export + retention** (H3, N15): `GET /api/admin/audit/export?from&to&team=` (CSV + JSON, no raw
  credentials, H3) and a `retention-days` audit policy resource; H2/H5 need no new code - a pruned
  lookup already answers "not found" and never fabricates.
- Tests: `Admin/`, `Work/` (scheduler -> work + team-visible notice + no silent item; queue-age;
  executor-offline visible on the read model; third failure -> one notice; cancel discloses effects).
- Commit: `feat(ops): schedules, queue age, executor-offline disclosure, audit export`.

## Step 7 - provider-core: one law for 429/403, echo budgets, webhooks (B6, N2, N26, N27, N29, N37)

Everything below lives in `src/adapters/provider-core/src/` so forgejo/github/gitlab/mattermost/telegram
share it instead of each re-inventing it.

- `HttpError` taxonomy (`status`, `retryAfterSeconds`) + `withRetry(client, fn)` honoring `Retry-After`
  on 429 and surfacing 403 distinctly. `OpResult` gains an optional `degraded?: {status, reason,
  retryAfterSeconds}` so a stall is observable, never swallowed (N7, F6).
- Loop budget: extend the existing echo suppression (`isBotAuthored` / `mentionsBot` in
  `normalize-shared.ts`) with a per-conversation exchange counter capped by a config number; on cap, stop
  and say so (B6, N2). The system-side half is a `loop-budget` resource consumed in the provider-ingress
  arm.
- Shared webhook-registration helper on the connector face (create/list/delete a hook) so an
  integration is one command, not a wiki page (I4).
- Ledger GC: bounded compaction of aged `connector_actions` rows in `ledger-core.ts`.
- Commit: `feat(provider-core): shared retry taxonomy, loop budget, webhook registration`.

## Step 8 - GitHub and GitLab (C7, D6, E1, E3, E4, K3-K6, N20)

Shared by both providers, then the vendor-specific remainder.

- **Inbound `event.provider.review.v1`** (new `protocol/json-schema/event.provider.review.v1.json`;
  required `reference`, `provider`, `providerDeliveryId`, `actorProviderAccountId`, `action`
  (`submitted|changed|dismissed`), `state` (`approved|changes-requested`), `text`; optional
  `commentId`, `headRef`, `payload`) and a `Kernel/Integrations/ProviderEventStore.cs` arm that appends
  review as a `work_events` row of kind `review.decision` (step 5). The review-comment normalizer becomes
  shared in `provider-core/normalize-shared.ts`; today GitHub has it and forgejo/gitlab lack it, so
  `src/adapters/communication/{forgejo,gitlab}/src/normalize.ts` emit it too.
- **Review on a completed item forks a new work** with a `successor` link and the review text as input
  (E4/E5); the original is untouched.
- **Connector ops** for both: `vcs.issue.create`, `vcs.issue.close`, `vcs.change_request.merge`
  (`{providerObjectId}`, plus the provider's merge method where the API needs one), so step 3's
  chat-ticket and step 5's merge-evidence paths work on both.
- **Advertised commands**: the adapter capability manifests publish
  `CMD_REPLY|CMD_REACT|CMD_UPDATE|CMD_ACKNOWLEDGE`. Today only mattermost/telegram advertise all four and
  the forgejo/github/gitlab adapters advertise `CMD_REPLY`, so the kernel currently cannot send a
  reaction or an acknowledgement to a code host; the reply handlers accept the family already, so this is
  a manifest fix plus per-adapter handlers where a verb has no provider equivalent.
- **Two-way media** on both trackers (inbound files -> `sourceRefs`, outbound `data.artifacts`) - copy
  the telegram implementation (`src/adapters/communication/telegram/src/media.ts`). A3's class set is
  text/markdown/pdf/docx/pptx/images.
- **Human owner on automation-opened issues** (C9): `vcs.issue.create` inputs gain a required
  `ownerProviderAccountId`, filled from the work owner.
- **System attribution** (D6, P9, N13, K6): a distinct bot account, asserted from its own credential via
  an identity probe - the `BotIdentityProbeUrl` mechanism in
  `src/facilitator/Kernel/Integrations/ProviderOptions.cs` - never inferred from a configured id; the
  adapter refuses to start when the probe disagrees with config. The step-5 rule enforces the
  not-a-human half kernel-side.
- `gitlab` adapter: its capability descriptor is `execution: "synchronous"` while its reply path is
  I/O-bound; align it with the forgejo/github adapters.
- Commit: `feat(github,gitlab): review events, issue create/close, merge, full command family`.

## Step 9 - Telegram (B4, C7, F5, N2)

- Outbound `conversationKind: "channel"` for a team-notice post (one path per surface, not a group post
  wearing a channel costume) via the new
  `protocol/json-schema/command.communication.post.v1.json` (`required: commandId, target, text,
  idempotencyKey`; `target.conversationKind` enum `dm|channel` only).
- Approval buttons (`inline_keyboard`) mapped onto the existing approval respond route, so a chat
  decision carries a proven identity (C7).
- The loop-budget guard moves from per-adapter to provider-core (step 7) so the other adapters get it for free.
- Commit: `feat(telegram): channel posting, approval buttons, shared loop budget`.

## Step 10 - Out-of-scope integrations, only where the core change reaches them

No feature work. The full build fails otherwise.

- `src/adapters/communication/kafka` is a README-only stub; leave it, or give it a package.json + one
  test so it is at least buildable.
- `@ofac/adapter` (harness SDK) + `codex`/`omp`/`deterministic`: only what the new
  completion/event law and the artifact-upload error taxonomy require.
- `src/gateway`: the consume-before-apply loop targets the held action from step 3
  (`command.approval.consume.v1` already exists).
- Dev Container Features (`features/`), `examples/aws`, `examples/local`: untouched unless a gate breaks.
- Commit only if a change was needed: `fix(integrations): follow the widened core contracts`.

## Step 11 - The scenario gate: a deterministic plane, plus the scenario harness

The gate must run in CI without a single external container, so faking is the product of this step.

**Deterministic faked plane** (new workspace package `tools/scenario-plane`, `@ofac/scenario-plane`):

- A **fake provider with a personality**: an HTTP host the provider adapters point at via base-URL
  config. Drivable per scenario - inject a mention, a ticket label, a review, a webhook re-delivery, a
  403, a 429, a vanished conversation. The mattermost/telegram/forgejo/github/gitlab adapters all take a
  base URL today, so the seam already exists.
- A **controllable clock**: a `TimeProvider` swap (the pattern already proven in
  `tests/Facilitator.Tests/Infrastructure/KernelTestFactory.cs` - `Clock = new FakeTimeProvider(...)`, and
  the fake-clock GC family in `GcDbFixture`), so expiry, nudge and cadence legs are instant.
- A **multi-identity IdP**: reuse `tests/Facilitator.Tests/Infrastructure/KeycloakFixture.cs`
  (`TokenForUserAsync(user, password, clientId, clientSecret)` against
  `reference/keycloak/realm.json`, which already carries alice/bob/carol). Add per-team group
  memberships so per-team standing (G5) is expressible.
- Deterministic fakes already ship and stay swapped in: `AiFakes`, `RoutingHarness`, `LinkTestHarness`.
- In-process kernel + Testcontainers Postgres, following
  `tests/Facilitator.Tests/AGENTS.md`: DB access only through `TestDbProbe`, per-feature
  `[Collection("postgres:<feature>")]`.

**Scenario harness** (`tests/Facilitator.Tests/ScenarioGate/`):

- One step per scenario id; a scenario runner that emits `{id, verdict, evidence}` as JSON
  (`--scenario <id> --json`) so a run fills the audit record; three lanes -
  `T1+T3` inside the existing xUnit wall budget, `T2` as a vitest e2e tier over the fake provider,
  `T4` manual, recorded only and never a gate.
- Verdict vocabulary: `covered | partial | absent | silent-absent | divergent | unexpressible |
  environment-blocked`.
- New Make targets (add each to the `.PHONY` line at `Makefile:1`): `scenario-gate` (`dotnet test
  --filter FullyQualifiedName~ScenarioGate`), `scenario-plane-up`, `scenario-gate-e2e`, and
  `ref-env` running the demoted infra legs.
- T4 rubric: `docs/scenarios/manual-rubric.md` - scenario id, procedure, expected, score field.
- Commit: `test(scenarios): deterministic scenario plane and gate harness`.

## Step 12 - Documentation

- `docs/scenarios/audit-results.md`: `id | verdict | lane | codified-test | evidence`, one row per
  approved scenario + the 41 survivors; generated from the harness, no prose.
- `docs/scenarios/approved-scenarios.md`: fill each `Audit:` line from a real run - a verdict, never a
  belief.
- One **domain-concept index** in `docs/scenarios/`: a term per concept (work item, held action,
  notification, contributor, team scope, switch) cross-linked to the spec section that governs it.
  `scenario-catalog.md` stays the frozen idea-space (the audit skill forbids re-editing it).
- `docs/runbooks/`: add `consent-expired.md`, `notification-undeliverable.md`,
  `contributor-retired.md`, `work-merged.md`; update `work-stuck.md` with queue-age and
  executor-offline disclosure.
- **Manual infra plan**: a runbook recording the legs demoted from CI - compose slim/full legs
  (`reference/checks/verify.sh`, `verify-full.sh`, `verify-gitlab.sh`, `verify-replicas.sh`, today behind
  `make provider-e2e` / `make link-e2e`) and the acceptance law `up slim --no-seed && referencectl
  verify` from a clean tree. The law still binds for any change that touches the reference env inputs.
- Signposts: new `src/facilitator/Kernel/Teams/AGENTS.md` (+ `Notifications/` if it becomes its own
  area), and touch the pointers in `AGENTS.md`, `src/adapters/AGENTS.md`,
  `tests/Facilitator.Tests/AGENTS.md`. Every touched area's signpost stays a <=40-line index.
- `.omp/skills/facilitator-architecture/SKILL.md` **in the same commits** that land the schema,
  endpoints and tables (root `AGENTS.md` maintenance rule).
- `docs/specs/open_facilitator_framework_architecture.md`: add a short as-built pointer per newly
  implemented area. The spec is the target design and is not rewritten.
- `README.md`: add rows to the feature table **by hand** - it is prettier-dirty at baseline, never
  `prettier --write` it.
- Commit: `docs(scenarios): audit results, runbooks and signposts for the new surfaces`.

## Critical files & anchors

- `src/facilitator/Kernel/Work/WorkState.cs` - single state authority (`StateGuard.WorkEdges`,
  `AllWorkWires`, the CHECK source); read before touching transitions; new states widen the table, the
  wire codec and the migration CHECK together, never a side channel.
- `src/facilitator/Kernel/Shared/Persistence/ApprovalLedger.cs` +
  `Kernel/Work/ApprovalIngress.cs` - the conditional-update + lazy-expiry + consume-authorization law
  the held-action store must copy; also where the consent-authority evaluator and the
  requester-not-approver check land.
- `src/facilitator/Kernel/Integrations/ProviderEventStore.cs` - 1326 lines holding the arms,
  `ArmEffect`, `CreateWorkFromProviderAsync`, `AppendCommand` (outbox addressing) and
  `InputNote`/`AppendWorkInputAsync`; the single integration point for team scoping, self-write
  detection, the loop budget and the new review arm.
- `src/facilitator/Kernel/Shared/Persistence/Configurations/WorkEventEntityConfiguration.cs` - the
  `work_events_kind_check` literal list; every new timeline kind is a code + CHECK + migration change here.
- `src/adapters/provider-core/src/{ops-core.ts, ledger-core.ts, normalize-shared.ts}` - the law every
  provider pair copies: `OpTable`/`OpError`/`stampedBody`/`payloadSha256`, the settle ledger, echo
  suppression.
- `reference/config/facilitator.json` - the `identity:roles` and `identity:authorization:operations`
  bindings every new route must be added to, plus the roster.

## Verification

- Every step: `make build && make test && make lint && make docs-check && pnpm -r --if-present
  typecheck` from `/workspaces/open-facilitator`, then commit. Never push.
- **Concrete inputs, expected observables** (the new-behavior proof):
  - Step 1: `PATCH /api/admin/config/<resource>` with a token holding only `team-member` -> 403;
    as `operator` -> 200 + a dated, authored, reasoned audit row.
  - Step 2: `POST /api/works` then `POST /api/works/{id}/merge {intoWorkId, reason}` ->
    `GET /api/works/{id}` answers **303**; both histories still list their original rows.
  - Step 3: approve, then mutate the held action's inputs, then the gateway consume -> 409
    digest-mismatch and the held action stays `pending`; two responders at once -> one
    `approval.decided` event, the loser told who decided.
  - Step 4: telegram DM to an account with no `provider_identities` row -> a public anonymized post in
    the team channel naming the cause; a receipt naming a deleted channel ->
    `notifications.state = obsolete` + an `address-obsolete` timeline row.
  - Step 5: `POST /api/works` assigning to a contributor handle -> a timeline row naming
    `contributor:<handle>`; an `actorEvidenceRef` naming a human for a contributor-only capability -> 4xx
    refusal with the rule named.
  - Step 8: a review webhook to the sink -> a `review.decision` timeline row, and on a completed
    work -> a new linked work naming the original in its history.
  - Step 9: a team-level notice -> a post at `conversationKind: "channel"`; an approval button press ->
    the approval flips to `approved` with the decider recorded as `oidc:iss|sub`.
  - Step 11: `make scenario-gate` reports every T1/T3 scenario id with a verdict;
    `make scenario-gate-e2e` reports the T2 ids.
- **Newly failing tests are the per-step proof**, not a later phase: each step's test bullet is written
  in the same commit.
- **Reference env**: `make compose-full`, then a github and a gitlab leg (mirror the gitlab leg in
  `reference/checks/verify-gitlab.sh`, and `make gitlab-up` for a cold GitLab): ask in chat -> ticket ->
  PR -> review comment -> new linked work -> human closes the ticket -> the work settles.
- **Acceptance law**: when a step changes a reference-env input (the new config sections, the roster, the
  new routes), that commit re-proves `up slim --no-seed && referencectl verify` exits 0 from clean.

## Assumptions & contingencies

- **No built-in human team membership for the legacy `team-member` role.** If a test needs a member with
  no roster row, seed a roster row rather than widening the fallback.
- **Team scoping** resolves through `works.project_id -> projects.team_id` for existing rows, and
  directly through `works.team_id` for new ones; the migration backfills `team_id` from the project. If a
  row has neither, it is operator-visible and unscoped - never assumed to be the caller's.
- **If the reference env cannot host two human IdP logins** for a T3 leg, the lane degrades to
  environment-blocked with a re-run note rather than dropping the scenario.
- **forgejo** is the sibling of github/gitlab and keeps working: the widened commands must stay
  additive for it. If a forgejo REST surface genuinely lacks an op (`vcs.discussion.reply`/`resolve`),
  forgejo omits the descriptor from its manifest rather than faking it - the manifest-filtering pattern
  already expresses an absent capability.
- **Mattermost** stays untouched by this plan unless a widened `@ofac/provider-core` command breaks its
  build. If it breaks, fix the build only.
- **T4** scenarios are never a CI gate (manual rubric, `docs/scenarios/manual-rubric.md`): an
  environment-blocked or manual verdict is a legitimate outcome.
- **`retry-policy` finally gains a reader**; today the admin config plane is stored-but-unread for it.
  Making the outbox read it is a behavior change - scope it to the outbox retry path only, so nothing
  else's behavior moves.
