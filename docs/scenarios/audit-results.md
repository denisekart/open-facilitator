| Scenario | Verdict | Lane | Codified test | Evidence |
|---|---|---|---|---|
| A1 | partial | T2-adapter | ForgejoIngressTests.UnlabeledOpenedIssueMakesNoWork · js:src/adapters/communication/mattermost/test/normalize.test.ts | kernel-side silence proven; the chat no-mention leg lives in adapter normalize |
| A2 | partial | T2-adapter | ProviderIngressTests.ReactionNotesAuditsAndNeverTransitions · ProviderIngressTests.TerminalTransitionAppendsOneReplyToBinding | reaction-vs-reply scaling by ask size not codified; both reply modes are |
| A3 | covered | T2-adapter | TelegramIngressTests.LabeledMentionRoutesWorkToRuleTeamAndCarriesMediaIntoAssign · js:src/adapters/communication/telegram/test/media.test.ts | unsupported-class honest skip proven at the adapter |
| A4 | partial | T2-adapter | ProviderIngressTests.UnlinkedMentionRepliesWithoutWork · NotificationLedgerTests.UnlinkedDmTargetFallsBackToTheTeamChannelAnonymized | outsider served nothing, team lands on the channel via the ladder |
| A5 | covered | T1-gate | InteractionLifecycleTests.EventParksAssignmentTimelinesQuestionAndFansOutToDefaultEndpoint · InteractionProviderAutoRespondTests.BoundThreadMessageAnswersOldestPendingRequestOnce | parks non-failed; an entitled answer unparks |
| A6 | partial | T2-adapter | NotificationLedgerTests.CadenceResolvesChannelOverTeamOverSystem · InteractionLifecycleTests.EventParksAssignmentTimelinesQuestionAndFansOutToDefaultEndpoint | ladder codified; choosing where an entitled human lives is the ladder's team leg |
| A7 | covered | T2-adapter | ProviderIngressTests.TerminalTransitionAppendsOneReplyToBinding · ProviderIngressTests.InputOnCompletedWork_ForksSuccessor_AndNamesIt | — |
| A8 | covered | T1-gate | AdminSurfaceTests.SummaryCountsEverySeededLedgerSection · OpsSurfaceTests.AuditExportCoversTheWindowInBothFormats | API tier; the SPA is make admin-e2e |
| A9 | covered | T1-gate | WorkLifecycleHttpTests.FullLifecycle_WalksStates_TimelineAndOutboxComplete · CliSmokeTests.CliMode_RidesPublicApi_CreateAndGetRoundTrip | — |
| A10 | covered | T1-gate | OpsSurfaceTests.ScheduleFiringCreatesWorkAndAnnouncesItToTheTeam · OpsSurfaceTests.MalformedCadenceIsRefusedAtTheSurface | — |
| A11 | covered | T1-gate | WebhookSinkTests.RawDeliveryQueuesWebhookCommandOnceAndReplayDedupes · WorkRecordTests.Merge_KeepsLoserHistory_RedirectsToSurvivor_ExposesLink | — |
| A12 | partial | T2-adapter | TeamRosterTests.AWorkOfAForeignTeamIsInvisibleNotForbidden | foreign work invisible (no leak); the dependency leg itself is uncodified |
| A13 | partial | T1-gate | AssignRuleRoutingTests.MatchingLabelRoutesCodingCapabilityToRuleEndpoint · RoutingFunnelTests.SingleLiveCandidateRoutesOnceWithLedgerEffectAndAudit | routing codified; re-route-after-misroute rides the same funnel untested |
| A14 | partial | T2-adapter | LinkIngressTests.UnknownCommandFailsClosedWithAuditRow · IngressValidationTests.OversizedBody_ReturnsPayloadTooLarge | fail-closed legs codified; nonsense classification is the AI plane |
| A15 | covered | T2-adapter | DuplicateIngressTests.FixtureSteer_AcceptedOnce_DuplicateReplay_InvalidFixturesFailClosed · ProviderIngressTests.RenumberedRedeliveryNotesTheWork_NeverDoublesTheAsk | — |
| B1 | covered | T1-gate | ProviderIngressTests.LinkMintChallengeRedeemFlowWithFences · EnrollmentTokenTests.MintThenEnrollMintsWorkingLinkToken | — |
| B2 | covered | T2-adapter | ProviderIngressTests.UnlinkedMentionRepliesWithoutWork | — |
| B3 | covered | T1-gate | SessionLossTests.DeadOwner_FailsAttemptOnceWithValidRetryableEvent · ContributorTests.RetirementMovesOpenWorkToTheTeamAndKeepsTheName | — |
| B4 | covered | T2-adapter | NotificationLedgerTests.UnlinkedDmTargetFallsBackToTheTeamChannelAnonymized · NotificationLedgerTests.NoLadderLeftUndeliverableNamesTheReason | — |
| B5 | covered | T2-adapter | NotificationLedgerTests.AmbiguousNameAsksInsteadOfPicking | — |
| B6 | covered | T2-adapter | ProviderIngressTests.BotEchoUpdatesBindingLedgerOnly · ProviderIngressTests.ReviewVerdictIsADecisionWithAccountability | — |
| B7 | covered | T1-gate | ConsentAuthorityTests.TwoRespondersOneDecidedAndTheLoserSeesWhoDecided · ApprovalPushConsumeTests.LinkRecordedApprovalCarriesRecorderAndApprovePushesDecidedOnce | — |
| C1 | covered | T1-gate | ApprovalLifecycleTests.RequiredWithActionRecordsPendingRowWithKernelDigest · TracePropagationTests.UntracedAssign_StampedIntoStoredEnvelopeAndSpanTrace | — |
| C2 | covered | T1-gate | ConsentAuthorityTests.HeldCommandStoredWithTheApprovalRow · ConsentAuthorityTests.ConsumeReturnsTheKernelStoredEnvelope | — |
| C3 | covered | T1-gate | ConsentAuthorityTests.OutsiderDenyDoesNotConsumeTheApproval | — |
| C4 | covered | T1-gate | ConsentAuthorityTests.TwoRespondersOneDecidedAndTheLoserSeesWhoDecided | — |
| C5 | covered | T1-gate | ConsentAuthorityTests.ExpiryFlipsApprovalAndHeldActionAndParksTheWork | fake-clock expiry; the slow-consume race rides the same lazy-expiry law |
| C6 | covered | T1-gate | ConsentAuthorityTests.NotRequesterRuleDeniesTheRequestingSideAsSoleConsent · ConsentAuthorityTests.ConsumeReturnsTheKernelStoredEnvelope | — |
| C7 | covered | T1-gate | ConsentAuthorityTests.ChatDecisionDispatchesTrackerRecordAndCarrierWork · ConsentAuthorityTests.NudgeRepliesInTheBoundConversation · js:src/adapters/communication/telegram/test/handler.test.ts | buttons ride the reply command's optional buttons; a lapsing consent nudges with them in the bound conversation |
| C9 | covered | T1-gate | ConsentAuthorityTests.NotRequesterRuleDeniesTheRequestingSideAsSoleConsent | — |
| C10 | partial | T1-gate | TeamRosterTests.TeamSwitchOverridesTheSystemDefaultAndIsVersioned · TeamRosterTests.OperatorSeatsAndRemovesAMemberWithAuditedProvenance | writes versioned+audited; the consent-rule backdating leg is untested |
| C11 | partial | T2-adapter | LinkIngressTests.UnknownCommandFailsClosedWithAuditRow · MemoryQuotaGcTests.DailyBudgetRefusesThirdRetainWithTypedProblem | typed-problem refusals per surface; prose legibility is a manual rubric |
| D1 | partial | T2-adapter | SlashCommandTests.StatusCommandRepliesDeterministicallyAndLedgersSlash · OpsSurfaceTests.QueueReadAgesThePendingIntents | on-demand status + queue age codified; proactive per-item cadence untested |
| D2 | covered | T1-gate | NudgeSweeperTests.BudgetSpentAtCap_ThenSilent_AndAdminSeesStalled · FlowControlTests.WindowFullPausesAndReceiptResumesAndDeliversNext · OpsSurfaceTests.GatewayQueueReasonRendersOnTheExecutionRead | — |
| D4 | covered | T1-gate | WorkRecordTests.PartialCompletion_RequiresSuccessor_RecordsLinkAtomically · ResultManifestVerticalTests.CompletedWithManifestRefRecordsChainHead | partial completion is a first-class record demanding a successor |
| D5 | covered | T2-adapter | ProviderIngressTests.TerminalTransitionAppendsOneReplyToBinding | outbox addresses link.{endpointId} of the bound conversation |
| D6 | partial | T2-adapter | js:src/adapters/communication/github-connector/test/ops.test.ts · ProviderIngressTests.BotEchoUpdatesBindingLedgerOnly | bot identity stamped per adapter; one coherent cross-provider voice is the pair law |
| E1 | covered | T1-gate | ResultManifestVerticalTests.CompletedWithManifestRefRecordsChainHead · ArtifactRejectionTests.TamperedBytesRejectSlotAndAudit | — |
| E2 | covered | T1-gate | ArtifactRetentionGcTests.ExpiredDaysPolicyCollectsAndDeletesBlob · ResultManifestGcTests.RecordedManifestPinsBlobRegardlessOfWorkState | — |
| E3 | partial | T2-adapter | WorkRecordTests.SteerOfTerminalWork_StaysIllegal_ReopenNeverHappens | terminal stays terminal; the handover-to-human transition is untested |
| E4 | covered | T2-adapter | ProviderIngressTests.ReviewVerdictIsADecisionWithAccountability · ProviderIngressTests.ReviewOnCompletedWorkForksSuccessor · PrDispatchTests.PrRequestDispatchesCreateAndParksBranchMarker | — |
| E5 | partial | T2-adapter | ProviderIngressTests.InputOnCompletedWork_ForksSuccessor_AndNamesIt | fork-on-new-input codified; wrong-vs-mind classification rides the AI plane |
| E6 | covered | T1-gate | SessionLossTests.DeadOwner_FailsAttemptOnceWithValidRetryableEvent · OpsSurfaceTests.ThirdFailureSaysSoExactlyOnce | failed-with-reason is a legitimate end |
| F1 | covered | T1-gate | OpsSurfaceTests.ExecutionReadDisclosesEndpointAndLiveness · NudgeSweeperTests.OfflineOwner_SkippedWithoutBurningBudget | — |
| F2 | partial | T1-gate | ReceiptRetryTests.ChildBusyRejectionReoffersAndTheLaterAcceptSettlesIt · ReplayOnReconnectTests.DroppedSocketReplaysOnlyTheUnackedDeliveryOnReconnect | same-delivery identity proven; inherited context across executor restart is the link plane's resume law |
| F3 | covered | T1-gate | OpsSurfaceTests.ThirdFailureSaysSoExactlyOnce · SessionLossTests.DeadOwner_FailsAttemptOnceWithValidRetryableEvent | — |
| F4 | covered | T1-gate | OpsSurfaceTests.CancelDisclosesIrreversibleEffects | — |
| F5 | covered | T2-adapter | NotificationLedgerTests.VanishedChannelMarksTheAddressObsoleteAndTheItemEndures | — |
| F6 | covered | T2-adapter | js:src/adapters/provider-core/test/http-loop-webhook.test.ts · ProviderIngressTests.ProviderEventRefusedWhenFeatureDisabled · NotificationLedgerTests.ProviderDegradedDisclosesPerStatus | retry taxonomy + degraded disclosure per status |
| F7 | covered | T1-gate | RestartSurvivalTests.RestartAfterAcceptedEvents_NoLossNoDuplicate · LeaseRecoveryTests.ExpiredLease_ReclaimedAndDelivered · ReplicaIdentityTests.LeftoverLiveLease_RevokedAndKernelStarts | — |
| G1 | covered | T1-gate | TeamRosterTests.AWorkOfAForeignTeamIsInvisibleNotForbidden · ArtifactAuthorizationTests.ForeignTeamReadsAndWritesAnswerAbsent | — |
| G5 | covered | T1-gate | TeamRosterTests.OperatorSeesEveryTeamWithItsOwnStanding · TeamRosterTests.TeamOperatorRunsItsOwnTeamWithoutGlobalAuthority | — |
| G7 | partial | T2-adapter | TelegramIngressTests.LabeledMentionRoutesWorkToRuleTeamAndCarriesMediaIntoAssign | binding routes to one team; two-teams-one-channel is the pair's binding ledger |
| H1 | unexpressible | T4-manual | — | T4 rubric: docs/scenarios/manual-rubric.md |
| H2 | unexpressible | T4-manual | — | T4 rubric |
| H3 | covered | T1-gate | OpsSurfaceTests.AuditExportCoversTheWindowInBothFormats · OpsSurfaceTests.AuditRetentionPrunesOnlyAgedRows | — |
| H5 | unexpressible | T4-manual | — | T4 rubric; the pruned-vs-absent API leg is the export's window law |
| I1 | covered | T2-adapter | EnrollmentTests.EnrollReturnsTokenAndTrustModeAndSeedsDefaultTeam · TeamRosterTests.RosterSeedsFromConfigThenDbIsTruth | — |
| I2 | covered | T1-gate | TeamRosterTests.TeamSwitchOverridesTheSystemDefaultAndIsVersioned · AdminConfigStoreTests.ListReportsEveryRegisteredResourceWithExistence · AdminConfigStoreTests.NeverWrittenResourceGetsConfigNotSet404 | — |
| I3 | unexpressible | T4-manual | — | T4 rubric; the machine leg is the audit export |
| I4 | partial | T1-gate | AdminConfigStoreTests.MissingChangeReasonHeaderGets400 · AdminSurfaceTests.AuthzMatrixGatesTheSurfaceByRole · TeamRosterTests.TeamSwitchOverridesTheSystemDefaultAndIsVersioned | audited operator writes + role matrix codified; the per-team may-do matrix leg rides the rules engine |
| I5 | partial | T1-gate | OpsSurfaceTests.QueueReadAgesThePendingIntents · FlowControlTests.WindowFullPausesAndReceiptResumesAndDeliversNext | aging + window flow control codified; overload ordering is dispatcher FIFO by AvailableAt |
| I6 | covered | T1-gate | WorkRecordTests.Cost_RoundTripsVerbatim · AdminSurfaceTests.WorkDetailCarriesAssignmentsApprovalsManifests | — |
| I7 | partial | T2-adapter | ProviderIngressTests.ProviderEventRefusedWhenFeatureDisabled · AdminSurfaceTests.AdminTimelineMatchesWorkTimelineVerbatim | history lives in the kernel timeline, not the connector; per-provider render-as-text is the SPA's admin-e2e tier |
| K1 | covered | T2-adapter | ContributorTests.RegistrationSeatsTheContributorOnTheRoster · ContributorTests.HumanEvidenceAgainstContributorOnlyCapabilityIsRefusedNotRouted | — |
| K2 | covered | T1-gate | ConsentAuthorityTests.TrackerWithoutOwnerAccountRecordsTheGap · ContributorTests.HumanEvidenceAgainstContributorOnlyCapabilityIsRefusedNotRouted | — |
| K3 | partial | T2-adapter | ProviderIngressTests.LinkedMentionCreatesWorkOnceAndDuplicateReturnsJudgment | contributor-actor work codified for mentions; label-opened legs ride the same store |
| K4 | covered | T2-adapter | ProviderIngressTests.ReviewVerdictIsADecisionWithAccountability | — |
| K6 | covered | T2-adapter | ContributorTests.HandleCollisionsAcrossTeamsRefuseTheWrite · ProviderIngressTests.ReviewVerdictIsADecisionWithAccountability | — |
| K7 | covered | T1-gate | ContributorTests.RemitChangeIsAnAuditedOperatorAction · ContributorTests.MissingReasonRefusesEveryContributorWrite | — |
| K9 | partial | T2-adapter | ContributorTests.RegistrationSeatsTheContributorOnTheRoster | seat/unseat codified; no many-contributors scale leg |
| K10 | covered | T1-gate | ContributorTests.RetirementMovesOpenWorkToTheTeamAndKeepsTheName | — |
| L2 | covered | T1-gate | ConsentAuthorityTests.TrackerWithoutOwnerAccountRecordsTheGap · ConsentAuthorityTests.ChatDecisionWithoutTrackerRecordsIdentityAndNamesTheGap | accountability ends at a named human or a recorded gap |
| N1 | covered | T1-gate | ForgejoIngressTests.UnlabeledOpenedIssueMakesNoWork · js:src/adapters/communication/mattermost/test/normalize.test.ts | — |
| N2 | covered | T1-gate | ProviderIngressTests.BotEchoUpdatesBindingLedgerOnly · ProviderIngressTests.LoopBudgetStopsTheConversationAtTheCap · js:src/adapters/provider-core/test/http-loop-webhook.test.ts | shared loop budget stops the dialogue |
| N3 | covered | T1-gate | ConsentAuthorityTests.OutsiderDenyDoesNotConsumeTheApproval · ApprovalLifecycleTests.RequiredWithActionRecordsPendingRowWithKernelDigest | — |
| N4 | covered | T1-gate | NotificationLedgerTests.AmbiguousNameAsksInsteadOfPicking · NotificationLedgerTests.UniqueNameResolvesThroughTheLinkedIdentity | — |
| N5 | covered | T1-gate | ConsentAuthorityTests.HeldCommandStoredWithTheApprovalRow · ConsentAuthorityTests.MutateThenConsumeReturnsTheMutatedStoredCopy | the digest covers the kernel-stored envelope, not the requester's bytes |
| N6 | covered | T1-gate | ConsentAuthorityTests.ExpiryFlipsApprovalAndHeldActionAndParksTheWork | lazy expiry at consume: a slow consume on an expired row is denied |
| N7 | covered | T1-gate | IngressValidationTests.OversizedBody_ReturnsPayloadTooLarge · LinkIngressTests.UnknownCommandFailsClosedWithAuditRow · OpsSurfaceTests.ThirdFailureSaysSoExactlyOnce | typed problems, audit rows and escalation never wear each other's face |
| N8 | covered | T1-gate | NotificationLedgerTests.EmissionWritesLedgerRowAndOutboxRowAtomically · ProviderIngressTests.TerminalTransitionAppendsOneReplyToBinding | — |
| N9 | covered | T1-gate | ArtifactAuthorizationTests.ForeignTeamReadsAndWritesAnswerAbsent · TeamRosterTests.AWorkOfAForeignTeamIsInvisibleNotForbidden | — |
| N10 | partial | T1-gate | ArtifactUploadVerticalTests.LinkUploadHappyPathStoresVerifiesAndTimelines | artifact timeline-linked; the owner's-explanation leg is judgment |
| N11 | covered | T1-gate | DuplicateIngressTests.FixtureSteer_AcceptedOnce_DuplicateReplay_InvalidFixturesFailClosed · WebhookSinkTests.RawDeliveryQueuesWebhookCommandOnceAndReplayDedupes | — |
| N12 | partial | T1-gate | RestartSurvivalTests.RestartAfterAcceptedEvents_NoLossNoDuplicate · ConsentAuthorityTests.NudgeBeforeExpiryWritesTimelineAndStampsOnce | no double-ask for deliveries; settled questions stay settled via the approval ledger |
| N13 | covered | T1-gate | WorkloadIdentityTests.ModeBRoundTripRotatesAndOldTokenDies · ContributorTests.HumanEvidenceAgainstContributorOnlyCapabilityIsRefusedNotRouted | — |
| N14 | covered | T1-gate | NotificationLedgerTests.VanishedChannelMarksTheAddressObsoleteAndTheItemEndures | work addresses the binding; the binding survives the channel |
| N15 | covered | T1-gate | EnrollmentTokenTests.ListNeverLeaksTheRawLiteral | — |
| N16 | partial | T1-gate | OpsSurfaceTests.CancelDisclosesIrreversibleEffects · ConsentAuthorityTests.NudgeBeforeExpiryWritesTimelineAndStampsOnce | why-legs per surface; the completeness rubric rides I3 (T4) |
| N17 | partial | T1-gate | NudgeSweeperTests.BudgetSpentAtCap_ThenSilent_AndAdminSeesStalled · ProvisioningLawTests.NoPolicyRow_SweepIsExactlyNothing | per-assignment budget + no-policy silence; no global per-day budget beyond the memory plane |
| N18 | covered | T1-gate | TeamRosterTests.TeamOperatorRunsItsOwnTeamWithoutGlobalAuthority · TeamRosterTests.PlainMemberMayReadRosterButNotWriteIt | — |
| N19 | covered | T1-gate | ProviderIngressTests.ReviewVerdictIsADecisionWithAccountability · ContributorTests.HandleCollisionsAcrossTeamsRefuseTheWrite | — |
| N20 | partial | T1-gate | ProviderIngressTests.ReviewVerdictIsADecisionWithAccountability | automation review enters the process as a decision; stage-skipping is provider pipeline state |
| N22 | covered | T1-gate | ConsentAuthorityTests.TrackerWithoutOwnerAccountRecordsTheGap | the trail names a human or records the gap; a contributor is never the terminus |
| N23 | covered | T1-gate | ConsentAuthorityTests.ChatDecisionDispatchesTrackerRecordAndCarrierWork · NotificationLedgerTests.EmissionWritesLedgerRowAndOutboxRowAtomically | held questions ride the notification ladder atomically |
| N24 | partial | T1-gate | ApprovalLifecycleTests.RequiredWithActionRecordsPendingRowWithKernelDigest | stored action + digest carry content; a minimum-decidability gate is untested |
| N25 | absent | T1-gate | — | no throttle or grouping on consent request volume is codified |
| N26 | covered | T1-gate | js:src/adapters/provider-core/test/core.test.ts · WebhookSinkTests.RawDeliveryQueuesWebhookCommandOnceAndReplayDedupes | idempotency-keyed ops + settle ledger make retries idempotent |
| N27 | partial | T1-gate | ProviderIngressTests.ProviderEventRefusedWhenFeatureDisabled · NotificationLedgerTests.ProviderDegradedDisclosesPerStatus | per-status disclosure grouped by provider; cross-team fan-out grouping untested |
| N28 | covered | T1-gate | OpsSurfaceTests.QueueReadAgesThePendingIntents · NudgeSweeperTests.BudgetSpentAtCap_ThenSilent_AndAdminSeesStalled | — |
| N29 | covered | T1-gate | ProviderIngressTests.BotEchoUpdatesBindingLedgerOnly · ProviderIngressTests.LoopBudgetStopsTheConversationAtTheCap | self-write detection + shared exchange cap |
| N30 | covered | T1-gate | GenerationFenceTests.StaleGenerationCompletedEvent_Refused422_WithFailedInbox_AndUntouchedLedger · ConsentAuthorityTests.ExpiryFlipsApprovalAndHeldActionAndParksTheWork | generation fences + consume-time authorization refuse stale moments |
| N31 | absent | T1-gate | — | no collision-detection leg between concurrent contributors is codified |
| N32 | covered | T1-gate | ContributorTests.HandleCollisionsAcrossTeamsRefuseTheWrite · ContributorTests.RegistrationSeatsTheContributorOnTheRoster | one distinct contributor row per automation, handles unique per team |
| N33 | covered | T1-gate | EvidenceAdmissionTests.UnknownKernelEvidenceDeniesWithAuditRow | self-report without admitted evidence denies with an audit row |
| N34 | partial | T1-gate | TeamRosterTests.TeamSwitchOverridesTheSystemDefaultAndIsVersioned · ContributorTests.RemitChangeIsAnAuditedOperatorAction | versioned+reasoned writes; the old-rule-meaning diff rendering is untested |
| N37 | partial | T1-gate | AdminSurfaceTests.SummaryCountsEverySeededLedgerSection · WorkLifecycleHttpTests.FullLifecycle_WalksStates_TimelineAndOutboxComplete | read stores vs ledgers codified; a full account-trail sweep is one step |
| N38 | covered | T1-gate | ResultManifestVerticalTests.CompletedWithManifestRefRecordsChainHead · ArtifactRejectionTests.TamperedBytesRejectSlotAndAudit | — |
| N39 | covered | T1-gate | ProviderIngressTests.ProviderEventRefusedWhenFeatureDisabled · TeamRosterTests.TeamSwitchOverridesTheSystemDefaultAndIsVersioned | per-feature and per-team granularity proven |
| N40 | covered | T1-gate | TeamRosterTests.TeamOperatorRunsItsOwnTeamWithoutGlobalAuthority · ConsentAuthorityTests.ChatDecisionDispatchesTrackerRecordAndCarrierWork | — |
| N41 | partial | T1-gate | ArtifactRetentionGcTests.ExpiredDaysPolicyCollectsAndDeletesBlob · MemoryQuotaGcTests.DailyBudgetRefusesThirdRetainWithTypedProblem · OpsSurfaceTests.AuditRetentionPrunesOnlyAgedRows | each store honours its own rule; copies outside the stores are unproven |
| N42 | partial | T1-gate | ProviderIngressTests.LinkMintChallengeRedeemFlowWithFences · NotificationLedgerTests.AmbiguousNameAsksInsteadOfPicking | challenge-based linking merges identities; two-accounts detection is the collision leg |
| N43 | partial | T1-gate | DagGateTests.CompletedSiblingReleasesGatedAssignWithDeterministicIdentities | gated edges codified; cycle detection and depth alarms are absent |
| N44 | partial | T1-gate | ConsentAuthorityTests.OutsiderDenyDoesNotConsumeTheApproval · NotificationLedgerTests.UnlinkedDmTargetFallsBackToTheTeamChannelAnonymized | foreign reads answer absent, fallback anonymizes; no held-action preview surface exists |
