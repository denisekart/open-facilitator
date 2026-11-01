// The child-transport wire: one NDJSON frame per line over a Unix socket or
// a 127.0.0.1 TCP fallback — identical protocol on both (Plan 06 lock). This
// module is pure: no sockets, no timers. `encodeFrame`/`createFrameParser`
// are the single codec; the listener (`listen.ts`) and the child-side client
// (`connect.ts`) both feed socket bytes through it.
//
// Frame law: one JSON object per line, single trailing `\n`; every frame
// carries `{ v: 1, type: <literal> }`. Payloads reuse the frozen 05
// vocabulary verbatim (`ChildMessage`/`ChildReply` — import, never redefine).
// Oversized lines are rejected once (`oversize`) and the connection dies;
// a malformed line is dropped alone (`parse-error`) — it kills nothing but
// itself.
import type { CapabilityManifestV1, EnvelopeCommand, EnvelopeEvent } from "@ofac/protocol";
import type { ChildReply } from "./child.js";

/** Same byte gate as the SDK's MAX_ENVELOPE_BYTES: frames embed envelopes. */
export const MAX_CHILD_FRAME_BYTES = 262_144;

/** Commands the Gateway pushes to a child: 05's vocabulary minus `register`
 * (registration rides the `hello` frame; the child sees the same messages). */
export type ChildWireCommand =
  | { kind: "start"; executionId: string; executionGeneration: number; command: EnvelopeCommand }
  | { kind: "steer"; executionId: string; command: EnvelopeCommand }
  | { kind: "status"; executionId: string }
  | { kind: "stop"; executionId: string; reason: string };

/** `register-ack` denial reasons (locked literals). */
export type RegisterDenyReason =
  | "duplicate-id"
  | "denied: peer-uid-mismatch"
  | "denied: bad-token"
  | "denied: child-id-namespace"
  | "manifest-conflict"
  | "too-many-children";

/** `goaway` reasons (locked literals). */
export type GoawayReason = "gateway-shutdown" | "replaced";

// ── child → Gateway ─────────────────────────────────────────────────────────

export interface HelloFrame {
  v: 1;
  type: "hello";
  childWorkspaceId: string;
  token: string;
  manifest: CapabilityManifestV1;
}

export interface ReplyFrame {
  v: 1;
  type: "reply";
  requestId: string;
  reply: ChildReply;
}

/** The child→Gateway event relay: the Gateway forwards `envelope` bytes
 * unmodified (05 law); Plan 07's `event.publish` profiles this frame. */
export interface EventFrame {
  v: 1;
  type: "event";
  requestId: string;
  envelope: EnvelopeEvent;
}

export interface ChildHeartbeatFrame {
  v: 1;
  type: "heartbeat";
  childWorkspaceId: string;
}

// ── Gateway → child ─────────────────────────────────────────────────────────

export interface CommandFrame {
  v: 1;
  type: "command";
  requestId: string;
  message: ChildWireCommand;
}

export interface RegisterAckFrame {
  v: 1;
  type: "register-ack";
  accepted: boolean;
  reason?: string | undefined;
}

export interface GoawayFrame {
  v: 1;
  type: "goaway";
  reason: GoawayReason;
}

export type ChildWireFrame =
  | HelloFrame
  | ReplyFrame
  | EventFrame
  | ChildHeartbeatFrame
  | CommandFrame
  | RegisterAckFrame
  | GoawayFrame;

/** Wire frame types a CHILD may send. */
const CHILD_TO_GATEWAY = new Set(["hello", "reply", "event", "heartbeat"]);
/** Wire frame types the GATEWAY may send. */
const GATEWAY_TO_CHILD = new Set(["command", "register-ack", "goaway"]);

/** Serialize one frame: compact JSON + exactly one trailing newline. */
export function encodeFrame(frame: ChildWireFrame): string {
  return `${JSON.stringify(frame)}\n`;
}

export type FrameParseError =
  { kind: "parse-error"; detail: string } | { kind: "oversize"; bytes: number };

export interface FrameParserOptions {
  /** Byte gate; defaults to MAX_CHILD_FRAME_BYTES. */
  maxBytes?: number;
  /** Direction gate: frames of the other direction fail like malformed lines. */
  expect: "child" | "gateway";
}

export interface FrameParser {
  /** Feed raw socket bytes (Buffer or string); emits complete frames. */
  push(chunk: Buffer | string): void;
  /** Bytes buffered but not yet newline-terminated (for liveness checks). */
  pendingBytes(): number;
}

/**
 * Incremental NDJSON parser. Buffer concat, split on `\n`, one frame per
 * line. Oversized: reports `oversize` ONCE and stops emitting (the owner
 * must destroy the connection). Malformed/v1-mismatched/wrong-direction
 * lines: report `parse-error`, drop the line, connection survives.
 */
export function createFrameParser(
  onFrame: (frame: ChildWireFrame) => void,
  onError: (error: FrameParseError) => void,
  options: FrameParserOptions,
): FrameParser {
  const maxBytes = options.maxBytes ?? MAX_CHILD_FRAME_BYTES;
  const allowed = options.expect === "child" ? CHILD_TO_GATEWAY : GATEWAY_TO_CHILD;
  let buffer = Buffer.alloc(0);
  let dead = false;

  const handleLine = (line: Buffer): void => {
    let doc: unknown;
    try {
      doc = JSON.parse(line.toString("utf8")) as unknown;
    } catch (error) {
      onError({
        kind: "parse-error",
        detail: error instanceof Error ? error.message : String(error),
      });
      return;
    }
    if (typeof doc !== "object" || doc === null || Array.isArray(doc)) {
      onError({ kind: "parse-error", detail: "frame is not an object" });
      return;
    }
    const record = doc as Record<string, unknown>;
    const frameType =
      "type" in record && typeof record["type"] === "string" ? record["type"] : undefined;
    if (record["v"] !== 1 || frameType === undefined || !allowed.has(frameType)) {
      onError({ kind: "parse-error", detail: "frame is not a v1 frame of the expected direction" });
      return;
    }
    onFrame(doc as ChildWireFrame);
  };

  return {
    push(chunk) {
      if (dead) return;
      buffer = Buffer.concat([buffer, typeof chunk === "string" ? Buffer.from(chunk) : chunk]);
      for (;;) {
        const newline = buffer.indexOf(0x0a);
        if (newline < 0) {
          if (buffer.length > maxBytes) {
            dead = true;
            onError({ kind: "oversize", bytes: buffer.length });
          }
          return;
        }
        const line = buffer.subarray(0, newline);
        buffer = buffer.subarray(newline + 1);
        if (line.length > maxBytes) {
          dead = true;
          onError({ kind: "oversize", bytes: line.length });
          return;
        }
        if (line.length > 0) handleLine(line);
      }
    },
    pendingBytes() {
      return buffer.length;
    },
  };
}
