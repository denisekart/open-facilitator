// The child-side client: `connectChild` attaches a child process to a
// Gateway listener (unix socket, or the 127.0.0.1 fallback with an explicit
// port). One connect = one hello with the child's identity + token +
// manifest; the register-ack decides: accepted resolves, denied rejects
// with the Gateway's locked reason. After the ack the connection is the
// child's executor seam inverted: inbound `command` frames land in the
// child's handler, replies + events ride back out, heartbeats keep the
// Gateway's lease open, `goaway` closes honestly. Reconnect is the
// CALLER's call (Plan 07's adapter owns restart policy): a dead socket
// settles every in-flight handler run so the Gateway can answer receipts
// truthfully (the reply simply never arrives → transport timeout).
import { connect } from "node:net";
import type { CapabilityManifestV1, EnvelopeEvent } from "@ofac/protocol";
import type { ChildReply } from "./child.js";
import {
  MAX_CHILD_FRAME_BYTES,
  createFrameParser,
  encodeFrame,
  type ChildWireFrame,
  type CommandFrame,
  type GoawayReason,
  type ReplyFrame,
} from "./childwire.js";

export interface ConnectChildOptions {
  /** `<gatewayId>/<name>` — must sit in the Gateway's namespace. */
  childWorkspaceId: string;
  token: string;
  manifest: CapabilityManifestV1;
  /** unix socket path; omit with `port` for the TCP fallback. */
  path?: string | undefined;
  port?: number | undefined;
  host?: string | undefined;
  /** The child's own command handler: its answer IS the reply frame. */
  onCommand(frame: CommandFrame): Promise<ChildReply>;
  /** Gateway business cadence echoed from config; default 30s. */
  heartbeatSeconds?: number | undefined;
  /** hello+ack deadline; default 5s. */
  handshakeTimeoutSeconds?: number | undefined;
  /** Fired when the Gateway says goodbye before the socket dies. */
  onNotice?(notice: { type: "goaway"; reason: GoawayReason }): void;
}

export interface ChildConnection {
  readonly connected: boolean;
  /** Relay a child-published event upstream (fire-and-forget wire). */
  publishEvent(env: EnvelopeEvent): Promise<void>;
  /** The Gateway sends `goaway`; this just stops heartbeats + closes. */
  close(): Promise<void>;
  /** Resolves/rejects when the socket dies (denial rejects with the reason). */
  waitClosed(): Promise<void>;
}

let eventCounter = 0;

export async function connectChild(options: ConnectChildOptions): Promise<ChildConnection> {
  const socket =
    options.path !== undefined
      ? connect({ path: options.path })
      : connect({ port: options.port ?? 0, host: options.host ?? "127.0.0.1" });
  const closed = Promise.withResolvers<void>();
  const ack = Promise.withResolvers<void>();
  let acked = false;
  let heartbeat: ReturnType<typeof setInterval> | undefined;

  const send = (frame: ChildWireFrame): void => {
    socket.write(encodeFrame(frame));
  };
  const sendHeartbeat = (): void => {
    if (!socket.destroyed)
      send({ v: 1, type: "heartbeat", childWorkspaceId: options.childWorkspaceId });
  };

  const handshakeTimer = setTimeout(
    () => {
      socket.destroy();
      closed.reject(new Error("register-ack timeout"));
    },
    (options.handshakeTimeoutSeconds ?? 5) * 1000,
  );
  handshakeTimer.unref?.();

  const parser = createFrameParser(
    (frame) => {
      if (!acked) {
        if (frame.type !== "register-ack") return;
        if (!frame.accepted) {
          socket.destroy();
          closed.reject(new Error(`register denied: ${frame.reason ?? "unknown"}`));
          return;
        }
        acked = true;
        clearTimeout(handshakeTimer);
        sendHeartbeat();
        heartbeat = setInterval(sendHeartbeat, (options.heartbeatSeconds ?? 30) * 1000);
        heartbeat.unref?.();
        ack.resolve();
        return;
      }
      if (frame.type === "command") {
        const command = frame;
        void options
          .onCommand(command)
          .then((reply) => {
            if (socket.destroyed) return;
            const out: ReplyFrame = { v: 1, type: "reply", requestId: command.requestId, reply };
            send(out);
          })
          .catch(() => {
            if (socket.destroyed) return;
            const out: ReplyFrame = {
              v: 1,
              type: "reply",
              requestId: command.requestId,
              reply: { kind: "rejected", reason: "child handler failed" },
            };
            send(out);
          });
        return;
      }
      if (frame.type === "goaway") {
        options.onNotice?.({ type: "goaway", reason: frame.reason });
        socket.end();
      }
    },
    () => {
      /* malformed lines kill nothing but themselves (child side too) */
    },
    { expect: "gateway", maxBytes: MAX_CHILD_FRAME_BYTES },
  );

  socket.on("data", (chunk: Buffer) => parser.push(chunk));
  socket.on("error", (error) => {
    if (!acked) closed.reject(error);
    else closed.resolve();
  });
  socket.on("close", () => {
    clearInterval(heartbeat);
    closed.resolve();
  });

  send({
    v: 1,
    type: "hello",
    childWorkspaceId: options.childWorkspaceId,
    token: options.token,
    manifest: options.manifest,
  });

  try {
    await Promise.race([ack.promise, closed.promise]);
  } catch (error) {
    // Pre-ack socket death / denial: the rejection carries the honest cause.
    throw error instanceof Error ? error : new Error(String(error));
  }
  if (!acked) throw new Error("connection died before register-ack");

  return {
    get connected() {
      return !socket.destroyed && !socket.writableEnded;
    },
    async publishEvent(env: EnvelopeEvent): Promise<void> {
      eventCounter += 1;
      send({ v: 1, type: "event", requestId: `ev-${eventCounter}`, envelope: env });
    },
    async close(): Promise<void> {
      clearInterval(heartbeat);
      socket.end();
    },
    waitClosed: () => closed.promise,
  };
}
