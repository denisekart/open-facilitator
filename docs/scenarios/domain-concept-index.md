# Domain-concept index

One term per concept the scenario gate's vocabulary depends on, each linked
to the code that owns it and the spec section that governs it. The spec
(`open_facilitator_framework_architecture.md`) is the target design and is
not rewritten here; `scenario-catalog.md` stays the frozen idea-space. This
index is the bridge: scenario wording → as-built term → governing text.

| Term | As-built meaning | Owner (code) | Governing spec |
|---|---|---|---|
| Work item | the durable unit of asks; canonical states, never a channel message | `Kernel/Work/WorkState.cs` (the one state authority) | §4 Domain model, §4.1 Canonical states |
| Held action | a command parked inside an approval row, executed only on a valid consume | `Kernel/Work/ApprovalIngress.cs`, `Shared/Persistence/ApprovalLedger.cs` | §12 Team authorization policy, §12.1 Approval lifecycle |
| Consent ticket | the human-facing half of a held action: one question, its digest, its deadline | `Kernel/Work/ConsentTicketService.cs` | §12, §12.2 Audit record |
| Notification | a ledger row + outbox command pair, emitted atomically, resolved over the egress ladder | `Kernel/Notifications/NotificationLedger.cs`, `EgressResolver.cs` | §5.2 Message types, §13 threaded communication |
| Egress ladder | bound conversation → team channel → anonymized public post → undeliverable-with-reason | `Kernel/Notifications/EgressResolver.cs` | §13.1 Canonical conversation reference |
| Contributor | an automation seated as a process member: unique handle, human owner, audited remit | `Kernel/Teams/ContributorStore.cs`, `ContributorEndpoints.cs` | §11.1 Three distinct identities, §12 |
| Team scope | the partition every read/authorisation resolves against; foreign team = absent, not forbidden | `Kernel/Teams/TeamScope.cs`, `TeamRosterStore.cs` | §3.4 Ownership and exposure, §8.4 Team configuration layout |
| Team switch | a versioned, attributed, reasoned config row at team or channel scope | `Kernel/Teams/TeamSettingsStore.cs`, `EffectiveConfigResolver` | §8.4, §12 |
| Merge | a work's terminal dedup: loser keeps history, reads 303 to the survivor, never unmerged | `Kernel/Work/WorkEndpoints.cs` (merge arm) | §4.2 Cross-workspace dependencies |
| Review verdict | a review comment normalised to a decision with accountability (fork or note, never silence) | `Kernel/Integrations/ProviderEventStore.cs` (review arm), `provider-core/normalize-shared.ts` | §15.5 Completion and review semantics |
| Loop budget | the per-conversation exchange cap that stops two automations talking to each other | `provider-core/loop-budget.ts`, telegram `loop-guard.ts` | §13.4 Prevent bot echo loops |
| Provider link | the bound conversation record between a kernel work and a provider thread | `Kernel/Integrations/` (binding ledger) | §13.1 Canonical conversation reference |
| Stall episode | one window of harness silence on one assignment, nudged within a cap | `Kernel/Work/NudgeSweeper.cs`, `NudgeLedger` | §6.4 Nudge and stalled-work semantics |
| Disclosure | the law that failures, queue age and executor liveness are named on reads, never silence | `Kernel/Work/OpsSurface` reads, `ExecutionReads.cs` | §5.2, §9 Gateway responsibilities |
| Schedule | a kernel-side cadence that creates work and announces it to the team | `Kernel/Teams/ScheduleEndpoints.cs` | §4 Domain model (work entry), §12 |
| Capability manifest | a harness's advertised capabilities (see each harness's functionality-list export) | `protocol/json-schema/capability-manifest.v1.json`, `adapter-sdk/src/capabilities.ts` | §10 Capability manifests, §5.3 Link handshake |
| Functional-area switch | the per-provider feature gate (disabled feature = `unsupported`, honest refusal) | `Kernel/Integrations/ProviderOptions.cs` | §13.5 Adapter contracts and scopes |
