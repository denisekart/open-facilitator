// Minimal hand-rolled SignalR JSON-protocol codec, foreign to the workspace:
// it mirrors only the frozen wire shape (protocol/openapi/link-v1.yaml's
// HTTP-fallback note points at /hubs/link as the same-link surface over
// WebSockets) — handshake `{protocol,version}`, JSON messages delimited by
// the 0x1E record separator, frames keyed by `type`. Type codes (SignalR
// JSON protocol, version 1): 0 handshake response · 1 invocation · 6 completion ·
// 7 close. This client SENDS invocations and RECEIVES invocations
// (Deliver/Disconnect), one completion (Hello's WelcomeFrame), and closes.

/** The JSON protocol's record separator; every emitted frame is terminated with it. */
export const FRAME_SEPARATOR = "\u001e";

/** Encode a hub invocation: type 1, non-streaming. */
export function encodeInvocation(invocationId: string, target: string, args: unknown[]): string {
  return JSON.stringify({ type: 1, invocationId, target, arguments: args }) + FRAME_SEPARATOR;
}

/** Encode the opening handshake for protocol "json" version 1, no headers. */
export function encodeHandshake(): string {
  return JSON.stringify({ protocol: "json", version: 1 }) + FRAME_SEPARATOR;
}

/** Split a received text blob into complete frames; returns leftover tail. */
export function splitFrames(buffer: string): { frames: string[]; rest: string } {
  const parts = buffer.split(FRAME_SEPARATOR);
  const rest = parts.pop() ?? "";
  return { frames: parts.filter((p) => p.length > 0), rest };
}

export type Decoded =
  | { kind: "handshake"; error?: string }
  | { kind: "invocation"; target: string; args: unknown[] }
  | { kind: "completion"; invocationId: string; result?: unknown; error?: string }
  | { kind: "close"; error?: string };

/** Parse one wire frame; returns null for ack/ping/ping-response frames.
 * A frame with no `type` is the kernel's handshake ACK as sent when the
 * upgrade carried no `json.framing.v1` subprotocol (SignalR downgrades the
 * `{"type":6,"invocationId":""}` ack to a bare `{}` in that mode). */
export function decodeFrame(text: string): Decoded | null {
  const frame = JSON.parse(text) as Record<string, unknown>;
  switch (frame["type"]) {
    case undefined:
      return { kind: "handshake" };
    case 3: {
      // No-subprotocol mode: the hub result rides a type-3 frame keyed by
      // `result`, closed by a bare completion; decode it as the completion.
      if (!("result" in frame) && !("error" in frame)) {
        return null;
      }

      const invocationId = String(frame["invocationId"] ?? "");
      const error = frame["error"];
      return typeof error === "string"
        ? { kind: "completion", invocationId, error }
        : { kind: "completion", invocationId, result: frame["result"] };
    }
    case 0: {
      const error = frame["error"];
      return { kind: "handshake", ...(typeof error === "string" ? { error } : {}) };
    }
    case 1: {
      const args = frame["arguments"];
      return {
        kind: "invocation",
        target: String(frame["target"]),
        args: Array.isArray(args) ? args : [],
      };
    }
    case 6: {
      const invocationId = String(frame["invocationId"] ?? "");
      const error = frame["error"];
      if (typeof error === "string") {
        return { kind: "completion", invocationId, error };
      }
      return { kind: "completion", invocationId, result: frame["result"] };
    }
    case 7: {
      const error = frame["error"];
      return { kind: "close", ...(typeof error === "string" ? { error } : {}) };
    }
    default:
      return null;
  }
}
