/**
 * @ofac/otel — env-only three-signal bootstrap for the team plane.
 *
 * One entry point, {@link startTelemetryFromEnv}: a process configures its
 * telemetry exclusively through the standard OTEL_* environment (no config
 * file, no YAML key). No endpoint in the env (or OTEL_SDK_DISABLED=true)
 * returns a no-op handle synchronously WITHOUT importing any SDK module, so a
 * telemetry-dark process pays no module cost and starts no timers.
 *
 * Traces/metrics register whenever an endpoint is set; the OTLP log bridge is
 * opt-in per process via OTEL_LOGS_EXPORTER, so each process keeps exactly
 * one log pipeline by default and stdout stays the durable artifact.
 * Nothing here ever throws out of the emit path: an unreachable endpoint must
 * not fail a business operation.
 */

// Type-only: erased at compile time, so the dark path stays module-free.
import type { LoggerProvider } from "@opentelemetry/sdk-logs";
import type { Meter, Tracer } from "@opentelemetry/api";

export type { Meter, Tracer } from "@opentelemetry/api";

export interface TelemetryHandle {
  /** False when the env named no endpoint: no SDK, no exporter, no timers. */
  readonly emitting: boolean;
  /** OTLP log bridge is active (OTEL_LOGS_EXPORTER opted in + endpoint set). */
  readonly logsExported: boolean;
  /** Extra fields every log line should carry so stdout lines join the trace. */
  traceFields(): Record<string, string>;
  /** W3C carrier for the ACTIVE span (undefined outside a span / when dark). */
  currentTraceparent(): string | undefined;
  /** Instruments against the registered (or no-op) meter provider. */
  meter(name: string): Meter;
  /** Tracer for hand-opened spans (no-op tracer when dark). */
  tracer(name: string): Tracer;
  /**
   * One span around `run`, parented on the carrier when it parses (the
   * envelope IS the carrier — law 4). Identity wrapper when dark.
   */
  instrument<T>(
    name: string,
    carrier: { traceparent?: string | undefined },
    run: () => Promise<T>,
  ): Promise<T>;
  /** Emits one OTLP log record (no-op unless {@link logsExported}). */
  log(fields: Record<string, unknown>): void;
  /** Resolves once the SDK registration finished (never rejects). */
  ready(): Promise<void>;
  /** Flushes and stops every processor/timer. Safe to call twice. */
  shutdown(): Promise<void>;
}

export interface TelemetryOptions {
  /** Caller's fallback for service.name when OTEL_SERVICE_NAME is unset. */
  serviceName: string;
  /** Meter name the caller instruments against, e.g. "ofac.gateway". */
  meterName: string;
  /** Per-process identity (gatewayId/endpointId-based) -> service.instance.id. */
  instanceId: string;
  /** Caller-owned resource attributes (ofac.team, ofac.endpoint_id, …). */
  resourceAttributes?: Record<string, string> | undefined;
  /** Optional transform applied to log fields before the OTLP bridge emits. */
  logBridge?: (fields: Record<string, unknown>) => Record<string, unknown>;
}

const NOOP_HANDLE: TelemetryHandle = {
  emitting: false,
  logsExported: false,
  traceFields: () => ({}),
  currentTraceparent: () => undefined,
  // Hand-rolled no-op instruments: instrument calls stay syntactically
  // identical on the dark path, and ZERO @opentelemetry module loads.
  meter: () => EMPTY_METER,
  tracer: () => EMPTY_TRACER,
  instrument: (_name, _carrier, run) => run(),
  log: () => undefined,
  ready: async () => undefined,
  shutdown: async () => undefined,
};

const NOOP_INSTRUMENT = {
  add: () => undefined,
  record: () => undefined,
} as unknown as ReturnType<Meter["createCounter"]>;

const EMPTY_METER: Meter = {
  createCounter: () => NOOP_INSTRUMENT,
  createUpDownCounter: () => NOOP_INSTRUMENT,
  createHistogram: () => NOOP_INSTRUMENT,
  createGauge: () => NOOP_INSTRUMENT,
} as unknown as Meter;

const EMPTY_TRACER: Tracer = {
  startSpan: () => ({
    end: () => undefined,
    setAttribute: () => undefined,
    setStatus: () => undefined,
    isRecording: () => false,
    spanContext: () => undefined,
    recordException: () => undefined,
    addEvent: () => undefined,
  }),
  startActiveSpan: (_name: string, _span: unknown, fn?: unknown) => {
    const callback = (fn ?? _span) as (span: never) => unknown;
    return callback(undefined as never);
  },
} as unknown as Tracer;

function envList(name: string): string[] {
  return (process.env[name] ?? "")
    .split(",")
    .map((v) => v.trim())
    .filter((v) => v.length > 0);
}

function sdkDisabled(): boolean {
  return (process.env["OTEL_SDK_DISABLED"] ?? "").trim().toLowerCase() === "true";
}

function logsOptedIn(): boolean {
  return envList("OTEL_LOGS_EXPORTER").some((v) => v.toLowerCase() !== "none");
}

/**
 * Reads the environment and starts (or skips) the three-signal SDK.
 * Synchronous decision + handle; the SDK modules load asynchronously behind
 * the emitting branch only.
 */
export function startTelemetryFromEnv(options: TelemetryOptions): TelemetryHandle {
  const endpoint = (process.env["OTEL_EXPORTER_OTLP_ENDPOINT"] ?? "").trim();
  if (sdkDisabled() || endpoint.length === 0) {
    return NOOP_HANDLE;
  }
  return startEmitting(options, logsOptedIn());
}

interface TracingRegistration {
  traceFields(): Record<string, string>;
  currentTraceparent(): string | undefined;
  meter(name: string): Meter;
  tracer(name: string): Tracer;
  instrument<T>(
    name: string,
    carrier: { traceparent?: string | undefined },
    run: () => Promise<T>,
  ): Promise<T>;
  shutdown(): Promise<void>;
}

function startEmitting(
  options: TelemetryOptions,
  logsExported: boolean,
): TelemetryHandle {
  // Late-bound after registration; every read goes through it so the handle
  // stays valid (and silent) even while the SDK registration is in flight.
  let tracing: TracingRegistration = {
    traceFields: () => ({}),
    currentTraceparent: () => undefined,
    meter: () => EMPTY_METER,
    tracer: () => EMPTY_TRACER,
    instrument: (_name, _carrier, run) => run(),
    shutdown: async () => undefined,
  };
  let emitLog: (fields: Record<string, unknown>) => void = () => undefined;
  let started = Promise.resolve();
  let warnOnce = false;

  started = register(
    options,
    logsExported,
    (registration) => {
      tracing = registration;
    },
    (fn) => {
      emitLog = fn;
    },
    (message) => {
      if (!warnOnce) {
        warnOnce = true;
        console.warn(`ofac-otel: ${message}`);
      }
    },
  ).catch(() => undefined);

  return {
    emitting: true,
    logsExported,
    traceFields: () => tracing.traceFields(),
    currentTraceparent: () => tracing.currentTraceparent(),
    // A forwarding meter: captures taken BEFORE registration still see the
    // real instruments once the SDK lands (calls before that are no-ops).
    meter: (name) => forwardingMeter(() => tracing.meter(name)),
    tracer: (name) => forwardingTracer(() => tracing.tracer(name)),
    instrument: (name, carrier, run) => tracing.instrument(name, carrier, run),
    log: (fields) => emitLog(fields),
    ready: () => started,
    shutdown: async () => {
      await started;
      await tracing.shutdown();
    },
  };
}

// Late-bound instrument facades: the caller may capture meter()/tracer()
// synchronously at construction, while the SDK registers asynchronously.
const NOOP_OBSERVABLE = { addCallback: () => undefined, removeCallback: () => undefined };

// The two structural views used to defer to the real provider once it lands.
type InstrumentFactory = (name: string, options?: unknown) => unknown;
type MeterFactories = Record<string, InstrumentFactory>;
type TracerSurface = { startSpan: (...a: unknown[]) => unknown; startActiveSpan: (...a: unknown[]) => unknown };

function forwardingMeter(get: () => Meter): Meter {
  const factories = (): MeterFactories => get() as unknown as MeterFactories;
  const create = (kind: string): InstrumentFactory => {
    return (name, options) =>
      // The target may still be the no-op facade (registration racing);
      // observable kinds fall back to a no-op callback holder.
      factories()[kind]?.(name, options) ??
      (kind.startsWith("createObservable") ? NOOP_OBSERVABLE : NOOP_INSTRUMENT);
  };
  return {
    createCounter: create("createCounter"),
    createUpDownCounter: create("createUpDownCounter"),
    createHistogram: create("createHistogram"),
    createGauge: create("createGauge"),
    createObservableCounter: create("createObservableCounter"),
    createObservableUpDownCounter: create("createObservableUpDownCounter"),
    createObservableGauge: create("createObservableGauge"),
  } as unknown as Meter;
}

function forwardingTracer(get: () => Tracer): Tracer {
  const surface = (): TracerSurface => get() as unknown as TracerSurface;
  return {
    startSpan: (...args: unknown[]) => surface().startSpan(...args),
    startActiveSpan: (...args: unknown[]) => surface().startActiveSpan(...args),
  } as unknown as Tracer;
}

// Dynamic imports here are the plan's law, not style: a telemetry-dark
// process must pay zero module cost, so every @opentelemetry/* module loads
// only behind the emitting branch (static imports would load for every
// consumer of the package).
async function register(
  options: TelemetryOptions,
  logsExported: boolean,
  setTracing: (r: TracingRegistration) => void,
  setEmitLog: (fn: (fields: Record<string, unknown>) => void) => void,
  warnFirstFailure: (message: string) => void,
): Promise<void> {
  const api = await import("@opentelemetry/api");
  const [
    resourcesPkg,
    tracePkg,
    spanSdk,
    metricsPkg,
    metricsExporter,
    traceExporter,
    contextHooks,
    core,
    logsApi,
  ] = await Promise.all([
    import("@opentelemetry/resources"),
    import("@opentelemetry/sdk-trace-node"),
    import("@opentelemetry/sdk-trace-base"),
    import("@opentelemetry/sdk-metrics"),
    import("@opentelemetry/exporter-metrics-otlp-http"),
    import("@opentelemetry/exporter-trace-otlp-http"),
    import("@opentelemetry/context-async-hooks"),
    import("@opentelemetry/core"),
    import("@opentelemetry/api-logs"),
  ]);
  const logsParts = logsExported
    ? await Promise.all([
        import("@opentelemetry/sdk-logs"),
        import("@opentelemetry/exporter-logs-otlp-http"),
      ])
    : null;

  // Resource: the env detector parses OTEL_RESOURCE_ATTRIBUTES (including
  // ofac.deployment) and OTEL_SERVICE_NAME per spec; service.name falls back
  // to the caller's name only when the env omits it; instance identity is
  // code-side, never env-side (law: the fleet must read as distinct
  // resources). Later merges win, so env beats SDK defaults.
  const fromEnv = resourcesPkg.detectResources({
    detectors: [resourcesPkg.envDetector],
  });
  const ownAttributes: Record<string, string> = {
    "service.instance.id": options.instanceId,
    ...(options.resourceAttributes ?? {}),
  };
  if (fromEnv.attributes["service.name"] === undefined) {
    ownAttributes["service.name"] = options.serviceName;
  }
  const resource = resourcesPkg
    .defaultResource()
    .merge(fromEnv)
    .merge(resourcesPkg.resourceFromAttributes(ownAttributes));

  // Sampler deliberately unset: the SDK default is ParentBased and
  // OTEL_TRACES_SAMPLER(_ARG) stay the operator's dial.
  const provider = new tracePkg.NodeTracerProvider({
    resource,
    spanProcessors: [
      new spanSdk.BatchSpanProcessor(new traceExporter.OTLPTraceExporter()),
    ],
  });
  const contextManager = new contextHooks.AsyncLocalStorageContextManager();
  contextManager.enable();
  api.context.setGlobalContextManager(contextManager);
  api.propagation.setGlobalPropagator(new core.W3CTraceContextPropagator());
  api.trace.setGlobalTracerProvider(provider);

  const reader = new metricsPkg.PeriodicExportingMetricReader({
    exporter: new metricsExporter.OTLPMetricExporter(),
  });
  const meterProvider = new metricsPkg.MeterProvider({ resource, readers: [reader] });
  api.metrics.setGlobalMeterProvider(meterProvider);
  // The caller's meter is a global-provider lookup once registration lands.
  api.metrics.getMeter(options.meterName);

  let loggerProvider: LoggerProvider | null = null;
  if (logsParts !== null) {
    const [logsSdk, logsExporter] = logsParts;
    loggerProvider = new logsSdk.LoggerProvider({
      resource,
      processors: [
        new logsSdk.BatchLogRecordProcessor({
          exporter: new logsExporter.OTLPLogExporter(),
        }),
      ],
    });
    const logger = loggerProvider.getLogger("ofac.otel.bridge");
    setEmitLog((fields) => {
      try {
        const shaped = options.logBridge ? options.logBridge(fields) : fields;
        const attributes: Record<string, string | number | boolean> = {};
        for (const [key, value] of Object.entries(shaped)) {
          if (
            typeof value === "string" ||
            typeof value === "number" ||
            typeof value === "boolean"
          ) {
            attributes[key] = value;
          } else if (value !== undefined && value !== null) {
            attributes[key] = String(value);
          }
        }
        logger.emit({
          severityNumber: logsApi.SeverityNumber.INFO,
          body: String(shaped["message"] ?? ""),
          attributes,
          context: api.context.active(),
        });
      } catch {
        // law: the emit path never fails a business operation
      }
    });
  }

  // First-export probe: one warn line on an unreachable endpoint, then quiet.
  // (The OTLP exporters report failures through diag, never by throwing.)
  void reader
    .forceFlush()
    .then(() => provider.forceFlush())
    .catch(() => {
      warnFirstFailure(
        `first export to ${process.env["OTEL_EXPORTER_OTLP_ENDPOINT"]} failed; telemetry stays quiet`,
      );
    });

  setTracing({
    traceFields() {
      const spanContext = api.trace.getSpanContext(api.context.active());
      if (!spanContext || !api.isSpanContextValid(spanContext)) {
        return {};
      }
      return { traceId: spanContext.traceId, spanId: spanContext.spanId };
    },
    currentTraceparent() {
      const spanContext = api.trace.getSpanContext(api.context.active());
      if (!spanContext || !api.isSpanContextValid(spanContext)) {
        return undefined;
      }
      return `00-${spanContext.traceId}-${spanContext.spanId}-01`;
    },
    meter(name) {
      return api.metrics.getMeter(name);
    },
    tracer(name) {
      return api.trace.getTracer(name);
    },
    async instrument(name, carrier, run) {
      const tracer = api.trace.getTracer("ofac.otel");
      let parent: ReturnType<typeof api.propagation.extract> | undefined;
      if (carrier.traceparent !== undefined) {
        parent = api.propagation.extract(
          api.context.active(),
          { traceparent: carrier.traceparent },
          {
            get: (c, key) => (key === "traceparent" ? c[key] : undefined),
            keys: (c) => Object.keys(c),
          },
        );
      }
      return tracer.startActiveSpan(
        name,
        { kind: api.SpanKind.SERVER },
        parent ?? api.context.active(),
        async (span) => {
          try {
            const result = await run();
            span.setStatus({ code: api.SpanStatusCode.OK });
            return result;
          } catch (error) {
            span.setStatus({
              code: api.SpanStatusCode.ERROR,
              message: error instanceof Error ? error.message : String(error),
            });
            throw error;
          } finally {
            span.end();
          }
        },
      );
    },
    async shutdown() {
      // Never-throw law: an unreachable endpoint must not fail a business
      // operation — and stop() IS one. A failed flush is the warn-once line.
      const failed = (label: string) => (err: unknown): void => {
        warnFirstFailure(
          `${label} during shutdown (${err instanceof Error ? err.message : String(err)}); telemetry stays quiet`,
        );
      };
      const settle = async (what: Promise<unknown>, label: string): Promise<void> => {
        await what.catch(failed(label));
      };
      await settle(reader.shutdown(), "metric flush");
      await settle(meterProvider.shutdown(), "meter provider");
      await settle(provider.shutdown(), "span flush");
      if (loggerProvider !== null) {
        await settle(loggerProvider.shutdown(), "log flush");
      }
      try {
        contextManager.disable();
      } catch (err) {
        failed("context manager")(err);
      }
    },
  });
}
