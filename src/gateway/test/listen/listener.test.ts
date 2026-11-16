// Listener unit tier: a real unix-socket listener on a temp path, driven by
// `connectChild` and raw sockets. Admission law, lease, and offline honesty
// are observed as wire behavior (register-ack reasons, socket death), not
// through internals.
import { mkdtempSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { Socket, createServer } from "node:net";
import { createConsoleLogger } from "@ofac/workspace-link";
import type { CapabilityDescriptor, CapabilityManifestV1 } from "@ofac/protocol";
import type { ChildTransportConfig } from "../../src/config.js";
import { builtinChildManifest } from "../../src/child.js";
import { ChildFleet } from "../../src/child-link.js";
import { startChildListener, tokenMatches, type ChildListener } from "../../src/listen.js";
import { connectChild } from "../../src/connect.js";
import { childIdentity, memoryKeyStore, seededKey } from "./key-store.js";
import { childChannelUri } from "../../src/childwire.js";
import { signProofForGateway } from "@ofac/workspace-link";
import { encodeFrame, type ChildWireFrame } from "../../src/childwire.js";
import { afterEach, describe, expect, it } from "vitest";

const GATEWAY_ID = "gw.unit-lsn";

function manifestWith(capId: string): CapabilityManifestV1 {
  const base = builtinChildManifest("echo");
  return {
    ...base,
    capabilities: base.capabilities.map((c) =>
      c.id === "research.analyze" ? { ...c, id: capId } : c,
    ) as [CapabilityDescriptor, ...CapabilityDescriptor[]],
  };
}

interface Harness {
  config: ChildTransportConfig;
  fleet: ChildFleet;
  lines: string[];
  listener: ChildListener;
}

let live: { harness: Harness; closes: (() => Promise<void>)[] } | null = null;

async function listening(
  options: {
    leaseMissedBeats?: number;
    heartbeatSeconds?: number;
    handshakeTimeoutSeconds?: number;
    childSecret?: string;
    entries?: Record<string, { token?: string; maxInFlight: number; timeoutSeconds: number }>;
    tcp?: boolean;
    /** Pre-pinned identities: the child then authenticates by proof only. */
    pinned?: Record<string, ReturnType<typeof childIdentity>>;
  } = {},
): Promise<Harness> {
  const stateDir = mkdtempSync(tmpdir() + "/gw-lsn-");
  const config: ChildTransportConfig = {
    socket: { path: join(stateDir, "child.sock") },
    handshakeTimeoutSeconds: options.handshakeTimeoutSeconds ?? 5,
    leaseMissedBeats: options.leaseMissedBeats ?? 100,
    ...(options.tcp === true ? { tcp: { port: await freeTcpPort() } } : {}),
  };
  const fleet = new ChildFleet(createConsoleLogger({ level: "error" }));
  for (const [name, entry] of Object.entries(options.entries ?? {})) {
    fleet.entries.set(`${GATEWAY_ID}/${name}`, { name, ...entry });
  }
  const lines: string[] = [];
  const tracking = {
    debug: (m: string, f?: Record<string, unknown>) =>
      lines.push(`debug ${m} ${JSON.stringify(f ?? {})}`),
    info: (m: string, f?: Record<string, unknown>) =>
      lines.push(`info ${m} ${JSON.stringify(f ?? {})}`),
    warn: (m: string, f?: Record<string, unknown>) =>
      lines.push(`warn ${m} ${JSON.stringify(f ?? {})}`),
    error: (m: string, f?: Record<string, unknown>) =>
      lines.push(`error ${m} ${JSON.stringify(f ?? {})}`),
  };
  const keys = memoryKeyStore(
    Object.entries(options.pinned ?? {}).map(([name, identity]) =>
      seededKey(`${GATEWAY_ID}/${name}`, identity),
    ),
  );
  const listener = await startChildListener(config, {
    fleet,
    logger: tracking,
    gatewayId: GATEWAY_ID,
    heartbeatSeconds: options.heartbeatSeconds ?? 30,
    keys,
    ...(options.childSecret !== undefined ? { childSecret: options.childSecret } : {}),
  });
  harnessKeys = keys;
  live = { harness: { config, fleet, lines, listener }, closes: [] };
  return live.harness;
}

async function freeTcpPort(): Promise<number> {
  const probe = createServer();
  await new Promise<void>((resolve) => {
    probe.listen(0, "127.0.0.1", resolve);
  });
  const address = probe.address();
  const port = typeof address === "object" && address !== null ? address.port : 0;
  probe.close();
  return port;
}

let harnessKeys: ReturnType<typeof memoryKeyStore> | null = null;

/** The pins written so far (first-pin / rotation assertions). */
export function pins(): ReturnType<typeof memoryKeyStore>["pins"] {
  if (harnessKeys === null) throw new Error("no live listener");
  return harnessKeys.pins;
}

async function child(
  h: Harness,
  name: string,
  manifest: CapabilityManifestV1,
  token = "t",
  identity?: ReturnType<typeof childIdentity>,
) {
  const conn = await connectChild({
    childWorkspaceId: `${GATEWAY_ID}/${name}`,
    token,
    ...(identity === undefined ? {} : { identity }),
    manifest,
    path: h.config.socket.path,
    onCommand: async () => ({ kind: "accepted", executionId: "" }),
    heartbeatSeconds: 1,
  });
  live?.closes.push(() => conn.close());
  return conn;
}

async function waitFor(what: string, probe: () => boolean): Promise<void> {
  const deadline = Date.now() + 5_000;
  for (;;) {
    if (probe()) return;
    if (Date.now() > deadline) throw new Error(`timeout waiting for ${what}`);
    await new Promise((r) => setTimeout(r, 20));
  }
}

function rawConnect(path: string): Promise<Socket> {
  const socket = new Socket();
  return new Promise((resolve, reject) => {
    socket.once("connect", () => resolve(socket));
    socket.once("error", reject);
    socket.connect({ path });
  });
}

function readFrame(socket: Socket): Promise<ChildWireFrame> {
  return new Promise((resolve) => {
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
  });
}

afterEach(async () => {
  const current = live;
  live = null;
  if (current !== null) {
    for (const close of current.closes) await close().catch(() => undefined);
    await current.harness.listener.close();
  }
});

describe("tokenMatches", () => {
  it("constant-time equality", () => {
    expect(tokenMatches("secret", "secret")).toBe(true);
    expect(tokenMatches("secret", "secreT")).toBe(false);
    expect(tokenMatches("secret", "")).toBe(false);
  });
});

describe("child listener admission", () => {
  it("valid hello: register-ack accepted, fleet online, connectionCount 1", async () => {
    const h = await listening();
    const conn = await child(h, "alpha", builtinChildManifest("echo"));
    expect(conn.connected).toBe(true);
    expect(h.fleet.member(`${GATEWAY_ID}/alpha`)).toBeDefined();
    expect(h.fleet.generationOf(`${GATEWAY_ID}/alpha`)).toBe(1);
    expect(h.listener.connectionCount()).toBe(1);
  });

  it("re-register after disconnect bumps the child generation", async () => {
    const h = await listening();
    const first = await child(h, "alpha", builtinChildManifest("echo"));
    await first.close();
    await waitFor("fleet marks offline", () => h.fleet.member(`${GATEWAY_ID}/alpha`) === undefined);
    await child(h, "alpha", builtinChildManifest("echo"));
    expect(h.fleet.generationOf(`${GATEWAY_ID}/alpha`)).toBe(2);
  });

  it("bad token: denied bad-token, fleet untouched", async () => {
    const h = await listening({ childSecret: "right" });
    const socket = await rawConnect(h.config.socket.path);
    const ack = readFrame(socket);
    socket.write(
      encodeFrame({
        v: 1,
        type: "hello",
        childWorkspaceId: `${GATEWAY_ID}/beta`,
        token: "wrong",
        manifest: builtinChildManifest("echo"),
      }),
    );
    const frame = await ack;
    expect(frame).toMatchObject({
      type: "register-ack",
      accepted: false,
      reason: "denied: bad-token",
    });
    expect(h.fleet.member(`${GATEWAY_ID}/beta`)).toBeUndefined();
    socket.destroy();
  });

  it("foreign id namespace: denied child-id-namespace", async () => {
    const h = await listening({ childSecret: "x" });
    const socket = await rawConnect(h.config.socket.path);
    const ack = readFrame(socket);
    socket.write(
      encodeFrame({
        v: 1,
        type: "hello",
        childWorkspaceId: "gw.someone-else/kid",
        token: "x",
        manifest: builtinChildManifest("echo"),
      }),
    );
    expect(await ack).toMatchObject({ accepted: false, reason: "denied: child-id-namespace" });
    socket.destroy();
  });

  it("per-child token wins over the global secret", async () => {
    const h = await listening({
      childSecret: "global",
      entries: { gamma: { token: "personal", maxInFlight: 1, timeoutSeconds: 120 } },
    });
    await expect(child(h, "gamma", builtinChildManifest("echo"), "personal")).resolves.toBeTruthy();
  });

  it("duplicate-id: live holder keeps the connection, newcomer denied", async () => {
    const h = await listening();
    const first = await child(h, "delta", builtinChildManifest("echo"));
    const socket = await rawConnect(h.config.socket.path);
    const ack = readFrame(socket);
    socket.write(
      encodeFrame({
        v: 1,
        type: "hello",
        childWorkspaceId: `${GATEWAY_ID}/delta`,
        token: "t",
        manifest: manifestWith("other.cap"),
      }),
    );
    expect(await ack).toMatchObject({ accepted: false, reason: "duplicate-id" });
    // First-wins law: the LIVE holder is untouched.
    expect(first.connected).toBe(true);
    expect(h.fleet.member(`${GATEWAY_ID}/delta`)?.manifest).toEqual(builtinChildManifest("echo"));
    socket.destroy();
  });

  it("manifest conflict: same (id,version) with different JSON is denied", async () => {
    const h = await listening();
    await child(h, "eps", manifestWith("shared.cap"));
    const socket = await rawConnect(h.config.socket.path);
    const ack = readFrame(socket);
    socket.write(
      encodeFrame({
        v: 1,
        type: "hello",
        childWorkspaceId: `${GATEWAY_ID}/zeta`,
        token: "t",
        manifest: {
          ...manifestWith("shared.cap"),
          capabilities: [
            {
              id: "shared.cap",
              version: 1,
              execution: "synchronous",
              acceptedContexts: ["uri-reference"],
              outputKinds: ["other.kind"],
              outputMediaTypes: ["text/plain"],
              supportsSteering: false,
              supportsCancellation: false,
              supportsNudge: false,
            },
          ],
        },
      }),
    );
    expect(await ack).toMatchObject({ accepted: false, reason: "manifest-conflict" });
    socket.destroy();
  });

  it("byte-identical duplicate capability: accepted (union dedupes)", async () => {
    const h = await listening();
    await child(h, "eta", manifestWith("shared.cap"));
    await expect(child(h, "theta", manifestWith("shared.cap"))).resolves.toBeTruthy();
    const union = h.fleet.unionManifest("default");
    expect(union.capabilities.filter((c) => c.id === "shared.cap")).toHaveLength(1);
  });

  it("silence past the handshake window: socket dies with no register-ack", async () => {
    const h = await listening({ handshakeTimeoutSeconds: 1 });
    const socket = await rawConnect(h.config.socket.path);
    const died = new Promise<void>((resolve) => socket.once("close", resolve));
    socket.write(Buffer.from("i am not a hello\n"));
    await died;
    await waitFor("server notices", () => h.listener.connectionCount() === 0);
  });
});

describe("child listener lease + offline", () => {
  it("missed heartbeats: lease expires, child marked offline, frame-dropped path stays quiet", async () => {
    const h = await listening({ heartbeatSeconds: 1, leaseMissedBeats: 1 });
    // Raw socket: hello, then silence (no heartbeat frames at all).
    const socket = await rawConnect(h.config.socket.path);
    const ack = readFrame(socket);
    socket.write(
      encodeFrame({
        v: 1,
        type: "hello",
        childWorkspaceId: `${GATEWAY_ID}/quiet`,
        token: "t",
        manifest: builtinChildManifest("echo"),
      }),
    );
    expect(await ack).toMatchObject({ accepted: true });
    // Cadence 1s, 1 missed beat: the socket dies ~1s after admission.
    const died = await Promise.race([
      new Promise<boolean>((resolve) => socket.once("close", () => resolve(true))),
      new Promise<boolean>((resolve) => setTimeout(() => resolve(false), 4000)),
    ]);
    expect(died).toBe(true);
    await waitFor("fleet marks offline", () => h.fleet.member(`${GATEWAY_ID}/quiet`) === undefined);
    // Offline honesty: the route survives so future assigns answer child-disconnected.
    expect(h.fleet.route("research.analyze")).toEqual({
      childId: `${GATEWAY_ID}/quiet`,
      online: false,
    });
    socket.destroy();
  });

  it("socket death without lease expiry: fleet offline + generation preserved", async () => {
    const h = await listening();
    const conn = await child(h, "killme", builtinChildManifest("echo"));
    await conn.close();
    await waitFor(
      "fleet marks offline",
      () => h.fleet.member(`${GATEWAY_ID}/killme`) === undefined,
    );
    expect(h.fleet.route("research.analyze")).toEqual({
      childId: `${GATEWAY_ID}/killme`,
      online: false,
    });
    expect(h.fleet.generationOf(`${GATEWAY_ID}/killme`)).toBe(1);
  });
});

describe("pinned-key law", () => {
  it("first hello with the bootstrap token pins the key and logs child-key-pinned", async () => {
    const identity = childIdentity();
    const h = await listening({ childSecret: "pair" });
    await child(h, "alpha", builtinChildManifest("echo"), "pair", identity);
    expect(pins()).toHaveLength(1);
    expect(pins()[0]).toMatchObject({
      childId: `${GATEWAY_ID}/alpha`,
      keyId: identity.keyId,
      publicKey: identity.publicKeyBase64Url,
      algorithm: "ed25519",
    });
    expect(h.lines.some((l) => l.includes("gateway.child-key-pinned"))).toBe(true);
  });

  it("after the pin the token is dead: an empty-token hello WITH a proof is admitted", async () => {
    const identity = childIdentity();
    const h = await listening({ childSecret: "pair", pinned: { alpha: identity } });
    // The child presents NO token (retired) and proves possession instead.
    await expect(
      child(h, "alpha", builtinChildManifest("echo"), "", identity),
    ).resolves.toBeTruthy();
    expect(pins()).toHaveLength(0); // admitted, nothing new pinned
  });

  it("after the pin a missing or wrong proof is denied bad-token", async () => {
    const identity = childIdentity();
    const h = await listening({ childSecret: "pair", pinned: { alpha: identity } });
    const socket = await rawConnect(h.config.socket.path);
    const ack = readFrame(socket);
    socket.write(
      encodeFrame({
        v: 1,
        type: "hello",
        childWorkspaceId: `${GATEWAY_ID}/alpha`,
        token: "pair", // the retired token authenticates nothing
        publicKey: identity.publicKeyBase64Url,
        manifest: builtinChildManifest("echo"),
      }),
    );
    expect(await ack).toMatchObject({ accepted: false, reason: "denied: bad-token" });
    socket.destroy();
  });

  it("a proof by a different key is denied", async () => {
    const identity = childIdentity();
    const other = childIdentity();
    const h = await listening({ childSecret: "pair", pinned: { alpha: identity } });
    await expect(child(h, "alpha", builtinChildManifest("echo"), "", other)).rejects.toThrow(
      /denied: bad-token/,
    );
  });

  it("the same proof twice is a replay: the second hello is denied", async () => {
    const identity = childIdentity();
    const h = await listening({ childSecret: "pair", pinned: { alpha: identity } });
    const proof = signProofForGateway(identity, {
      childWorkspaceId: `${GATEWAY_ID}/alpha`,
      htu: childChannelUri(`${GATEWAY_ID}/alpha`),
      nonce: "captured-nonce",
    });
    const hello = {
      v: 1 as const,
      type: "hello" as const,
      childWorkspaceId: `${GATEWAY_ID}/alpha`,
      token: "",
      publicKey: identity.publicKeyBase64Url,
      proof,
      manifest: builtinChildManifest("echo"),
    };
    const first = await rawConnect(h.config.socket.path);
    const firstAck = readFrame(first);
    first.write(encodeFrame(hello));
    expect(await firstAck).toMatchObject({ accepted: true });
    // The live holder denies a newcomer `duplicate-id` before auth is reached,
    // so the replay arrives once the first connection is gone: the freshness
    // law must outlive the connection that spent the proof.
    first.destroy();
    await waitFor("fleet marks offline", () => h.fleet.member(`${GATEWAY_ID}/alpha`) === undefined);
    const second = await rawConnect(h.config.socket.path);
    const secondAck = readFrame(second);
    second.write(encodeFrame(hello));
    const secondVerdict = await secondAck;
    expect(secondVerdict).toMatchObject({ accepted: false, reason: "denied: bad-token" });
    expect(h.lines.some((l) => l.includes("child-proof-replay"))).toBe(true);
    first.destroy();
    second.destroy();
  });

  it("a rotated bootstrap token re-pins a new key (loss/rebuild) and logs child-key-rotated", async () => {
    const identity = childIdentity();
    const replacement = childIdentity();
    const h = await listening({ childSecret: "pair", pinned: { alpha: identity } });
    await expect(
      child(h, "alpha", builtinChildManifest("echo"), "pair", replacement),
    ).resolves.toBeTruthy();
    expect(pins()).toHaveLength(1);
    expect(pins()[0]).toMatchObject({
      keyId: replacement.keyId,
      rotatedAt: expect.any(String),
    });
    expect(h.lines.some((l) => l.includes("gateway.child-key-rotated"))).toBe(true);
  });

  it("a hello denied for a manifest conflict does not spend the bootstrap token", async () => {
    const identity = childIdentity();
    const h = await listening({ childSecret: "pair" });
    const socket = await rawConnect(h.config.socket.path);
    const ack = readFrame(socket);
    socket.write(
      encodeFrame({
        v: 1,
        type: "hello",
        childWorkspaceId: `${GATEWAY_ID}/zulu`,
        token: "pair",
        publicKey: identity.publicKeyBase64Url,
        // Schema violation (the manifest `kind` const): a body that can never route.
        manifest: {
          ...builtinChildManifest("echo"),
          kind: "NotACapabilityManifest",
        } as unknown as CapabilityManifestV1,
      }),
    );
    expect(await ack).toMatchObject({ accepted: false, reason: "manifest-conflict" });
    expect(pins()).toHaveLength(0); // the token is still live
    socket.destroy();
    await expect(
      child(h, "zulu", builtinChildManifest("echo"), "pair", identity),
    ).resolves.toBeTruthy();
    expect(pins()).toHaveLength(1);
  });

  it("an unauthenticated fleet still pins a presented key", async () => {
    const identity = childIdentity();
    const h = await listening(); // no secret at all
    await child(h, "alpha", builtinChildManifest("echo"), "", identity);
    expect(pins()).toHaveLength(1);
    expect(h.lines.some((l) => l.includes("child-auth-disabled"))).toBe(true);
  });
});

describe("connectChild", () => {
  it("denied hello rejects connectChild with the locked reason", async () => {
    const h = await listening({ childSecret: "right" });
    await expect(
      connectChild({
        childWorkspaceId: `${GATEWAY_ID}/nope`,
        token: "wrong",
        manifest: builtinChildManifest("echo"),
        path: h.config.socket.path,
        onCommand: async () => ({ kind: "accepted", executionId: "" }),
      }),
    ).rejects.toThrow(/register denied: denied: bad-token/);
  });

  it("commands reach the child handler and replies ride back", async () => {
    const h = await listening();
    const seen: string[] = [];
    const conn = await connectChild({
      childWorkspaceId: `${GATEWAY_ID}/worker`,
      token: "t",
      manifest: builtinChildManifest("echo"),
      path: h.config.socket.path,
      onCommand: async (frame) => {
        seen.push(frame.message.kind);
        return { kind: "accepted", executionId: frame.message.executionId };
      },
      heartbeatSeconds: 1,
    });
    live?.closes.push(() => conn.close());
    const link = h.fleet.member(`${GATEWAY_ID}/worker`);
    if (link === undefined) throw new Error("link missing");
    const reply = await link.executor.send({
      kind: "status",
      executionId: "exec-x",
    });
    expect(reply).toEqual({ kind: "accepted", executionId: "exec-x" });
    expect(seen).toEqual(["status"]);
  });
});
