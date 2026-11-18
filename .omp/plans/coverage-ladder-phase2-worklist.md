# Phase 2 per-file worklists (from Phase 1 baseline artifacts)

uncovered = lines.total - lines.covered per shipped file. Areas target >= 90% lines.

## TypeScript areas

### src/otel — 55.7% lines, gap to 90%: 60 lines

-   77 uncovered @ 55.74%  `src/otel/src/index.ts`

### src/gateway — 79.7% lines, gap to 90%: 164 lines

-  133 uncovered @ 76.25%  `src/gateway/src/runtime.ts`
-   65 uncovered @ 0%  `src/gateway/src/bin/gateway.ts`
-   44 uncovered @ 78.43%  `src/gateway/src/listen.ts`
-   32 uncovered @ 81.71%  `src/gateway/src/child-link.ts`
-   11 uncovered @ 87.91%  `src/gateway/src/ledger.ts`
-   10 uncovered @ 84.37%  `src/gateway/src/connect.ts`
-    6 uncovered @ 72.72%  `src/gateway/src/claims.ts`
-    6 uncovered @ 88.23%  `src/gateway/src/child.ts`
-    5 uncovered @ 96.85%  `src/gateway/src/config.ts`
-    4 uncovered @ 81.81%  `src/gateway/src/child-keys.ts`
-    3 uncovered @ 93.02%  `src/gateway/src/telemetry.ts`
-    2 uncovered @ 95.74%  `src/gateway/src/policy.ts`
-    1 uncovered @ 95.45%  `src/gateway/src/diagnostics.ts`

### src/workspace-link — 55.2% lines, gap to 90%: 347 lines

-   70 uncovered @ 0%  `src/workspace-link/src/bin/linkd.ts`
-   65 uncovered @ 0%  `src/workspace-link/src/core/proof.ts`
-   50 uncovered @ 0%  `src/workspace-link/src/core/session.ts`
-   48 uncovered @ 0%  `src/workspace-link/src/node/signalr.ts`
-   39 uncovered @ 2.5%  `src/workspace-link/src/core/credential.ts`
-   36 uncovered @ 10%  `src/workspace-link/src/core/keystore.ts`
-   22 uncovered @ 75.28%  `src/workspace-link/src/node/http.ts`
-   18 uncovered @ 58.13%  `src/workspace-link/src/core/command-dispatcher.ts`
-   17 uncovered @ 87.02%  `src/workspace-link/src/core/client.ts`
-   11 uncovered @ 67.64%  `src/workspace-link/src/core/reconnect-supervisor.ts`
-   10 uncovered @ 74.35%  `src/workspace-link/src/core/memory-ledger.ts`
-   10 uncovered @ 9.09%  `src/workspace-link/src/core/handlers.ts`
-    9 uncovered @ 18.18%  `src/workspace-link/src/node/capabilities.ts`
-    5 uncovered @ 80.76%  `src/workspace-link/src/core/handshaker.ts`
-    4 uncovered @ 33.33%  `src/workspace-link/src/core/token-rotation.ts`
-    4 uncovered @ 75%  `src/workspace-link/src/core/logging.ts`
-    4 uncovered @ 75%  `src/workspace-link/src/core/errors.ts`
-    4 uncovered @ 90%  `src/workspace-link/src/core/delivery-pipeline.ts`
-    4 uncovered @ 80.95%  `src/workspace-link/src/core/config.ts`
-    3 uncovered @ 91.66%  `src/workspace-link/src/node/ledger.ts`
-    3 uncovered @ 72.72%  `src/workspace-link/src/core/outbox-drain.ts`
-    3 uncovered @ 85.71%  `src/workspace-link/src/core/enroll.ts`
-    3 uncovered @ 89.28%  `src/workspace-link/src/core/dispatch-queue.ts`
-    2 uncovered @ 93.1%  `src/workspace-link/src/core/fault-injector.ts`
-    1 uncovered @ 94.73%  `src/workspace-link/src/core/link-event-bus.ts`
-    1 uncovered @ 92.3%  `src/workspace-link/src/core/heartbeat-monitor.ts`

### src/adapters/provider-core — 64.6% lines, gap to 90%: 93 lines

-   33 uncovered @ 57.69%  `src/adapters/provider-core/src/config-core.ts`
-   24 uncovered @ 17.24%  `src/adapters/provider-core/src/normalize-shared.ts`
-   21 uncovered @ 4.54%  `src/adapters/provider-core/src/hooks-core.ts`
-   18 uncovered @ 0%  `src/adapters/provider-core/src/adapter-face.ts`
-   15 uncovered @ 53.12%  `src/adapters/provider-core/src/ops-core.ts`
-    8 uncovered @ 90.12%  `src/adapters/provider-core/src/webhook-receiver.ts`
-    8 uncovered @ 82.6%  `src/adapters/provider-core/src/http-core.ts`
-    2 uncovered @ 93.1%  `src/adapters/provider-core/src/ledger-core.ts`

### src/adapters/harnesses/adapter-sdk — 54.1% lines, gap to 90%: 280 lines

-  258 uncovered @ 25%  `src/adapters/harnesses/adapter-sdk/src/testing.ts`
-   47 uncovered @ 0%  `src/adapters/harnesses/adapter-sdk/src/media-law.ts`
-   21 uncovered @ 67.18%  `src/adapters/harnesses/adapter-sdk/src/artifacts.ts`
-   19 uncovered @ 91.4%  `src/adapters/harnesses/adapter-sdk/src/adapter.ts`
-    7 uncovered @ 0%  `src/adapters/harnesses/adapter-sdk/src/kernel-leg.ts`
-    6 uncovered @ 82.85%  `src/adapters/harnesses/adapter-sdk/src/memory.ts`

### src/adapters/provisioning-oci — 71.5% lines, gap to 90%: 48 lines

-   42 uncovered @ 0%  `src/adapters/provisioning-oci/src/bin/launcher.ts`
-   15 uncovered @ 0%  `src/adapters/provisioning-oci/src/index.ts`
-    6 uncovered @ 87.5%  `src/adapters/provisioning-oci/src/docker.ts`
-    5 uncovered @ 95.49%  `src/adapters/provisioning-oci/src/handler.ts`
-    5 uncovered @ 87.5%  `src/adapters/provisioning-oci/src/config.ts`

### src/protocol — 91.2% lines, gap to 90%: 0 lines

-    8 uncovered @ 88.23%  `src/protocol/src/validate.ts`

## .NET areas (unit tier = Facilitator.Tests run)

### src/facilitator — 87.9% lines (41268/46938), gap to 90%: 977 lines

-  502 uncovered @ 82%  `src/facilitator/Kernel/Workflows/WorkflowRunner.cs`
-  332 uncovered @ 60%  `src/facilitator/Kernel/Work/ContextBuilder.cs`
-  280 uncovered @ 60%  `src/facilitator/Kernel/Ai/CommandApplier.cs`
-  260 uncovered @ 86%  `src/facilitator/Kernel/Integrations/ProviderEventStore.cs`
-  236 uncovered @ 0%  `src/facilitator/Kernel/Workflows/WorkflowShadowReport.cs`
-  232 uncovered @ 82%  `src/facilitator/Kernel/Shared/Memory/MemoryBroker.cs`
-  186 uncovered @ 4%  `src/facilitator/Kernel/Ai/LiteLlmDecisionsGateway.cs`
-  182 uncovered @ 72%  `src/facilitator/Kernel/Ai/ChatProcessor.cs`
-  178 uncovered @ 73%  `src/facilitator/Kernel/Provisioning/ProvisioningProcessor.cs`
-  118 uncovered @ 6%  `src/facilitator/Kernel/Ai/OpenAiChatCompletionsGateway.cs`
-  104 uncovered @ 52%  `src/facilitator/Kernel/Work/ArtifactIngress.cs`
-   96 uncovered @ 83%  `src/facilitator/Kernel/Admin/AdminConfigStore.cs`
-   94 uncovered @ 55%  `src/facilitator/Kernel/Work/ContextFetchService.cs`
-   90 uncovered @ 76%  `src/facilitator/Kernel/Teams/ContributorStore.cs`
-   90 uncovered @ 82%  `src/facilitator/Kernel/Link/LinkIngress.cs`
-   82 uncovered @ 11%  `src/facilitator/Kernel/Work/ScheduleSpec.cs`
-   78 uncovered @ 83%  `src/facilitator/Kernel/Workflows/WorkflowDefinitionStore.cs`
-   78 uncovered @ 74%  `src/facilitator/Kernel/Work/CloudEventIngress.cs`
-   74 uncovered @ 70%  `src/facilitator/Kernel/Link/LinkProof.cs`
-   70 uncovered @ 90%  `src/facilitator/Kernel/Shared/Storage/ArtifactLedger.cs`
-   68 uncovered @ 85%  `src/facilitator/Kernel/Notifications/NotificationLedger.cs`
-   64 uncovered @ 81%  `src/facilitator/Kernel/Work/MediaReplyPolicy.cs`
-   54 uncovered @ 96%  `src/facilitator/Kernel/Work/EfCoordinationStore.cs`
-   54 uncovered @ 73%  `src/facilitator/Kernel/Teams/TeamSettingsStore.cs`
-   52 uncovered @ 84%  `src/facilitator/Kernel/Work/WorkFork.cs`
-   52 uncovered @ 85%  `src/facilitator/Kernel/Work/MemoryIngress.cs`
-   52 uncovered @ 62%  `src/facilitator/Kernel/Shared/Cli/CliRunner.cs`
-   50 uncovered @ 87%  `src/facilitator/Kernel/Work/ResultIngress.cs`
-   50 uncovered @ 88%  `src/facilitator/Kernel/Work/ConsentTicketService.cs`
-   50 uncovered @ 88%  `src/facilitator/Kernel/Ai/DecodeSink.cs`
-   44 uncovered @ 94%  `src/facilitator/Kernel/Shared/Persistence/LinkEnrollmentStore.cs`
-   44 uncovered @ 96%  `src/facilitator/Kernel/Admin/AdminReadStores.cs`
-   42 uncovered @ 92%  `src/facilitator/Kernel/Workflows/WorkflowTriggerEmitter.cs`
-   42 uncovered @ 85%  `src/facilitator/Kernel/Work/IngressProcessor.cs`
-   42 uncovered @ 56%  `src/facilitator/Kernel/Ai/CommunicationDecision.cs`
-   40 uncovered @ 80%  `src/facilitator/Kernel/Workflows/WorkflowSink.cs`
-   40 uncovered @ 95%  `src/facilitator/Kernel/Routing/RoutingSink.cs`
-   38 uncovered @ 84%  `src/facilitator/Kernel/Workflows/WorkflowRunner.Actions.cs`
-   38 uncovered @ 92%  `src/facilitator/Kernel/Shared/Storage/ResultManifestLedger.cs`
-   38 uncovered @ 82%  `src/facilitator/Kernel/Shared/Persistence/IngressStore.cs`
-   38 uncovered @ 95%  `src/facilitator/Kernel/Ai/ChatEndpoints.cs`
-   34 uncovered @ 95%  `src/facilitator/Kernel/Work/MemoryEndpoints.cs`
-   34 uncovered @ 86%  `src/facilitator/Kernel/Shared/Storage/S3ArtifactStore.cs`
-   34 uncovered @ 73%  `src/facilitator/Kernel/Ai/PromptRenderer.cs`
-   32 uncovered @ 97%  `src/facilitator/Kernel/Shared/Persistence/ApprovalLedger.cs`

### protocol/dotnet — 8.5% lines (186/2188), gap to 90%: 1784 lines

- 1978 uncovered @ 0%  `protocol/dotnet/Ofac.Protocol/Generated/WireTypes.cs`
-   22 uncovered @ 86%  `protocol/dotnet/Ofac.Protocol/Validation/EnvelopeValidator.cs`
-    2 uncovered @ 96%  `protocol/dotnet/Ofac.Protocol/Validation/ControlFrameValidator.cs`

