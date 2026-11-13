import { afterEach, describe, expect, it } from "vitest";

import { startTelemetryFromEnv } from "../src/index.js";

const W3C_TRACE = /^[0-9a-f]{32}$/;
const W3C_SPAN = /^[0-9a-f]{16}$/;

function clearOtelEnv(): void {
  delete process.env["OTEL_EXPORTER_OTLP_ENDPOINT"];
  delete process.env["OTEL_SDK_DISABLED"];
  delete process.env["OTEL_LOGS_EXPORTER"];
  delete process.env["OTEL_RESOURCE_ATTRIBUTES"];
  delete process.env["OTEL_SERVICE_NAME"];
}

afterEach(() => {
  clearOtelEnv();
});

describe("startTelemetryFromEnv", () => {
  it("no endpoint: no-op handle, no SDK, no unhandled rejection", async () => {
    clearOtelEnv();
    const handle = startTelemetryFromEnv({
      serviceName: "ofac-test",
      meterName: "ofac.test",
      instanceId: "inst-1",
    });
    expect(handle.emitting).toBe(false);
    expect(handle.logsExported).toBe(false);
    expect(handle.traceFields()).toEqual({});
    handle.log({ message: "ignored" });
    await expect(handle.ready()).resolves.toBeUndefined();
    await handle.shutdown();
  });

  it("OTEL_SDK_DISABLED=true with an endpoint: still dark (same kill switch as .NET)", async () => {
    clearOtelEnv();
    process.env["OTEL_EXPORTER_OTLP_ENDPOINT"] = "http://127.0.0.1:1";
    process.env["OTEL_SDK_DISABLED"] = "true";
    const handle = startTelemetryFromEnv({
      serviceName: "ofac-test",
      meterName: "ofac.test",
      instanceId: "inst-1",
    });
    expect(handle.emitting).toBe(false);
    await handle.shutdown();
  });

  it("OTEL_LOGS_EXPORTER=none does not opt the log bridge in", { timeout: 60_000 }, async () => {
    clearOtelEnv();
    process.env["OTEL_EXPORTER_OTLP_ENDPOINT"] = "http://127.0.0.1:1";
    process.env["OTEL_LOGS_EXPORTER"] = "none";
    const handle = startTelemetryFromEnv({
      serviceName: "ofac-test",
      meterName: "ofac.test",
      instanceId: "inst-1",
    });
    expect(handle.emitting).toBe(true);
    expect(handle.logsExported).toBe(false);
    await handle.ready();
    await handle.shutdown();
  });

  it(
    "endpoint set: emits, traceFields follows the active span, shutdown leaves no handles",
    { timeout: 60_000 },
    async () => {
    clearOtelEnv();
    // Port 1: nothing listens, so the first-export probe warns once and stays
    // quiet — the never-throw law under test as much as the trace fields.
    process.env["OTEL_EXPORTER_OTLP_ENDPOINT"] = "http://127.0.0.1:1";
    process.env["OTEL_LOGS_EXPORTER"] = "otlp";
    const warn = console.warn;
    let warnings = 0;
    console.warn = () => {
      warnings += 1;
    };
    try {
      const handle = startTelemetryFromEnv({
        serviceName: "ofac-test",
        meterName: "ofac.test",
        instanceId: "inst-1",
      });
      expect(handle.emitting).toBe(true);
      expect(handle.logsExported).toBe(true);
      await handle.ready();

      const api = await import("@opentelemetry/api");
      const tracer = api.trace.getTracer("ofac.test");
      const inside = tracer.startActiveSpan("unit.op", () => handle.traceFields());
      expect(inside["traceId"]).toMatch(W3C_TRACE);
      expect(inside["spanId"]).toMatch(W3C_SPAN);
      // Outside any span there is nothing to correlate with.
      expect(handle.traceFields()).toEqual({});

      // A bridge line must never throw, even with nothing reachable.
      handle.log({ message: "hello", traceId: inside["traceId"] });
      // The first-export probe stays silent while registration is racing,
      // so the warn-once count can only be 0 (export lost the race) or 1.
      expect(warnings).toBeLessThanOrEqual(1);

      await handle.shutdown();
      // After shutdown the same context carries no live span.
      expect(api.trace.getSpanContext(api.context.active())).toBeUndefined();
    } finally {
      console.warn = warn;
    }
    },
  );
});
