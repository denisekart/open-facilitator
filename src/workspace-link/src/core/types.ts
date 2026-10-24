// The transport seam + SDK-facing types. The client core (`"."`) imports ONLY
// these interfaces — SignalR/SQLite live behind `"./node"`, unit tests behind
// a mock. Frame shapes are the frozen contract types re-exported from
// @ofac/protocol; the stricter per-kind types below are compile-time
// refinements of the generated `ControlFrame` union member set.
import type {
  CapabilityManifestV1,
  EnvelopeCommand,
  EnvelopeEvent,
  EnvelopeObject,
  LinkDeliveryV1,
  ReceiptV1,
  SupportedTransport,
} from "@ofac/protocol";
import type { ProblemDetails } from "./errors.js";

export type { ProblemDetails };

/**
 * Frozen envelope wire-size gate. The fixture index (protocol/conformance-
 * fixtures/index.json, limits.maxEnvelopeBytes) is the normative number and
 * @ofac/protocol does not re-export it as a constant, so the literal stands
 * here with the fixture harness (both languages) as the drift gate.
 */
export const MAX_ENVELOPE_BYTES = 262144;

// ── Control frames (kind-refined views of the generated ControlFrame) ─────

export interface HelloFrame {
  kind: "hello";
  protocolVersions: [string, ...string[]];
  supportedTransports: [SupportedTransport, ...SupportedTransport[]];
  endpointId: string;
  instanceId: string;
  capabilityManifestDigest: string;
  lastKnownAck?: string;
}

export interface WelcomeFrame {
  kind: "welcome";
  sessionId: string;
  leaseEpoch: number;
  maxInFlight: number;
  heartbeatSeconds: number;
  protocolVersion: string;
}

export interface HeartbeatFrame {
  kind: "heartbeat";
  sessionId: string;
  leaseEpoch: number;
}

export interface FlowControlFrame {
  kind: "flow_control";
  sessionId: string;
  maxInFlight: number;
}

export interface GoodbyeFrame {
  kind: "goodbye";
  sessionId: string;
  reason: string;
}

/**
 * Hub `Envelope` return / HTTP status-derived result. The hub answers this
 * exact JSON shape (LinkHub.Envelope ⇒ EnvelopeAck record); the HTTP transport
 * derives it from status codes: 202 accepted, 200 duplicate, 4xx rejected +
 * parsed ProblemDetails.
 */
export interface EnvelopeAck {
  status: "accepted" | "duplicate" | "rejected";
  deliveryId?: string;
  problem?: ProblemDetails;
}
/** The transport seam: two implementations in `"./node"`, mocks in tests. */
export interface LinkTransport {
  /** Advertised in hello; defaults to ["signalr"] when the impl omits it. */
  readonly supportedTransports?: [SupportedTransport, ...SupportedTransport[]];
  connect(): Promise<void>;
  /** `null` ⇒ server pushed the incompatible goodbye and refused the session (terminal). */
  hello(frame: HelloFrame): Promise<WelcomeFrame | null>;
  envelope(env: EnvelopeObject): Promise<EnvelopeAck>;
  receipt(r: ReceiptV1): Promise<void>;
  /** `false` ⇒ session stale (stale epoch / no row): drop to the reconnect loop. */
  heartbeat(hb: HeartbeatFrame): Promise<boolean>;
  goodbye(gb: GoodbyeFrame): Promise<void>;
  close(): Promise<void>;
  onDeliver(cb: (d: LinkDeliveryV1) => void): void;
  onFlow(cb: (f: FlowControlFrame) => void): void;
  onDisconnect(cb: (g: GoodbyeFrame) => void): void;
}

/** Bearer-token seam: `getToken` feeds every request; `refresh` runs on 401. */
export interface LinkTokenProvider {
  getToken(): Promise<string>;
  refresh?(): Promise<void>;
}

// ── Handler contract (HarnessAdapter envelope-handling subset; Plan 05
// composes the full lifecycle on top of this) ───────────────────────────────

export type CommandOutcome =
  | { kind: "accepted" }
  /** Maps to receipt status "rejected". */
  | { kind: "rejected"; reason: string }
  /** Maps to receipt status "unsupported". */
  | { kind: "unsupported"; reason?: string };

export interface CommandContext {
  deliveryId: string;
  envelope: EnvelopeCommand;
  signal: AbortSignal;
  publishEvent(env: EnvelopeEvent): Promise<EnvelopeAck>;
}

export interface LinkHandler {
  onCommand(cmd: EnvelopeCommand, ctx: CommandContext): Promise<CommandOutcome>;
}

/**
 * The capability manifest as the frozen schema requires it: the generated
 * `CapabilityManifestV1` already carries `ownerTeam` (verified against
 * src/protocol/src/generated/types.ts:89 at Plan 04 Step 0), so the
 * intersection from the plan collapses to an alias — kept for a stable name.
 */
export type WorkspaceCapabilityManifest = CapabilityManifestV1;

/** The live session as the welcome advertises it; surfaced via LinkClient.session. */
export interface LinkSession {
  sessionId: string;
  leaseEpoch: number;
  maxInFlight: number;
  heartbeatSeconds: number;
  protocolVersion: string;
}
export type LinkState = "idle" | "connecting" | "handshaking" | "ready" | "reconnecting" | "closed";

export interface LinkStats {
  delivered: number;
  duplicate: number;
  processed: number;
  failed: number;
  unsupported: number;
  reconnects: number;
}

/** Injected logger (default console); correlation IDs ride JSON fields (A:281). */
export interface LinkLogger {
  debug(msg: string, fields?: Record<string, unknown>): void;
  info(msg: string, fields?: Record<string, unknown>): void;
  warn(msg: string, fields?: Record<string, unknown>): void;
  error(msg: string, fields?: Record<string, unknown>): void;
}
