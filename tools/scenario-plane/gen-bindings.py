#!/usr/bin/env python3
"""Generate docs/scenarios/scenario-bindings.json - the scenario gate manifest.

Bindings are a REVIEW artifact: each verdict names the codified tests that
prove it (dotnet: Facilitator.Tests.<Folder>.<Class>.<Method>; js: a repo-
relative vitest file). Run after editing approved-scenarios.md or a test
rename; the ScenarioGate theory fails closed when a binding rots.
"""
import json, pathlib, re, sys

ROOT = pathlib.Path(__file__).resolve().parents[2]
CAT = (ROOT / "docs/scenarios/approved-scenarios.md").read_text()

# id -> (verdict, [evidence], note). Evidence dotnet ids omit the
# "Facilitator.Tests." prefix; js evidence is "js:<path>".
X = "Facilitator.Tests."
B = {
"A1": ("partial", ["ForgejoIngressTests.UnlabeledOpenedIssueMakesNoWork", "js:src/adapters/communication/mattermost/test/normalize.test.ts"], "kernel-side silence proven; the chat no-mention leg lives in adapter normalize"),
"A2": ("partial", ["ProviderIngressTests.ReactionNotesAuditsAndNeverTransitions", "ProviderIngressTests.TerminalTransitionAppendsOneReplyToBinding"], "reaction-vs-reply scaling by ask size not codified; both reply modes are"),
"A3": ("covered", ["TelegramIngressTests.LabeledMentionRoutesWorkToRuleTeamAndCarriesMediaIntoAssign", "js:src/adapters/communication/telegram/test/media.test.ts"], "unsupported-class honest skip proven at the adapter"),
"A4": ("partial", ["ProviderIngressTests.UnlinkedMentionRepliesWithoutWork", "NotificationLedgerTests.UnlinkedDmTargetFallsBackToTheTeamChannelAnonymized"], "outsider served nothing, team lands on the channel via the ladder"),
"A5": ("covered", ["InteractionLifecycleTests.EventParksAssignmentTimelinesQuestionAndFansOutToDefaultEndpoint", "InteractionProviderAutoRespondTests.BoundThreadMessageAnswersOldestPendingRequestOnce"], "parks non-failed; an entitled answer unparks"),
"A6": ("partial", ["NotificationLedgerTests.CadenceResolvesChannelOverTeamOverSystem", "InteractionLifecycleTests.EventParksAssignmentTimelinesQuestionAndFansOutToDefaultEndpoint"], "ladder codified; choosing where an entitled human lives is the ladder's team leg"),
"A7": ("covered", ["ProviderIngressTests.TerminalTransitionAppendsOneReplyToBinding", "ProviderIngressTests.InputOnCompletedWork_ForksSuccessor_AndNamesIt"], ""),
"A8": ("covered", ["AdminSurfaceTests.SummaryCountsEverySeededLedgerSection", "OpsSurfaceTests.AuditExportCoversTheWindowInBothFormats"], "API tier; the SPA is make admin-e2e"),
"A9": ("covered", ["WorkLifecycleHttpTests.FullLifecycle_WalksStates_TimelineAndOutboxComplete", "CliSmokeTests.CliMode_RidesPublicApi_CreateAndGetRoundTrip"], ""),
"A10": ("covered", ["OpsSurfaceTests.ScheduleFiringCreatesWorkAndAnnouncesItToTheTeam", "OpsSurfaceTests.MalformedCadenceIsRefusedAtTheSurface"], ""),
"A11": ("covered", ["WebhookSinkTests.RawDeliveryQueuesWebhookCommandOnceAndReplayDedupes", "WorkRecordTests.Merge_KeepsLoserHistory_RedirectsToSurvivor_ExposesLink"], ""),
"A12": ("partial", ["TeamRosterTests.AWorkOfAForeignTeamIsInvisibleNotForbidden"], "foreign work invisible (no leak); the dependency leg itself is uncodified"),
"A13": ("partial", ["AssignRuleRoutingTests.MatchingLabelRoutesCodingCapabilityToRuleEndpoint", "RoutingFunnelTests.SingleLiveCandidateRoutesOnceWithLedgerEffectAndAudit"], "routing codified; re-route-after-misroute rides the same funnel untested"),
"A14": ("partial", ["LinkIngressTests.UnknownCommandFailsClosedWithAuditRow", "IngressValidationTests.OversizedBody_ReturnsPayloadTooLarge"], "fail-closed legs codified; nonsense classification is the AI plane"),
"A15": ("covered", ["DuplicateIngressTests.FixtureSteer_AcceptedOnce_DuplicateReplay_InvalidFixturesFailClosed", "ProviderIngressTests.RenumberedRedeliveryNotesTheWork_NeverDoublesTheAsk"], ""),
"B1": ("covered", ["ProviderIngressTests.LinkMintChallengeRedeemFlowWithFences", "EnrollmentTokenTests.MintThenEnrollMintsWorkingLinkToken"], ""),
"B2": ("covered", ["ProviderIngressTests.UnlinkedMentionRepliesWithoutWork"], ""),
"B3": ("covered", ["SessionLossTests.DeadOwner_FailsAttemptOnceWithValidRetryableEvent", "ContributorTests.RetirementMovesOpenWorkToTheTeamAndKeepsTheName"], ""),
"B4": ("covered", ["NotificationLedgerTests.UnlinkedDmTargetFallsBackToTheTeamChannelAnonymized", "NotificationLedgerTests.NoLadderLeftUndeliverableNamesTheReason"], ""),
"B5": ("covered", ["NotificationLedgerTests.AmbiguousNameAsksInsteadOfPicking"], ""),
"B6": ("covered", ["ProviderIngressTests.BotEchoUpdatesBindingLedgerOnly", "ProviderIngressTests.ReviewVerdictIsADecisionWithAccountability"], ""),
"B7": ("covered", ["ConsentAuthorityTests.TwoRespondersOneDecidedAndTheLoserSeesWhoDecided", "ApprovalPushConsumeTests.LinkRecordedApprovalCarriesRecorderAndApprovePushesDecidedOnce"], ""),
"C1": ("covered", ["ApprovalLifecycleTests.RequiredWithActionRecordsPendingRowWithKernelDigest", "TracePropagationTests.UntracedAssign_StampedIntoStoredEnvelopeAndSpanTrace"], ""),
"C2": ("covered", ["ConsentAuthorityTests.HeldCommandStoredWithTheApprovalRow", "ConsentAuthorityTests.ConsumeReturnsTheKernelStoredEnvelope"], ""),
"C3": ("covered", ["ConsentAuthorityTests.OutsiderDenyDoesNotConsumeTheApproval"], ""),
"C4": ("covered", ["ConsentAuthorityTests.TwoRespondersOneDecidedAndTheLoserSeesWhoDecided"], ""),
"C5": ("covered", ["ConsentAuthorityTests.ExpiryFlipsApprovalAndHeldActionAndParksTheWork"], "fake-clock expiry; the slow-consume race rides the same lazy-expiry law"),
"C6": ("covered", ["ConsentAuthorityTests.NotRequesterRuleDeniesTheRequestingSideAsSoleConsent", "ConsentAuthorityTests.ConsumeReturnsTheKernelStoredEnvelope"], ""),
"C7": ("covered", ["ConsentAuthorityTests.ChatDecisionDispatchesTrackerRecordAndCarrierWork", "js:src/adapters/communication/telegram/test/handler.test.ts"], "buttons ride the reply command's optional buttons"),
"C9": ("covered", ["ConsentAuthorityTests.NotRequesterRuleDeniesTheRequestingSideAsSoleConsent"], ""),
"C10": ("partial", ["TeamRosterTests.TeamSwitchOverridesTheSystemDefaultAndIsVersioned", "TeamRosterTests.OperatorSeatsAndRemovesAMemberWithAuditedProvenance"], "writes versioned+audited; the consent-rule backdating leg is untested"),
"C11": ("partial", ["LinkIngressTests.UnknownCommandFailsClosedWithAuditRow", "MemoryQuotaGcTests.DailyBudgetRefusesThirdRetainWithTypedProblem"], "typed-problem refusals per surface; prose legibility is a manual rubric"),
"D1": ("partial", ["SlashCommandTests.StatusCommandRepliesDeterministicallyAndLedgersSlash", "OpsSurfaceTests.QueueReadAgesThePendingIntents"], "on-demand status + queue age codified; proactive per-item cadence untested"),
"D2": ("covered", ["NudgeSweeperTests.BudgetSpentAtCap_ThenSilent_AndAdminSeesStalled", "FlowControlTests.WindowFullPausesAndReceiptResumesAndDeliversNext", "OpsSurfaceTests.GatewayQueueReasonRendersOnTheExecutionRead"], ""),
"D4": ("covered", ["WorkRecordTests.PartialCompletion_RequiresSuccessor_RecordsLinkAtomically", "ResultManifestVerticalTests.CompletedWithManifestRefRecordsChainHead"], "partial completion is a first-class record demanding a successor"),
"D5": ("covered", ["ProviderIngressTests.TerminalTransitionAppendsOneReplyToBinding"], "outbox addresses link.{endpointId} of the bound conversation"),
"D6": ("partial", ["js:src/adapters/communication/github-connector/test/ops.test.ts", "ProviderIngressTests.BotEchoUpdatesBindingLedgerOnly"], "bot identity stamped per adapter; one coherent cross-provider voice is the pair law"),
"E1": ("covered", ["ResultManifestVerticalTests.CompletedWithManifestRefRecordsChainHead", "ArtifactRejectionTests.TamperedBytesRejectSlotAndAudit"], ""),
"E2": ("covered", ["ArtifactRetentionGcTests.ExpiredDaysPolicyCollectsAndDeletesBlob", "ResultManifestGcTests.RecordedManifestPinsBlobRegardlessOfWorkState"], ""),
"E3": ("partial", ["WorkRecordTests.SteerOfTerminalWork_StaysIllegal_ReopenNeverHappens"], "terminal stays terminal; the handover-to-human transition is untested"),
"E4": ("covered", ["ProviderIngressTests.ReviewVerdictIsADecisionWithAccountability", "ProviderIngressTests.ReviewOnCompletedWorkForksSuccessor", "PrDispatchTests.PrRequestDispatchesCreateAndParksBranchMarker"], ""),
"E5": ("partial", ["ProviderIngressTests.InputOnCompletedWork_ForksSuccessor_AndNamesIt"], "fork-on-new-input codified; wrong-vs-mind classification rides the AI plane"),
"E6": ("covered", ["SessionLossTests.DeadOwner_FailsAttemptOnceWithValidRetryableEvent", "OpsSurfaceTests.ThirdFailureSaysSoExactlyOnce"], "failed-with-reason is a legitimate end"),
"F1": ("covered", ["OpsSurfaceTests.ExecutionReadDisclosesEndpointAndLiveness", "NudgeSweeperTests.OfflineOwner_SkippedWithoutBurningBudget"], ""),
"F2": ("partial", ["ReceiptRetryTests.ChildBusyRejectionReoffersAndTheLaterAcceptSettlesIt", "ReplayOnReconnectTests.DroppedSocketReplaysOnlyTheUnackedDeliveryOnReconnect"], "same-delivery identity proven; inherited context across executor restart is the link plane's resume law"),
"F3": ("covered", ["OpsSurfaceTests.ThirdFailureSaysSoExactlyOnce", "SessionLossTests.DeadOwner_FailsAttemptOnceWithValidRetryableEvent"], ""),
"F4": ("covered", ["OpsSurfaceTests.CancelDisclosesIrreversibleEffects"], ""),
"F5": ("covered", ["NotificationLedgerTests.VanishedChannelMarksTheAddressObsoleteAndTheItemEndures"], ""),
"F6": ("covered", ["js:src/adapters/provider-core/test/http-loop-webhook.test.ts", "ProviderIngressTests.ProviderEventRefusedWhenFeatureDisabled", "NotificationLedgerTests.ProviderDegradedDisclosesPerStatus"], "retry taxonomy + degraded disclosure per status"),
"F7": ("covered", ["RestartSurvivalTests.RestartAfterAcceptedEvents_NoLossNoDuplicate", "LeaseRecoveryTests.ExpiredLease_ReclaimedAndDelivered", "ReplicaIdentityTests.LeftoverLiveLease_RevokedAndKernelStarts"], ""),
"G1": ("covered", ["TeamRosterTests.AWorkOfAForeignTeamIsInvisibleNotForbidden", "ArtifactAuthorizationTests.ForeignTeamReadsAndWritesAnswerAbsent"], ""),
"G5": ("covered", ["TeamRosterTests.OperatorSeesEveryTeamWithItsOwnStanding", "TeamRosterTests.TeamOperatorRunsItsOwnTeamWithoutGlobalAuthority"], ""),
"G7": ("partial", ["TelegramIngressTests.LabeledMentionRoutesWorkToRuleTeamAndCarriesMediaIntoAssign"], "binding routes to one team; two-teams-one-channel is the pair's binding ledger"),
"H1": ("unexpressible", [], "T4 rubric: docs/scenarios/manual-rubric.md"),
"H2": ("unexpressible", [], "T4 rubric"),
"H3": ("covered", ["OpsSurfaceTests.AuditExportCoversTheWindowInBothFormats", "OpsSurfaceTests.AuditRetentionPrunesOnlyAgedRows"], ""),
"H5": ("unexpressible", [], "T4 rubric; the pruned-vs-absent API leg is the export's window law"),
"I1": ("covered", ["EnrollmentTests.EnrollReturnsTokenAndTrustModeAndSeedsDefaultTeam", "TeamRosterTests.RosterSeedsFromConfigThenDbIsTruth"], ""),
"I2": ("covered", ["TeamRosterTests.TeamSwitchOverridesTheSystemDefaultAndIsVersioned", "AdminConfigStoreTests.ListReportsEveryRegisteredResourceWithExistence", "AdminConfigStoreTests.NeverWrittenResourceGetsConfigNotSet404"], ""),
"I3": ("unexpressible", [], "T4 rubric; the machine leg is the audit export"),
"I4": ("partial", ["AdminConfigStoreTests.MissingChangeReasonHeaderGets400", "AdminSurfaceTests.AuthzMatrixGatesTheSurfaceByRole", "TeamRosterTests.TeamSwitchOverridesTheSystemDefaultAndIsVersioned"], "audited operator writes + role matrix codified; the per-team may-do matrix leg rides the rules engine"),
"I5": ("partial", ["OpsSurfaceTests.QueueReadAgesThePendingIntents", "FlowControlTests.WindowFullPausesAndReceiptResumesAndDeliversNext"], "aging + window flow control codified; overload ordering is dispatcher FIFO by AvailableAt"),
"I6": ("covered", ["WorkRecordTests.Cost_RoundTripsVerbatim", "AdminSurfaceTests.WorkDetailCarriesAssignmentsApprovalsManifests"], ""),
"I7": ("partial", ["ProviderIngressTests.ProviderEventRefusedWhenFeatureDisabled", "AdminSurfaceTests.AdminTimelineMatchesWorkTimelineVerbatim"], "history lives in the kernel timeline, not the connector; per-provider render-as-text is the SPA's admin-e2e tier"),
"K1": ("covered", ["ContributorTests.RegistrationSeatsTheContributorOnTheRoster", "ContributorTests.HumanEvidenceAgainstContributorOnlyCapabilityIsRefusedNotRouted"], ""),
"K2": ("covered", ["ConsentAuthorityTests.TrackerWithoutOwnerAccountRecordsTheGap", "ContributorTests.HumanEvidenceAgainstContributorOnlyCapabilityIsRefusedNotRouted"], ""),
"K3": ("partial", ["ProviderIngressTests.LinkedMentionCreatesWorkOnceAndDuplicateReturnsJudgment"], "contributor-actor work codified for mentions; label-opened legs ride the same store"),
"K4": ("covered", ["ProviderIngressTests.ReviewVerdictIsADecisionWithAccountability"], ""),
"K6": ("covered", ["ContributorTests.HandleCollisionsAcrossTeamsRefuseTheWrite", "ProviderIngressTests.ReviewVerdictIsADecisionWithAccountability"], ""),
"K7": ("covered", ["ContributorTests.RemitChangeIsAnAuditedOperatorAction", "ContributorTests.MissingReasonRefusesEveryContributorWrite"], ""),
"K9": ("partial", ["ContributorTests.RegistrationSeatsTheContributorOnTheRoster"], "seat/unseat codified; no many-contributors scale leg"),
"K10": ("covered", ["ContributorTests.RetirementMovesOpenWorkToTheTeamAndKeepsTheName"], ""),
"L2": ("covered", ["ConsentAuthorityTests.TrackerWithoutOwnerAccountRecordsTheGap", "ConsentAuthorityTests.ChatDecisionWithoutTrackerRecordsIdentityAndNamesTheGap"], "accountability ends at a named human or a recorded gap"),
"N1": ("covered", ["ForgejoIngressTests.UnlabeledOpenedIssueMakesNoWork", "js:src/adapters/communication/mattermost/test/normalize.test.ts"], ""),
"N2": ("covered", ["ProviderIngressTests.BotEchoUpdatesBindingLedgerOnly", "ProviderIngressTests.LoopBudgetStopsTheConversationAtTheCap", "js:src/adapters/provider-core/test/http-loop-webhook.test.ts"], "shared loop budget stops the dialogue"),
"N3": ("covered", ["ConsentAuthorityTests.OutsiderDenyDoesNotConsumeTheApproval", "ApprovalLifecycleTests.RequiredWithActionRecordsPendingRowWithKernelDigest"], ""),
"N4": ("covered", ["NotificationLedgerTests.AmbiguousNameAsksInsteadOfPicking", "NotificationLedgerTests.UniqueNameResolvesThroughTheLinkedIdentity"], ""),
"N5": ("covered", ["ConsentAuthorityTests.HeldCommandStoredWithTheApprovalRow", "ConsentAuthorityTests.MutateThenConsumeReturnsTheMutatedStoredCopy"], "the digest covers the kernel-stored envelope, not the requester's bytes"),
"N6": ("covered", ["ConsentAuthorityTests.ExpiryFlipsApprovalAndHeldActionAndParksTheWork"], "lazy expiry at consume: a slow consume on an expired row is denied"),
"N7": ("covered", ["IngressValidationTests.OversizedBody_ReturnsPayloadTooLarge", "LinkIngressTests.UnknownCommandFailsClosedWithAuditRow", "OpsSurfaceTests.ThirdFailureSaysSoExactlyOnce"], "typed problems, audit rows and escalation never wear each other's face"),
"N8": ("covered", ["NotificationLedgerTests.EmissionWritesLedgerRowAndOutboxRowAtomically", "ProviderIngressTests.TerminalTransitionAppendsOneReplyToBinding"], ""),
"N9": ("covered", ["ArtifactAuthorizationTests.ForeignTeamReadsAndWritesAnswerAbsent", "TeamRosterTests.AWorkOfAForeignTeamIsInvisibleNotForbidden"], ""),
"N10": ("partial", ["ArtifactUploadVerticalTests.LinkUploadHappyPathStoresVerifiesAndTimelines"], "artifact timeline-linked; the owner's-explanation leg is judgment"),
"N11": ("covered", ["DuplicateIngressTests.FixtureSteer_AcceptedOnce_DuplicateReplay_InvalidFixturesFailClosed", "WebhookSinkTests.RawDeliveryQueuesWebhookCommandOnceAndReplayDedupes"], ""),
"N12": ("partial", ["RestartSurvivalTests.RestartAfterAcceptedEvents_NoLossNoDuplicate", "ConsentAuthorityTests.NudgeBeforeExpiryWritesTimelineAndStampsOnce"], "no double-ask for deliveries; settled questions stay settled via the approval ledger"),
"N13": ("covered", ["WorkloadIdentityTests.ModeBRoundTripRotatesAndOldTokenDies", "ContributorTests.HumanEvidenceAgainstContributorOnlyCapabilityIsRefusedNotRouted"], ""),
"N14": ("covered", ["NotificationLedgerTests.VanishedChannelMarksTheAddressObsoleteAndTheItemEndures"], "work addresses the binding; the binding survives the channel"),
"N15": ("covered", ["EnrollmentTokenTests.ListNeverLeaksTheRawLiteral"], ""),
"N16": ("partial", ["OpsSurfaceTests.CancelDisclosesIrreversibleEffects", "ConsentAuthorityTests.NudgeBeforeExpiryWritesTimelineAndStampsOnce"], "why-legs per surface; the completeness rubric rides I3 (T4)"),
"N17": ("partial", ["NudgeSweeperTests.BudgetSpentAtCap_ThenSilent_AndAdminSeesStalled", "ProvisioningLawTests.NoPolicyRow_SweepIsExactlyNothing"], "per-assignment budget + no-policy silence; no global per-day budget beyond the memory plane"),
"N18": ("covered", ["TeamRosterTests.TeamOperatorRunsItsOwnTeamWithoutGlobalAuthority", "TeamRosterTests.PlainMemberMayReadRosterButNotWriteIt"], ""),
"N19": ("covered", ["ProviderIngressTests.ReviewVerdictIsADecisionWithAccountability", "ContributorTests.HandleCollisionsAcrossTeamsRefuseTheWrite"], ""),
"N20": ("partial", ["ProviderIngressTests.ReviewVerdictIsADecisionWithAccountability"], "automation review enters the process as a decision; stage-skipping is provider pipeline state"),
"N22": ("covered", ["ConsentAuthorityTests.TrackerWithoutOwnerAccountRecordsTheGap"], "the trail names a human or records the gap; a contributor is never the terminus"),
"N23": ("covered", ["ConsentAuthorityTests.ChatDecisionDispatchesTrackerRecordAndCarrierWork", "NotificationLedgerTests.EmissionWritesLedgerRowAndOutboxRowAtomically"], "held questions ride the notification ladder atomically"),
"N24": ("partial", ["ApprovalLifecycleTests.RequiredWithActionRecordsPendingRowWithKernelDigest"], "stored action + digest carry content; a minimum-decidability gate is untested"),
"N25": ("absent", [], "no throttle or grouping on consent request volume is codified"),
"N26": ("covered", ["js:src/adapters/provider-core/test/core.test.ts", "WebhookSinkTests.RawDeliveryQueuesWebhookCommandOnceAndReplayDedupes"], "idempotency-keyed ops + settle ledger make retries idempotent"),
"N27": ("partial", ["ProviderIngressTests.ProviderEventRefusedWhenFeatureDisabled", "NotificationLedgerTests.ProviderDegradedDisclosesPerStatus"], "per-status disclosure grouped by provider; cross-team fan-out grouping untested"),
"N28": ("covered", ["OpsSurfaceTests.QueueReadAgesThePendingIntents", "NudgeSweeperTests.BudgetSpentAtCap_ThenSilent_AndAdminSeesStalled"], ""),
"N29": ("covered", ["ProviderIngressTests.BotEchoUpdatesBindingLedgerOnly", "ProviderIngressTests.LoopBudgetStopsTheConversationAtTheCap"], "self-write detection + shared exchange cap"),
"N30": ("covered", ["GenerationFenceTests.StaleGenerationCompletedEvent_Refused422_WithFailedInbox_AndUntouchedLedger", "ConsentAuthorityTests.ExpiryFlipsApprovalAndHeldActionAndParksTheWork"], "generation fences + consume-time authorization refuse stale moments"),
"N31": ("absent", [], "no collision-detection leg between concurrent contributors is codified"),
"N32": ("covered", ["ContributorTests.HandleCollisionsAcrossTeamsRefuseTheWrite", "ContributorTests.RegistrationSeatsTheContributorOnTheRoster"], "one distinct contributor row per automation, handles unique per team"),
"N33": ("covered", ["EvidenceAdmissionTests.UnknownKernelEvidenceDeniesWithAuditRow"], "self-report without admitted evidence denies with an audit row"),
"N34": ("partial", ["TeamRosterTests.TeamSwitchOverridesTheSystemDefaultAndIsVersioned", "ContributorTests.RemitChangeIsAnAuditedOperatorAction"], "versioned+reasoned writes; the old-rule-meaning diff rendering is untested"),
"N37": ("partial", ["AdminSurfaceTests.SummaryCountsEverySeededLedgerSection", "WorkLifecycleHttpTests.FullLifecycle_WalksStates_TimelineAndOutboxComplete"], "read stores vs ledgers codified; a full account-trail sweep is one step"),
"N38": ("covered", ["ResultManifestVerticalTests.CompletedWithManifestRefRecordsChainHead", "ArtifactRejectionTests.TamperedBytesRejectSlotAndAudit"], ""),
"N39": ("covered", ["ProviderIngressTests.ProviderEventRefusedWhenFeatureDisabled", "TeamRosterTests.TeamSwitchOverridesTheSystemDefaultAndIsVersioned"], "per-feature and per-team granularity proven"),
"N40": ("covered", ["TeamRosterTests.TeamOperatorRunsItsOwnTeamWithoutGlobalAuthority", "ConsentAuthorityTests.ChatDecisionDispatchesTrackerRecordAndCarrierWork"], ""),
"N41": ("partial", ["ArtifactRetentionGcTests.ExpiredDaysPolicyCollectsAndDeletesBlob", "MemoryQuotaGcTests.DailyBudgetRefusesThirdRetainWithTypedProblem", "OpsSurfaceTests.AuditRetentionPrunesOnlyAgedRows"], "each store honours its own rule; copies outside the stores are unproven"),
"N42": ("partial", ["ProviderIngressTests.LinkMintChallengeRedeemFlowWithFences", "NotificationLedgerTests.AmbiguousNameAsksInsteadOfPicking"], "challenge-based linking merges identities; two-accounts detection is the collision leg"),
"N43": ("partial", ["DagGateTests.CompletedSiblingReleasesGatedAssignWithDeterministicIdentities"], "gated edges codified; cycle detection and depth alarms are absent"),
"N44": ("partial", ["ConsentAuthorityTests.OutsiderDenyDoesNotConsumeTheApproval", "NotificationLedgerTests.UnlinkedDmTargetFallsBackToTheTeamChannelAnonymized"], "foreign reads answer absent, fallback anonymizes; no held-action preview surface exists"),
}

VERDICTS = {"covered", "partial", "absent", "silent-absent", "divergent", "unexpressible", "environment-blocked"}

entries = []
for blk in re.split(r"\n### ", CAT)[1:]:
    lines = blk.splitlines()
    m = re.match(r"([A-Z]\d+) — (.*)", lines[0])
    if not m:
        continue
    sid, title = m.group(1), m.group(2).strip()
    body = "\n".join(lines[1:])
    tier = re.search(r"\*\*Tier:\*\* (T\d)", body).group(1)
    if sid not in B:
        sys.exit(f"missing binding for {sid}")
    v, ev, note = B[sid]
    entries.append({"id": sid, "family": sid[0], "tier": tier, "title": title,
                    "verdict": v, "lane": "T4-manual" if tier == "T4" else ("T2-adapter" if tier == "T2" else "T1-gate"),
                    "evidence": ev, "note": note})
# The verdict column was stamped onto this table by an earlier gate run;
# the (id, text, family) cells are what the manifest consumes.
for m in re.finditer(r"^\| (N\d+) \| (.+?) \| ([A-Z]) \|", CAT, re.M):
    sid = m.group(1)
    if sid not in B:
        sys.exit(f"missing binding for {sid}")
    v, ev, note = B[sid]
    entries.append({"id": sid, "family": m.group(3), "tier": "T1", "title": m.group(2).strip(),
                    "verdict": v, "lane": "T1-gate", "evidence": ev, "note": note, "negative": True})

for e in entries:
    assert e["verdict"] in VERDICTS, e["id"]

doc = {"$comment": "Scenario gate manifest. ScenarioGateTests proves every evidence id exists; tools/ScenarioGate prints the audit rows. Generated by tools/scenario-plane/gen-bindings.py, then hand-reviewed.", "scenarios": entries}
out = ROOT / "docs/scenarios/scenario-bindings.json"
out.write_text(json.dumps(doc, indent=1) + "\n")
print(f"{len(entries)} scenarios -> {out.relative_to(ROOT)}")
