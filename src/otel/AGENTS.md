# Telemetry (`@ofac/otel`)

Env-only three-signal bootstrap for every team-plane process (gateway,
workers, adapters, launcher children). One entry point,
`startTelemetryFromEnv`; the process's `OTEL_*` environment IS the config.

## Invariants invisible in code

- **Dark must be free.** No `OTEL_EXPORTER_OTLP_ENDPOINT` (or
  `OTEL_SDK_DISABLED=true`) returns the shared `DARK_TELEMETRY` handle
  SYNCHRONOUSLY without importing a single SDK module: no timers, no
  context hooks, no exporter sockets. Every SDK import lives behind the
  env check — keep them dynamic.
- **The emit path never throws.** Telemetry must not fail a business
  operation: export failures warn once (`first export … stays quiet`) and
  go silent; `log`/`instrument` swallow their own errors. `ready()` never
  rejects; `shutdown()` is idempotent.
- **Logs are opt-in per process.** Traces/metrics register whenever an
  endpoint is set; the OTLP log bridge requires `OTEL_LOGS_EXPORTER` too,
  so each process keeps exactly ONE log pipeline by default and stdout
  stays the durable artifact.
- **The envelope is the carrier.** `instrument(name, carrier, run)` parents
  on `carrier.traceparent` when it parses — a team-plane span joins the
  kernel's trace by W3C re-parenting (same trace id, new parent id), never
  by inventing ids. Downstream envelopes carry the ACTIVE span's carrier
  (`currentTraceparent`), so an instrumented plane legitimately rewrites
  the carrier while keeping the trace id.
- **Per-signal metrics endpoint is spec-shaped.**
  `OTEL_EXPORTER_OTLP_METRICS_ENDPOINT` carries a COMPLETE URL (the JS
  exporter uses it verbatim — no path appended). The .NET exporter reads
  this var only code-side (see KernelServiceCollection); keep the compose
  overlays pointing both planes at the collector's full per-signal URL
  (path included).
- **Low cardinality, machine labels only.** Counter labels are finite
  state words; ids/tokens/prose ride spans and log attributes, never
  metric labels (the sink dump is PII-grepped by `reference/checks/verify.sh`).

## Layout

`src/index.ts` — the whole package: dark handle, env parse, dynamic SDK
registration, resource builder, log bridge, `instrument`. `test/` pins the
dark contract (module-free), the carrier join, and the resource identity.
