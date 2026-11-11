# Approved scenarios (post-audit)

Derived from `scenario-catalog.md` after the audit pass of 2026-11-10. This file is the working
yardstick: only the scenarios judged valid for this version, with the audit's decisions woven into
each `Expected` line, plus the negatives that survived. The original catalog stays untouched as the
full idea-space; anything cut from it is listed with its reason in the appendix so the decision is
reversible with a record.

**Use:** for each scenario, run the `Check:` lines against the system and record a verdict
(covered / partial / absent / silent-absent / divergent / unexpressible / environment-blocked —
definitions in the catalog §16). `Tier:` says what a codified test needs:

| Tier | Harness |
|---|---|
| **T1** | Script: kernel API + database assertions + local IdP. Deterministic, CI-able. |
| **T2** | Fake provider with a personality: a drivable chat/ticket host that can inject awkward events. |
| **T3** | Identity gymnastics: multiple real logins with different group memberships, controllable clock. |
| **T4** | Manual rubric: human judgment, scored by hand. |

---

## Global principles (extracted from the audit — every scenario inherits these)

These bind everywhere; a scenario's `Check:` lines do not restate them.

- **P1 — Explicit interaction only.** Work or reactions trigger on: a mention of the system, a tag/label
  on a ticket, or an unambiguous command. Ordinary conversation never triggers. Ambiguous intent is
  clarified before work starts, never guessed.
- **P2 — Team scope gates everything.** Any action that should trigger the system must be within a
  team's scope. An outsider's ticket notifies the team; work begins only when the team signals
  (approval, tag, assignment).
- **P3 — One thread = one work item.** Anyone may add context and it is accounted for; only the owner
  or team-approved members may *act*. Completed items are never reopened — a follow-up is a new item
  linked to the old one.
- **P4 — Identities are earned, never invented.** A chat account must be confirmed against the
  identity provider at least once. Unlinked people may contribute context (valuable, retained) but
  can never invoke actions. Links can be revoked.
- **P5 — Public by default.** Communication is public; only authentication-related exchanges belong in
  private channels. A failed private delivery falls back to an anonymized public message with a
  correction window and a visible abandonment reason.
- **P6 — Transparency without secrets.** Refusals and statuses are honest and maximally informative,
  but never leak secret data: not team ownership, not other people's contacts — only what is safe to
  share for the reader's context.
- **P7 — One consent = one item = one digest.** Approvals are bound to a digest and are never
  batched, even when six identical items are approved together.
- **P8 — Humans resume; the system nudges.** Nothing auto-restarts an expired or blocked work item.
  A resume is an explicit human signal (e.g. `@system resume`, or answering the blocking question).
  Nudges toward humans are encouraged.
- **P9 — The system never wears a human's face.** Everything the system does is attributable to the
  system. It may *link* or *tag* a human; it may not look like a human authored it.
- **P10 — Work does not mix across teams.** Teams may see each other's *public* knowledge only.
- **P11 — History is never squashed.** Reused as context, kept as long as audit retention says,
  readable even when the originating system or conversation is gone. Missing/pruned history answers
  honestly as "I don't know".
- **P12 — Completion is checkable.** "Done" must rest on an artefact: an open change request, a
  document, a new ticket, a released version — never on the word of the executor.
- **P13 — Operators own the machine, teams own their work.** Behaviour/permission switches and rule
  changes are operator-role actions (system-level overrides team-level). Scale and cost controls
  inside a team's execution environment are the team's affair; the system only observes their
  effects. Cost (tokens etc.) attaches to the work item.
- **P14 — Minimum viable surface is two things:** one communication platform and one workspace.
  Everything else (ticketing, code host, pipeline) is valuable additive, not a precondition.

---

## Family A — work enters

### A1 — Sentence in a channel, no mention
**Expected:** silence. Only explicit interaction (P1) produces a reaction.
**Check:** a plausible-work sentence without a mention creates no item and no reply; the same
sentence *with* a mention does.
**Tier:** T2
**Audit:** partial (T2-adapter; evidence: ForgejoIngressTests.UnlabeledOpenedIssueMakesNoWork)

### A2 — Explicit ask, open channel
**Expected:** acknowledgement scaled to the ask — a reaction for small requests, a reply for larger
ones — naming the item. Similar requests are deduplicated or merged; with enough accumulated
intent, the merge becomes a steering request against the existing item instead of a new one.
**Check:** small ask ⇒ reaction only; large ask ⇒ reply with item reference; second similar ask
from the same person ⇒ merged/steered, not a second item.
**Tier:** T2
**Audit:** partial (T2-adapter; evidence: ProviderIngressTests.ReactionNotesAuditsAndNeverTransitions)

### A3 — Ask with an attachment
**Scope narrowed by audit:** manageable types only — text, Markdown, PDF, Word, PowerPoint,
images. Video/audio: later, if justified.
**Expected:** the attachment reaches the executor readable, the item history records it, failures
are stated, not swallowed.
**Check:** an image + sentence ⇒ executor receives both; a video (unsupported class) ⇒ honest skip
note, work still proceeds on the text.
**Tier:** T2
**Audit:** covered (T2-adapter; evidence: TelegramIngressTests.LabeledMentionRoutesWorkToRuleTeamAndCarriesMediaIntoAssign)

### A4 — Outsider's ticket tags the system
**Expected:** P2. The outsider gets no direct service: the system tags/notifies team members (chat
or tracker) that a ticket awaits triage. Work begins only on the team's signal. Short replies
preferred.
**Check:** outsider ticket + tag ⇒ team notification, no executor started; team applies the
work-label ⇒ item created and dispatched; outsider receives at most a brief neutral response.
**Tier:** T2
**Audit:** partial (T2-adapter; evidence: ProviderIngressTests.UnlinkedMentionRepliesWithoutWork)

### A5 — Insufficient information
**Expected:** the blocker is *the missing information*, never a specific person dependency. State is
visibly "waiting for input". Resume triggers: any answer carrying the needed fact **plus** an
explicit signal (`@system resume`, or an answer to the posed question).
**Check:** item parks in a non-failed, non-running state; a comment from any entitled member
+ `@system resume` continues the *same* item; capacity is not consumed while parked.
**Tier:** T1
**Audit:** covered (T1-gate; evidence: InteractionLifecycleTests.EventParksAssignmentTimelinesQuestionAndFansOutToDefaultEndpoint)

### A6 — Question routed to the right place
**Expected:** ideal — the system chooses the channel where the question becomes visible to someone
who can answer. Acceptable — the question is asked in at least one human-visible place. Never
silent.
**Check:** question raised while origin conversation is a ticket and the team lives in chat ⇒ the
question appears somewhere a team member will see it (either place is valid; zero places fails).
**Tier:** T2
**Audit:** partial (T2-adapter; evidence: NotificationLedgerTests.CadenceResolvesChannelOverTeamOverSystem)

### A7 — Follow-up in a bound conversation
**Expected:** P3. Further messages join the existing item; anyone may add context; only owner or
approved members may act. Completed item + follow-up ⇒ new linked item, never a reopen.
**Check:** plain reply on an open item ⇒ no new item, history row present; a non-owner reply ⇒
context recorded, no state change; reply after completion ⇒ new item with a link back.
**Tier:** T2
**Audit:** covered (T2-adapter; evidence: ProviderIngressTests.TerminalTransitionAppendsOneReplyToBinding)

### A8 — Operator view entry point
**Expected:** it exists but is *not* the emphasized core workflow. One work item may travel across
integrations (chat ↔ ticket ↔ code host) as long as the system tracks where communication is
currently active.
**Check:** create item via operator view ⇒ history names the real person; an item originating in
chat, later discussed on a ticket, keeps one identity and one "active where" marker.
**Tier:** T1
**Audit:** covered (T1-gate; evidence: AdminSurfaceTests.SummaryCountsEverySeededLedgerSection)

### A9 — API-initiated work
**Expected:** P4 hardened: no API may *invent* a person. A caller acts either as itself (an
identified application) or with a proven link to a person; unverified attribution to a human is
refused.
**Check:** API call claiming "on behalf of alice" without a valid link/credential ⇒ refused or
recorded as the application's own request, never as alice's.
**Tier:** T1
**Audit:** covered (T1-gate; evidence: WorkLifecycleHttpTests.FullLifecycle_WalksStates_TimelineAndOutboxComplete)

### A10 — Work from the clock
**Expected:** a scheduled item surfaces in a team-visible place — chat message tagging the owners,
a tracker ticket, either — as long as a human is in the loop.
**Check:** schedule fires ⇒ team-visible notification exists and names accountable people; no
silent item appears.
**Tier:** T1
**Audit:** covered (T1-gate; evidence: OpsSurfaceTests.ScheduleFiringCreatesWorkAndAnnouncesItToTheTeam)

### A11 — Work from an external event, and merging
**Expected:** dedupe may be human-steered ("this is a duplicate of X") or automatic once a
certainty threshold is met. A merge never squashes history: merged content stays as context.
**Check:** two events, second declared duplicate ⇒ one item, both histories retained and linked;
after merge, the original ids still resolve (redirect, not erase).
**Tier:** T1
**Audit:** covered (T1-gate; evidence: WebhookSinkTests.RawDeliveryQueuesWebhookCommandOnceAndReplayDedupes)

### A12 — Cross-team dependency
**Expected:** an *interrupting* event. The system raises it in chat ("I need feature A from team B's
backlog to proceed — I am pausing; do you want to wait or cancel?"). A human decides; the item
does not hang unnoticed.
**Check:** child dependency on another team ⇒ a question appears in the requesting team's
conversation with wait/cancel options; the parent's state visibly reflects the wait.
**Tier:** T2
**Audit:** partial (T2-adapter; evidence: TeamRosterTests.AWorkOfAForeignTeamIsInvisibleNotForbidden)

### A13 — Misrouted ask
**Expected:** loud, not silent: a misroute raises a visible alarm and a human triages it.
**Check:** ask whose target capability belongs to no rostered team ⇒ routed to triage with a
visible flag; the asker is told a human will pick it up.
**Tier:** T1
**Audit:** partial (T1-gate; evidence: AssignRuleRoutingTests.MatchingLabelRoutesCodingCapabilityToRuleEndpoint)

### A14 — Nonsense ask
**Expected:** exactly one refusal, as informative as P6 allows: reason + what would have worked.
No secret leakage (ownership, contacts).
**Check:** malformed command ⇒ single reply; reply text contains no other-team identifiers; a
second identical send ⇒ still at most one refusal per delivery group.
**Tier:** T2
**Audit:** partial (T2-adapter; evidence: LinkIngressTests.UnknownCommandFailsClosedWithAuditRow)

### A15 — Same ask twice
**Expected:** when the item's own identity is determinable, dedupe on it; otherwise judgment.
Repeating "update translations" with *different content* is a different item.
**Check:** byte-identical delivery twice ⇒ one item + duplicate noted; same command, different
payload ⇒ two items.
**Tier:** T2

---

## Family B — standing and identity
**Audit:** covered (T2-adapter; evidence: DuplicateIngressTests.FixtureSteer_AcceptedOnce_DuplicateReplay_InvalidFixturesFailClosed)

### B1 — Identity linking
**Expected:** a person can start linking unattended, gets a usable message explaining how, and can
revoke the link later. P4.
**Check:** unlinked user sends `/link` ⇒ challenge issued + instructions; redemption as a real IdP
subject binds it; revoke removes future action rights.
**Tier:** T3
**Audit:** covered (T1-gate; evidence: ProviderIngressTests.LinkMintChallengeRedeemFlowWithFences)

### B2 — Unlinked person participates
**Expected:** their message still reaches the executor as context (valuable input), but no command
from them is executable.
**Check:** unlinked user comments with useful detail ⇒ recorded as unverified context on the item;
unlinked user issues a command ⇒ polite refusal, no action.
**Tier:** T2
**Audit:** covered (T2-adapter; evidence: ProviderIngressTests.UnlinkedMentionRepliesWithoutWork)

### B3 — Owner disappears
**Expected:** the item becomes team-owned — never abandoned, never hidden.
**Check:** owner's account deprovisioned ⇒ item appears in the team's unowned/attention view with
state intact.
**Tier:** T3
**Audit:** covered (T1-gate; evidence: SessionLossTests.DeadOwner_FailsAttemptOnceWithValidRetryableEvent)

### B4 — Private delivery impossible
**Expected:** P5. Fallback = anonymized message in the public channel, correction window, and if
abandoned, a visible reason. Private channels reserved for auth.
**Check:** outcome requiring a DM to a never-`/start`ed user ⇒ public anonymized notice naming the
cause; abandonment leaves a reason in the item history.
**Tier:** T2
**Audit:** covered (T2-adapter; evidence: NotificationLedgerTests.UnlinkedDmTargetFallsBackToTheTeamChannelAnonymized)

### B5 — Name collision
**Expected:** ambiguity is always surfaced, never silently resolved.
**Check:** "tell Alex" with two matching team members ⇒ disambiguation question, not a pick.
**Tier:** T2
**Audit:** covered (T2-adapter; evidence: NotificationLedgerTests.AmbiguousNameAsksInsteadOfPicking)

### B6 — Machine-originated input
**Expected:** best case, the system recognises its own writes and another machine's writes;
fallback, a loop budget caps exchanges. A shared human+machine credential is always a configuration
warning: hope for the best, design for the worst.
**Check:** two fakes ping-pong ⇒ exchanges stop at the budget with a visible notice; adapter
configured with a human-named credential ⇒ warning at startup.
**Tier:** T2
**Audit:** covered (T2-adapter; evidence: ProviderIngressTests.BotEchoUpdatesBindingLedgerOnly)

### B7 — Delegation
**Expected:** delegation is explicit data. Further delegation is allowed and is a transfer of
ownership, so it is recorded as one.
**Check:** alice delegates a consent to bob ⇒ bob decides, history names both; bob delegates to carol
⇒ ownership transfer recorded.
**Tier:** T3

---

## Family C — consent and consequential actions
**Audit:** covered (T1-gate; evidence: ConsentAuthorityTests.TwoRespondersOneDecidedAndTheLoserSeesWhoDecided)

### C1 — Permitted action, audit trail
**Expected:** it runs, and the record is reconstructible for as long as retention is kept — a
shorter audit window is the operator's legitimate choice (P11).
**Check:** after a permitted action, the trail answers: who asked, which rule allowed it, when.
**Tier:** T1
**Audit:** covered (T1-gate; evidence: ApprovalLifecycleTests.RequiredWithActionRecordsPendingRowWithKernelDigest)

### C2 — Held action, single approver
**Expected:** the item parks visibly with the proposal, requester, and expiry. The approver may
navigate to another place (operator view, ticket) to make an *informed* decision — that is a viable
design — but the decision itself must be expressible from where the notice was seen.
**Check:** held action ⇒ state "awaiting approval" with a locatable proposal; a digest is recorded;
approve from wherever ⇒ executes exactly once.
**Tier:** T1
**Audit:** covered (T1-gate; evidence: ConsentAuthorityTests.HeldCommandStoredWithTheApprovalRow)

### C3 — Unentitled approver answers
**Expected:** P6: entitled or not, they can *see* the action exists; they cannot *act* on it. The
denial does not consume the held action.
**Check:** outsider of the eligible set responds ⇒ refusal, action still pending for the entitled;
outsider can read the item's status.
**Tier:** T3
**Audit:** covered (T1-gate; evidence: ConsentAuthorityTests.OutsiderDenyDoesNotConsumeTheApproval)

### C4 — Two approvers answer at once
**Expected:** one wins, the other learns honestly it was already decided. Low practical impact
(communication happens in external systems anyway) but the answer must be truthful.
**Check:** concurrent identical responds ⇒ exactly one decision recorded; loser gets
"already decided by X", not a generic error.
**Tier:** T1
**Audit:** covered (T1-gate; evidence: ConsentAuthorityTests.TwoRespondersOneDecidedAndTheLoserSeesWhoDecided)

### C5 — Consent staleness
**Expected:** audit verdict: **the digest principle stands** — consent binds to the digest; a
changed proposal requires new consent. Expiry is terminal; revocation before apply wins.
**Check:** mutate the held proposal ⇒ old consent cannot apply; expire then respond ⇒ terminal deny;
revoke then respond ⇒ deny.
**Tier:** T1
**Audit:** covered (T1-gate; evidence: ConsentAuthorityTests.ExpiryFlipsApprovalAndHeldActionAndParksTheWork)

### C6 — Consent ignored
**Expected:** a nudge before expiry is desirable; expiry then parks the item. Any restart is
**explicit and human** (P8) — for coding work, a restart may reuse the branch and prior context,
but only because a human asked.
**Check:** no response past expiry ⇒ item terminal + requester + owner notified; nudge precedes
expiry; no auto-retry observed.
**Tier:** T1
**Audit:** covered (T1-gate; evidence: ConsentAuthorityTests.NotRequesterRuleDeniesTheRequestingSideAsSoleConsent)

### C7 — Consent from chat
**Expected (audit decision):** a chat decision manifests as a ticket in the tracker, and a separate
work item carries that ticket. Chat-only items remain legitimate for research-class, size-scoped
work. (This is the known-soft seam; the target shape is a decision in chat that is
*provenance-equivalent* to a decision in the operator view, for an entitled, linked person.)
**Check:** chat `/approve` by a linked entitled member ⇒ decision recorded with the person's proven
identity (not merely "chat user 123"), ticket/comment evidence created, later audit shows the chain.
**Tier:** T3
**Audit:** covered (T1-gate; evidence: ConsentAuthorityTests.ChatDecisionDispatchesTrackerRecordAndCarrierWork)

### C9 — Requester ≠ approver
**Expected:** team owners can interject at any level. Significant work is carried by a human — an
automated work item's external record (e.g. a tracker ticket) has a human owner.
**Check:** requester tries to be the sole consent for a rule marked "not-requester" ⇒ denied; an
automation-created tracker ticket carries a human owner field.
**Tier:** T1
**Audit:** covered (T1-gate; evidence: ConsentAuthorityTests.NotRequesterRuleDeniesTheRequestingSideAsSoleConsent)

### C10 — Rule changes
**Expected:** changing system rules is an admin-UI action, restricted to the operator role. Period.
**Check:** team member attempts a rule change ⇒ refused; operator applies it ⇒ dated, authored,
reasoned audit entry; the change is visible in later authorizations.
**Tier:** T3
**Audit:** partial (T1-gate; evidence: TeamRosterTests.TeamSwitchOverridesTheSystemDefaultAndIsVersioned)

### C11 — Policy refusal is explained
**Expected:** one clear "I cannot do this because …" plus the next action — distinct in tone from a
failure, actionable, no secrets (P6).
**Check:** action denied by rule ⇒ message names the next step ("ask your team operator"), and is
visibly a refusal, not a crash.
**Tier:** T2

---

## Family D — keeping people informed
**Audit:** partial (T2-adapter; evidence: LinkIngressTests.UnknownCommandFailsClosedWithAuditRow)

### D1 — Progress cadence
**Expected:** the team owner chooses chattiness for their team; any member may request a one-time
status update.
**Check:** quiet-team setting ⇒ outcome posts but no step-by-step chatter; `status` request ⇒
exactly one current-state reply.
**Tier:** T2
**Audit:** partial (T2-adapter; evidence: SlashCommandTests.StatusCommandRepliesDeterministicallyAndLedgersSlash)

### D2 — Stalled work, capacity caps
**Expected:** two cap layers: team-level scaling (the team's gateway/environment) and
per-task scaling (the team's harness). The system's job is only to observe capacity-limited as a
legitimate, visible state — not to own the limits.
**Check:** executor at its cap ⇒ item shows "waiting for capacity" (not "running"); nothing in the
system hard-codes a per-task concurrency limit.
**Tier:** T1
**Audit:** covered (T1-gate; evidence: NudgeSweeperTests.BudgetSpentAtCap_ThenSilent_AndAdminSeesStalled)

### D4 — Partial success
**Expected:** caveats are resolved *before* completion. "Partly done, remainder tracked" is
expressible as: this item done + a new item in the backlog as the definition-of-done artifact.
**Check:** work that can finish only partially ⇒ done-state carries the linked successor item; no
silent half-done.
**Tier:** T1
**Audit:** covered (T1-gate; evidence: WorkRecordTests.PartialCompletion_RequiresSuccessor_RecordsLinkAtomically)

### D5 — Outcome where the request came from
**Expected:** when a ticket originated the work and discussion moved to chat, notify *both*; the
final "done" lands on the origin (the ticket).
**Check:** ticket-origin item ⇒ completion comment on the ticket + a courtesy notice in the chat
thread; both reference the same item.
**Tier:** T2
**Audit:** covered (T2-adapter; evidence: ProviderIngressTests.TerminalTransitionAppendsOneReplyToBinding)

### D6 — The system speaks as itself
**Expected:** P9, never any exception. It may link or tag a human; it never authors as one.
**Check:** every system write is visibly system-attributed in the provider and in the item history.
**Tier:** T2

---

## Family E — outcomes and handover
**Audit:** partial (T2-adapter; evidence: js:src/adapters/communication/github-connector/test/ops.test.ts)

### E1 — Completion is backed by something checkable
**Expected:** P12 — the backing is contextual: an open change request, a research document, a new
issue, a deployed version. "Done" alone is never an outcome.
**Check:** completed item ⇒ its history references at least one external artefact or produced
object; an assert-otherwise item cannot reach done.
**Tier:** T1
**Audit:** covered (T1-gate; evidence: ResultManifestVerticalTests.CompletedWithManifestRefRecordsChainHead)

### E2 — Produced things outlive the conversation
**Check:** conversation deleted/archived ⇒ artefact still resolvable by link, access rules intact.
**Tier:** T1
**Audit:** covered (T1-gate; evidence: ArtifactRetentionGcTests.ExpiredDaysPolicyCollectsAndDeletesBlob)

### E3 — Handover to a human; the system stops
**Expected:** the system recognizes a terminal signal — ticket closed, change request closed,
`/stop` — and stops participating. The human reports completion by ordinary means (closing the
ticket); the item settles accordingly.
**Check:** human closes the ticket ⇒ item transitions to done with the human's close as evidence; no
further system comments after the stop signal.
**Tier:** T2
**Audit:** partial (T2-adapter; evidence: WorkRecordTests.SteerOfTerminalWork_StaysIllegal_ReopenNeverHappens)

### E4 — Review feedback on automated output
**Expected (audit decision):** review-driven follow-up is a **separate work item** carrying the old
item's context + review comments — the pause between change-request and review can be long, so
one item would mix two contexts.
**Check:** review comments arrive on a completed item's change request ⇒ new item, linked, with the
comments as input; original untouched.
**Tier:** T2
**Audit:** covered (T2-adapter; evidence: ProviderIngressTests.ReviewVerdictIsADecisionWithAccountability)

### E5 — "Wrong" vs "changed my mind"
**Expected:** no distinction; both follow the same flow as code review: the objection is input, the
resolution may fork a new item (E4).
**Check:** requester disputes an outcome ⇒ handled identically for both intents, nothing lost.
**Tier:** T2
**Audit:** partial (T2-adapter; evidence: ProviderIngressTests.InputOnCompletedWork_ForksSuccessor_AndNamesIt)

### E6 — Refusal / nothing-produced is a legitimate end
**Expected:** a closing reason suffices — a ticket close reason or a chat comment.
**Check:** refusal outcome ⇒ readable reason in history; distinguishable from failure/expiry.
**Tier:** T1

---

## Family F — failure and recovery
**Audit:** covered (T1-gate; evidence: SessionLossTests.DeadOwner_FailsAttemptOnceWithValidRetryableEvent)

### F1 — Executor offline; queue silent no more
**Expected:** waiting work is surfaced to the people who can fix it — system operators for
infrastructure, team members for their own work — with a reason.
**Check:** executor deregistered, item queued ⇒ operator-visible stall with cause; queue drains on
reconnect without a new request.
**Tier:** T1
**Audit:** covered (T1-gate; evidence: OpsSurfaceTests.ExecutionReadDisclosesEndpointAndLiveness)

### F2 — Retry inherits context
**Expected:** everything recorded in a failed attempt becomes context for the next; repeat work only
when unavoidable.
**Check:** attempt 1 writes observations, fails ⇒ attempt 2's inputs include attempt 1's record; no
duplicate side effects from a replayed step.
**Tier:** T1
**Audit:** partial (T1-gate; evidence: ReceiptRetryTests.ChildBusyRejectionReoffersAndTheLaterAcceptSettlesIt)

### F3 — Repeated failure ends with a human, not a policy
**Narrowed by audit:** the system does not own retry policy judgment; it must ensure a human is in
the loop at the outcome and the attempts are visible.
**Check:** third consecutive failure ⇒ one human-facing notification with the attempt count; no
silent infinite loop.
**Tier:** T1
**Audit:** covered (T1-gate; evidence: OpsSurfaceTests.ThirdFailureSaysSoExactlyOnce)

### F4 — Cancellation discloses effects
**Expected:** irreversible effects (a deployment) ⇒ surfacing is critical and prominent. Reversible
ones (isolated branch) ⇒ a mention suffices. Cancellation reaches offline executors too.
**Check:** cancel after deploy ⇒ prominent "already deployed" note on the item; cancel with queued
work ⇒ executor never runs it.
**Tier:** T1
**Audit:** covered (T1-gate; evidence: OpsSurfaceTests.CancelDisclosesIrreversibleEffects)

### F5 — Conversation disappears
**Expected:** the item endures; a vanished channel becomes a marked-obsolete address and the system
moves on — it does not chase integration-side changes, it deals with consequences.
**Check:** channel deleted ⇒ delivery degrades to a logged fallback; item history keeps the
obsolete-channel marker.
**Tier:** T2
**Audit:** covered (T2-adapter; evidence: NotificationLedgerTests.VanishedChannelMarksTheAddressObsoleteAndTheItemEndures)

### F6 — Provider unhappy
**Expected:** permission problems are disclosed to the **team** operators who can grant them; a rate
limit stalls only that one integration, disclosed to **system** operators. No global stall.
**Check:** fake provider returns 403 to one connector ⇒ team-operator-visible permission notice;
429 on the same ⇒ other integrations continue; both recover from state, no loss.
**Tier:** T2
**Audit:** covered (T2-adapter; evidence: js:src/adapters/provider-core/test/http-loop-webhook.test.ts)

### F7 — Coordinator restart
**Expected:** held consents survive restart still held; at-least-once redelivery is acceptable *when
redelivery is safe*; nothing a human decided is lost.
**Check:** restart with a held approval + a queued delivery ⇒ approval still pending, delivery
arrives once or twice with identical visible effect (idempotent), no orphaned item.
**Tier:** T1

---

## Family G — several teams (narrowed: no handovers, no joint consent this version)
**Audit:** covered (T1-gate; evidence: RestartSurvivalTests.RestartAfterAcceptedEvents_NoLossNoDuplicate)

### G1 — Visible separation
**Expected:** P10 — work never mixes. Teams may see each other's public knowledge; nothing more.
**Check:** team A lists items ⇒ zero team B rows; team B private artefact unreadable from A (404,
not 403).
**Tier:** T1
**Audit:** covered (T1-gate; evidence: TeamRosterTests.AWorkOfAForeignTeamIsInvisibleNotForbidden)

### G5 — One person, several teams
**Expected:** standing is evident from the communication context — the channel/ticket says on whose
behalf the person speaks.
**Check:** same user acts in team A and team B contexts ⇒ per-team standing applied and recorded
with the context it came from.
**Tier:** T3
**Audit:** covered (T1-gate; evidence: TeamRosterTests.OperatorSeesEveryTeamWithItsOwnStanding)

### G7 — Shared channel, two teams
**Expected:** P1 + never duplicate work: if team attribution is unclear, ask before starting.
**Check:** ambiguous ask in a two-team channel ⇒ a clarifying question, zero items started;
after clarification ⇒ exactly one item on one team.
**Tier:** T2

---

## Family H — knowledge and audit
**Audit:** partial (T2-adapter; evidence: TelegramIngressTests.LabeledMentionRoutesWorkToRuleTeamAndCarriesMediaIntoAssign)

### H1 — Context scopes and self-correction
**Expected:** context is team-owned or system-wide. It is self-correcting: a wrong belief can be
replaced. Private context becomes public only by re-categorization or by being shared through a
public integration (e.g. written into a ticket).
**Check:** correction applied ⇒ later answers use the new belief; a private-memory item quoted into
a public ticket becomes public for that content only; no other private leak.
**Tier:** T4
**Audit:** unexpressible (T4-manual; evidence: no codified test)

### H2 — Ask later
**Expected:** answerable while history is retained; pruned ⇒ honest "I don't know" or a pointer to
external documentation.
**Check:** query a retained decision ⇒ answer with person/date/artefact; query a pruned one ⇒
"I don't know", never a fabrication.
**Tier:** T4
**Audit:** unexpressible (T4-manual; evidence: no codified test)

### H3 — Audit scope = whatever the operator wants
**Check:** operator requests actions-in-period for any chosen team/system subset ⇒ exportable,
complete for the scope, no raw credentials.
**Tier:** T1
**Audit:** covered (T1-gate; evidence: OpsSurfaceTests.AuditExportCoversTheWindowInBothFormats)

### H5 — Absent vs pruned
**Expected (audit decision):** acceptable indistinguishability — both answer "I don't know".
**Check:** both cases yield the same honest non-answer.
**Tier:** T4

---

## Family I — operating it
**Audit:** unexpressible (T4-manual; evidence: no codified test)

### I1 — Minimum viable team setup
**Expected:** P14 — a team is productive with one communication platform + one workspace. Ticketing,
code host, pipeline are additive.
**Check:** bring up with chat + workspace only ⇒ asks flow, work executes, outcomes return; adding a
tracker later changes no prior behaviour.
**Tier:** T2
**Audit:** covered (T2-adapter; evidence: EnrollmentTests.EnrollReturnsTokenAndTrustModeAndSeedsDefaultTeam)

### I2 — Switch granularity and precedence
**Expected:** switches at three levels: per-channel, per-team (team operators), per-system (system
operator; overrides cross-cutting team preferences).
**Check:** disable one behaviour for one channel ⇒ only it changes; system-level switch ⇒ wins
against a contradicting team-level setting.
**Tier:** T1
**Audit:** covered (T1-gate; evidence: TeamRosterTests.TeamSwitchOverridesTheSystemDefaultAndIsVersioned)

### I3 — "Why did it do that?"
**Expected:** tiered truth — a team member gets a brief summary + a pointer to where the decision
was made; an operator gets full audit detail.
**Check:** ask-as-member ⇒ short answer + locatable reference; ask-as-operator ⇒ full trail.
**Tier:** T4
**Audit:** unexpressible (T4-manual; evidence: no codified test)

### I4 — Changing what a team may do
**Expected:** new integration = infra change; behaviour/permission changes = configuration by
operators. Teams do not self-experiment with rules.
**Check:** operator config change takes effect, audited; a team-member attempt is refused.
**Tier:** T3
**Audit:** partial (T1-gate; evidence: AdminConfigStoreTests.MissingChangeReasonHeaderGets400)

### I5 — Queue order and visible starvation
**Expected:** arrival order (no priority field this version). Starvation must be visible. Scaling
laws for team-run environments live in the team's environment (gateway-controlled harness scale,
team token budgets).
**Check:** overload a team ⇒ queue order holds; a long-waiting item is surfaced as waiting-long;
no system-side per-team concurrency knob exists.
**Tier:** T1
**Audit:** partial (T1-gate; evidence: OpsSurfaceTests.QueueReadAgesThePendingIntents)

### I6 — Cost attaches to the work
**Check:** an item with consumption ⇒ a cost figure on the item record, visible to owner/operator.
**Tier:** T1
**Audit:** covered (T1-gate; evidence: WorkRecordTests.Cost_RoundTripsVerbatim)

### I7 — History outlives its connector
**Check:** remove the ticketing integration ⇒ old items still render their history and links as
text, not as errors.
**Tier:** T2

---

## Family K — non-human contributors (narrowed per audit)
**Audit:** partial (T2-adapter; evidence: ProviderIngressTests.ProviderEventRefusedWhenFeatureDisabled)

### K1 — A contributor identity the team can point at
**Expected:** the identity's *form* follows the platform (bot account, synthetic account, whatever
the platform supports) — but it is always: team-owned, named, unique, discoverable by team members.
**Check:** team directory shows the contributor with its name, id, and owning team.
**Tier:** T2
**Audit:** covered (T2-adapter; evidence: ContributorTests.RegistrationSeatsTheContributorOnTheRoster)

### K2 — Assignable, distinct, known
**Narrowed:** KPI/throughput tracking of automated work is out of scope this version.
**Expected:** a separate identity the team visibly recognizes as automated; assignable from the
team's normal flow.
**Check:** assign an item to the contributor by name ⇒ it holds and moves the item like any
assignee.
**Tier:** T1
**Audit:** covered (T1-gate; evidence: ConsentAuthorityTests.TrackerWithoutOwnerAccountRecordsTheGap)

### K3 — Contributions read like a human's
**Expected:** output is natural, human-readable, with enough context to review. Reviewers are team
members chosen by the integration's own mapping (group or named individuals). All the team's
required checks apply — the contributor can fail them like anyone.
**Check:** a contributor-authored change request has description/tests/linkage; required checks gate
it; review requests reach the mapping's targets.
**Tier:** T2
**Audit:** partial (T2-adapter; evidence: ProviderIngressTests.LinkedMentionCreatesWorkOnceAndDuplicateReturnsJudgment)

### K4 — Participates in review as any other participant
**Expected:** the automation responds to objections and works improvements like any participant.
Decision audit (who accepted what) lives primarily in the integration's own traces; the system
records that the *human* review decisions happened.
**Check:** reviewer objects ⇒ contributor responds and revises; a human approval on the change
request appears in the item's history as a human decision.
**Tier:** T2
**Audit:** covered (T2-adapter; evidence: ProviderIngressTests.ReviewVerdictIsADecisionWithAccountability)

### K6 — Honest attribution
**Expected:** P9 — never credited to a human. The team configures whatever distinct identity the
platform needs (MR author, commit trailer); the system enforces "not silently a human".
**Check:** contributor's change request author ≠ any team member's human account; item history
names the contributor identity.
**Tier:** T2
**Audit:** covered (T2-adapter; evidence: ContributorTests.HandleCollisionsAcrossTeamsRefuseTheWrite)

### K7 — Remit changes are operator actions
**Expected:** a team operator (part of the team, close at hand) adjusts the contributor's remit;
config-level, audited (C10).
**Check:** operator widens the remit ⇒ dated, authored audit entry; new action allowed; team member
cannot do it.
**Tier:** T3
**Audit:** covered (T1-gate; evidence: ContributorTests.RemitChangeIsAnAuditedOperatorAction)

### K9 — Scaling contributors
**Expected:** one identity may be shared by multiple instances, scaled within the team's allowance;
running two identities is also the team's option. Collision handling among instances is the team's
design problem; the system must keep instances attributable to the same identity consistently.
**Check:** two instances of one identity ⇒ both sets of work attribute to the single identity,
correctly; team-level allowance bounds instance count.
**Tier:** T2
**Audit:** partial (T2-adapter; evidence: ContributorTests.RegistrationSeatsTheContributorOnTheRoster)

### K10 — Offboarding a contributor
**Expected:** treated like a team member leaving (B3): open work becomes team-owned, history stays
legible, nothing auto-rewrites.
**Check:** retire contributor identity ⇒ its open items appear in the team's attention view; its
past records still name it.
**Tier:** T3

---

## Family L — accountability (single surviving scenario)
**Audit:** covered (T1-gate; evidence: ContributorTests.RetirementMovesOpenWorkToTheTeamAndKeepsTheName)

### L2 — Accountability terminates at people
**Expected:** for any consequential action, the trail reaches a *group* responsible for it and an
identifiable person who steered the process. No deeper chain required by design.
**Check:** any automated consequential action ⇒ trail shows owning team (group) + steering person.
**Tier:** T1

---

## Negative scenarios (survivors)

| ID | Must not happen | Family | Verdict |
|---|---|---|---|
| N1 | An unrelated conversation creates work, or every message gets a reply. | A | covered |
| N2 | The system answers its own message and starts an unbounded exchange with another automation. | B | covered |
| N3 | An action runs that nobody permitted, or is permitted by a rule nobody can point at. | C | covered |
| N4 | A person's display name is treated as their identity, or a name collision silently picks someone. | B | covered |
| N5 | Consent to one thing is accepted as consent to a different thing that merely looks similar. | C | covered |
| N6 | An expired or withdrawn consent executes anyway because a slow process got there later. | C | covered |
| N7 | A failure looks like a refusal, a refusal looks like silence, or silence looks like completion. | F | covered |
| N8 | An outcome is posted where nobody was told the request existed. | D | covered |
| N9 | Private material from one team becomes visible to another team by being quoted into a shared conversation. | G | covered |
| N10 | A produced thing is visible as an unexplained broken link rather than as something the viewer may not see. | E | partial |
| N11 | Two deliveries of one message produce two work items, with no note that a duplicate was seen. | A | covered |
| N12 | A restarted system asks a human the same question twice, or applies one answer twice. | F | partial |
| N13 | A credential belonging to a person is used by automation in a way that makes it look like that person acted. | K | covered |
| N14 | Work is lost because a channel was renamed, a ticket closed, or a person left. | F | covered |
| N15 | The audit trail contains the raw credential, or omits the decision that made an action lawful. | H | covered |
| N16 | A person cannot find out, from the system, why the system did the thing it just did. | I | partial |
| N17 | An unattended automation accumulates waiting-for-humans items that nobody owns. | C | partial |
| N18 | A wide authority is granted as a convenience, and never revisited because nobody can see it. | C | covered |
| N19 | An automated participant acts with no identity a team member can look up, so a record says only that "something" did it. | K | covered |
| N20 | Automated work runs beside the team's process instead of through it — skipping review, checks, or conventions because it is automated. | K | partial |
| N22 | A contributor is credited as the accountable party for an action, ending the trail at a machine instead of at a person or role. | L | covered |
| N23 | A held action sits with its question never shown to anyone entitled to answer it. | C | covered |
| N24 | A consent request carries too little to decide on, so approving means approving whatever happens next. | C | partial |
| N25 | Consent requests arrive at a rate that teaches reflexive approval; nothing throttles, groups, or protects the approver's attention. | C | absent |
| N26 | A retry repeats a visible side effect: a second comment, a second outward message, a second deployment. | F | covered |
| N27 | One upstream fault fans out into items, consents, and alerts at every team at once, ungrouped. | F | partial |
| N28 | A stalled queue is quiet: work waiting for a capacity that never returns tells no one. | F | covered |
| N29 | The system's own write (comment, change, label) re-enters as new input and triggers work again. | B | covered |
| N30 | A queued instruction executes after its moment has passed — after a cancellation, a closure, a hand-off — with nothing checking the context still stands. | F | covered |
| N31 | Two automated participants make overlapping changes and the clash surfaces only after it is visible. | K | absent |
| N32 | Every automated participant shares one identity, so records separate human from machine but nothing more. | K | covered |
| N33 | A contributor's self-report ("checks passed", "nothing broken") is recorded as verified evidence rather than as an unverified claim. | K | covered |
| N34 | A new permission rule silently changes what an older rule used to mean, with no note to anyone whose earlier action relied on the old reading. | C | partial |
| N37 | An item's readable account and its durable trail disagree about the same facts. | F | partial |
| N38 | "Completed" rests on the executor's word with nothing checkable attached to it. | E | covered |
| N39 | Silencing one unwanted behaviour requires switching off a large part of the system. | I | covered |
| N40 | A team's routine action exists only on the operator's surface, so the team needs central help to do its own work. | I | covered |
| N41 | A forgetting rule is half-honoured: copies survive in summaries, quotes, or caches beyond the rule's reach. | H | partial |
| N42 | One person becomes two participants across systems, or two become one, and standing, notifications, and history split accordingly. | B | partial |
| N43 | Work items wait on each other in a cycle, or a wait chain outgrows anyone's ability to follow it, and neither is flagged. | A | partial |
| N44 | Held-action content reaches an unentitled reader sideways — a shared preview, a notification body — where the item itself would have refused. | C | partial |

N40 note: audit reframes it — the operator view is explicitly *not* the core workflow, so a team
action trapped there is a design violation by the system's own principle.

---

## Appendix — cut in this pass (reversible, with the audit's words)

Positives: **B8** shared-device consent ("don't even consider this") · **C8** batch consent
("never batch — bad for auditability") · **C12** emergency/break-glass route (not part of the
original system) · **C13** outward customer messaging (internal-company scope only) · **D3** work
moving between executors (not in scope yet) · **D7** quiet-hours/mute profiles (none planned) ·
**D8** incident tempo (complexity not planned; a single incident → a single item as a feeling) ·
**F8** new-member mid-flight narrative (history owned by integrating systems) · **G2, G3, G4, G6,
G8** cross-team handover/joint-consent/topology ("this complexity was not planned at this point")
· **H4** sensitive-material policy (forgetting is real and inescapable; the rest deferred) ·
**K5** convention conformance (harness/team-owned, facilitator unaware) · **K8** self-reported
limits (harness-side) · **L1** company-rule governance · **L3** company-wide rollups ("never —
team autonomy") · **L4** per-capability least privilege (team-owned credentials) · **L5** comparable
arrangements (no predefined shape).

Negatives: **N21** invisible automated volume (KPI tracking out of scope) · **N35** history
rewritten by reorg (with G8) · **N36** handover over-exposure (with G2).
**Audit:** covered (T1-gate; evidence: ConsentAuthorityTests.TrackerWithoutOwnerAccountRecordsTheGap)
