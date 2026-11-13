// Gateway telemetry: the @ofac/otel bootstrap wired into the runtime.
// Every behavior is env-driven (law 2: no YAML key, no config field):
// `startTelemetryFromEnv` decides synchronously — no OTEL_EXPORTER_OTLP_ENDPOINT
// (or OTEL_SDK_DISABLED=true) means a dark handle whose instrument() is the
// identity wrapper, so unit tests and telemetry-dark boxes pay nothing.
//
// The instrument hook is what gives every delivery a `link.deliver` span
// parented on the inbound envelope carrier (workspace-link owns the seam;
// this file owns the SDK). The outbound carriers (approval publish + child
// start frames) then carry the ACTIVE span, so the kernel-side ingress join
// hangs off the gateway's own trace.
//
// Metrics keep the banned-label law: workId/assignmentId/deliveryId/
// childWorkspaceId/path NEVER ride as labels. The fleet gauge is observable
// (sampled at collection time, label = state only); the delivery counters are
// deltas of the LinkStats counters the SDK already maintains.
import { startTelemetryFromEnv } from "@ofac/otel";
import type { TelemetryHandle } from "@ofac/otel";
import type { LinkStats } from "@ofac/workspace-link";

export interface GatewayTelemetryStatus {
  emitting: boolean;
  logsExported: boolean;
  endpointSet: boolean;
}

interface Counters {
  delivered: { accepted: number; rejected: number; unsupported: number };
  duplicates: number;
  reconnects: number;
  fleetLive: number;
  fleetOffline: number;
}

interface Instruments {
  delivered: { add(n: number, attrs: { outcome: string }): void };
  duplicates: { add(n: number): void };
  reconnects: { add(n: number): void };
  decisions: { add(n: number, attrs: { decision: string; reason: string }): void };
}

/** Everything the runtime needs from telemetry, dark-path safe. */
export class GatewayTelemetry {
  readonly handle: TelemetryHandle;
  private readonly instruments: Instruments | null;
  private last: Counters | null = null;
  private fleetCounts = { live: 0, offline: 0 };

  constructor(handle: TelemetryHandle) {
    this.handle = handle;
    if (!handle.emitting) {
      this.instruments = null;
      return;
    }
    const meter = handle.meter("ofac.gateway");
    const delivered = meter.createCounter("ofac.link.delivered");
    const duplicates = meter.createCounter("ofac.link.duplicates");
    const reconnects = meter.createCounter("ofac.link.reconnects");
    const decisions = meter.createCounter("ofac.audit.decisions");
    const fleet = meter.createObservableUpDownCounter("ofac.child.fleet");
    fleet.addCallback((result) => {
      result.observe(this.fleetCounts.live, { state: "live" });
      result.observe(this.fleetCounts.offline, { state: "offline" });
    });
    this.instruments = {
      delivered: delivered as unknown as Instruments["delivered"],
      duplicates: duplicates as unknown as Instruments["duplicates"],
      reconnects: reconnects as unknown as Instruments["reconnects"],
      decisions: decisions as unknown as Instruments["decisions"],
    };
  }

  /** The workspace-link seam: one `link.deliver` span per delivery. */
  instrument<T>(
    name: string,
    carrier: { traceparent?: string | undefined },
    run: () => Promise<T>,
  ): Promise<T> {
    return this.handle.instrument(name, carrier, run);
  }

  /** `{traceId}` when a span is active — spread into every audit line. */
  auditTraceFields(): Record<string, string> {
    return this.handle.traceFields();
  }

  /** The outbound carrier: the ACTIVE span, else the envelope's own value. */
  carrierFor(fallback: string | undefined): string | undefined {
    return this.handle.currentTraceparent() ?? fallback;
  }

  /** One decision audit line happened (finite labels: machine codes only). */
  decision(decision: "allow" | "deny" | "confirm", reason: string): void {
    this.instruments?.decisions.add(1, { decision, reason });
  }

  /** Log bridge line (stdout stays the durable artifact; this is additive). */
  log(fields: Record<string, unknown>): void {
    this.handle.log(fields);
  }

  /** Deltas since the previous observation of the runtime's own counters. */
  observe(stats: LinkStats, children: { state: string }[]): void {
    const outcomeOf = (kind: "accepted" | "rejected" | "unsupported"): number =>
      kind === "accepted"
        ? stats.processed - (this.last?.delivered.accepted ?? 0)
        : kind === "rejected"
          ? stats.failed - (this.last?.delivered.rejected ?? 0)
          : stats.unsupported - (this.last?.delivered.unsupported ?? 0);
    const live = children.filter((c) => c.state === "connected").length;
    const offline = children.length - live;
    this.fleetCounts = { live, offline };
    if (this.instruments !== null) {
      const accepted = outcomeOf("accepted");
      const rejected = outcomeOf("rejected");
      const unsupported = outcomeOf("unsupported");
      if (accepted > 0) this.instruments.delivered.add(accepted, { outcome: "accepted" });
      if (rejected > 0) this.instruments.delivered.add(rejected, { outcome: "rejected" });
      if (unsupported > 0) {
        this.instruments.delivered.add(unsupported, { outcome: "unsupported" });
      }
      const dup = stats.duplicate - (this.last?.duplicates ?? 0);
      if (dup > 0) this.instruments.duplicates.add(dup);
      const rec = stats.reconnects - (this.last?.reconnects ?? 0);
      if (rec > 0) this.instruments.reconnects.add(rec);
    }
    this.last = {
      delivered: {
        accepted: stats.processed,
        rejected: stats.failed,
        unsupported: stats.unsupported,
      },
      duplicates: stats.duplicate,
      reconnects: stats.reconnects,
      fleetLive: live,
      fleetOffline: offline,
    };
  }

  status(): GatewayTelemetryStatus {
    return {
      emitting: this.handle.emitting,
      logsExported: this.handle.logsExported,
      endpointSet: (process.env["OTEL_EXPORTER_OTLP_ENDPOINT"] ?? "").trim().length > 0,
    };
  }

  shutdown(): Promise<void> {
    return this.handle.shutdown();
  }
}

/** Env-only bootstrap for one gateway process (called once per runtime). */
export function startGatewayTelemetry(
  gatewayId: string,
  team: string,
): GatewayTelemetry {
  const handle = startTelemetryFromEnv({
    serviceName: "ofac-gateway",
    meterName: "ofac.gateway",
    instanceId: gatewayId,
    resourceAttributes: { "ofac.team": team, "ofac.endpoint_id": gatewayId },
  });
  return new GatewayTelemetry(handle);
}
