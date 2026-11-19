// Listener transport legs the admission suite cannot reach on a fresh temp
// path: the stale-socket probe (corpse takeover vs live-holder refusal), bind
// failures (tcp + tls), the physical gates (peer-uid, fleet cap, the TLS
// channel-binding proof), the child-generation fence on inbound frames, and
// the forced-close path.
import { spawnSync } from "node:child_process";
import { existsSync, mkdtempSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { createServer, Socket } from "node:net";
import { connect as connectTls, type TLSSocket } from "node:tls";
import { createConsoleLogger, signProofForGateway } from "@ofac/workspace-link";
import type { CapabilityManifestV1 } from "@ofac/protocol";
import { afterEach, describe, expect, it } from "vitest";
import type { ChildTransportConfig } from "../../src/config.js";
import { builtinChildManifest } from "../../src/child.js";
import { ChildFleet } from "../../src/child-link.js";
import {
  startChildListener,
  type ChildListener,
  type ChildListenerDeps,
} from "../../src/listen.js";
import { connectChild } from "../../src/connect.js";
import { childChannelUri, encodeFrame, type ChildWireFrame } from "../../src/childwire.js";
import { childIdentity, memoryKeyStore, seededKey } from "./key-store.js";

const GATEWAY_ID = "gw.unit-lsn2";

let closes: (() => Promise<void>)[] = [];
let listener: ChildListener | null = null;
let lines: string[] = [];

afterEach(async () => {
  if (listener !== null) await listener.close().catch(() => undefined);
  listener = null;
  for (const close of closes) await close().catch(() => undefined);
  closes = [];
  lines = [];
});

function trackingLogger(): { lines: string[]; logger: ChildListenerDeps["logger"] } {
  const collected: string[] = [];
  const push =
    (level: string) =>
    (m: string, f?: Record<string, unknown>): void => {
      collected.push(`${level} ${m} ${JSON.stringify(f ?? {})}`);
    };
  return {
    lines: collected,
    logger: {
      debug: push("debug"),
      info: push("info"),
      warn: push("warn"),
      error: push("error"),
    },
  };
}

function configFor(dir: string, over: Partial<ChildTransportConfig> = {}): ChildTransportConfig {
  return {
    socket: { path: join(dir, "child.sock") },
    handshakeTimeoutSeconds: 5,
    leaseMissedBeats: 100,
    ...over,
  };
}

async function listening(
  config: ChildTransportConfig,
  options: {
    pinned?: Record<string, ReturnType<typeof childIdentity>>;
    childSecret?: string;
    peerCredentials?: ChildListenerDeps["peerCredentials"];
  } = {},
): Promise<ChildFleet> {
  const tracking = trackingLogger();
  lines = tracking.lines;
  const fleet = new ChildFleet(createConsoleLogger({ level: "error" }));
  listener = await startChildListener(config, {
    fleet,
    logger: tracking.logger,
    gatewayId: GATEWAY_ID,
    heartbeatSeconds: 30,
    keys: memoryKeyStore(
      Object.entries(options.pinned ?? {}).map(([name, identity]) =>
        seededKey(`${GATEWAY_ID}/${name}`, identity),
      ),
    ),
    ...(options.childSecret !== undefined ? { childSecret: options.childSecret } : {}),
    ...(options.peerCredentials !== undefined ? { peerCredentials: options.peerCredentials } : {}),
  });
  return fleet;
}

function rawConnect(path: string): Promise<Socket> {
  const socket = new Socket();
  const { promise, resolve, reject } = Promise.withResolvers<Socket>();
  socket.once("connect", () => resolve(socket));
  socket.once("error", reject);
  socket.connect({ path });
  return promise;
}

function readFrame(socket: Socket): Promise<ChildWireFrame> {
  const { promise, resolve } = Promise.withResolvers<ChildWireFrame>();
  let buffer = "";
  const onData = (chunk: Buffer): void => {
    buffer += chunk.toString("utf8");
    const newline = buffer.indexOf("\n");
    if (newline >= 0) {
      socket.off("data", onData);
      resolve(JSON.parse(buffer.slice(0, newline)) as ChildWireFrame);
    }
  };
  socket.on("data", onData);
  return promise;
}

async function waitFor(what: string, probe: () => boolean): Promise<void> {
  const deadline = Date.now() + 5_000;
  for (;;) {
    if (probe()) return;
    if (Date.now() > deadline) throw new Error(`timeout waiting for ${what}`);
    await new Promise((r) => setTimeout(r, 20));
  }
}

function hello(name: string, manifest?: CapabilityManifestV1): Buffer {
  return Buffer.from(
    encodeFrame({
      v: 1,
      type: "hello",
      childWorkspaceId: `${GATEWAY_ID}/${name}`,
      token: "t",
      manifest: manifest ?? builtinChildManifest("echo"),
    } as ChildWireFrame),
  );
}

/** Connect + hello over a raw socket, expecting an accepted register-ack. */
async function accepted(name: string, path: string): Promise<Socket> {
  const socket = await rawConnect(path);
  closes.push(async () => {
    socket.destroy();
  });
  const ack = readFrame(socket);
  socket.write(hello(name));
  expect(await ack).toMatchObject({ accepted: true });
  return socket;
}

/** A self-signed cert for the TLS listener leg. */
function selfSignedCert(dir: string): { cert: string; key: string } {
  const cert = join(dir, "child.crt");
  const key = join(dir, "child.key");
  const result = spawnSync(
    "openssl",
    [
      "req",
      "-x509",
      "-newkey",
      "rsa:2048",
      "-keyout",
      key,
      "-out",
      cert,
      "-days",
      "2",
      "-nodes",
      "-subj",
      "/CN=localhost",
    ],
    { encoding: "utf8" },
  );
  if (result.status !== 0) throw new Error(`openssl unavailable: ${result.stderr}`);
  return { cert, key };
}

/** Bind-and-release a TCP port: a free-port oracle (and an occupied one while held). */
async function occupiedTcp(): Promise<{ port: number; release: () => Promise<void> }> {
  const server = createServer();
  const { promise, resolve } = Promise.withResolvers<void>();
  server.listen(0, "127.0.0.1", resolve);
  await promise;
  const address = server.address();
  const port = typeof address === "object" && address !== null ? address.port : 0;
  return {
    port,
    release: () =>
      new Promise<void>((res) => {
        server.close(() => res());
      }),
  };
}

describe("stale-socket probe", () => {
  it("a corpse (nothing answers the path) is unlinked and the bind proceeds", async () => {
    const dir = mkdtempSync(join(tmpdir(), "gw-lsn2-"));
    const config = configFor(dir);
    // A dead unix socket: a child process binds the path, then SIGKILLs
    // itself — a normal exit would unlink the file, a kill leaves the corpse.
    const script = `const s=require("node:net").createServer();s.listen(${JSON.stringify(config.socket.path)},()=>process.kill(process.pid,"SIGKILL"));`;
    spawnSync(process.execPath, ["-e", script], { stdio: "ignore" });
    expect(existsSync(config.socket.path)).toBe(true);
    await listening(config);
    const conn = await connectChild({
      childWorkspaceId: `${GATEWAY_ID}/alpha`,
      token: "t",
      manifest: builtinChildManifest("echo"),
      path: config.socket.path,
      onCommand: async () => ({ kind: "accepted", executionId: "" }),
    });
    closes.push(() => conn.close());
    expect(conn.connected).toBe(true);
  });

  it("a live listener on the path: the probe refuses the takeover (fail fast)", async () => {
    const dir = mkdtempSync(join(tmpdir(), "gw-lsn2-"));
    const config = configFor(dir);
    const holder = createServer();
    const bound = Promise.withResolvers<void>();
    holder.listen(config.socket.path, () => bound.resolve());
    await bound.promise;
    try {
      await expect(listening(config)).rejects.toThrow(/child socket in use/);
      listener = null; // the failed start left nothing to tear down
    } finally {
      await new Promise<void>((res) => {
        holder.close(() => res());
      });
    }
  });
});

describe("bind failures", () => {
  it("tcp leg on an occupied port: typed bind error names the endpoint", async () => {
    const dir = mkdtempSync(join(tmpdir(), "gw-lsn2-"));
    const occupied = await occupiedTcp();
    try {
      await expect(listening(configFor(dir, { tcp: { port: occupied.port } }))).rejects.toThrow(
        new RegExp(`child transport cannot bind tcp:127.0.0.1:${String(occupied.port)}`),
      );
      listener = null;
    } finally {
      await occupied.release();
    }
  });

  it("tls leg on an occupied port: the TLS sibling carries the same law", async () => {
    const dir = mkdtempSync(join(tmpdir(), "gw-lsn2-"));
    const { cert, key } = selfSignedCert(dir);
    const occupied = await occupiedTcp();
    try {
      await expect(
        listening(configFor(dir, { tls: { cert, key, port: occupied.port } })),
      ).rejects.toThrow(/child transport cannot bind tls:0\.0\.0\.0:/);
      listener = null;
    } finally {
      await occupied.release();
    }
  });
});

describe("TLS listener leg", () => {
  it("serves the same frames and its proof binds the negotiated channel", async () => {
    const dir = mkdtempSync(join(tmpdir(), "gw-lsn2-"));
    const { cert, key } = selfSignedCert(dir);
    const probe = await occupiedTcp();
    const port = probe.port;
    await probe.release(); // a free port, freshly negotiated
    const identity = childIdentity();
    await listening(configFor(dir, { tls: { cert, key, port } }), {
      pinned: { alpha: identity },
      childSecret: "pair",
    });
    // A plain-TLS client whose proof carries the WRONG channel binding is
    // denied (the non-empty cbh law is live on the TLS leg).
    const socket: TLSSocket = connectTls({ port, host: "127.0.0.1", rejectUnauthorized: false });
    closes.push(async () => {
      socket.destroy();
    });
    const ack = readFrame(socket);
    socket.write(
      encodeFrame({
        v: 1,
        type: "hello",
        childWorkspaceId: `${GATEWAY_ID}/alpha`,
        token: "",
        publicKey: identity.publicKeyBase64Url,
        proof: signProofForGateway(identity, {
          childWorkspaceId: `${GATEWAY_ID}/alpha`,
          htu: childChannelUri(`${GATEWAY_ID}/alpha`),
          nonce: "n-1",
          cbh: "sha256:deadbeef",
        }),
        manifest: builtinChildManifest("echo"),
      }),
    );
    expect(await ack).toMatchObject({ accepted: false, reason: "denied: bad-token" });
    expect(lines.some((l) => l.includes("child-proof-failed"))).toBe(true);
    expect(listener?.port).toBe(port); // the tls port is the advertised endpoint
  });
});

describe("physical gates + fleet cap", () => {
  it("allowedUid set and the peer matches (SO_PEERCRED on this box): admitted", async () => {
    const dir = mkdtempSync(join(tmpdir(), "gw-lsn2-"));
    const config = configFor(dir, {
      socket: { path: join(dir, "child.sock"), allowedUid: process.getuid?.() ?? 0 },
    });
    await listening(config);
    await expect(
      connectChild({
        childWorkspaceId: `${GATEWAY_ID}/uid-ok`,
        token: "t",
        manifest: builtinChildManifest("echo"),
        path: config.socket.path,
        onCommand: async () => ({ kind: "accepted", executionId: "" }),
      }),
    ).resolves.toBeTruthy();
  });

  it("allowedUid set, the seam sees a stranger: denied peer-uid-mismatch", async () => {
    const dir = mkdtempSync(join(tmpdir(), "gw-lsn2-"));
    const config = configFor(dir, { socket: { path: join(dir, "child.sock"), allowedUid: 4242 } });
    await listening(config, { peerCredentials: () => ({ uid: 1 }) });
    const socket = await rawConnect(config.socket.path);
    const ack = readFrame(socket);
    socket.write(hello("intruder"));
    expect(await ack).toMatchObject({ accepted: false, reason: "denied: peer-uid-mismatch" });
    socket.destroy();
  });

  it("allowedUid set, the seam has NO credentials: admitted (platform honesty)", async () => {
    const dir = mkdtempSync(join(tmpdir(), "gw-lsn2-"));
    const config = configFor(dir, { socket: { path: join(dir, "child.sock"), allowedUid: 4242 } });
    await listening(config, { peerCredentials: () => undefined });
    await expect(
      connectChild({
        childWorkspaceId: `${GATEWAY_ID}/no-creds`,
        token: "t",
        manifest: builtinChildManifest("echo"),
        path: config.socket.path,
        onCommand: async () => ({ kind: "accepted", executionId: "" }),
      }),
    ).resolves.toBeTruthy();
  });

  it("32 live children: the 33rd hello is denied too-many-children", async () => {
    const dir = mkdtempSync(join(tmpdir(), "gw-lsn2-"));
    const config = configFor(dir);
    await listening(config);
    for (let i = 0; i < 32; i += 1) {
      await accepted(`kid${String(i)}`, config.socket.path);
    }
    const socket = await rawConnect(config.socket.path);
    const ack = readFrame(socket);
    socket.write(hello("kid32"));
    expect(await ack).toMatchObject({ accepted: false, reason: "too-many-children" });
    socket.destroy();
  });
});

describe("child-generation fence on inbound frames", () => {
  it("a frame from a superseded connection is logged + dropped; the fresh one rides", async () => {
    const dir = mkdtempSync(join(tmpdir(), "gw-lsn2-"));
    const config = configFor(dir);
    const fleet = await listening(config);
    const stale = await accepted("alpha", config.socket.path);
    // Race simulation (public API only): the fleet marks alpha offline while
    // its socket is still open, and a re-register takes generation 2.
    fleet.markOffline(`${GATEWAY_ID}/alpha`);
    const fresh = await accepted("alpha", config.socket.path);
    const reply = encodeFrame({
      v: 1,
      type: "reply",
      requestId: "ch-x",
      reply: { kind: "accepted", executionId: "" },
    });
    stale.write(reply); // gen 1 vs fleet gen 2: fenced
    fresh.write(reply); // gen 2: an orphan reply, not a stale-generation drop
    await waitFor("stale-generation warn", () =>
      lines.some((l) => l.includes("stale-child-generation")),
    );
    expect(lines.filter((l) => l.includes("stale-child-generation"))).toHaveLength(1);
    expect(lines.some((l) => l.includes("orphan-child-reply-dropped"))).toBe(true);
  });
});

describe("listener close", () => {
  it("a child that ignores goaway is force-destroyed; the socket file is gone", async () => {
    const dir = mkdtempSync(join(tmpdir(), "gw-lsn2-"));
    const config = configFor(dir);
    await listening(config);
    const socket = await rawConnect(config.socket.path); // raw: never reads the goaway
    const ack = readFrame(socket);
    socket.write(hello("stubborn"));
    expect(await ack).toMatchObject({ accepted: true });
    const died = Promise.withResolvers<void>();
    socket.once("close", () => died.resolve());
    const current = listener;
    listener = null;
    await current?.close(); // the 250ms forced sweep destroys the stubborn socket
    await died.promise;
    expect(existsSync(config.socket.path)).toBe(false);
    socket.destroy();
  });
});
