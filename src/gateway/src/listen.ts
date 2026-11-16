// The child listener: a node:net server on the unix socket (0o600 file,
// stale-file takeover probe) and, optionally, the identical protocol on
// 127.0.0.1:port for children without a socket mount, plus a TLS listener
// for remote children. Admission law (locked): hello within
// handshakeTimeoutSeconds or the socket dies silently; id-namespace,
// identity (pin the key on the first hello with the bootstrap token, verify a
// proof of possession by the pinned key on every hello after), optional
// SO_PEERCRED uid, the fleet cap, duplicate-id (first-wins: the LIVE holder is
// untouched, the newcomer is denied), and the fleet's manifest-conflict gate
// each answer a register-ack denial. Heartbeat lease: leaseMissedBeats cadences
// of silence (cadence unit = the Gateway's heartbeatSeconds, which the
// child echoes on the child leg too) mark the child offline and destroy
// the socket.
import { existsSync, chmodSync, mkdirSync, readFileSync, unlinkSync } from "node:fs";
import { dirname } from "node:path";
import { createHash, timingSafeEqual } from "node:crypto";
import { createServer, Socket, type Server } from "node:net";
import { createServer as createTlsServer, TLSSocket } from "node:tls";
import type { LinkLogger } from "@ofac/workspace-link";
import { keyIdFromPublicKey, verifyProof } from "@ofac/workspace-link";
import { validateAgainstSchemaId } from "@ofac/protocol";
import type { ChildEntryConfig, ChildTransportConfig } from "./config.js";
import { channelBindingOf, memoryChildKeyStore, type ChildKeyStore } from "./child-keys.js";
import { MANIFEST_SCHEMA_ID } from "./child.js";
import { ChildFleet, ChildLink, MAX_CHILDREN } from "./child-link.js";
import {
  MAX_CHILD_FRAME_BYTES,
  createFrameParser,
  encodeFrame,
  childChannelUri,
  type ChildWireFrame,
  type EventFrame,
  type HelloFrame,
  type RegisterDenyReason,
  type ReplyFrame,
} from "./childwire.js";

/** Accepted hello proofs remembered for replay detection (LRU by insertion). */
const MAX_SEEN_PROOFS = 4096;

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
   * The pinned-key store — normally the execution ledger's `child_keys`
   * (`keyStoreFromLedger`), which survives restarts. Omitted ⇒ an in-RAM
   * store (`memoryChildKeyStore`): the test kits' fake Gateway, whose pins
   * are only as durable as the process that fakes it.
   */
  keys?: ChildKeyStore | undefined;
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

/** The identity decision for one hello: denied, or admitted (`via` says how)
 * with an optional pin the caller writes after its own gates pass. */
interface AuthVerdict {
  ok: boolean;
  via?: "token" | "proof" | "none" | undefined;
  pin?: { keyId: string; publicKey: string } | undefined;
  /** Set when a re-pin replaces an earlier key (rotation, not first pin). */
  replacesKeyId?: string | undefined;
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

/** The TLS sibling of `listenOn`: same bind law, same frame handler. */
function listenOnTls(
  options: { port: number; host: string; cert: Buffer; key: Buffer },
  onConnection: (socket: Socket) => void,
): Promise<Server> {
  const endpoint = `tls:${options.host}:${options.port}`;
  const server = createTlsServer({ cert: options.cert, key: options.key }, (socket: TLSSocket) =>
    onConnection(socket),
  ) as unknown as Server;
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
    bound.resolve();
  });
  server.listen({ port: options.port, host: options.host });
  return bound.promise.then(() => server);
}

/** Listen on the configured endpoints and serve the child protocol. */
export async function startChildListener(
  config: ChildTransportConfig,
  deps: ChildListenerDeps,
): Promise<ChildListener> {
  const { fleet, logger } = deps;
  const keys = deps.keys ?? memoryChildKeyStore();
  const heartbeatMs = deps.heartbeatSeconds * 1000;
  const leaseMissLimit = Math.max(1, config.leaseMissedBeats);
  const connections = new Set<Connection>();
  // Proof freshness for this listener's lifetime: a captured hello cannot be
  // replayed, because its `jti` is already known here. Bounded: one entry per
  // accepted hello, and the cap drops the oldest, so a months-long gateway that
  // re-hellos on every reconnect cannot grow without bound.
  const seenJtis = new Set<string>();
  const rememberProof = (proofId: string): void => {
    if (seenJtis.size >= MAX_SEEN_PROOFS) {
      const oldest = seenJtis.values().next().value as string | undefined;
      if (oldest !== undefined) seenJtis.delete(oldest);
    }
    seenJtis.add(proofId);
  };
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
  if (config.tls !== undefined) {
    // Remote children: the identical frames over TLS, where the proof is the
    // whole authentication (no peer-uid is observable across a network) and the
    // negotiated parameters are what a non-empty `cbh` is computed from.
    servers.push(
      await listenOnTls(
        {
          port: config.tls.port,
          host: "0.0.0.0",
          cert: readFileSync(config.tls.cert),
          key: readFileSync(config.tls.key),
        },
        onConnection,
      ),
    );
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
        refreshLease(conn); // any frame is liveness (cadence law)
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
    const auth = admitAuth(conn.socket, frame);
    if (!auth.ok) return;
    if (!validateAgainstSchemaId(MANIFEST_SCHEMA_ID, frame.manifest).ok) {
      denyReason("manifest-conflict"); // a bad manifest cannot route anything
      return;
    }
    if (fleet.conflicts(frame.manifest)) {
      denyReason("manifest-conflict"); // deny-ambiguity: first-come keeps routing
      return;
    }
    // Admission is complete — only NOW is the pin written. A hello denied by any
    // gate above leaves the bootstrap token live, so a corrected child can
    // present it again: authorization dies with the identity it authorized.
    if (auth.pin !== undefined) {
      const pinned = keys.pinChildKey(id, auth.pin.keyId, auth.pin.publicKey, "ed25519");
      logger.info(
        auth.replacesKeyId === undefined ? "gateway.child-key-pinned" : "gateway.child-key-rotated",
        {
          childId: id,
          keyId: pinned.keyId,
          ...(auth.replacesKeyId === undefined ? {} : { fromKeyId: auth.replacesKeyId }),
          auth: auth.via,
          pinnedAt: pinned.pinnedAt,
          rotatedAt: pinned.rotatedAt,
        },
      );
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

  /**
   * Identity + authentication, then the optional peer-uid gate. Two shapes:
   *
   *  - **first hello** (no key pinned yet): the config bootstrap token must
   *    match, and it PINS `frame.publicKey` — the token retires forever, so
   *    this is the only hello it ever authenticates.
   *  - **every later hello**: a detached proof of possession by the pinned key.
   *    The token is empty and irrelevant; a bad, stale or missing proof is the
   *    same `denied: bad-token` the old law used, so the deny vocabulary and the
   *    child-side retry behaviour are unchanged.
   *
   * A denial is already answered + closed when this returns false.
   */
  /**
   * Identity + authentication; a denial is already answered + closed when the
   * verdict comes back `ok: false`. A positive verdict may carry a DEFERRED
   * pin: the caller writes it only once every other admission gate has passed,
   * so a hello refused for a bad manifest cannot spend the bootstrap token.
   */
  function admitAuth(socket: Socket, frame: HelloFrame): AuthVerdict {
    const childId = frame.childWorkspaceId;
    const pinned = keys.childKey(childId);
    const cbh = channelBindingOf(socket);
    const secret = fleet.configuredEntry(childId)?.token ?? deps.childSecret;
    if (pinned !== null) {
      const presented = frame.publicKey;
      if (presented !== undefined && presented !== pinned.publicKey) {
        // A different key: only the bootstrap token can re-pin, and only when the
        // operator rotated it in the config — the loss/rebuild story, which is
        // re-enrollment one level down.
        if (secret === undefined || !tokenMatches(secret, frame.token ?? "")) {
          return admitDeny(socket, frame, "denied: bad-token");
        }
        if (!admitChannel(socket, frame)) return { ok: false };
        return {
          ok: true,
          via: "token",
          pin: { keyId: keyIdFromPublicKey(presented), publicKey: presented },
          replacesKeyId: pinned.keyId,
        };
      }
      // Same key (or no key presented, which is the pinned child re-helloing):
      // a proof of possession or nothing.
      if (frame.proof === undefined || frame.proof === "") {
        return admitDeny(socket, frame, "denied: bad-token");
      }
      const verdict = verifyProof(frame.proof, {
        endpointId: childId,
        htm: "WS",
        htu: childChannelUri(childId),
        publicKeyBase64Url: pinned.publicKey,
        ...(cbh === "" ? {} : { cbh }),
      });
      if (!verdict.ok) {
        logger.warn("child-proof-failed", { childId, reason: verdict.reason });
        return admitDeny(socket, frame, "denied: bad-token");
      }
      const proofId = `${childId}|${verdict.claims.jti}`;
      if (seenJtis.has(proofId)) {
        // The same proof twice on this listener: a captured hello replayed.
        logger.warn("child-proof-replay", { childId, jti: verdict.claims.jti });
        return admitDeny(socket, frame, "denied: bad-token");
      }
      rememberProof(proofId);
      return admitChannel(socket, frame) ? { ok: true, via: "proof" } : { ok: false };
    }
    // No key pinned yet: the bootstrap token is the whole story, and all it buys
    // is the pin. With no secret configured the fleet is unauthenticated (the
    // pre-existing dev posture) and a presented key is still pinned, so the
    // transport upgrades itself the moment a child grows an identity.
    if (secret === undefined) {
      if (!authWarned) {
        authWarned = true;
        logger.warn("child-auth-disabled", {
          hint: "no per-child token nor OFAC_CHILD_SECRET: any same-uid process may attach",
        });
      }
      return admitChannel(socket, frame) ? pinVerdict(frame, "none") : { ok: false };
    }
    if (!tokenMatches(secret, frame.token ?? "")) {
      return admitDeny(socket, frame, "denied: bad-token");
    }
    return admitChannel(socket, frame) ? pinVerdict(frame, "token") : { ok: false };
  }

  /** Pin the presented key (if any) once the hello is otherwise admissible. */
  function pinVerdict(frame: HelloFrame, via: "token" | "none"): AuthVerdict {
    if (frame.publicKey === undefined) {
      return { ok: true, via }; // nothing presented: nothing to pin
    }
    return {
      ok: true,
      via,
      pin: { keyId: keyIdFromPublicKey(frame.publicKey), publicKey: frame.publicKey },
    };
  }

  /** The physical gate that applies to every admitted hello. */
  function admitChannel(socket: Socket, frame: HelloFrame): boolean {
    if (config.socket.allowedUid !== undefined) {
      const peer = deps.peerCredentials?.(socket) ?? nodePeerCredentials(socket);
      if (peer !== undefined && peer.uid !== config.socket.allowedUid) {
        admitDeny(socket, frame, "denied: peer-uid-mismatch");
        return false;
      }
    }
    return true;
  }

  /** Answers + closes; the verdict carries the denial for the caller's fork. */
  function admitDeny(socket: Socket, frame: HelloFrame, reason: RegisterDenyReason): AuthVerdict {
    logger.warn("child-register-denied", { reason, childId: frame.childWorkspaceId });
    write(socket, { v: 1, type: "register-ack", accepted: false, reason });
    socket.end();
    return { ok: false };
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
    ...(config.tls !== undefined
      ? { port: config.tls.port }
      : config.tcp !== undefined
        ? { port: config.tcp.port }
        : {}),
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
