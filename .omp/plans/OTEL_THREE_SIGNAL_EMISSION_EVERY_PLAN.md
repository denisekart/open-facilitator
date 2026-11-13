# Plan: OTel three-signal emission - every service instruments itself

## Context

The idea `.omp/ideas/opentelemetry-integration.md` asks that every built service - the .NET kernel and each team-owned Node process - own its OpenTelemetry instrumentation and emit **traces, logs and metrics** over OTLP, configured only by standard `OTEL_*` env vars, silent when the env names no endpoint, the collector staying optional infrastructure, and with the reference acceptance run proving that all three signals actually arrive. Today the kernel emits traces + metrics and has no log pipeline at all (`src/facilitator/Kernel/KernelServiceCollection.cs:360-377`), no HTTP-client span for the two model hops, never joins the inbound `traceparent` a worker sends back, and every Node process is telemetry-dark. End state: the round-trip trace closes across both planes, `GET /api/admin/audit?traceId=` answers "which audit rows belong to this span", and `referencectl verify` fails if any signal stops flowing.

**Posture choices (settled for this plan, no re-litigation):** standard env only (no new kernel config keys, no YAML `otel:` section, `Otel__Endpoint` remains the single kernel on/off switch); OTLP logs are **opt-in per process** via `OTEL_LOGS_EXPORTER` so each process keeps exactly one log pipeline by default (`reference/` opts in); the TS bootstrap is a **new workspace package `@ofac/otel`**.

**Four corrections to the idea's as-built section, verified this session - they change the plan:**
1. **`OTEL_SDK_DISABLED` IS honoured by .NET** - `~/.nuget/packages/opentelemetry/1.20.1/lib/net10.0/OpenTelemetry.dll` contains the literal string `OTEL_SDK_DISABLED` and the message `Disabled because OTEL_SDK_DISABLED is true.` (the idea says it is not honoured). Same-name kill switch on both stacks.
2. **`OTEL_TRACES_EXPORTER` / `OTEL_METRICS_EXPORTER` / `OTEL_LOGS_EXPORTER` are read by the .NET SDK** (same assembly), but they select built-in exporters only - `none` disables, and a code-side `AddOtlpExporter()` is additive and NOT enabled by those values. On the kernel they are a negative control; the kernel's opt-in is code + `Otel:Endpoint`.
3. **The pinned dashboard `aspire-dashboard:13.4.2` (`reference/versions.env:8`) has no metrics REST API.** Probed live against the running slim stack: 200 `/api/telemetry/{spans,logs,traces,traces/{id},resources}`, 404 for `/api/telemetry/metrics`, `/api/telemetry/metrics/providers`, `/api/telemetry/metrics/namespaces`. Route list read out of `/app/Aspire.Dashboard.dll` confirms it: `/api/telemetry/{logs,resources,spans,traces,traces/,validateToken}` only. Metrics are asserted with a throwaway collector leg, not through the dashboard.
4. **`OTEL_EXPORTER_OTLP_*` does NOT honour `OTEL_SDK_DISABLED`**: the OTLP assembly contains no such string, so the kernel's log pipeline registers a live OTLP exporter even under the kill switch. Hence the kernel's log-signal gate is code-side, matching the existing trace/metric gate.

## Step 1 - Kernel: log signal, HTTP-client spans, resource identity (one commit, no protocol change)

`Directory.Packages.props` - bump all four existing OTel pins `1.20.1` -> `1.21.0` (`:17-20`) and add exactly one new pin:

```xml
<PackageVersion Include="OpenTelemetry.Instrumentation.Http" Version="1.21.0" />
```

1.21.0 is the current stable of all five packages (NuGet flatcontainer read 2026-11-13) and is mandatory, not cosmetic: `OpenTelemetry.Instrumentation.Http` 1.21.0 has a hard floor `OpenTelemetry.Api.ProviderBuilderExtensions >= 1.21.0`, so 1.20.2 would drag a second SDK version into the graph. `src/facilitator/Facilitator.csproj` gets the matching `<PackageReference Include="OpenTelemetry.Instrumentation.Http" />` next to the other four (`:24-27`). Keep the component-package posture - do NOT introduce the `OpenTelemetry.AspNetCore` meta-package (the file comment at `KernelServiceCollection.cs:33-35` states this law).

Replace the body of `private static WebApplicationBuilder AddOpenTelemetry(this WebApplicationBuilder builder)` (`src/facilitator/Kernel/KernelServiceCollection.cs:360-377`) so it reads exactly:

```csharp
var otel = builder.Services.AddOpenTelemetry();
otel.ConfigureResource(r => r.AddAttributes(
[
    new("service.instance.id", KernelOptions.ReplicaId(builder.Configuration)),
    new("ofac.deployment", builder.Configuration["Otel:Deployment"] ?? "reference"),
]));
otel.WithTracing(t =>
{
    t.AddSource(KernelActivity.SourceName);
    t.AddAspNetCoreInstrumentation(o => o.Filter = ctx =>
        !ctx.Request.Path.StartsWithSegments("/api/health")
        && !ctx.Request.Path.StartsWithSegments("/api/dev"));
    t.AddHttpClientInstrumentation(o => o.Filter = req =>
        req.RequestUri?.Scheme is "http" or "https");
});
otel.WithMetrics(m => m.AddMeter(KernelMetrics.MeterName));
otel.WithLogging(b =>
{
    b.AddOpenTelemetry();
    if (LogsExportEnabled(builder.Configuration))
    {
        b.AddOtlpExporter();
    }
});
if (!string.IsNullOrEmpty(builder.Configuration["Otel:Endpoint"]))
{
    otel.WithMetrics(m => m.AddOtlpExporter());
    otel.WithTracing(t => t.AddOtlpExporter());
}
```

All five members exist in the pinned/1.21 graph (verified against package XML docs): `OpenTelemetryBuilderSdkExtensions.ConfigureResource(IOpenTelemetryBuilder, Action<ResourceBuilder>)`, `WithLogging(IOpenTelemetryBuilder, Action<LoggerProviderBuilder>)`, `Logs.OtlpLogExporterHelperExtensions.AddOtlpExporter(LoggerProviderBuilder)`, `Microsoft.Extensions.Logging.OpenTelemetryLoggingExtensions.AddOpenTelemetry(ILoggingBuilder)`, `AspNetCoreTraceInstrumentationOptions.Filter`, `HttpClientTraceInstrumentationOptions.FilterHttpRequestMessage` (exposed as `Filter`). `using OpenTelemetry.Resources;` + `using Facilitator.Kernel.Shared;` for `KernelOptions.ReplicaId` (`Kernel/Shared/KernelOptions.cs:14`) and the `KeyValuePair<string, object>` collection expression needs `using System.Collections.Generic;` if not already implicit.

Two design facts to preserve in the comment block:
- The `WithLogging` call is **unconditional** because a log pipeline with no exporter costs nothing; the exporter only appears when `LogsExportEnabled` is true. Add that private helper next to `AddOpenTelemetry`:

```csharp
/// <summary>Logs opt in per process: an OTLP log exporter exists only when
/// OTEL_LOGS_EXPORTER names one AND the kernel's own switch is on. .NET reads
/// OTEL_LOGS_EXPORTER but only for the built-in 'none' selector, so the
/// opt-in is code-side - the same asymmetry as Otel:Endpoint for the others.
/// stdout stays the durable artifact either way.</summary>
private static bool LogsExportEnabled(IConfiguration configuration) =>
    !string.IsNullOrEmpty(configuration["Otel:Endpoint"])
    && Environment.GetEnvironmentVariable("OTEL_LOGS_EXPORTER")
        ?.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
        .Any(v => !string.Equals(v, "none", StringComparison.OrdinalIgnoreCase)) is true;
```

- `service.instance.id` comes from `Kernel:ReplicaId` so the two-replica overlay is distinguishable in any viewer (law: a fleet must read as two resources). No new config surface: `Otel:Deployment` is a purely cosmetic label, and `OTEL_RESOURCE_ATTRIBUTES`/`OTEL_SERVICE_NAME` still flow through the SDK default resource (`OpenTelemetry.Extensions.Hosting` 1.20.1+ parses both - confirmed by string scan). Keep `Otel:Endpoint` as the only on/off key; do NOT add an `otel:` YAML section anywhere.
- URL query strings are already redacted by both instrumentations (`DisableUrlQueryRedaction` exists and stays unset), and neither captures `Authorization`/`Set-Cookie` - so the idea's PII requirement needs no filter code, only these filters that drop the chatty `/api/health` + `/api/dev` spans.

## Step 2 - Kernel: join the inbound link carrier (the highest-value gap)

`src/facilitator/Kernel/Link/LinkIngress.cs` - `AdmitAsync(string rawBody, LinkPrincipal workload, CancellationToken ct)` (`:63-64`) currently runs the whole admission with **no span at all** except the raw steer arm, which opens `KernelActivity.Source.StartActivity("ingress.process", ActivityKind.Internal)` at `:377` and never reads the envelope's carrier. Result: a worker-originated event starts a fresh kernel trace instead of joining the one the kernel itself minted and stamped.

Change, in `AdmitAsync`, immediately after the gate screen succeeds (`:66-70`) and before any arm branch:

```csharp
// Durable-queue trace law, inbound twin: the carrier rides the envelope, so
// extract-or-fresh here and let every arm run as its child. isRemote:true -
// the sender is another process (docs/protocol.md:16, envelope.json:51-53).
var linkParent = default(ActivityContext);
if (screen.Root.ValueKind == JsonValueKind.Object
    && screen.Root.TryGetProperty("traceparent", out var linkTp)
    && linkTp.ValueKind == JsonValueKind.String)
{
    ActivityContext.TryParse(linkTp.GetString(), null, isRemote: true, out linkParent);
}
using var ingressSpan = KernelActivity.Source.StartActivity(
    "ingress.process", ActivityKind.Internal, linkParent);
ingressSpan?.SetTag("facilitator.endpoint_id", workload.EndpointId);
```

`screen.Root` is the parsed envelope (`JsonElement`); `System.Diagnostics` + `System.Text.Json` are already imported in that file. Then **delete the nested raw span** at `:377` (`using (KernelActivity.Source.StartActivity("ingress.process", ActivityKind.Internal))`) and de-indent its block - keeping it would open a second span of the same name inside the first, double-counting nothing in metrics (latency is recorded by `IngressPipeline.WithSpanAsync`, a different call path) but producing two sibling-looking spans per steer.

Do NOT touch `IngressPipeline.WithSpanAsync` (`src/facilitator/Kernel/Work/IngressPipeline.cs:20-34`) or its callers (`Kernel/Work/IngressProcessor.cs:98,157,183,211,244`, `Kernel/Work/CloudEventIngress.cs:174`): those are the HTTP/cloud-event ingress paths, where ASP.NET Core instrumentation already supplies the parent context. The link path has no HTTP span because the SignalR hub calls `AdmitAsync` directly (`Kernel/Link/LinkHub.cs:130`) and the REST twin is `Kernel/Link/LinkEndpoints.Enroll.cs:222` - so for the REST twin the new span correctly nests inside the ASP.NET Core span, and for the hub it becomes the root, joined to the sender's trace by extraction.

New test in `tests/Facilitator.Tests/Dispatch/TracePropagationTests.cs` (collection `[Collection("postgres:dispatch-link")]`, fixture `DispatchDbFixture`, reuse the in-file `CapturingLinkSink` + `SpanCollector` (`:26-58`) idioms and `KernelTestFactory(fixture, noDispatcher: true, _ => sink, settings: …)` - `settings` is applied via `UseSetting`, `tests/Facilitator.Tests/Infrastructure/KernelTestFactory.cs:36,43`): post to `POST /api/link/v1/events` an envelope carrying `traceparent: 00-<32hex>-<16hex>-01` whose trace id differs from every ambient id, then assert from `SpanCollector.Spans` that the `ingress.process` activity has `RootId ==` that trace id and `ParentId ==` the sent span id, and `ActivityLinks` is empty (a join, not a link). Name: `LinkIngress_CarriesInboundTraceparentIntoIngressSpan`.

## Step 3 - Kernel: `GET /api/admin/audit?traceId=` + SPA trace filter

`src/facilitator/Kernel/Admin/AdminEndpoints.cs:94-95` - add the parameter to the `/audit` handler and forward it:

```csharp
group.MapGet("/audit", async (string? action, string? actor, DateTime? since, string? decision,
    string? correlation, string? traceId, int? limit, AdminReadStores stores, CancellationToken ct) =>
    Results.Ok(await stores.AuditAsync(action, actor, since, decision, correlation, traceId, limit ?? 50, ct)));
```

`src/facilitator/Kernel/Admin/AdminReadStores.cs:554-593` - widen `AuditAsync` to `public async Task<IReadOnlyList<AdminAuditRow>> AuditAsync(string? action, string? actor, DateTime? since, string? decision, string? correlation, string? traceId, int limit, CancellationToken ct)` and add, beside the existing `correlation` arm (`:575-578`), the equality filter in the same shape:

```csharp
if (traceId is { Length: > 0 })
{
    query = query.Where(a => a.TraceId == traceId);
}
```

`trace_id` is already written (`Kernel/Shared/Identity/AuditWriter.cs:66` - `Activity.Current?.TraceId.ToHexString()`, i.e. **lowercase 32 hex, no `00-` prefix and no dashes**), already projected (`AdminReadStores.cs:588,592`, `AdminAuditRow.TraceId` `:194`) and already returned. `/audit/export` (`AdminEndpoints.cs:99-102`, `AdminReadStores.cs:651-653`) deliberately keeps dropping trace ids so a pasted export leaks nothing - leave it alone. Update the only two callers of `AuditAsync` (the endpoint above; grep `AuditAsync(` before editing - there is no other).

SPA (`src/facilitator/ui/`):
- `src/components/AuditView.svelte` - add `let trace = $state("")` beside `actor`/`action`/`correlation` (`:23-25`), seed it from the route in the same block that reads `wanted.get("correlation")` (`:48-50`), add `traceId` (the query key is byte-identical to the endpoint's parameter name) to the `for (const key of [...])` list (`:29-33`) and to `apply()`'s params (`:54-70`), and render a per-row copy chip + a "filter to this trace" link in the table row where `traceId` is available. `src/lib/types.ts:213` already declares `traceId: string | null` on `AdminAuditRow`, so no type change.
- `src/components/AuditView.test.ts` - the existing assertion at `:118` pins the exact query string (`/api/admin/audit?decision=deny&correlation=abc&limit=100`); key order in the built `URLSearchParams` must stay `decision, actor, action, correlation, traceId, since` order-stable so that assertion still passes, and add one case that a route carrying `traceId=<32hex>` produces `…&traceId=<32hex>&limit=100`.
- `src/components/WorkInspector.svelte:273-285` - the `data-testid="trace-chip"` button copies `correlationId` only. Make it show both ids: keep the existing chip and its test id byte-for-byte (the `ofac-admin-ui` testid rule), and add a sibling chip `data-testid="trace-chip-trace"` that copies `row.traceId` when non-null, `title` naming the viewer as the destination so an operator never mistakes the ledger key for the dashboard key.

## Step 4 - New package `@ofac/otel`: env-only bootstrap on the 2.x line

New workspace member `src/otel` (add `- "src/otel"` to `pnpm-workspace.yaml:1-24`, above `src/gateway` so the topology reads bottom-up). Package name `@ofac/otel`, `type: module`, tsup build like its siblings, exports `.` only. Runtime deps, all `catalog:`: `@opentelemetry/api`, `@opentelemetry/core`, `@opentelemetry/resources`, `@opentelemetry/sdk-trace-node`, `@opentelemetry/sdk-trace-base`, `@opentelemetry/sdk-metrics`, `@opentelemetry/sdk-logs`, `@opentelemetry/exporter-trace-otlp-http`, `@opentelemetry/exporter-metrics-otlp-http`, `@opentelemetry/exporter-logs-otlp-http`, `@opentelemetry/semantic-conventions`.

**Catalog pins (append to the `catalog:` block, `pnpm-workspace.yaml:35-75`), verified on npm 2026-11-13 as one coherent set:**

```yaml
  # @ofac/otel (OTel three-signal plan). One version line on purpose: the
  # trace stack has no 3.x release, so pin the 2.x/latest-2 line throughout.
  "@opentelemetry/api": "^1.10.0"
  "@opentelemetry/core": "2.12.1"
  "@opentelemetry/resources": "2.12.1"
  "@opentelemetry/sdk-trace-node": "2.12.1"
  "@opentelemetry/sdk-trace-base": "2.12.1"
  "@opentelemetry/sdk-trace": "2.12.1"
  "@opentelemetry/context-async-hooks": "2.12.1"
  "@opentelemetry/sdk-metrics": "2.12.1"
  "@opentelemetry/sdk-logs": "0.223.2"
  "@opentelemetry/exporter-trace-otlp-http": "0.223.2"
  "@opentelemetry/exporter-metrics-otlp-http": "0.223.2"
  "@opentelemetry/exporter-logs-otlp-http": "0.223.2"
  "@opentelemetry/otlp-transformer": "0.223.2"
  "@opentelemetry/otlp-exporter-base": "0.223.2"
  "@opentelemetry/semantic-conventions": "^1.45.0"
```

The idea's version list (`sdk-metrics@^3.1.1` + `sdk-trace-node@^2.12.0` + `exporter-*@^0.301.2`) does **not** install cleanly: `exporter-*@0.301.2` requires `@opentelemetry/core@3.1.1` while `sdk-trace-node@2.12.0` requires `core@2.12.0`, and `sdk-trace-node` has **no 3.x release at all** (`dist-tags` = `latest: 2.12.0`, `latest-2: 2.12.1`). The 2.12.1 + 0.223.2 combination is the coherent one: `exporter-trace-otlp-http@0.223.2` -> `sdk-trace@2.12.1`, `otlp-transformer@0.223.2` -> `core@2.12.1`/`sdk-logs@0.223.2`, `sdk-logs@0.223.2` -> `core@2.12.1`. No `@opentelemetry/sdk-node` and no `instrumentation-http`: `sdk-node` pulls gRPC/Zipkin/Prometheus exporters into every Feature tarball (`features/build.mjs:19-26` deploys each runtime package with `pnpm deploy --prod`) and `instrumentation-http` cannot see the outbound calls, which are `fetch` (undici) - e.g. `src/gateway/src/runtime.ts:1441` `await (this.deps.fetchImpl ?? fetch)(…)`. `@opentelemetry/instrumentation-undici` has no 0.223.x release, so it is out of this plan entirely.

Public surface (`src/otel/src/index.ts`), one function plus two types - no other exported entry point:

```ts
export interface TelemetryHandle {
  /** False when the env named no endpoint: no SDK, no exporter, no timers. */
  readonly emitting: boolean;
  /** OTLP log bridge is active (OTEL_LOGS_EXPORTER opted in + endpoint set). */
  readonly logsExported: boolean;
  /** Extra fields every log line should carry so stdout lines join the trace. */
  traceFields(): Record<string, string>;
  shutdown(): Promise<void>;
}

export interface TelemetryOptions {
  serviceName: string;                       // caller's fallback for OTEL_SERVICE_NAME
  meterName: string;                         // e.g. "ofac.gateway"
  instanceId: string;                        // gatewayId/endpointId-based
  logBridge?: (fields: Record<string, unknown>) => Record<string, unknown>;
}

export function startTelemetryFromEnv(options: TelemetryOptions): TelemetryHandle;
```

Behaviour, all of it env-driven - no config file, no YAML key (law 2):
- `OTEL_SDK_DISABLED=true` or no `OTEL_EXPORTER_OTLP_ENDPOINT` -> return the no-op handle (`emitting: false`, `traceFields()` = `{}`, `shutdown()` resolves). Nothing is imported from the exporters on that path: use dynamic `await import()` inside the emitting branch behind a `void startAsync()` so a telemetry-dark process pays no module cost (the handle is created synchronously; `emitting` is decided from env before any import).
- Otherwise build the three providers from signal-specific exporters and register a `NodeContextManager` from `@opentelemetry/context-async-hooks` so spans nest across `await`. Resource = `resourceFromEnv()` from `@opentelemetry/resources` (honours `OTEL_RESOURCE_ATTRIBUTES` + `OTEL_SERVICE_NAME`), plus `ofac.deployment` and `service.instance.id = options.instanceId`, with `service.name` falling back to `options.serviceName` only when the env omits `OTEL_SERVICE_NAME`.
- Sampler: **set none in code.** `ParentBasedSampler` is the SDK default and `OTEL_TRACES_SAMPLER` + `OTEL_TRACES_SAMPLER_ARG` are read by these packages; a programmatic `SetSampler` would silently override the operator's dial.
- `traceFields()` returns `{ traceId, spanId }` (32-hex / 16-hex, lower) from the active span context, or `{}` - this is the injection point the Gateway audit line and `LinkLogger` fields use.
- `logBridge`: when `OTEL_LOGS_EXPORTER` names a non-`none` value, also emit each line as an OTLP log record through `@opentelemetry/exporter-logs-otlp-http` (`logsExported: true`), stamped with the active `trace_id`/`span_id`; stdout stays the durable artifact (one pipeline per process stays true because the bridge is additive-only when the operator asks for it).
- Never throw: wrap the emit path so an unreachable endpoint cannot fail a business operation (law 5). Log one `warn` line at startup when an endpoint is set and the first export fails, then go quiet.

`src/otel/test/telemetry.test.ts` (vitest, `vitest ^5.0.5` from the catalog): (a) no `OTEL_EXPORTER_OTLP_ENDPOINT` -> `emitting === false`, zero handles on `require`d module graph, no unhandled rejection; (b) `OTEL_SDK_DISABLED=true` with an endpoint -> `emitting === false`; (c) endpoint set -> `emitting === true`, `traceFields()` inside a started span returns a 32-hex `traceId` matching a W3C `traceparent` regex, and after `shutdown()` the process has no open handles (assert with `afterEach` + explicit `await handle.shutdown()`).

## Step 5 - Gateway + workspace-link: extract on deliver, span, fill `traceId`

`src/workspace-link` owns propagation so every consumer gets it free, and it must stay dependency-free: `core/` gets **no** `@opentelemetry` import. The seam is an injected hook.

- `src/workspace-link/src/core/types.ts` — extend `LinkClientOptions` (`:41-59`) with one optional member:
  ```ts
  /** Trace seam: wraps one delivery's handler invocation. Default = run it. */
  instrument?: <T>(name: string, env: EnvelopeCommand, run: () => Promise<T>) => Promise<T>;
  ```
  Default implementation `(name, env, run) => run()` so every existing test and consumer is byte-compatible.
- `src/workspace-link/src/core/command-dispatcher.ts` — `invokeHandler` (`:90-113`) wraps the single `handler.onCommand(env, ctx)` call (`:105`) and `routeDecided` (`:78-88`) wraps `handler.onDecided?.(...)` (`:87`) in `this.deps.instrument?.("link.deliver", env, () => …) ?? run()`. Thread `instrument` through the constructor deps object (`:21-33`) from `LinkClient` (`core/client.ts:96`, where `new CommandDispatcher({...})` is built) — that is the whole SDK change: one option, two wrap sites, no protocol change.
- Outbound carrier: `LinkClient.publishEvent` (`core/client.ts:260-289`) already stamps `traceparent` onto the outgoing envelope when the caller passes one and the envelope has none (`:267`); the Gateway now passes the **active span's** id instead of the inbound envelope's, so a Gateway-side span becomes the parent for the kernel-side join of Step 2. Sites: `runtime.ts:1096` and `runtime.ts:1212` (approval publish/consume) change from `cmd.traceparent` to the active trace fields with `cmd.traceparent` as fallback.
- Transports need nothing: `node/http.ts` (`:221-233`) and `node/signalr.ts` (`:57-65`) carry the envelope, and the envelope is the carrier (law 4). Do NOT add a session-scoped header at `signalr.ts:59` — control frames are closed schemas (`protocol/json-schema/control.v1.json:9,60,98,120,142`) and a session leg is attributed by resource + `facilitator.session_epoch`, not by a carrier.
- `src/gateway/src/runtime.ts`:
  - `start()` (`:301`) calls `startTelemetryFromEnv({ serviceName: "ofac-gateway", meterName: "ofac.gateway", instanceId: config.gatewayId })` once, stores the handle, and passes `instrument` into `createLinkClient` (`:324`). `stop()` (`:371`) awaits `handle.shutdown()` **last**, after the goodbye is sent.
  - `gatewayHandler().onCommand` (`:647-678`) and `onDecided` (`:679`) are unchanged in shape; the span comes from the SDK hook, so no per-command-type edits.
  - `auditPolicy` call sites `:999-1016` and `:1544-1561` add `...(this.telemetry?.traceFields() ?? {})` — `PolicyAuditFields.traceId` already exists (`src/gateway/src/audit.ts:25`) and is currently never filled at either site.
  - `child-link.ts:159` builds `{ v: 1, type: "command", requestId, message: msg }`; `msg` carries the envelope, so the child sees the carrier with zero schema change — the only edit is that `msg`'s envelope now holds the Gateway span's `traceparent` (same expression as the publish sites above).
  - `GatewayStatus` (`:121-142`) gains `telemetry: { emitting: boolean; logsExported: boolean; endpointSet: boolean }`; `startDiagnostics` (`src/gateway/src/diagnostics.ts:15-32`) serves it verbatim through the existing single `/status` route — that makes "am I emitting?" answerable without a viewer.
  - Metrics: new meter `ofac.gateway` with finite-label instruments fed from the existing `LinkStats` object (`core/types.ts:154-161`, surfaced at `GatewayStatus.linkStats`) and the child fleet: `ofac.link.delivered` (counter, labels `outcome: accepted|rejected|unsupported`), `ofac.link.duplicates`, `ofac.link.reconnects`, `ofac.child.fleet` (up-down gauge, label `state: live|offline`), `ofac.audit.decisions` (counter, labels `decision: allow|deny|confirm`, `reason` = the existing machine reason codes only). Banned labels: workId, assignmentId, deliveryId, childWorkspaceId, path.
- Tests: `src/gateway/test/` gains a case that a delivery with `traceparent` produces a `link.deliver` span parented to it (assert through an injected in-memory span exporter created via the SDK test hooks, or by asserting `auditPolicy` received a `traceId` equal to the delivered carrier's trace id — the latter needs no new test dependency and is the preferred assertion). `src/workspace-link` gets a dispatcher unit test proving the default `instrument` is identity (existing suites must stay green untouched).

## Step 6 - Adapters + launcher: spans and counters where the work actually happens

- `src/adapters/harnesses/adapter-sdk/src/adapter.ts` — `const traceparent = command.traceparent;` (`:321`) becomes the extracted parent for one span per execution named `worker.execute` (`@ofac/otel` `instrument` hook passed through `LinkClientOptions` by each worker bin), and the existing re-stamp sites (`:359,402,498`, plus `src/artifacts.ts:307`) keep copying the carrier but now from the active span when one exists. Harness-reported token counts, where the harness reports them, land as `worker.tokens` (counter, labels `kind: prompt|completion`) — never a span attribute holding text.
- `src/adapters/provider-core` — `hooks-core.ts:63` keeps copying `ctx.command.traceparent`; add one span per webhook→envelope→publish around `webhook-receiver.ts`'s handler named `provider.webhook` (tag `ofac.provider`, `ofac.reason`), and counters `ofac.provider.events` (labels `provider`, `outcome: normalized|duplicate|rejected`) and `ofac.provider.http_errors` (labels `provider`, `status: 429|403|5xx`). The classification already exists: `http-core.ts:109-119` `httpErrorFrom` maps a response onto the taxonomy and `withRetry` (`:75`) is where 429/403 are distinguished — take the label from there, do not re-parse.
- `src/adapters/provisioning-oci/src/handler.ts` — spans `provision.start` / `provision.stop` at the two call sites (`:96`, `:128` region) plus a counter `ofac.provisioning.ops` with labels `op: start|stop` x `reason` — deliberately the kernel's vocabulary (`facilitator.provisioning.ops`, `KernelMetrics.cs`), so the two halves of the scale law line up: the kernel's record of an op and the executor's record of the same op share labels.
- The env-expansion law is unchanged and constrains Step 7: `handler.ts:93-105` replaces a value only when it consists **solely** of `$VARNAME`, resolved against the **launcher's** own env (`reference/compose.provisioning.yaml:31-41`). A literal `http://aspire:18890` therefore passes through untouched — no launcher code change is needed to give elastic children an endpoint.

## Step 7 - Deployment wiring + the dead alarm

**`reference/config/facilitator.env`** (the kernel's only env source; `compose.slim.yaml:101-107` and `compose.full.yaml:157-159` both read it as `env_file`, and `compose.replicas.yaml` inherits it through `extends` — a team-side key added here would NOT reach the workspace, so team-side keys go into compose):
- After `:33`, add the two lines the kernel needs to be log-opt-in and labelled:
  ```
  OTEL_LOGS_EXPORTER=otlp
  OTEL_RESOURCE_ATTRIBUTES=service.namespace=ofac,deployment.environment.name=reference,ofac.deployment=reference-slim
  ```
- In the provisioning block, after `:52`, give elastic children the same endpoint by **literal value** (the `$`-law means `$NAME` would be resolved from the launcher's env, which does have it — but a literal is simpler and provably passes through):
  ```
  Provision__Workloads__0__Env__OTEL_EXPORTER_OTLP_ENDPOINT=http://aspire:18890
  Provision__Workloads__0__Env__OTEL_EXPORTER_OTLP_PROTOCOL=http/protobuf
  ```
  plus `Provision__Workloads__0__Env__OTEL_SERVICE_NAME=ofac-harness-elastic` and an `OTEL_RESOURCE_ATTRIBUTES=…ofac.link_plane_role=child` line.
- Do NOT set `OTEL_TRACES_SAMPLER` here (defaults to parentbased always-on; the dial stays documented, unused).

**Team-side containers — add the four-key block to each `environment:` map.** Exact insertion points: `reference/compose.slim.yaml:145-155` (`demo-workspace`); `reference/compose.full.yaml:87-98` (`mattermost-adapter`), `:122-135` (`tg-adapter`), `:208-224` (`forgejo-adapter`), `:242-248` (`demo-workspace`), `:271-287` (`coding-workspace`); `reference/compose.provisioning.yaml:31-41` (`provisioning-launcher`). Per-service values:

|Service|`OTEL_SERVICE_NAME`|`ofac.link_plane_role`|`OTEL_SDK_DISABLED`|
|---|---|---|---|
|`demo-workspace` (gateway+worker+supervisor)|`ofac-gateway`|`gateway`|not set|
|`coding-workspace`|`ofac-gateway-coding`|`gateway`|not set|
|`mattermost-adapter` / `tg-adapter` / `forgejo-adapter`|`ofac-mattermost-adapter` / `ofac-telegram-adapter` / `ofac-forgejo-adapter`|`adapter`|not set|
|`provisioning-launcher`|`oci-launcher`|`launcher`|`"true"`|

Common to all: `OTEL_EXPORTER_OTLP_ENDPOINT: http://aspire:18890`, `OTEL_EXPORTER_OTLP_PROTOCOL: http/protobuf`, `OTEL_RESOURCE_ATTRIBUTES: service.namespace=ofac,deployment.environment.name=reference,ofac.deployment=reference-slim,ofac.link_plane_role=<role>`. The launcher is pinned **off** deliberately and honestly: it is not instrumented in this plan (Step 6 instruments only the OCI handler's ops, and the launcher's own runtime is a thin poll loop), so a stray `OTEL_*` would imply instrumentation it does not have. Set `OTEL_SDK_DISABLED` so the silence is a decision, not an accident, and `compose.replicas.yaml:46`'s `demo-workspace` restatement + `compose.replicas.full.yaml` inherit them through `extends`.
- `service.instance.id` on the TS side comes from `config.gatewayId`/`endpointId` inside `startTelemetryFromEnv`, never from env — otherwise the two-replica overlay's `Kernel__ReplicaId` trick has no team-side counterpart.

**AWS example** (`examples/aws/`): fix the dead alarm in `lib/observability.ts:71` — `metricName: "facilitator.outbox.dead_letters"` is not an instrument; the real name is `facilitator.delivery.dead_letter_count` (`KernelMetrics.cs:83`), and with `TreatMissingData.NOT_BREACHING` the current alarm can never fire. Add a second alarm on `facilitator.queue.oldest_wait_seconds` (`KernelMetrics.cs:85`) with the same `alarm(...)` helper shape and `statistic: "Maximum"`, threshold 300 seconds. `lib/kernel-service.ts:113-116` gains `OTEL_LOGS_EXPORTER: "otlp"` and an `OTEL_RESOURCE_ATTRIBUTES` with `ofac.deployment=example-aws`; `lib/team-plane.ts:136-160` (the container `environment` map) gains the same four team-side keys. Keep the ADOT sidecar (`lib/adot.ts:20,33-56`) as-is — it stays optional infrastructure. The kernel keeps signal-specific `AddOtlpExporter`; do **not** switch to `UseOtlpExporter`, and if anyone later does, the kernel wiring comment must say it is all-signals and cannot mix with signal-specific registration.

**Local example** — `examples/local/templates/node/compose.yaml.tmpl:32-33,72-73` already forwards the OTLP pair into both the workspace and the launcher, and `tools/localctl/src/render.ts:221-224` already renders the four kernel keys, so nothing is plumbed: only `render.ts` gains `OTEL_SERVICE_NAME`/`OTEL_RESOURCE_ATTRIBUTES` per node box (`ofac.deployment=example-local`) so the TS processes actually emit. `examples/local/README.md:229-237` changes "Aspire spans soft" to state the leg is a hard assertion for team-side traces and metrics (Step 8).

## Step 8 - Acceptance: prove three signals, silence, PII

`reference/checks/verify.sh` — one new step id `signal-emission` and one new helper family, registered exactly like the existing eleven: append `signal-emission` to `VERIFY_ORDER` (`:25-27`), add `signal-emission) echo trace-correlation dead-letters-empty ;;` to `verify_deps_of` (`:28-42`) and a `signal-emission) step_signal_emission ;;` arm to the `case` in `verify_run_step` (`:616-632`). It reads the same `$trace_id` the trace step already derived from the outbox stamp (`:549-578`) — stash it in a file-level var during `step_trace_correlation` (`:560`) instead of re-querying Postgres.

The step asserts four things, each by name + resource, never by "some data appeared":
1. **Traces, cross-plane.** `GET http://localhost:18888/api/telemetry/traces/$trace_id` (returns 200 + OTLP JSON on the pinned 13.4.2, verified live) and `jq` the `resourceSpans` for **≥ 2 distinct `service.name` values including `facilitator` and `ofac-gateway`**, and at least one span named `link.deliver`. Today's assertion (`verify_dashboard_trace`, `:580-586`, `spans?traceId=` count ≥ 1, facilitator only) is the hole this closes; keep that helper for the trace step and add the wider one for this step.
2. **Logs.** `GET …/api/telemetry/logs?resource=ofac-gateway` returns ≥ 1 record and ≥ 1 of them has `traceId == $trace_id` (200 + `{data:{resourceLogs:[…]},totalCount,returnedCount}` on 13.4.2, verified live; an unknown resource name returns 404, which is itself the "resource never emitted" failure signal). This single assertion proves the log bridge, the trace correlation and the resource name at once.
3. **Metrics** — not through the dashboard (404, correction 3 above). Add a **verification-only** collector to the existing overlay stack: `reference/compose.telemetry.yaml` with a `telemetry-sink` service on `otel/opentelemetry-collector-contrib:${OTELCOL_TAG:?set in versions.env}` (add `OTELCOL_TAG=0.144.0` to `reference/versions.env`, matching the version the AWS example already ships, `adot.ts:20`), config `reference/config/otelcol-verify.yaml`: OTLP receivers 4317/4318, `debug` exporter with `verbosity: detailed`, `metrics` pipeline only. Append `-f compose.telemetry.yaml` to `overlays=(…)` in `referencectl:39`. Then `step_signal_emission` greps `compose logs telemetry-sink` for the two required instrument identities: `facilitator.delivery.attempts` with `service.name=facilitator`, and `ofac.link.delivered` with `service.name=ofac-gateway` + `ofac.team` + `ofac.endpoint_id` resource attributes. One extra container, no config committed for the runtime, and exactly the shape `examples/aws/lib/adot.ts:33-56` already ships.
4. **Silence + PII.** Reuse the existing `verify_dump_logs` helper. Assert the two silence properties as a **documented manual leg** rather than a compose-mutating step (the run must not tear the stack down): `./referencectl down && OTEL_EXPORTER_OTLP_ENDPOINT= Otel__Endpoint= ./referencectl up slim` then `./referencectl verify --only trace-correlation,dead-letters-empty` exits 0 and `docker compose logs` contains no line matching `[Oo]pen[Tt]elemetry|OTLP` — write this into `reference/README.md` next to the ports table (`:36`, whose "kernel exporter target" row must now say kernel + team plane) so the silence law is a stated, runnable property. For PII: grep the collector `debug` dump for `$DEMO_ENROLLMENT_TOKEN`, the seeded progress note text from `$VERIFY_MAIN_OUTBOX_WHERE`'s envelope, and the bearer value used by `verify_get` — zero hits, machine reason codes only.
- Replica law: the two-replica matrix (`reference/checks/verify-replicas.sh`, driven by `referencectl verify --replicas`) gains one case asserting `/api/telemetry/resources` lists two entries named `facilitator` distinguished by `service.instance.id` (`facilitator-a` / `facilitator-b`, matching `GET /api/admin/replicas`) — that is what Step 1's `ConfigureResource` buys.
- `make example-local-smoke` (`Makefile:72-77`) — `examples/local/tools/localctl/src/smoke.ts:264-290` currently runs the telemetry leg with `await leg("telemetry", false, …)` (soft). Flip the flag to `true` for the trace/metric part and query `/api/telemetry/resources` for a node-side `ofac-*` resource in addition to the existing span count (`dashboardSpanCount`, `:75-80`).
- Docs/skill sync in the same commit: `.omp/skills/facilitator-architecture/SKILL.md:27` (`Otel:Endpoint (OTLP exporter when set)` → add the `OTEL_LOGS_EXPORTER` opt-in and the fact that logs/metrics/traces all ride `OTEL_EXPORTER_OTLP_*`), `reference/README.md:5,36,103`, `docs/getting-started.md:4`, `docs/getting-started-agents.md:39`. Step counts quoted as "slim 11" (`referencectl:18,283`) become 12 — update those two comment lines and `reference/checks/verify.sh`'s own header comment.

## Critical files & anchors

|File|Region|Why|
|---|---|---|
|`src/facilitator/Kernel/KernelServiceCollection.cs`|`360-377` (`AddOpenTelemetry`, verbatim replacement in Step 1) + `:325-326` (`AddHttpClient("ai")`, `("chat-loopback")` — the two clients the new instrumentation covers)|Every kernel-signal change lands in this one method|
|`src/facilitator/Kernel/Link/LinkIngress.cs`|`63-66` (add the extract + span after the gate screen), `377` (delete the raw nested span)|The cross-plane join; nothing else closes it|
|`src/workspace-link/src/core/command-dispatcher.ts`|`21-33` (deps), `87` + `105` (the two handler calls to wrap)|The one seam that gives every Node process a delivery span without touching each adapter|
|`src/gateway/src/runtime.ts`|`999-1016`, `1544-1561` (`auditPolicy` — fill the existing `traceId`), `1096`, `1212` (carrier source), `121-142` (`GatewayStatus`), `301`/`371` (start/stop)|The Gateway's three trace obligations + the status field|
|`reference/checks/verify.sh`|`25-27` (`VERIFY_ORDER`), `28-42` (`verify_deps_of`), `549-586` (trace step + dashboard helper), `616-632` (dispatch `case`)|Where emission becomes a gate instead of a hope|

Also load-bearing: `pnpm-workspace.yaml:35-75` (catalog), `Dockerfile.node:13-46` (a new workspace member needs TWO `COPY` lines — its `package.json` in the manifest layer **and** its source in the build layer — or `pnpm install --frozen-lockfile` at `:29` fails on the missing member), `features/build.mjs:19-26` (`@ofac/otel` needs no entry: it is a dependency of entries, pulled by `pnpm deploy --prod`, but confirm the tarball grows rather than breaks).

## Verification

Run order, cheapest failure first, all from the repo root:

1. `make build` — zero warnings is a build requirement (`TreatWarningsAsErrors` + `eslint --max-warnings 0`), so the new packages and the widened `AuditAsync` signature are caught here. Then `pnpm -r --if-present typecheck` and `pnpm install` (rewrites `pnpm-lock.yaml` for the 15 new catalog pins).
2. `dotnet test --filter "FullyQualifiedName~Dispatch"` — concrete expectation: the new `LinkIngress_CarriesInboundTraceparentIntoIngressSpan` sees an `ingress.process` `Activity` whose `RootId` equals the trace id it posted and whose `ParentId` equals the posted span id. Before Step 2 that same assertion fails with a fresh random `RootId` — that is the proof the join is real.
3. `pnpm -C src/otel test` then `pnpm -C src/gateway test` — the no-endpoint case must report `emitting: false` and leave no open handles (a hung worker is the failure signature of a wrongly-unconditional SDK).
4. Fresh gate from a clean tree: `./referencectl down; ./referencectl up slim && ./referencectl verify` → `signal-emission` **passes** with a detail line naming both resource names, the log record's trace id, and the two metric identities found in the collector log. Then break each signal on purpose and watch the step fail: unset `Otel__Endpoint` (traces + metrics fail), unset `OTEL_LOGS_EXPORTER` on `demo-workspace` only (logs fail), and `OTEL_SDK_DISABLED=true` on `demo-workspace` (both team-side assertions fail while `facilitator`'s still pass — the asymmetry between the two stacks is exactly what that case pins).
5. Silence law, manually: bring the stack up with both endpoint vars empty; `verify --only trace-correlation,dead-letters-empty` exits 0, no OTLP error line in `docker compose logs`, and the kernel log has zero lines mentioning telemetry after startup.
6. `./referencectl up slim --replicas 2 && ./referencectl verify --replicas` → two `facilitator` resources distinguished by `service.instance.id`.
7. `make example-local-smoke` → the telemetry leg is now hard: it fails if the node bundle emits nothing, which it will if `@ofac/otel` was not wired into the node image (`Dockerfile.node:13-46`) — that is the intended failure, not flake.

## Assumptions & contingencies

- **Dashboard API shape** — `/api/telemetry/{spans,logs,traces,traces/{id},resources}` return 200 on the running pinned 13.4.2 today (probed this session); `traces/{id}` and `logs` were confirmed with a synthetic trace id and an unknown-resource 404 respectively. If a re-check finds `traces/{id}` unusable from a script, fall back to `/api/telemetry/spans?traceId=` and group `resourceSpans[]` by `service.name` in `jq` — same assertion, one more `jq` stage.
- **Collector tag** — if `otel/opentelemetry-collector-contrib:0.144.0` cannot be pulled in this environment, any recent `otelcol-contrib` tag works: the leg greps for instrument names, not for a version-specific log format. If the `debug` exporter's metric dump proves too terse to assert resource attributes, switch that leg's exporter to `file` and parse the JSON; keep the two instrument identities as the assertion.
- **`supervisor`-spawned env** — a `demo-workspace` container runs several units under `features/shared/ofac-supervisor.sh`; compose-level `OTEL_*` reaches all of them, so `service.name` comes from `OTEL_SERVICE_NAME` per unit only if the supervisor exports a per-unit override. Contingency: if the supervisor cannot vary it, the gateway leg emits as `ofac-gateway` and the worker's execution span rides the **same** resource — acceptable (one team box, one resource), and then also set `ofac.link_plane_role=gateway` only.
- **A second 2.x/0.223.x pin surprise** — if pnpm resolves an unexpected peer conflict among the 15 pins, drop `@opentelemetry/sdk-trace`, `@opentelemetry/context-async-hooks`, `@opentelemetry/otlp-transformer` and `@opentelemetry/otlp-exporter-base` from the catalog (they are transitive, listed only to keep one line) and re-resolve; the direct-dependency set alone is coherent per the dependency scans above.
- **`Otel:Deployment`** is cosmetic. If a reviewer rejects a second kernel config key, hardcode `ofac.deployment` from `OTEL_RESOURCE_ATTRIBUTES` alone and delete that one attribute line from Step 1 — nothing else depends on it.
