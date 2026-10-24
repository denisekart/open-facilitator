// The child listener: a node:net server on the unix socket (0o600 file,
// stale-file takeover probe) and, optionally, the identical protocol on
// 127.0.0.1:port for children without a socket mount. Admission law
// (locked): hello within handshakeTimeoutSeconds or the socket dies
// silently; id-namespace, token (constant-time), optional SO_PEERCRED uid,
// the fleet cap, duplicate-id (first-wins: the LIVE holder is untouched,
// the newcomer is denied), and the fleet's manifest-conflict gate each
// answer a register-ack denial. Heartbeat lease: leaseMissedBeats cadences
// of silence (cadence unit = the Gateway's heartbeatSeconds, which the
// child echoes on the child leg too) mark the child offline and destroy
// the socket.
import { existsSync, chmodSync, mkdirSync, unlinkSync } from "node:fs";
import { dirname } from "node:path";
import { createHash, timingSafeEqual } from "node:crypto";
import { createServer, Socket, type Server } from "node:net";
import type { LinkLogger } from "@ofac/workspace-link";
import { validateAgainstSchemaId } from "@ofac/protocol";
import type { ChildEntryConfig, ChildTransportConfig } from "./config.js";
import { MANIFEST_SCHEMA_ID } from "./child.js";
import { ChildFleet, ChildLink, MAX_CHILDREN } from "./child-link.js";
import {
  MAX_CHILD_FRAME_BYTES,
  createFrameParser,
  encodeFrame,
  type ChildWireFrame,
  type EventFrame,
  type HelloFrame,
  type RegisterDenyReason,
  type ReplyFrame,
} from "./childwire.js";

const CHILD_NAME_RE = /^[a-z0-9]([a-z0-9-]*[a-z0-9])?$/;
const CHILD_ID_MAX = 128;

export interface ChildListenerDeps {
  fleet: ChildFleet;
  logger: LinkLogger;
  /** The id namespace every child must sit in: `<gatewayId>/<name>`. */
  gatewayId: string;
  /** Gateway heartbeat cadence: the lease-miss detector's clock unit. */
  heartbeatSeconds: number;
  /** Gateway-wide child secret (OFAC_CHILD_SECRET); wins over per-child tokens. */
  childSecret?: string | undefined;
  /**
   * Called after every accepted register-ack: the runtime persists the
   * fleet shadow (generation bump), re-POSTs the union, and probes the
   * child's `running` rows (the re-register reconcile trigger).
   */
  onRegistered?(link: ChildLink): void;
  /** Socket died for an admitted child (fleet already marked it offline). */
  onDisconnected?(childId: string): void;
  /** Test seam: override the SO_PEERCRED lookup (platform honesty). */
  peerCredentials?(socket: Socket): { uid: number } | undefined;
}

export interface ChildListener {
  socketPath?: string | undefined;
  port?: number | undefined;
  connectionCount(): number;
  close(): Promise<void>;
}

interface Connection {
  socket: Socket;
  link?: ChildLink;
  leaseTimer: ReturnType<typeof setInterval> | undefined;
}

/** Constant-time token compare (hash-equal): no length or byte timing leak. */
export function tokenMatches(expected: string, provided: string): boolean {
  const a = createHash("sha256").update(expected, "utf8").digest();
  const b = createHash("sha256")
    .update(provided ?? "", "utf8")
    .digest();
  return timingSafeEqual(a, b);
}

/**
 * Stale-socket takeover: something ANSWERS the path ⇒ a live Gateway owns
 * it ⇒ fail fast (two Gateways on one path means lost children). Refused /
 * missing ⇒ unlink the corpse, bind, tighten to 0o600.
 */
async function probeSocket(path: string): Promise<void> {
  if (!existsSync(path)) return;
  const probe = new Socket();
  const outcome = await new Promise<"live" | "dead">((resolve) => {
    const settle = (value: "live" | "dead"): void => {
      probe.destroy();
      resolve(value);
    };
    probe.setTimeout(1000, () => settle("live"));
    probe.once("connect", () => settle("live"));
    probe.once("error", () => settle("dead"));
    probe.connect({ path });
  });
  if (outcome === "live") throw new Error(`child socket in use: ${path}`);
  unlinkSync(path);
}

function listenOn(
  options: { path: string } | { port: number; host: string },
  onConnection: (socket: Socket) => void,
  afterBind?: (server: Server) => void,
): Promise<Server> {
  const endpoint =
    "path" in options ? `unix:${options.path}` : `tcp:${options.host}:${options.port}`;
  const server = createServer((socket) => onConnection(socket));
  const bound = Promise.withResolvers<void>();
  const startup = (error: Error): void => {
    bound.reject(
      new Error(
        `child transport cannot bind ${endpoint}: ${error instanceof Error ? error.message : String(error)}`,
      ),
    );
  };
  server.once("error", startup);
  server.once("listening", () => {
    server.removeListener("error", startup);
    server.on("error", () => {
      /* post-bind per-listener noise; connections carry their own errors */
    });
    afterBind?.(server);
    bound.resolve();
  });
  server.listen(options);
  return bound.promise.then(() => server);
}

/** Listen on the configured endpoints and serve the child protocol. */
export async function startChildListener(
  config: ChildTransportConfig,
  deps: ChildListenerDeps,
): Promise<ChildListener> {
  const { fleet, logger } = deps;
  const heartbeatMs = deps.heartbeatSeconds * 1000;
  const leaseMissLimit = Math.max(1, config.leaseMissedBeats);
  const connections = new Set<Connection>();
  let closing = false;
  let authWarned = false;

  const servers: Server[] = [];
  mkdirSync(dirname(config.socket.path), { recursive: true, mode: 0o700 });
  await probeSocket(config.socket.path);
  servers.push(
    await listenOn({ path: config.socket.path }, onConnection, () => {
      chmodSync(config.socket.path, 0o600);
    }),
  );
  if (config.tcp !== undefined) {
    servers.push(await listenOn({ port: config.tcp.port, host: "127.0.0.1" }, onConnection));
  }

  function write(socket: Socket, frame: ChildWireFrame): void {
    socket.write(encodeFrame(frame));
  }

  function onConnection(socket: Socket): void {
    if (closing) {
      socket.destroy();
      return;
    }
    const conn: Connection = { socket, leaseTimer: undefined };
    connections.add(conn);
    socket.on("error", () => {
      /* the close handler runs the teardown once */
    });
    socket.on("close", () => {
      connections.delete(conn);
      clearInterval(conn.leaseTimer);
      conn.leaseTimer = undefined;
      if (conn.link !== undefined) {
        conn.link.notifyDisconnected();
        fleet.markOffline(conn.link.childId);
        deps.onDisconnected?.(conn.link.childId);
      }
    });

    // Pre-hello: nothing useful within the handshake window ⇒ die silently
    // (locked: NO register-ack for a timeout — a hello never came).
    const handshake = setTimeout(() => {
      if (conn.link === undefined) socket.destroy();
    }, config.handshakeTimeoutSeconds * 1000);
    handshake.unref?.();

    const parser = createFrameParser(
      (frame) => {
        if (conn.link === undefined) {
          if (frame.type === "hello") onHello(conn, frame as HelloFrame);
          return; // a non-hello first frame just waits out the hello deadline
        }
        refreshLease(conn); // any frame is liveness (S:386 cadence law)
        if (frame.type === "reply" || frame.type === "event") {
          // Child-generation fence (locked): a frame from a superseded
          // connection changes NOTHING — log, drop, never settle/forward.
          if (conn.link.generation !== fleet.generationOf(conn.link.childId)) {
            logger.warn("stale-child-generation", {
              childId: conn.link.childId,
              frameGeneration: conn.link.generation,
            });
            return;
          }
          conn.link.onFrame(frame as ReplyFrame | EventFrame);
        }
      },
      (error) => {
        logger.warn("child-frame-dropped", {
          childId: conn.link?.childId ?? "(pre-hello)",
          reason: error.kind,
        });
        if (error.kind === "oversize") socket.destroy();
      },
      { expect: "child", maxBytes: MAX_CHILD_FRAME_BYTES },
    );
    socket.on("data", (chunk: Buffer) => parser.push(chunk));
  }

  function childNameOf(id: string): string {
    return id.split("/").slice(1).join("/");
  }

  function onHello(conn: Connection, frame: HelloFrame): void {
    const deny = (reason: RegisterDenyReason): void => {
      logger.warn("child-register-denied", { reason, childId: frame.childWorkspaceId });
      write(conn.socket, { v: 1, type: "register-ack", accepted: false, reason });
      conn.socket.end();
    };
    const denyReason = (reason: RegisterDenyReason): false => (deny(reason), false);

    const id = frame.childWorkspaceId;
    const prefix = `${deps.gatewayId}/`;
    if (typeof id === "string" && fleet.member(id) !== undefined) {
      // First-wins: a LIVE holder of the id denies the newcomer, untouched.
      denyReason("duplicate-id");
      return;
    }
    if (
      typeof id !== "string" ||
      id.length > CHILD_ID_MAX ||
      !id.startsWith(prefix) ||
      !CHILD_NAME_RE.test(id.slice(prefix.length))
    ) {
      denyReason("denied: child-id-namespace");
      return;
    }
    if (fleet.size >= MAX_CHILDREN) {
      denyReason("too-many-children");
      return;
    }
    if (!admitAuth(conn.socket, frame)) return;
    if (!validateAgainstSchemaId(MANIFEST_SCHEMA_ID, frame.manifest).ok) {
      denyReason("manifest-conflict"); // a bad manifest cannot route anything
      return;
    }
    if (fleet.conflicts(frame.manifest)) {
      denyReason("manifest-conflict"); // deny-ambiguity: first-come keeps routing
      return;
    }

    const entry: ChildEntryConfig = fleet.configuredEntry(id) ?? {
      name: childNameOf(id),
      maxInFlight: 1,
      timeoutSeconds: 120,
    };
    const link = new ChildLink({
      childWorkspaceId: id,
      manifest: frame.manifest,
      generation: fleet.generationOf(id) + 1, // re-register bumps the fence
      entry,
      write: (f) => write(conn.socket, f),
      logger,
    });
    conn.link = link;
    fleet.register(link);
    write(conn.socket, { v: 1, type: "register-ack", accepted: true });
    refreshLease(conn);
    logger.info("child connected", {
      childId: id,
      childGeneration: link.generation,
      capabilities: frame.manifest.capabilities.map((c) => `${c.id} v${c.version}`),
    });
    deps.onRegistered?.(link);
  }

  /** Token + optional peer-uid gate; a denial already answered + closed. */
  function admitAuth(socket: Socket, frame: HelloFrame): boolean {
    const entry = fleet.configuredEntry(frame.childWorkspaceId);
    // A configured child authenticates with ITS token; the global
    // OFAC_CHILD_SECRET is the fallback for children without one.
    const secret = entry?.token ?? deps.childSecret;
    if (secret === undefined) {
      if (!authWarned) {
        authWarned = true;
        logger.warn("child-auth-disabled", {
          hint: "no per-child token nor OFAC_CHILD_SECRET: any same-uid process may attach",
        });
      }
    } else if (!tokenMatches(secret, frame.token ?? "")) {
      return admitDeny(socket, frame, "denied: bad-token");
    }
    if (config.socket.allowedUid !== undefined) {
      const peer = deps.peerCredentials?.(socket) ?? nodePeerCredentials(socket);
      if (peer !== undefined && peer.uid !== config.socket.allowedUid) {
        return admitDeny(socket, frame, "denied: peer-uid-mismatch");
      }
    }
    return true;
  }

  function admitDeny(socket: Socket, frame: HelloFrame, reason: RegisterDenyReason): false {
    logger.warn("child-register-denied", { reason, childId: frame.childWorkspaceId });
    write(socket, { v: 1, type: "register-ack", accepted: false, reason });
    socket.end();
    return false;
  }

  function refreshLease(conn: Connection): void {
    clearInterval(conn.leaseTimer);
    let missed = 0;
    conn.leaseTimer = setInterval(() => {
      missed += 1;
      if (missed >= leaseMissLimit) {
        logger.warn("child-lease-expired", { childId: conn.link?.childId });
        conn.socket.destroy();
      }
    }, heartbeatMs);
    conn.leaseTimer.unref?.();
  }

  return {
    socketPath: config.socket.path,
    ...(config.tcp !== undefined ? { port: config.tcp.port } : {}),
    connectionCount: () => connections.size,
    async close() {
      closing = true;
      for (const conn of connections) {
        conn.link?.goaway("gateway-shutdown");
        conn.socket.end();
      }
      // net.Server has no closeAllConnections: end politely, force after a beat.
      const forced = setTimeout(() => {
        for (const conn of connections) conn.socket.destroy();
      }, 250);
      forced.unref?.();
      await Promise.all(
        servers.map(
          (server) =>
            new Promise<void>((resolve) => {
              server.close(() => resolve());
            }),
        ),
      );
      clearTimeout(forced);
      for (const conn of connections) conn.socket.destroy();
      if (existsSync(config.socket.path)) unlinkSync(config.socket.path);
    },
  };
}

/** Linux SO_PEERCRED; undefined elsewhere (file-permission boundary then). */
function nodePeerCredentials(socket: Socket): { uid: number } | undefined {
  // @types/node 22 carries no SO_PEERCRED surface; runtime Node 22+ has it.
  const creds = (
    socket as Socket & {
      getSocketCredentials?(): { uid: number; gid: number; pid: number };
    }
  ).getSocketCredentials?.();
  return creds === undefined ? undefined : { uid: creds.uid };
}
