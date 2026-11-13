OpenTelemetry integration: every service emits its own three signals
**Status:** idea → needs a plan.
**Grows from:** the durable-queue trace law (`.omp/skills/facilitator-reference-env/SKILL.md`
"Read the stamp out of the outbox row — never a timing join"), the
correlation-ids-go-nowhere gap (`.omp/ideas/operator-console-next.md:41`), and the
observability plane the AWS example already ships (`examples/aws/lib/adot.ts`).

**Target:** each service — the .NET kernel and every team-owned Node process —
owns its instrumentation and emits traces, logs and metrics over OTLP,
configured ONLY by standard `OTEL_*` environment variables, with no assumed
telemetry proxy: no service depends on a collector existing to be correct, and
each one stays correct (and silent) when the env names no endpoint.

## The law (settled posture — challenge, don't re-litigate blind)

1. **Self-instrumenting services; a collector is optional infrastructure.**
   Every process ships its own SDK + OTLP exporter. A deployment may front them
   with a collector (the AWS example already does, `examples/aws/lib/adot.ts:20`)
   or point straight at a viewer (`reference/compose.slim.yaml:80-89`). Nothing
   in the system may REQUIRE the collector to be up.
2. **Environment-driven, spec-standard variables.** `OTEL_EXPORTER_OTLP_*`,
   `OTEL_SERVICE_NAME`, `OTEL_RESOURCE_ATTRIBUTES`, `OTEL_TRACES_SAMPLER`,
   `OTEL_PROPAGATORS`, `OTEL_SDK_DISABLED` — not per-service config files. The
   kernel keeps exactly one bespoke key, `Otel:Endpoint`, purely as an
   on/off switch (documented at `reference/config/facilitator.env:6-9`); the
   transport always comes from `OTEL_EXPORTER_OTLP_*`.
3. **Silent when unconfigured.** No endpoint in the env ⇒ no exporter, no
   background connection, no log noise, no startup failure. Today the kernel
   already behaves this way (`src/facilitator/Kernel/KernelServiceCollection.cs:370`
   gates both exporters on `Otel:Endpoint`); the same law must hold on the
   TypeScript side.
4. **One propagation law, three carriers.** W3C `traceparent` on HTTP headers,
   inside the link-plane envelope, and inside the child-wire frame — the same
   55-character string, extracted-or-fresh at every hop, never implicit across
   a durable boundary (the existing dispatcher rule,
   `src/facilitator/Kernel/Shared/Dispatch/DbEventDispatcher.cs:92-123`).
5. **Telemetry is an observer.** No telemetry failure may fail a business
   operation, and no telemetry may change one: the OTLP exporter sits after
   every law (audit-before-respond, outbox-first, fail-closed startup). This
   is the same posture the AI plane already uses ("degrade, never a business
   error", `.omp/skills/facilitator-architecture/SKILL.md` "AI plane").
6. **Signals are peers.** Today traces + metrics exist and logs do not
   (`src/facilitator/Kernel/KernelServiceCollection.cs:360-374` has `WithTracing`
   + `WithMetrics`, no `WithLogging`). OTLP logs are the actual gap, not a
   nicety: they are what makes a trace readable.

## As-built today (evidence, verified 2026-11-12)

### Kernel (.NET 10, ASP.NET Core)
- Packages, centrally pinned: `OpenTelemetry`, `…Exporter.OpenTelemetryProtocol`,
  `…Extensions.Hosting`, `…Instrumentation.AspNetCore` at 1.20.1
  (`Directory.Packages.props:17-20`, referenced `src/facilitator/Facilitator.csproj:24-27`).
  The `OpenTelemetry.AspNetCore` meta-package is deliberately not used
  (`src/facilitator/Kernel/KernelServiceCollection.cs:33-35`).
- Wiring: `src/facilitator/Kernel/KernelServiceCollection.cs:104` calls
  `builder.AddOpenTelemetry()`; the block at `:360-374` listens to
  `KernelActivity.SourceName` (`:365`), adds ASP.NET Core instrumentation
  (`:366`), adds the `facilitator` meter (`:368`), and adds OTLP exporters for
  metrics + traces only when `Otel:Endpoint` is non-empty (`:370-373`). **No
  logging pipeline is registered at all.**
- Spans (`KernelActivity`, source `Facilitator.Kernel`,
  `src/facilitator/Kernel/Shared/Dispatch/KernelActivity.cs:9-11`):
  `outbox.dispatch` (`Kernel/Shared/Dispatch/DbEventDispatcher.cs:107`, parent
  extracted from the stored envelope at `:104`, fresh carrier restamped and
  persisted at `:117-124`), `ingress.process`
  (`Kernel/Work/IngressPipeline.cs:23`, callers `Kernel/Work/IngressProcessor.cs:98,157,183,211,244`,
  `Kernel/Work/CloudEventIngress.cs:174`, plus a raw one at `Kernel/Link/LinkIngress.cs:377`),
  `link.handshake` (`Kernel/Link/LinkHub.cs:87`), `ai.decode`
  (`Kernel/Ai/DecodeSink.cs:130`), `ai.chat.turn` (`Kernel/Ai/ChatProcessor.cs:342`),
  `ai.action.execute` (`Kernel/Ai/ChatActionExecutor.cs:92`) — the last three
  take an explicit parent `ActivityContext` for the durable hop
  (`KernelActivity.cs:23,38,52`) and carry token usage (`:63-66`).
- Metrics: one meter `facilitator` (`Kernel/Shared/Dispatch/KernelMetrics.cs:13`),
  35+ instruments (`:77-116`) — counters per subsystem, latency histograms,
  and queue gauges served from a 5-second cache so observable callbacks never
  hit Postgres synchronously (`:81-85,97,116`). An in-process `MeterListener`
  (`:54,63-76`) accumulates every counter so `CounterTotals()` (`:265`) can
  answer `GET /api/admin/metrics` without a time-series backend
  (`Kernel/Admin/AdminEndpoints.cs:86`, `Kernel/Admin/AdminReadStores.cs:624,647`).
- Env-var surface actually supported by the installed 1.20.1 packages (read out
  of the package assemblies): the SDK reads `OTEL_TRACES_SAMPLER`/`_ARG`,
  `OTEL_PROPAGATORS`, `OTEL_TRACES_EXPORTER`, `OTEL_METRICS_EXPORTER`,
  `OTEL_LOGS_EXPORTER`, `OTEL_CONFIG_FILE`, `OTEL_LOG_LEVEL`, the
  `OTEL_BSP_*`/`OTEL_BLRP_*`/`OTEL_*_LIMIT` batch-and-limit family, and
  `ResourceBuilder.CreateDefault` parses `OTEL_RESOURCE_ATTRIBUTES` +
  `OTEL_SERVICE_NAME` (the ignore-these-when-host-config-is-present bug was
  fixed in 1.20.0). The OTLP package reads the full
  `OTEL_EXPORTER_OTLP_*` family including per-signal overrides
  (`…_TRACES_ENDPOINT`, `…_METRICS_HEADERS`, `…_LOGS_PROTOCOL`…), compression,
  TLS client certs and the experimental disk-retry directory. **`OTEL_SDK_DISABLED`
  is not honoured by .NET** (no such string in any of the four installed
  assemblies) — the kill switch on the kernel is `Otel:Endpoint` unset.
- Outbound HTTP today: two named clients, `"ai"` and `"chat-loopback"`
  (`KernelServiceCollection.cs:325-326`, used at `Kernel/Ai/OpenAiChatCompletionsGateway.cs:124`,
  `Kernel/Ai/LiteLlmDecisionsGateway.cs:169`, `Kernel/Ai/ChatActionExecutor.cs:166`).
  No HttpClient instrumentation is registered, so the model hop and the
  AiDeck loopback are trace holes with no timing signal either.

### Wire contract
- `traceparent` is a first-class envelope extension with the W3C pattern
  locked: `protocol/cloudevents-schemas/envelope.json:51-53`, documented
  lowercase-per-CE-JSON-binding in `docs/protocol.md:16`.
- A delivery is `{deliveryId, envelope, deliveredAt}` with
  `additionalProperties: false` (`protocol/cloudevents-schemas/link-delivery.v1.json:7`)
  — the carrier travels *inside* the envelope, which is why it survives both
  transports.
- Control frames are closed too (`protocol/json-schema/control.v1.json:9,60,98,120,142`):
  hello/welcome/heartbeat/flow-control/goodbye carry no trace context, so a
  session-scoped link leg is currently un-attributed.
- Link-plane HTTP surface is specified in `protocol/openapi/link-v1.yaml:68-126`
  (`postEvent`, `getDeliveries`, `postReceipt`).

### Team-owned services (TypeScript)
- **No OTel anywhere**: no `@opentelemetry/*` dependency in any workspace
  package (checked across every `package.json`; the only lockfile mention is
  vitest's optional peer, `pnpm-lock.yaml:3566`).
- The kernel's stamp already rides to the team side and back: the SDK reads
  `command.traceparent` and copies it onto progress/failed/completed envelopes
  (`src/adapters/harnesses/adapter-sdk/src/adapter.ts:321,359,402,498`,
  `src/adapters/harnesses/adapter-sdk/src/artifacts.ts:307`), provider-core
  does the same for its completion (`src/adapters/provider-core/src/hooks-core.ts:63`),
  and the Gateway does it for approval publish/consume
  (`src/gateway/src/runtime.ts:1096,1212`). The Gateway→child wire embeds the
  whole command envelope, carrier included, in the `command` frame
  (`src/gateway/src/childwire.ts:74-79`). The regression test pins it:
  `src/adapters/harnesses/adapter-sdk/test/adapter.test.ts:242-269`.
- **Nothing is created** on the TS side: no span is ever started from an
  inbound `traceparent`, no metric instrument exists (the only counters are an
  in-memory `LinkStats` object, `src/workspace-link/src/core/types.ts:154-161`,
  surfaced raw through the one-route diagnostics endpoint
  `src/gateway/src/diagnostics.ts:12-20`, port 8099 per
  `reference/config/workspace/gateway.yaml:28-29`).
- Logs are already structured JSON lines with correlation fields
  (`src/workspace-link/src/core/logging.ts:13-26`) — the cheapest possible
  bridge to OTLP log records.
- The Gateway's audit line already reserves a `traceId` field
  (`src/gateway/src/audit.ts:25`) but **neither call site fills it**
  (`src/gateway/src/runtime.ts:999-1013,1544-1556`).
- Propagation seams exist and are one line each: the SignalR transport builds
  its connection at `src/workspace-link/src/node/signalr.ts:59` (token via
  `accessTokenFactory`, never a query string, per the secret law at
  `docs/specs/open_facilitator_framework_architecture.md:698`) and the HTTP
  fallback sets its headers at `src/workspace-link/src/node/http.ts:222-225`.
  `@microsoft/signalr` 10.0.11 (pinned `pnpm-workspace.yaml:47`) merges
  `options.headers` into both the negotiation request and the WebSocket
  upgrade headers (`node_modules/@microsoft/signalr/dist/esm/HttpConnection.js:222-230,323`),
  so a per-frame/per-request carrier needs no protocol change.
- Inbound side of the kernel today: `LinkIngress.AdmitAsync`
  (`src/facilitator/Kernel/Link/LinkIngress.cs:63`) never reads the envelope's
  `traceparent`, so a worker→kernel event starts a *fresh* kernel trace instead
  of joining the one the kernel itself minted and stamped. This is the
  single highest-value gap.

### Deployments
- Reference env: `reference/config/facilitator.env:30-33` sets
  `Otel__Endpoint` + `OTEL_EXPORTER_OTLP_{ENDPOINT,PROTOCOL}` +
  `OTEL_SERVICE_NAME` (Aspire OTLP/HTTP :18890, dashboard/UI :18888 —
  `reference/compose.slim.yaml:80-89`, `reference/README.md:35-36`, pin
  `reference/versions.env:8` = 13.4.2). **No team-side container sets a single
  OTEL var** (`reference/compose.slim.yaml:139`,
  `reference/compose.full.yaml:84,118,205,241,268`,
  `reference/compose.provisioning.yaml:23`), so gateways, workers, the launcher
  and every adapter are telemetry-dark.
- Acceptance run: `step_trace_correlation` (`reference/checks/verify.sh:549-578`)
  already proves stamp → echo → dashboard spans via
  `GET :18888/api/telemetry/spans?traceId=` (`:582`). Nothing asserts a metric
  or a log record ever arrived.
- AWS example: the kernel task env is 1:1 with the reference env
  (`examples/aws/lib/kernel-service.ts:113-116`), an ADOT `otelcol-contrib`
  0.144.0 sidecar is shared verbatim by kernel and team-plane tasks
  (`examples/aws/lib/adot.ts:20,33-56`, `examples/aws/lib/kernel-service.ts:143-150`,
  `examples/aws/lib/team-plane.ts:184`) with OTLP in on 4317/4318 →
  `awsemf` metrics + `awsxray` traces + `debug` logs. **Dead alarm**: it reads
  `facilitator.outbox.dead_letters` (`examples/aws/lib/observability.ts:71`)
  while the instrument is `facilitator.delivery.dead_letter_count`
  (`src/facilitator/Kernel/Shared/Dispatch/KernelMetrics.cs:83`) — with
  `TreatMissingData.NOT_BREACHING` that alarm can never fire.
- Local home example: renders the same four keys for the kernel
  (`examples/local/tools/localctl/src/render.ts:221-224`), and the node bundle
  *already forwards* `OTEL_EXPORTER_OTLP_ENDPOINT`/`_PROTOCOL` to both the
  workspace and the launcher containers
  (`examples/local/templates/node/compose.yaml.tmpl:32-33,72-73`) — env plumbed
  into processes that ignore it.
- Target design says OTLP is expected, all three signals, dashboard is a
  viewer and not a store: `docs/specs/open_facilitator_framework_architecture.md:820,1368,1504,2161`.

## Per-service instrumentation matrix

| Service | Stack | Traces | Logs | Metrics | Today | Delta worth doing |
|---|---|---|---|---|---|---|
| `facilitator` kernel | .NET 10 | ✅ ASP.NET Core + 6 own span names | ❌ none | ✅ 35+ instruments, OTLP-gated | `KernelServiceCollection.cs:360-374` | add `WithLogging(...AddOtlpExporter())`; add `OpenTelemetry.Instrumentation.Http` 1.21.0 for the `ai`/`chat-loopback` clients; join inbound link `traceparent`; resource attrs |
| `gateway` (`@ofac/gateway`) | Node 22 | ❌ | JSON lines, no trace ids | ❌ (`LinkStats` only) | `runtime.ts:999`, `audit.ts:25` | bootstrap-on-env SDK, span per delivery/handle/publish, `traceId` in every audit line, instruments for link + child fleet |
| `workspace-link` SDK (`@ofac/workspace-link`, `linkd`) | Node 22 | ❌ | ✅ JSON (`logging.ts:19`) | ❌ | `core/client.ts`, `node/signalr.ts:59` | own the propagation + logger bridge so consumers get it free |
| harness adapters (`@ofac/harness-{deterministic,omp,codex}`, `@ofac/example-coding-worker`) | Node 22 | copies carrier | JSON | ❌ | `adapter-sdk/src/adapter.ts:321` | span the execution (queue→harness→result), token/cost counters where the harness reports them |
| provider adapters (`mattermost`, `telegram`, `forgejo`/`github`/`gitlab` + connector pairs) | Node 22 | copies carrier (`hooks-core.ts:63`) | JSON | ❌ | `forgejo/src/index.ts:116` `/healthz` | span webhook→envelope→publish; counters for normalised events, provider 429/403 (the `http-core.ts` retry law already classifies them) |
| `oci-launcher` (`@ofac/provisioning-oci`) | Node 22 | ❌ | JSON | ❌ | `handler.ts:96,128` | span `provision.start/stop`, counters by reason — the kernel's `facilitator.provisioning.ops` has no counterpart on the executor side |
| `facilitator-b` replica, `*-lb` nginx | — | inherits | inherits | inherits | `reference/compose.replicas.yaml:13,23` | `service.instance.id` = `Kernel:ReplicaId` so a fleet is distinguishable |
| Postgres/Keycloak/SeaweedFS/Aspire | vendored images | ❌ (none emit OTLP) | container logs | ❌ | `reference/versions.env:2-10` | out of scope: no sidecars for third-party images |

Node SDK shape (package names + versions verified against the npm registry
today): catalog entries `@opentelemetry/api@^1.10.0`,
`@opentelemetry/sdk-node@^0.301.2`, `@opentelemetry/sdk-trace-node@^2.12.0`,
`@opentelemetry/sdk-metrics@^3.1.1`, `@opentelemetry/sdk-logs@^3.1.1`,
`@opentelemetry/exporter-{trace,metrics,logs}-otlp-http@^0.301.2`,
`@opentelemetry/resources@^3.1.1`, `@opentelemetry/semantic-conventions@^1.45.0`,
`@opentelemetry/instrumentation-http@^0.301.2`. Verified env support in those
tarballs: `sdk-node`'s `create-from-env` reads `OTEL_TRACES_EXPORTER`,
`OTEL_LOGS_EXPORTER`, `OTEL_TRACES_SAMPLER`/`_ARG`, `OTEL_PROPAGATORS`,
`OTEL_SDK_DISABLED`, the BSP/BLRP + attribute-limit family and `OTEL_CONFIG_FILE`;
`resources` reads `OTEL_RESOURCE_ATTRIBUTES` + `OTEL_SERVICE_NAME`;
`otlp-exporter-base` reads `OTEL_EXPORTER_OTLP_*`. So `OTEL_SDK_DISABLED=true`
works on the TS side only — say so in the env contract rather than promise
parity. Prefer the signal-specific exporters over `sdk-node` in library code
(`sdk-node` pulls gRPC/ZIPKIN/Prometheus exporters as dependencies, which bloats
every Feature tarball built by `features/build.mjs:19-26` and every
`pnpm install --frozen-lockfile` layer of `Dockerfile.node:29`).
**New dependency versions belong in the `pnpm-workspace.yaml` catalog**
(`pnpm-workspace.yaml:35-75`, the pattern every `@ofac/*` package follows —
`src/gateway/package.json:33-39` puts all three of its third-party deps on
`catalog:`; `ws` in the Mattermost adapter is the existing exception), and the
lockfile changes with it.

## Propagation law (the whole thing, three sentences plus a table)

W3C `traceparent` is the only carrier; `tracestate`/`baggage` may ride but
nothing may depend on them. A process **extracts** when an inbound carrier
exists (HTTP header, envelope field, or the `traceparent` inside a child-wire
command frame) and **starts fresh** when it does not; a fresh trace that
crosses a durable boundary is stamped into the persisted record before delivery
— that is already the kernel's law and it is the right one
(`DbEventDispatcher.cs:92-123`, proven by
`tests/Facilitator.Tests/Dispatch/TracePropagationTests.cs:20,59-90` and
`reference/checks/verify.sh:549-578`).

| Hop | Carrier today | Carrier after |
|---|---|---|
| CLI/UI → kernel HTTP | response header only (`Kernel/Work/WorkEndpoints.cs:28`) | ASP.NET Core instrumentation (already on) extracts/injects; keep the explicit response header as a contract |
| kernel → outbox → link sink | `envelope.traceparent`, stamped/restamped (`DbEventDispatcher.cs:118-124`, routing sink `Kernel/Routing/RoutingSink.cs:384-386`) | unchanged (law 4) |
| kernel → Gateway (SignalR `Deliver` / `GET /deliveries`) | envelope inside `LinkDeliveryV1` | Gateway extracts from the envelope, not the socket |
| Gateway → kernel (`Envelope` hub method / `POST /api/link/v1/events`) | envelope, ignored by `LinkIngress` (`LinkIngress.cs:63`) | **kernel extracts** (`isRemote: true`) before opening `ingress.process` — one line, and the cross-plane trace closes |
| kernel ↔ Gateway control frames, heartbeats, receipts | none (closed schemas) | none by choice: a session leg is attributed to its endpoint resource + `facilitator.session_epoch` tag instead |
| Gateway → child (`command` frame) | full envelope inside the frame (`childwire.ts:74-79`) | child extracts; child spans parent to it |
| adapter → provider API | none | `@opentelemetry/instrumentation-http` injects on the client side (client spans only: provider-side traces are not ours) |
| kernel → LiteLLM/OpenAI | none | HttpClient instrumentation injects `traceparent`; whether the model proxy joins it is their business |
| AiDeck chat loopback | none (self-HTTP) | same instrumentation; the replayed request then continues the trace instead of branching |

SignalR note: `HttpConnectionOptions.headers` is applied to the WebSocket
upgrade as well as HTTP transport (`signalr` 10.0.11 `HttpConnection.js:323`),
so a *session-scoped* carrier can be attached at `signalr.ts:59` if a later
plan wants hub-method spans correlated at the transport level. Do not put
secrets or PII in it (secret law, spec `:698`).

## Correlation law (durable rows stay the source of truth)

OTLP is a mirror, never the ledger: the kernel's own persisted facts
(`facilitator.audit`, `facilitator.outbox`, `facilitator.work_events`) remain
the answer to "what happened", and the dashboard answers "why was it slow".
Three concrete joins, all of them already writable:

1. **`facilitator.audit.trace_id`** is written from `Activity.Current`
   (`Kernel/Shared/Identity/AuditWriter.cs:66`, column mapping
   `Configurations/AuditEventEntityConfiguration.cs:26-27`, table
   `Migrations/20261025235208_AddIdentityFoundation.cs:30`) and is returned by
   `GET /api/admin/audit` (`AdminReadStores.cs:588`) but **never accepted as a
   filter** (`AdminEndpoints.cs:94` takes `action/actor/since/decision/correlation`
   only). Adding `?traceId=` is the cheapest thing in this whole idea that an
   operator feels: a span → the audit rows of that decision, in one query.
   (`/audit/export` deliberately drops trace ids so a paste leaks nothing —
   `AdminReadStores.cs:651-653` — leave that alone.)
2. **`correlationid` is the business key, `traceparent` the technical one.**
   `work_events.correlation_id` (`Entities.cs:308`,
   `Migrations/20261023152937_InitialCreate.cs:270`) is what the SPA's trace
   chip copies (`src/facilitator/ui/src/components/WorkInspector.svelte:274-284`)
   and what `SearchAsync` should equality-match on
   (`AdminReadStores.cs:253,286` — the guid arm matches message ids only; this
   is `.omp/ideas/operator-console-next.md:41`, and OTLP does not fix it: the
   chip must show both ids, correlation for the ledger and trace for the
   dashboard).
3. **`KernelMetrics` and the admin roll-up stay the operator's surface.**
   `GET /api/admin/metrics` reads SQL gauges + the in-process counter snapshot
   (`AdminEndpoints.cs:86`, `AdminReadStores.cs:624-647`), which is why no
   telemetry backend is required for the SPA. OTLP metrics are a *copy* of that
   meter for whoever collects; `facilitator.audit.denies` /
   `facilitator.artifacts.stored` sparklines keep their own source
   (`AdminReadStores.cs:643-646`). The SPA keeps its own gauges — the dashboard
   is never the store (spec `:1368`).

Naming law for the two id spaces: `facilitator.*` tags on spans and instruments,
`ofac.*` for resource attributes we own. Never reuse an existing audit column
name as a tag name (`facilitator.endpoint_id` is a span tag today,
`LinkHub.cs:88`; keep it).

## Sampling, PII, resource attributes

- **Sampling.** Default `ParentBased(AlwaysOn)` on both stacks — a small system
  where "no spans for the leg I'm debugging" is the failure mode. One dial,
  env-only: `OTEL_TRACES_SAMPLER=parentbased_traceidratio` +
  `OTEL_TRACES_SAMPLER_ARG` (.NET ≥ 1.8 honours these; programmatic
  `SetSampler` would override them, so **set none in code**). Root spans worth
  keeping unconditionally — the dispatch/ingress/handshake spans — are already
  children of an HTTP or extracted root, so ratio sampling drops whole traces
  coherently rather than half of a leg. Head-based only; no tail sampling,
  because that needs a collector, and the collector is optional (law 1).
- **PII.** Rule: a span/log attribute is either an identifier, a machine reason
  code, or a bounded number. Never: prompt/response text, chat message bodies,
  artifact file names, memory entry content, OIDC tokens or claims verbatim,
  enrollment secrets. The Gateway's audit line is the precedent to copy —
  "subjects by canonical spelling, evidence by reference only, long-lived
  tokens NEVER ride the line" (`src/gateway/src/audit.ts:1-5`). Keep the
  existing tag shapes (`facilitator.ai.endpoint`, `.template`,
  `.prompt_tokens`: `KernelActivity.cs:25-27,63-66`) and add nothing with a
  human sentence in it. HTTP instrumentation must **drop** `Authorization`/
  `Set-Cookie` capture and record only `http.route`, status, and the
  `ofac.operation` id (the gate's operation names,
  `reference/config/facilitator.json` `identity:authorization:operations`)
  rather than full URLs — link-plane paths carry guids, not secrets, but the
  SPA paths can carry a `?q=`.
- **Resource attributes** (set once, from env, per process):
  `service.name` = one of `facilitator`, `ofac-gateway`, `ofac-linkd`,
  `ofac-harness-<name>`, `ofac-<provider>-adapter`, `ofac-forgejo-connector`,
  `oci-launcher` (`OTEL_SERVICE_NAME`);
  `service.instance.id` = kernel `Kernel:ReplicaId`
  (`Kernel/Shared/KernelOptions.cs:14-15`) or the runtime's `instanceId`/`endpointId`
  on the TS side, so a two-replica fleet reads as two resources
  (`reference/compose.replicas.yaml:13,23`);
  `service.namespace` + `deployment.environment.name` + `ofac.team` +
  `ofac.endpoint_id` + `ofac.link_plane_role` (gateway|child|adapter|launcher)
  via `OTEL_RESOURCE_ATTRIBUTES`;
  `ofac.deployment` = `reference-slim` / `reference-full` / `example-local` /
  `example-aws` so a shared viewer can filter by shape. No tenant/user id in
  the resource — that would make cardinality per-person.
- **Cardinality.** Metrics keep today's finite tag sets (`KernelMetrics.cs:120-260`
  tags are reason/status/basis/operation enums). Ban these as labels: work id,
  assignment id, delivery id, artifact id, subject, path-with-ids. The
  histograms (`facilitator.delivery.latency`,
  `facilitator.ingress.processing.latency`, `facilitator.memory.recall.latency`,
  `facilitator.artifact.store.latency`: `KernelMetrics.cs:79-80,98,109`) stay
  explicit-bucket or default-explicit; a Prometheus-shaped backend downstream
  will otherwise mint a thousand series per counter.
- **Logs.** One rule per process: exactly one log pipeline. Either the OTLP
  bridge or the existing stdout JSON — not both, or the AWS example ends up
  with a log twice (it already routes stdout through `awslogs`,
  `examples/aws/lib/kernel-service.ts:103`). Default here: **stdout stays the
  durable artifact, OTLP logs are opt-in via `OTEL_LOGS_EXPORTER`**. That also
  means the Aspire dashboard remains a *viewer* of a copy, matching the spec
  (`docs/specs/open_facilitator_framework_architecture.md:1368`). Log records
  must carry `trace_id`/`span_id` (the .NET bridge does this natively; on the
  TS side the `LinkLogger`'s `fields` object is the injection point,
  `logging.ts:16-22`).

## Env contract (what a deployment writes, nothing else)

```
# every process, both stacks
OTEL_SERVICE_NAME=<per-service name>
OTEL_RESOURCE_ATTRIBUTES=service.namespace=ofac,deployment.environment.name=<env>,ofac.team=<team>,ofac.endpoint_id=<endpoint>
OTEL_EXPORTER_OTLP_ENDPOINT=<endpoint>            # absent ⇒ silent
OTEL_EXPORTER_OTLP_PROTOCOL=http/protobuf         # pinned everywhere today
OTEL_EXPORTER_OTLP_HEADERS=<optional auth>
OTEL_TRACES_SAMPLER=parentbased_traceidratio      # optional
OTEL_TRACES_SAMPLER_ARG=1.0
# kernel only: on/off switch (transport still from the vars above)
Otel__Endpoint=<endpoint>
```

Unset ⇒ no exporter, no error. On the kernel that switch is `Otel:Endpoint`
because .NET's OTLP exporter registration is code-side
(`KernelServiceCollection.cs:370-373`); on the TS side the equivalent
`OTEL_SDK_DISABLED` / "no endpoint ⇒ no SDK" is spec-supported
(`@opentelemetry/resources` + `sdk-node` read `OTEL_SDK_DISABLED`) — the
asymmetry is worth one paragraph in `docs/` rather than a bespoke key, so
**do not** add an `otel:` section to any YAML config (`protocol/json-schema/policy-file.v1.json`
family stays telemetry-free).
Two corollaries: (a) launcher-provisioned elastic containers inherit telemetry
env through the existing `$`-name expansion
(`reference/config/facilitator.env:42-51`, `src/adapters/provisioning-oci/src/handler.ts:96,128`)
— adding `OTEL_EXPORTER_OTLP_ENDPOINT=$$OTLP_ENDPOINT` to the workload env is
enough, no launcher change; (b) the local example's node bundle already forwards
the pair, so that leg needs zero compose edits
(`examples/local/templates/node/compose.yaml.tmpl:32-33,72-73`).

## Landing order (each step independently shippable, each gated by `referencectl verify`)

1. **Kernel log signal + HTTP-client instrumentation** (pure addition,
   `KernelServiceCollection.cs:360-374`, two new pinned packages).
2. **Inbound join**: `LinkIngress.AdmitAsync` extracts the envelope carrier and
   parents `ingress.process` on it (`LinkIngress.cs:63,377`). Closes the
   round-trip: today the kernel's spans for a worker-originated event are a
   different trace from the one the kernel stamped.
3. **`GET /api/admin/audit?traceId=`** + trace-filterable audit view
   (shared component with `.omp/ideas/operator-console-next.md` Gap 2/3).
4. **A `@ofac/otel` bootstrap module** (or a `telemetry.ts` inside
   `@ofac/workspace-link`): `startTelemetryFromEnv({serviceName, meterName})`
   — reads only env, returns a no-op handle when no endpoint, wires
   `NodeSDK`-equivalent pieces + the `LinkLogger` bridge. Single seam, so
   adapters and the Gateway stay forks-free.
5. **Gateway + SDK instrumentation**: extract-on-deliver, span per
   delivery/handle/publish, fill `traceId` in `auditPolicy`
   (`runtime.ts:999,1544` — the field already exists, `audit.ts:25`), meters
   for the existing `LinkStats` + child fleet (`types.ts:154-161`).
   Diagnostics `/status` gains a `telemetry: {exported:bool, endpointSet:bool}`
   field — cheap, and it makes "am I emitting?" a question the runtime can
   answer (`diagnostics.ts:12-20`).
6. **Adapters + launcher**: provider event counters, webhook→publish spans,
   `provision.start/stop` spans and a `ofac.provisioning.ops` counter with the
   kernel's vocabulary (`start|stop` × reason) so the two halves of the scale
   law finally line up.
7. **Deployment wiring**: OTEL env on every team-side container in
   `reference/compose.{slim,full}.yaml` + the provisioning overlay's workload
   env; resource attributes everywhere; fix the dead CloudWatch alarm name
   (`examples/aws/lib/observability.ts:71` → `facilitator.delivery.dead_letter_count`)
   and add a second alarm on `facilitator.queue.oldest_wait_seconds`
   (`KernelMetrics.cs:85`) — the starvation signal the SPA already surfaces.
   If the AWS example also flips to `UseOtlpExporter`
   (all-signals, cannot mix with signal-specific `AddOtlpExporter` — documented
   in the 1.20.1 package) then say so once, in the kernel wiring comment.

## Verification (how a later session proves emission, not just wiring)

Backend, honestly: the pinned reference dashboard is
`aspire-dashboard:13.4.2` (`reference/versions.env:8`) and it is in-memory
(`docs/specs/open_facilitator_framework_architecture.md:2161`). Its documented
HTTP telemetry API exposes `/api/telemetry/{resources,logs,spans,traces,traces/{id}}`
(aspire.dev/dashboard/apis.md, read 2026-11-12) and `verify.sh` already uses
`/api/telemetry/spans` (`reference/checks/verify.sh:582`) — so **verification is
by REST query, and each signal is proved through its own endpoint rather than
by trusting the UI**. (`.omp/ideas/local-deployment-reference-infra.md:77-83`
claims all three signals arrive and that `Resume` persistence is available;
`Resume` needs 13.6, so that claim is pinned to a future pin, not to today.
A generic `otel/opentelemetry-collector-contrib` + a `debug`/`file` exporter
is the stack-independent alternative if the dashboard turns out to drop one
signal — one extra compose service, and it is exactly the shape the AWS
example already ships, `examples/aws/lib/adot.ts:33-56`.)

The gate, additive to `step_trace_correlation` (`reference/checks/verify.sh:549`):

1. **Traces, cross-plane.** After the seeded `work.assign`, take the outbox
   stamp `T` (existing code), then assert `GET :18888/api/telemetry/traces/T`
   returns spans from **at least two resources** — `facilitator` and
   `ofac-gateway` — and that a Gateway span name appears. Today's assertion
   (spans for `facilitator` only, `verify.sh:575`) is the hole this closes.
2. **Metrics.** Poll `GET :18888/api/telemetry/resources` for the new
   `ofac-*` service names, then assert the meter arrives: query the dashboard's
   metrics surface (REST where 13.4.2 offers it, otherwise the collector `debug`
   exporter) for `facilitator.delivery.attempts` and one
   `ofac.link.delivered`-shaped gateway counter with `ofac.team`/`ofac.endpoint_id`
   resource attributes. A metric assertion must be by **name + resource**, never
   by "some data appeared".
3. **Logs.** `GET :18888/api/telemetry/logs?resource=facilitator` returns ≥ 1
   record whose `trace_id` equals `T` — that single check proves the log↔trace
   bridge and the resource name in one assertion. Same for one team-side
   service once step 5 lands. (The documented endpoint set is current-dashboard;
   confirm it exists in the pinned 13.4.2 as part of landing this, and fall
   back to a collector `debug`-exporter leg if the pinned image predates it.)
4. **Silence law.** One leg with `Otel__Endpoint`/`OTEL_EXPORTER_OTLP_ENDPOINT`
   unset (or an unreachable endpoint): kernel + gateway still reach `ready`,
   `verify` still passes end to end, no restart, and no OTLP error line in
   `docker compose logs`. Silent degradation is a tested property, not an
   assumption.
5. **PII law.** Grep the collected span/log payloads for the enrollment token,
   the seeded message text, and the `Authorization` value from the run
   (`docker compose logs` + the dashboard query): zero hits. Machine reason
   codes only.
6. **Replica law.** `./referencectl up slim --replicas 2`
   (`reference/compose.replicas.yaml:11-30`): the viewer shows two
   `facilitator` resources distinguished by `service.instance.id`, matching
   `GET /api/admin/replicas`.
7. Local example: extend the existing soft telemetry leg of
   `make example-local-smoke` (`examples/local/README.md:230-236`,
   `Makefile:72`) to hard-assert the node bundle emits — the env plumbing is
   already there, so this step fails only if the TS SDK is absent or broken.
8. Docs/skill sync: `.omp/skills/facilitator-architecture/SKILL.md` config-keys
   line (`:27`, `Otel:Endpoint`) and the ports/env rows of
   `reference/README.md` / `examples/local/README.md` change in the same commit
   (doc-sync + skills-maintenance rules, `AGENTS.md:68,76`), and the new
   packages land in the catalog so `make build` / `pnpm -r build` stay
   warning-free (`AGENTS.md:18`) and the frozen-lockfile image build still
   resolves (`Dockerfile.node:29`).

## Non-goals
- No OpenTelemetry **Collector** requirement, no `otelcol` config files in
  `reference/`, no vendor APM. The AWS example keeps its sidecar because that
  platform needs an IAM-privileged shipper, not because the system needs a proxy.
- No profiling, no eBPF, no zero-code/auto-instrumentation attach.
- No telemetry from third-party images (Postgres, Keycloak, SeaweedFS,
  Mattermost, Forgejo, Kafka, Ollama) — no sidecars for images we don't build.
- No replacement of `GET /api/admin/metrics`, the audit ledger, or the SPA's
  queue views: the kernel must stay answerable with Postgres + HTTP and no
  telemetry backend (spec `:1368`).
- No new protocol surface: no control-frame or delivery-schema fields
  (both schemas are closed — `protocol/json-schema/control.v1.json:9,60`,
  `protocol/cloudevents-schemas/link-delivery.v1.json:7`). The envelope's
  existing `traceparent` is the whole protocol change.
- No per-request sampling decisions in application code, no custom `Sampler`,
  no tail-sampling dependency.

## Open questions (user decisions, not research items)
1. **Home for the TS bootstrap**: new workspace package `@ofac/otel` (clean
   dependency story, one more tarball in `features/build.mjs:19-26`) versus
   a module inside `@ofac/workspace-link` (zero new packages, but every
   consumer of the SDK drags the SDKs transitively). Recommendation:
   separate package, and the SDK reaches it through a one-line
   runtime-optional hook so the pure `core/` layer stays dependency-free.
2. **OTLP logs on by default or opt-in?** Law above says opt-in via
   `OTEL_LOGS_EXPORTER` to keep one log pipeline per process; the counter-argument
   is that the reference env otherwise keeps two views of the same lines.
3. **Signal-level endpoint split** (`OTEL_EXPORTER_OTLP_{TRACES,METRICS,LOGS}_ENDPOINT`,
   supported by both stacks): worth exposing in `reference/` now, or wait for a
   real deployment that needs it?
4. **Elastic/launcher child containers**: inherit the parent's OTLP endpoint
   implicitly (one env name, done), or a `Provision__Workloads__*__Env__`
   allowlist so a team can point its children at its own viewer?
5. **`ofac.*` resource-attribute prefix**: put our extra attributes under
   `ofac.*` (free-form, spec-conformant) or reserve an
   `Attributes:Resource` config section on the kernel so an operator can add
   keys without touching env composition?
