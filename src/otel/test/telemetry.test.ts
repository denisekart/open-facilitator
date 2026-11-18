/**
 * Deterministic env-matrix coverage for startTelemetryFromEnv.
 *
 * The three OTLP exporter packages (and the log SDK's provider) are replaced
 * by in-process stubs, so no request ever leaves the process: the exporter
 * wiring, the resource builder, the first-export warn-once probe and every
 * shutdown/flush leg are driven through the stubs' failure switches instead
 * of a port-1 black-hole and its retry timeouts. Every settle here is a
 * microtask drain, never a wall-clock wait.
 */
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import type { ExportResult } from "@opentelemetry/core";

const W3C_TRACE = /^[0-9a-f]{32}$/;
const W3C_SPAN = /^[0-9a-f]{16}$/;
const ENDPOINT = "http://collector.example:4318";

// Structural views of what the stubs capture out of the SDK — not external
// input, just the shapes the tests read back.
interface CapturedRecord {
  severityNumber: number;
  body: string;
  attributes: Record<string, unknown>;
}
interface CapturedMetrics {
  resource: { attributes: Record<string, string> };
  scopeMetrics: { scope: { name: string } }[];
}
interface FinishedSpan {
  name: string;
  spanContext(): { traceId: string; spanId: string };
  status: { code: number };
  attributes: Record<string, unknown>;
}

const state = vi.hoisted(() => ({
  spans: [] as unknown[],
  metrics: [] as unknown[],
  logRecords: [] as unknown[],
  exporterCtorCalls: 0,
  logExporterCtorCalls: 0,
  failTraceExport: false,
  failTraceShutdown: false,
  failMetricsExport: false,
  failReaderFlush: false,
  failLogShutdown: false,
  failProviderCtor: false,
  failDisable: false,
})) as {
  spans: unknown[];
  metrics: unknown[];
  logRecords: unknown[];
  exporterCtorCalls: number;
  logExporterCtorCalls: number;
  failTraceExport: boolean;
  failTraceShutdown: boolean;
  failMetricsExport: boolean;
  failReaderFlush: boolean;
  failLogShutdown: boolean;
  failProviderCtor: boolean;
  failDisable: boolean;
};

function resetStubState(): void {
  state.spans = [];
  state.metrics = [];
  state.logRecords = [];
  state.exporterCtorCalls = 0;
  state.logExporterCtorCalls = 0;
  state.failTraceExport = false;
  state.failTraceShutdown = false;
  state.failMetricsExport = false;
  state.failReaderFlush = false;
  state.failLogShutdown = false;
  state.failProviderCtor = false;
  state.failDisable = false;
}

// vi.mock factories are hoisted above the imports; they close over `state`,
// whose vi.hoisted initializer also runs before any module body.
vi.mock("@opentelemetry/exporter-trace-otlp-http", () => ({
  OTLPTraceExporter: class {
    constructor() {
      state.exporterCtorCalls += 1;
    }
    export(spans: unknown[], cb: (r: ExportResult) => void): void {
      state.spans.push(...spans);
      cb(
        state.failTraceExport
          ? { code: 1, error: new Error("stub span export refused") }
          : { code: 0 },
      );
    }
    shutdown(): Promise<void> {
      return state.failTraceShutdown
        ? Promise.reject(new Error("span exporter shutdown exploded"))
        : Promise.resolve();
    }
    forceFlush(): Promise<void> {
      return Promise.resolve();
    }
  },
}));

vi.mock("@opentelemetry/exporter-metrics-otlp-http", () => ({
  OTLPMetricExporter: class {
    constructor() {
      state.exporterCtorCalls += 1;
    }
    export(metrics: unknown, cb: (r: ExportResult) => void): void {
      state.metrics.push(metrics);
      cb(
        state.failMetricsExport
          ? { code: 1, error: new Error("stub metric export refused") }
          : { code: 0 },
      );
    }
    shutdown(): Promise<void> {
      return Promise.resolve();
    }
    // The reader's onForceFlush ends in exporter.forceFlush(): this is the
    // deterministic lever for "first export failed".
    forceFlush(): Promise<void> {
      return state.failReaderFlush
        ? Promise.reject(new Error("collector unreachable (stub)"))
        : Promise.resolve();
    }
  },
}));

vi.mock("@opentelemetry/exporter-logs-otlp-http", () => ({
  OTLPLogExporter: class {
    constructor() {
      state.exporterCtorCalls += 1;
      state.logExporterCtorCalls += 1;
    }
    export(_logs: unknown, cb: (r: ExportResult) => void): void {
      cb({ code: 0 });
    }
    shutdown(): Promise<void> {
      return state.failLogShutdown
        ? Promise.reject(new Error("log exporter shutdown exploded"))
        : Promise.resolve();
    }
    forceFlush(): Promise<void> {
      return Promise.resolve();
    }
  },
}));

// The provider is replaced with a capture stub so the bridge's emit lands in
// state instead of a batch processor racing an HTTP timeout.
vi.mock("@opentelemetry/sdk-logs", async (importOriginal) => {
  const real = await importOriginal<typeof import("@opentelemetry/sdk-logs")>();
  return {
    ...real,
    LoggerProvider: class {
      getLogger(): { emit(record: Record<string, unknown>): void } {
        return {
          emit: (record) => {
            state.logRecords.push(record);
          },
        };
      }
      shutdown(): Promise<void> {
        return Promise.resolve();
      }
    },
  };
});

// Deterministic failure lever for the "registration blew up" leg: a throwing
// provider constructor rejects register() on demand (mock factories are
// cached per file, so an import-time lever would only fire once).
vi.mock("@opentelemetry/sdk-trace-node", async (importOriginal) => {
  const real = await importOriginal<typeof import("@opentelemetry/sdk-trace-node")>();
  return {
    ...real,
    NodeTracerProvider: class extends real.NodeTracerProvider {
      constructor(config?: ConstructorParameters<typeof real.NodeTracerProvider>[0]) {
        if (state.failProviderCtor) {
          throw new Error("provider ctor exploded");
        }
        super(config);
      }
    },
  };
});

// Context propagation stays the real thing; only disable() gets a failure
// switch so the shutdown leg's context-manager catch is reachable.
vi.mock("@opentelemetry/context-async-hooks", async (importOriginal) => {
  const real = await importOriginal<typeof import("@opentelemetry/context-async-hooks")>();
  return {
    ...real,
    AsyncLocalStorageContextManager: class extends real.AsyncLocalStorageContextManager {
      override disable() {
        if (state.failDisable) {
          throw new Error("context manager disable exploded");
        }
        return super.disable();
      }
    },
  };
});

// Import-failure lever for the "registration blew up" leg lives in the
// sdk-trace-node mock above (a throwing provider ctor).

type Module = typeof import("../src/index.js");
type Options = import("../src/index.js").TelemetryOptions;

const BASE: Options = {
  serviceName: "ofac-test",
  meterName: "ofac.test",
  instanceId: "inst-1",
};

function clearOtelEnv(): void {
  for (const key of [
    "OTEL_EXPORTER_OTLP_ENDPOINT",
    "OTEL_SDK_DISABLED",
    "OTEL_LOGS_EXPORTER",
    "OTEL_RESOURCE_ATTRIBUTES",
    "OTEL_SERVICE_NAME",
  ]) {
    delete process.env[key];
  }
  vi.unstubAllEnvs();
}

/** Fresh module graph so the next import re-reads the env. */
async function freshModule(): Promise<Module> {
  vi.resetModules();
  return await import("../src/index.js");
}

/**
 * The probe and every shutdown leg are microtask-only once the stub exporters
 * answer synchronously, so draining the queue settles them — no clock waits.
 */
async function drain(): Promise<void> {
  for (let i = 0; i < 64; i += 1) {
    await Promise.resolve();
  }
}

function captureWarnings(): string[] {
  const warnings: string[] = [];
  vi.spyOn(console, "warn").mockImplementation((...args: unknown[]) => {
    warnings.push(args.map(String).join(" "));
  });
  return warnings;
}

function lastMetric(): CapturedMetrics {
  const captured = state.metrics.at(-1);
  if (captured === undefined) {
    throw new Error("expected the stub metric exporter to have been called");
  }
  return captured as CapturedMetrics;
}

beforeEach(() => {
  resetStubState();
  clearOtelEnv();
});

afterEach(async () => {
  clearOtelEnv();
  vi.restoreAllMocks();
  // The API global registry lives on globalThis and survives resetModules:
  // without a full disable, the next test's registrations lose the
  // register-once race and see the previous (shut-down) providers. The stub's
  // failure levers must be off before this teardown disable runs.
  state.failDisable = false;
  const api = await import("@opentelemetry/api");
  api.context.disable();
  api.trace.disable();
  api.metrics.disable();
  api.propagation.disable();
});

describe("startTelemetryFromEnv: dark env", () => {
  it("no endpoint: shared DARK_TELEMETRY handle, every member a no-op", async () => {
    const mod = await freshModule();
    const handle = mod.startTelemetryFromEnv(BASE);
    expect(handle).toBe(mod.DARK_TELEMETRY);
    expect(handle.emitting).toBe(false);
    expect(handle.logsExported).toBe(false);
    expect(handle.traceFields()).toEqual({});
    expect(handle.currentTraceparent()).toBeUndefined();
    handle.log({ message: "ignored" });
    // Hand-rolled no-op instruments: syntactically identical calls, no SDK.
    const meter = handle.meter("ofac.test");
    meter.createCounter("c").add(1);
    meter.createUpDownCounter("u").add(1);
    meter.createHistogram("h").record(1);
    meter.createGauge("g").record(1);
    const tracer = handle.tracer("ofac.test");
    const darkSpan = tracer.startSpan("s");
    darkSpan.setAttribute("k", 1);
    darkSpan.setStatus({ code: 1 });
    darkSpan.addEvent("e");
    darkSpan.recordException(new Error("ignored"));
    expect(darkSpan.isRecording()).toBe(false);
    expect(darkSpan.spanContext()).toBeUndefined();
    darkSpan.end();
    // 2-arg and 3-arg startActiveSpan both reach the callback.
    const two = await tracer.startActiveSpan("a", () => Promise.resolve("two"));
    const three = await tracer.startActiveSpan("b", {}, () => Promise.resolve("three"));
    expect([two, three]).toEqual(["two", "three"]);
    await expect(handle.instrument("op", {}, async () => "identity")).resolves.toBe("identity");
    await expect(handle.ready()).resolves.toBeUndefined();
    await handle.shutdown();
    await handle.shutdown(); // idempotent
  });

  it("blank endpoint and OTEL_SDK_DISABLED variants stay dark (same kill switch as .NET)", async () => {
    const mod = await freshModule();
    vi.stubEnv("OTEL_EXPORTER_OTLP_ENDPOINT", "   ");
    expect(mod.startTelemetryFromEnv(BASE).emitting).toBe(false);

    vi.stubEnv("OTEL_EXPORTER_OTLP_ENDPOINT", ENDPOINT);
    for (const v of ["true", " TRUE ", "True"]) {
      vi.stubEnv("OTEL_SDK_DISABLED", v);
      expect(mod.startTelemetryFromEnv(BASE).emitting).toBe(false);
    }
    vi.stubEnv("OTEL_SDK_DISABLED", "false");
    const emitting = mod.startTelemetryFromEnv(BASE);
    expect(emitting.emitting).toBe(true);
    await emitting.ready();
    await emitting.shutdown();
  });
});

describe("startTelemetryFromEnv: log-bridge opt-in", () => {
  it("unset or 'none' (in any list position) keeps the bridge off", { timeout: 60_000 }, async () => {
    const mod = await freshModule();
    vi.stubEnv("OTEL_EXPORTER_OTLP_ENDPOINT", ENDPOINT);
    for (const v of ["", "none", " none ,NONE"]) {
      vi.stubEnv("OTEL_LOGS_EXPORTER", v);
      const handle = mod.startTelemetryFromEnv(BASE);
      expect(handle.emitting).toBe(true);
      expect(handle.logsExported).toBe(false);
      handle.log({ message: "still a no-op" });
      await handle.ready();
      await handle.shutdown();
    }
    // Three registrations, traces + metrics only: no log exporter constructed.
    expect(state.logExporterCtorCalls).toBe(0);
    expect(state.exporterCtorCalls).toBe(6);
  });

  it("one non-none entry opts the bridge in; lines carry shaped attributes", async () => {
    const logsApi = await import("@opentelemetry/api-logs");
    const mod = await freshModule();
    vi.stubEnv("OTEL_EXPORTER_OTLP_ENDPOINT", ENDPOINT);
    vi.stubEnv("OTEL_LOGS_EXPORTER", " otlp , console ");
    const handle = mod.startTelemetryFromEnv({
      ...BASE,
      logBridge: (fields) => ({ ...fields, shaped: true }),
    });
    expect(handle.logsExported).toBe(true);
    handle.log({ message: "before ready" }); // still the noop sink
    await handle.ready();
    handle.log({ message: "hello", n: 1, b: true, obj: { a: 1 }, nil: null, und: undefined });
    expect(state.logRecords).toHaveLength(1);
    const record = state.logRecords[0] as CapturedRecord;
    expect(record.severityNumber).toBe(logsApi.SeverityNumber.INFO);
    expect(record.body).toBe("hello");
    expect(record.attributes).toEqual({
      message: "hello",
      n: 1,
      b: true,
      obj: "[object Object]", // non-primitives are stringified
      shaped: true,
    });
    expect("nil" in record.attributes).toBe(false);
    expect("und" in record.attributes).toBe(false);
    // A bridge that explodes must not fail the business operation.
    const boom = mod.startTelemetryFromEnv({
      ...BASE,
      logBridge: () => {
        throw new Error("bridge exploded");
      },
    });
    await boom.ready();
    expect(() => boom.log({ message: "survives" })).not.toThrow();
    await boom.shutdown();
    await handle.shutdown();
  });
});

describe("startTelemetryFromEnv: emitting env", () => {
  it("resource: env beats caller and identity stays code-side", async () => {
    const mod = await freshModule();
    vi.stubEnv("OTEL_EXPORTER_OTLP_ENDPOINT", ENDPOINT);
    vi.stubEnv("OTEL_RESOURCE_ATTRIBUTES", "ofac.team=blue,deployment.environment=stage");
    vi.stubEnv("OTEL_SERVICE_NAME", "env-svc");
    const handle = mod.startTelemetryFromEnv({
      ...BASE,
      resourceAttributes: { "ofac.endpoint_id": "e-1" },
    });
    await handle.ready();
    handle.meter("ofac.test").createCounter("warmed").add(1);
    await handle.shutdown();
    const attrs = lastMetric().resource.attributes;
    expect(attrs["service.name"]).toBe("env-svc"); // env wins over options
    expect(attrs["service.instance.id"]).toBe("inst-1"); // never env-side
    expect(attrs["ofac.team"]).toBe("blue");
    expect(attrs["ofac.endpoint_id"]).toBe("e-1");
    expect(attrs["deployment.environment"]).toBe("stage");
  });

  it("service.name falls back to the caller's name when the env omits it", async () => {
    const mod = await freshModule();
    vi.stubEnv("OTEL_EXPORTER_OTLP_ENDPOINT", ENDPOINT);
    const h2 = mod.startTelemetryFromEnv(BASE);
    await h2.ready();
    h2.meter("ofac.test").createCounter("warmed").add(1);
    await h2.shutdown();
    expect(lastMetric().resource.attributes["service.name"]).toBe("ofac-test");
  });

  it("carrier join, traceFields, carriers and instrument outcome labelling", async () => {
    const mod = await freshModule();
    vi.stubEnv("OTEL_EXPORTER_OTLP_ENDPOINT", ENDPOINT);
    const handle = mod.startTelemetryFromEnv(BASE);
    const traceId = "0f1e2d3c4b5a69788796a5b4c3d2e1f0";
    const carrierSpan = "4d0c0b0a09080706";
    // Before registration: identity wrapper, no fields, no carrier.
    await expect(handle.instrument("early", {}, async () => 7)).resolves.toBe(7);
    expect(handle.traceFields()).toEqual({});
    expect(handle.currentTraceparent()).toBeUndefined();
    await handle.ready();

    const inside = await handle.instrument(
      "link.deliver",
      { traceparent: `00-${traceId}-${carrierSpan}-01` },
      async () => ({
        fields: handle.traceFields(),
        carrier: handle.currentTraceparent(),
      }),
      { "ofac.kind": "sync" },
    );
    // The join: same trace, a NEW span (the wrapper is the child).
    expect(inside.fields["traceId"]).toBe(traceId);
    expect(inside.fields["spanId"]).not.toBe(carrierSpan);
    expect(inside.carrier).toBe(`00-${traceId}-${inside.fields["spanId"]}-01`);
    expect(handle.traceFields()).toEqual({});
    expect(handle.currentTraceparent()).toBeUndefined();

    // Malformed carrier: a fresh root trace, never a crash.
    const root = await handle.instrument("garbage", { traceparent: "not-a-carrier" }, async () =>
      handle.traceFields(),
    );
    expect(root["traceId"]).toMatch(W3C_TRACE);

    // A failed run ERRORs its own span; a late attributes fn tags the outcome;
    // a throwing attributes fn cannot fail the flush.
    await expect(
      handle.instrument(
        "op.fail",
        {},
        async () => {
          throw new Error("boom");
        },
        () => ({ "ofac.outcome": "error" }),
      ),
    ).rejects.toThrow("boom");
    await handle.instrument(
      "op.badattrs",
      {},
      async () => "ok",
      () => {
        throw new Error("attrs exploded");
      },
    );

    // Real instruments after registration, forwarded through the facades.
    const tracer = handle.tracer("ofac.test");
    tracer.startSpan("hand-opened").end();
    await new Promise<void>((resolve) => {
      tracer.startActiveSpan("active", {}, (span) => {
        span.end(); // the SDK tracer leaves ending to the callback
        resolve();
      });
    });
    await handle.shutdown();

    const finished = state.spans as FinishedSpan[];
    const deliver = finished.find((s) => s.name === "link.deliver");
    expect(deliver?.spanContext().traceId).toBe(traceId);
    expect(deliver?.attributes["ofac.kind"]).toBe("sync");
    expect(deliver?.status.code).toBe(1); // OK
    const failed = finished.find((s) => s.name === "op.fail");
    expect(failed?.status.code).toBe(2); // ERROR
    expect(failed?.attributes["ofac.outcome"]).toBe("error");
    expect(finished.some((s) => s.name === "op.badattrs")).toBe(true);
    expect(finished.some((s) => s.name === "hand-opened")).toBe(true);
    expect(finished.some((s) => s.name === "active")).toBe(true);
    const rootSpan = finished.find((s) => s.name === "garbage");
    expect(rootSpan?.spanContext().spanId).toMatch(W3C_SPAN);
  });

  it("forwarding meter: pre-registration captures materialize once the SDK lands", async () => {
    const mod = await freshModule();
    vi.stubEnv("OTEL_EXPORTER_OTLP_ENDPOINT", ENDPOINT);
    const handle = mod.startTelemetryFromEnv(BASE);
    const meter = handle.meter("ofac.test"); // captured BEFORE registration
    const counter = meter.createCounter("late-bind");
    const updown = meter.createUpDownCounter("late-bind-updown");
    const histogram = meter.createHistogram("late-bind-hist");
    const gauge = meter.createGauge("late-bind-gauge");
    const observable = meter.createObservableCounter("late-bind-obs");
    const updownObs = meter.createObservableUpDownCounter("late-bind-updown-obs");
    const gaugeObs = meter.createObservableGauge("late-bind-gauge-obs");
    let observed = 0;
    observable.addCallback((o) => {
      observed += 1;
      o.observe(5);
    });
    const second = (o: { observe: (v: number) => void }) => o.observe(1);
    observable.addCallback(second);
    updownObs.addCallback((o) => o.observe(2));
    gaugeObs.addCallback((o) => o.observe(3));
    // Pre-registration calls are no-ops, not throws.
    counter.add(1);
    updown.add(1);
    histogram.record(1);
    gauge.record(1);
    await handle.ready();
    // Post-registration calls hit the real instruments (materialize on call).
    counter.add(1);
    updown.add(1);
    histogram.record(1);
    gauge.record(1);
    observable.removeCallback(second);
    // An independent post-registration meter sees the global provider too.
    handle.meter("other").createCounter("direct").add(2);
    await handle.shutdown();
    expect(observed).toBe(1); // bound at materialization, fired by the shutdown collection
    const payload = lastMetric();
    expect(payload.scopeMetrics.some((s) => s.scope.name === "ofac.test")).toBe(true);
    const wire = JSON.stringify(payload);
    expect(wire).toContain("late-bind");
    expect(wire).toContain("direct");
  });

  it("first-export failure warns exactly once and names the endpoint", async () => {
    state.failReaderFlush = true;
    const warnings = captureWarnings();
    const mod = await freshModule();
    vi.stubEnv("OTEL_EXPORTER_OTLP_ENDPOINT", ENDPOINT);
    const handle = mod.startTelemetryFromEnv(BASE);
    await handle.ready();
    await drain();
    expect(warnings).toHaveLength(1);
    expect(warnings[0]).toContain(`first export to ${ENDPOINT} failed`);
    expect(warnings[0]).toContain("telemetry stays quiet");
    await handle.shutdown();
    // One warn line per handle, then silence: nothing more after shutdown.
    expect(warnings).toHaveLength(1);
  });

  it("failed flush legs warn once each with their label and never throw", async () => {
    state.failReaderFlush = true; // probe AND metric-flush leg fail
    state.failTraceShutdown = true; // span-flush leg fails
    state.failLogShutdown = true; // log leg only warns if warnOnce is spent
    state.failMetricsExport = true; // collection leg fails too
    const warnings = captureWarnings();
    const mod = await freshModule();
    vi.stubEnv("OTEL_EXPORTER_OTLP_ENDPOINT", ENDPOINT);
    vi.stubEnv("OTEL_LOGS_EXPORTER", "otlp");
    const handle = mod.startTelemetryFromEnv(BASE);
    await handle.ready();
    handle.meter("ofac.test").createCounter("warmed").add(1);
    await handle.shutdown(); // never throws out of shutdown either
    // warn-once: the probe fired first, so every later leg is silent.
    expect(warnings).toHaveLength(1);
    expect(warnings[0]).toContain("first export");
  });

  it("first failure during shutdown warns with its leg label", async () => {
    state.failTraceShutdown = true;
    const warnings = captureWarnings();
    const mod = await freshModule();
    vi.stubEnv("OTEL_EXPORTER_OTLP_ENDPOINT", ENDPOINT);
    const handle = mod.startTelemetryFromEnv(BASE);
    await handle.ready();
    await handle.shutdown();
    expect(warnings).toHaveLength(1);
    expect(warnings[0]).toContain("ofac-otel:");
    expect(warnings[0]).toMatch(/span flush during shutdown \(.*\); telemetry stays quiet/);
  });

  it("a throwing context-manager disable warns the never-throw law line", async () => {
    state.failDisable = true;
    const warnings = captureWarnings();
    const mod = await freshModule();
    vi.stubEnv("OTEL_EXPORTER_OTLP_ENDPOINT", ENDPOINT);
    const handle = mod.startTelemetryFromEnv(BASE);
    await handle.ready();
    await handle.shutdown();
    expect(warnings).toHaveLength(1);
    expect(warnings[0]).toContain(
      "context manager during shutdown (context manager disable exploded)",
    );
  });

  it("registration failure: ready resolves, handle degrades to the dark shape", async () => {
    state.failProviderCtor = true;
    const mod = await freshModule();
    vi.stubEnv("OTEL_EXPORTER_OTLP_ENDPOINT", ENDPOINT);
    const handle = mod.startTelemetryFromEnv(BASE);
    expect(handle.emitting).toBe(true); // optimistic until proven otherwise
    await expect(handle.ready()).resolves.toBeUndefined(); // never rejects
    expect(handle.traceFields()).toEqual({});
    expect(handle.currentTraceparent()).toBeUndefined();
    handle.log({ message: "dropped" });
    await expect(handle.instrument("op", {}, async () => "identity")).resolves.toBe("identity");
    // The pre-registration meter facade still no-ops forever.
    handle.meter("ofac.test").createCounter("c").add(1);
    // ...and so does the tracer facade: forwarding to the EMPTY_TRACER.
    expect(handle.tracer("ofac.test").startSpan("s").isRecording()).toBe(false);
    await handle.shutdown(); // the initial noop shutdown completes
  });
});
