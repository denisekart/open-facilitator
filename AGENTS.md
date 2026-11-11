# Project Context (Open Facilitator Framework)

**Open Facilitator Framework** — portable, pluggable, multi-workspace
orchestration of autonomous, assisted, and deterministic workloads across
independently operated execution environments. One persistent **Facilitator**
(.NET 10 kernel: ingress, work lifecycle, Link hub, dispatch) + team-owned
**Gateways** and **Workspaces** (TypeScript) that connect *outbound* and keep
execution authority team-side; small adapters wrap the harnesses.

## Truth pointers
- `docs/specs/` — normative for the *target* design; the code is authoritative
  for what *exists*. Check the tree before assuming a module/table exists.
- Implementation status: `.omp/plans/IMPLEMENTATION_ROADMAP_PLAN.md` (link only — never mirror).
- Wire contract: `protocol/` (schemas, fixtures, OpenAPI) — `src/protocol/AGENTS.md`.

## Commands
- `make build` — restore + Release build (both stacks). **Zero warnings is a
  build requirement on both stacks** (`TreatWarningsAsErrors`;
  `eslint --max-warnings 0`; strict `tsconfig.base.json`).
- `make test` — xUnit + vitest + conformance fixtures; the .NET suite has a < 10 min wall budget (testcontainers Postgres).
- `make lint`, `make docs-check`, `pnpm -r --if-present typecheck`.
- **Acceptance law:** the slim reference env is acceptance-required for every task and locally IS the gate (hosted ref-env CI jobs commented out, free-tier): `up slim --no-seed && referencectl verify` exits 0, from clean.
- e2e tier: `make link-e2e` (needs Postgres at `FACILITATOR_PG`; own server per file).
- Single slice: `dotnet test --filter "FullyQualifiedName~<Folder>"`,
  `pnpm -C <pkg> test`, `vitest run <file>`.

## Area signposts
When a tool loads only this file, open the nested AGENTS.md of every directory
you touch — the nearest file wins.

|Area|Signpost|
|---|---|
|Facilitator kernel|`src/facilitator/AGENTS.md`|
|Workspace Link SDK/runtime|`src/workspace-link/AGENTS.md`|
|Protocol package + contract tree|`src/protocol/AGENTS.md`|
|.NET test suite|`tests/Facilitator.Tests/AGENTS.md`|
|Scenario gate (plane + runner + manifest generator + walk assembly)|`tools/scenario-plane/README.md`|
|Reference environment inputs|`reference/AGENTS.md`|
|Dev Container Features|`features/AGENTS.md`|
|Communication/harness adapters (forgejo/github/gitlab pairs on provider-core)|`src/adapters/AGENTS.md`|
|AWS reference deployment (CDK, non-prod launcher)|`examples/aws/AGENTS.md`|

## Follow the truth
- Work request path: `src/facilitator/Kernel/Work/WorkEndpoints.cs` route table (`/works`, `/works/{id}/steer|transitions`, `/works/{id}/timeline`) →
  `src/facilitator/Kernel/Work/IngressProcessor.cs` → `src/facilitator/Kernel/Work/EfCoordinationStore.cs` → `facilitator.outbox` →
  `src/facilitator/Kernel/Shared/Dispatch/DbEventDispatcher.cs` + sinks.
- Message path (workspace↔kernel): `src/workspace-link/src/core/client.ts` →
  `src/facilitator/Kernel/Link/LinkHub.cs` (`/hubs/link`) or `/events` +
  `/deliveries` HTTP fallback → `src/facilitator/Kernel/Link/AdmissionGate.cs`
  → `src/facilitator/Kernel/Shared/Persistence/LinkLedger.cs` →
  `src/facilitator/Kernel/Link/LinkDeliveryPump.cs`; receipts from both
  transports converge on `src/facilitator/Kernel/Link/LinkReceipts.cs`.
- Provider webhook path: kernel sink `POST /api/ingress/webhooks/{provider}` (`src/facilitator/Kernel/Integrations/WebhookIngressEndpoints.cs`) → outbox `command.provider.webhook.v1` → the adapter's `@ofac/provider-core` webhook-receiver seam.
- Scenario law: changing behavior a manifest row covers (`docs/scenarios/scenario-bindings.json`) obliges updating its binding + rerunning `make scenario-gate`; walks live in `tests/Facilitator.ScenarioWalks/`.
- New endpoint: the feature's `*Endpoints.cs`. New table: entity in
  `src/facilitator/Kernel/Shared/Persistence/Entities.cs` + configuration
  under `Configurations/` + migration. New wire message type: schema in
  `protocol/json-schema/` → `pnpm protocol:codegen` → fixtures.
- Enrollment/auth: `src/facilitator/Kernel/Link/LinkEndpoints.Enroll.cs` +
  the `src/facilitator/Kernel/Shared/Auth` folder. Metrics/tracing: `KernelMetrics.cs` + `KernelActivity.cs` under Shared/Dispatch. CLI: `src/facilitator/Kernel/Shared/Cli`.

## omp workspace layer
- Profiles: dotnet-aspire, svelte. Stack is .NET + TypeScript — ignore
  zephyr/zmk skills unless on an embedded harness.
- Project skills `.omp/skills/`: `facilitator-architecture`,
  `facilitator-protocol`, `facilitator-workspace-packaging`,
  `facilitator-reference-env`. **Maintenance rule:** skills describe
  implemented reality — update the matching skill in the same change that
  lands a schema/endpoint/table/CLI.
- Plans → `.omp/plans/` (reviewable in-repo); `/continue` for handoff;
  `--plan` mode + `xd://propose` for approvals.
- Knowledge base: KB name = repo directory name, store `~/.omp/knowledge`;
  search it before re-reading files you don't have open; `knowledge_update`
  after meaningful edits to indexed content.
- **Doc-sync rule:** an AGENTS.md change rides in the same commit as the
  change it describes; `make docs-check` fails on dangling references,
  oversized signposts, plan-status prose, or an area that grew without a
  signpost. Area docs are ≤ 40-line indexes: purpose, invariants invisible in
  code, anchors — never restated behavior.
