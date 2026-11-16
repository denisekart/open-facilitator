// Foreign-shape link client (conformance items 1-2). Talks to the kernel
// ONLY through the surface published in protocol/openapi/link-v1.yaml plus
// the json-schema frames: enroll (mode (a) shared secret), capability
// advertisement, the /hubs/link WebSocket (hand-rolled SignalR JSON
// protocol), and the HTTP receipt endpoint. No workspace module is imported
// — that isolation is the point of this artifact.
//
// The credential law is demonstrated INDEPENDENTLY of every other package:
// this client hand-rolls the detached EdDSA JWS (proof.ts) and the identity
// keystore (identity.ts) on raw node:crypto, which is the point of the
// cross-language conformance artifact.
//
// Lifecycle orchestrated across two processes by the conformance runner:
//   phase1: enroll with a fresh public key -> challenge+proof for a session
//           -> advertise caps -> WS hello carrying a channel proof -> report
//           the delivered work -> drop the socket abruptly (no goodbye). The
//           identity (the private half) is written to --identity-file so
//           phase2 can prove key rotation.
//   phase2: re-enroll with a NEW key (the phase-1 key is now retired and must
//           stop authenticating — proven by attempting a proof with it) ->
//           WS hello carrying the lastKnownAck hint -> the kernel replays the
//           pending ledger row -> ack via the HTTP receipt endpoint ->
//           optionally wait for the dropped un-acked delivery to be re-offered
//           (in-flight lease expiry) -> clean goodbye.
import { createHash, randomUUID } from "node:crypto";
import { existsSync, readFileSync, writeFileSync } from "node:fs";

import {
  decodeFrame,
  type Decoded,
  encodeHandshake,
  encodeInvocation,
  splitFrames,
} from "./signalr.js";
import { generateIdentity, identityFromFile, type EndpointIdentity, type IdentityFile } from "./identity.js";
import { buildProof, parseChallengeNonce } from "./proof.js";

interface Args {
  url: string;
  token: string;
  endpointId: string;
  team: string;
  capability: string;
  mode: "phase1" | "phase2";
  identityFile?: string;
  lastKnownAck?: string;
  timeoutMs: number;
  expectRedeliver?: string;
}

function parseArgs(argv: string[]): Args {
  const flags = new Map<string, string>();
  for (let i = 0; i < argv.length; i += 1) {
    if (argv[i].startsWith("--") && i + 1 < argv.length) {
      flags.set(argv[i].slice(2), argv[++i]);
    }
  }
  const required = (name: string): string => {
    const value = flags.get(name);
    if (!value) {
      throw new Error(`missing --${name}`);
    }
    return value;
  };
  const mode = required("mode");
  if (mode !== "phase1" && mode !== "phase2") {
    throw new Error(`--mode must be phase1 or phase2, got '${mode}'`);
  }
  const optional = (name: string): Record<string, string> => {
    const value = flags.get(name);
    return value ? { [name]: value } : {};
  };
  return {
    url: required("url"),
    token: required("token"),
    endpointId: required("endpoint"),
    team: required("team"),
    capability: required("capability"),
    mode,
    ...optional("identity-file"),
    ...optional("last-known-ack"),
    ...optional("expect-redeliver"),
    timeoutMs: Number(flags.get("timeout-ms") ?? "60000"),
  };
}

function emit(event: string, fields: Record<string, unknown> = {}): void {
  process.stdout.write(JSON.stringify({ event, ...fields }) + "\n");
}

interface LiveArgs extends Args {
  /** The 5-minute session JWT — the link plane's only bearer. */
  sessionToken: string;
}

interface EnrollResponse {
  enrollmentId: string;
  trustMode: string;
  endpointKeyId: string;
  seq: number;
}

async function enrollWith(args: Args, secret: string, identity: EndpointIdentity): Promise<EnrollResponse> {
  const response = await fetch(`${args.url}/api/link/v1/enroll`, {
    method: "POST",
    headers: {
      // The code rides the Authorization header (bearer) AND the schema-
      // mandated body token — both documented enrollment shapes.
      authorization: `Bearer ${secret}`,
      "content-type": "application/json",
    },
    body: JSON.stringify({
      token: secret,
      endpointId: args.endpointId,
      instanceId: `foreign-${randomUUID()}`,
      trustMode: "separate-process-same-host",
      team: args.team,
      role: "child",
      publicKey: identity.publicKeyBase64Url,
      keyAlgorithm: "ed25519",
    }),
  });
  if (!response.ok) {
    throw new Error(`enroll failed: ${response.status} ${await response.text()}`);
  }
  return (await response.json()) as EnrollResponse;
}

/**
 * Challenge -> hand-rolled proof -> session JWT. Returns the session token,
 * or the refusal status when the kernel rejects the proof (401), which is
 * what phase 2 uses to PROVE a retired key is dead.
 */
async function sessionFor(
  args: Args,
  identity: EndpointIdentity,
  proofPath = "/api/link/v1/proof",
): Promise<{ sessionToken: string } | { status: number; keyId: string }> {
  const challenge = await fetch(`${args.url}/api/link/v1/challenge`, {
    method: "POST",
    headers: { "content-type": "application/json" },
    body: JSON.stringify({ endpointId: args.endpointId }),
  });
  if (!challenge.ok) {
    return { status: challenge.status, keyId: identity.keyId };
  }
  const { nonce } = (await challenge.json()) as { nonce: string };
  const { epoch, seq } = parseChallengeNonce(nonce);
  const proof = buildProof(identity, {
    endpointId: args.endpointId,
    htm: "POST",
    htu: `${args.url}${proofPath}`,
    nonce,
    seq,
    epoch,
  });
  const response = await fetch(`${args.url}${proofPath}`, {
    method: "POST",
    headers: { "content-type": "application/json" },
    body: JSON.stringify({ proof }),
  });
  if (!response.ok) {
    return { status: response.status, keyId: identity.keyId };
  }
  const body = (await response.json()) as { sessionToken: string };
  return { sessionToken: body.sessionToken };
}

async function linkFetch(args: LiveArgs, path: string, init?: RequestInit): Promise<Response> {
  return fetch(`${args.url}${path}`, {
    ...init,
    headers: {
      authorization: `Bearer ${args.sessionToken}`,
      "content-type": "application/json",
      ...(init?.headers ?? {}),
    },
  });
}

function saveIdentity(file: string | undefined, identity: EndpointIdentity): void {
  if (file) {
    writeFileSync(file, JSON.stringify(identity.toFile()));
  }
}

function loadIdentity(file: string | undefined): EndpointIdentity | undefined {
  if (!file || !existsSync(file)) {
    return undefined;
  }
  return identityFromFile(JSON.parse(readFileSync(file, "utf8")) as IdentityFile);
}

interface Welcome {
  sessionId: string;
  leaseEpoch: number;
  maxInFlight?: number;
  heartbeatSeconds?: number;
  protocolVersion?: string;
}

interface Delivery {
  deliveryId: string;
  workId?: string;
}

/** The channel URI as this client dials it: the base URL's http/https -> ws/wss. */
function hubHtuOf(args: Args): string {
  return `${args.url.replace(/^http/, "ws")}/hubs/link`;
}

interface Session {
  socket: WebSocket;
  welcome: Promise<Welcome>;
  /** Resolve with the next Deliver push (already-buffered ones first), or
   * reject after `timeoutMs`. */
  nextDelivery(timeoutMs: number): Promise<Delivery>;
  closed: Promise<void>;
}

function connect(args: LiveArgs, helloFrame: Record<string, unknown>): Session {
  const wsUrl = `${args.url.replace(/^http/, "ws")}/hubs/link`;
  // @types/node types only the (url, protocols) overload; undici also
  // accepts an options bag carrying the upgrade headers.
  const socket = new WebSocket(wsUrl, { headers: { authorization: `Bearer ${args.sessionToken}` } } as unknown as string[]);
  const welcome = Promise.withResolvers<Welcome>();
  const closed = Promise.withResolvers<void>();
  const buffered: Delivery[] = [];
  const waiters: ((delivery: Delivery) => void)[] = [];
  let buffer = "";
  let invocationSeq = 0;
  let heartbeatTimer: ReturnType<typeof setInterval> | undefined;

  socket.addEventListener("open", () => {
    socket.send(encodeHandshake());
  });
  socket.addEventListener("close", () => {
    clearInterval(heartbeatTimer);
    closed.resolve();
  });
  socket.addEventListener("error", () => welcome.reject(new Error("websocket error")));
  socket.addEventListener("message", (event: MessageEvent) => {
    buffer +=
      typeof event.data === "string"
        ? event.data
        : Buffer.from(event.data as ArrayBuffer).toString("utf8");
    const split = splitFrames(buffer);
    buffer = split.rest;
    for (const text of split.frames) {
      let frame: Decoded | null;
      try {
        frame = decodeFrame(text);
      } catch {
        continue;
      }
      if (!frame) {
        continue;
      }
      if (frame.kind === "handshake") {
        if (frame.error) {
          welcome.reject(new Error(`handshake rejected: ${frame.error}`));
          return;
        }
        invocationSeq += 1;
        socket.send(encodeInvocation(String(invocationSeq), "Hello", [helloFrame]));
      } else if (frame.kind === "completion" && frame.invocationId === "1") {
        if (frame.error) {
          welcome.reject(new Error(`Hello failed: ${frame.error}`));
        } else if (frame.result === null) {
          welcome.reject(new Error("Hello returned null (no compatible protocol version)"));
        } else {
          const w = frame.result as Welcome;
          // Hold the session lease for the whole scenario: refresh at a
          // third of the advertised cadence (the kernel's lease is
          // heartbeatSeconds * 3, so a missed beat never drops the session).
          const cadence = Math.max(2, Math.floor((w.heartbeatSeconds ?? 30) / 3));
          heartbeatTimer = setInterval(() => {
            invocationSeq += 1;
            socket.send(
              encodeInvocation(String(invocationSeq), "Heartbeat", [
                { kind: "heartbeat", sessionId: w.sessionId, leaseEpoch: w.leaseEpoch },
              ]),
            );
          }, cadence * 1000);
          welcome.resolve(w);
        }
      } else if (frame.kind === "invocation" && frame.target === "Deliver") {
        const raw = frame.args[0] as {
          deliveryId: string;
          envelope?: { data?: { workId?: string } };
        };
        const delivery: Delivery = {
          deliveryId: raw.deliveryId,
          ...(raw.envelope?.data?.workId ? { workId: raw.envelope.data.workId } : {}),
        };
        const waiter = waiters.shift();
        if (waiter) {
          waiter(delivery);
        } else {
          buffered.push(delivery);
        }
      } else if (frame.kind === "close") {
        welcome.reject(new Error(`connection closed: ${frame.error ?? "clean"}`));
      }
    }
  });

  return {
    socket,
    welcome: welcome.promise,
    nextDelivery(timeoutMs: number): Promise<Delivery> {
      const pending = Promise.withResolvers<Delivery>();
      const timer = setTimeout(() => {
        const index = waiters.indexOf(pending.resolve);
        if (index >= 0) {
          waiters.splice(index, 1);
        }
        pending.reject(new Error(`no delivery within ${timeoutMs}ms`));
      }, timeoutMs);
      const bufferedDelivery = buffered.shift();
      if (bufferedDelivery) {
        clearTimeout(timer);
        pending.resolve(bufferedDelivery);
      } else {
        waiters.push(pending.resolve);
      }
      return pending.promise;
    },
    closed: closed.promise,
  };
}

async function main(): Promise<number> {
  const args = parseArgs(process.argv.slice(2));
  // phase2 presents a NEW key; the identity phase1 left behind is the retired
  // one whose proof must now be refused. phase1 mints its own.
  const priorIdentity = args.mode === "phase2" ? loadIdentity(args.identityFile) : undefined;
  const identity = generateIdentity();
  const enrollment = await enrollWith(args, args.token, identity);
  const minted = await sessionFor(args, identity);
  if (!("sessionToken" in minted)) {
    throw new Error(`proof refused: ${minted.status} for ${minted.keyId}`);
  }
  const live: LiveArgs = { ...args, sessionToken: minted.sessionToken };
  saveIdentity(args.identityFile, identity);
  emit("enrolled", {
    enrollmentId: enrollment.enrollmentId,
    endpointKeyId: enrollment.endpointKeyId,
    seq: enrollment.seq,
    mode: args.mode,
  });

  if (args.mode === "phase2" && priorIdentity && priorIdentity.keyId !== identity.keyId) {
    // Rotation law: re-enrollment retires the previous KEY — a proof by it
    // must no longer buy a session on the link surface.
    const retired = await sessionFor(args, priorIdentity);
    emit("old-token-rejected", {
      status: "sessionToken" in retired ? 200 : retired.status,
      keyId: priorIdentity.keyId,
    });
  }

  const manifest = {
    apiVersion: "facilitator.framework/v1",
    kind: "CapabilityManifest",
    ownerTeam: args.team,
    capabilities: [{ id: args.capability, version: 1, execution: "synchronous" }],
  };
  const manifestText = JSON.stringify(manifest);
  const caps = await linkFetch(live, "/api/link/v1/capabilities", {
    method: "POST",
    body: manifestText,
  });
  if (!caps.ok) {
    throw new Error(`capabilities advertisement failed: ${caps.status} ${await caps.text()}`);
  }
  emit("caps-advertised");

  const helloFrame: Record<string, unknown> = {
    kind: "hello",
    protocolVersions: ["v1"],
    supportedTransports: ["websocket"],
    endpointId: args.endpointId,
    instanceId: `foreign-${randomUUID()}`,
    capabilityManifestDigest: `sha256:${createHash("sha256").update(manifestText).digest("hex")}`,
  };
  if (args.lastKnownAck) {
    helloFrame["lastKnownAck"] = args.lastKnownAck;
  }

  // The handshake proof binds to the channel URI exactly as THIS client dials
  // it (the base URL's http/https mapped to ws/wss). That is the point of a
  // foreign implementation: the kernel must accept a proof it did not mint.
  const hubChallenge = await fetch(`${args.url}/api/link/v1/challenge`, {
    method: "POST",
    headers: { "content-type": "application/json" },
    body: JSON.stringify({ endpointId: args.endpointId }),
  });
  if (!hubChallenge.ok) {
    throw new Error(`challenge refused: ${hubChallenge.status}`);
  }
  const hubNonce = ((await hubChallenge.json()) as { nonce: string }).nonce;
  const hubState = parseChallengeNonce(hubNonce);
  helloFrame["publicKey"] = identity.publicKeyBase64Url;
  helloFrame["proof"] = buildProof(identity, {
    endpointId: args.endpointId,
    htm: "WS",
    htu: hubHtuOf(args),
    nonce: hubNonce,
    seq: hubState.seq,
    epoch: hubState.epoch,
  });

  const session = connect(live, helloFrame);
  const welcome = await session.welcome;
  emit("handshaked", {
    leaseEpoch: welcome.leaseEpoch,
    sessionId: welcome.sessionId,
    protocolVersion: welcome.protocolVersion ?? null,
    mode: args.mode,
  });

  if (args.mode === "phase1") {
    const delivery = await session.nextDelivery(args.timeoutMs);
    emit("delivered", { deliveryId: delivery.deliveryId, workId: delivery.workId ?? null });
    // Abrupt drop: exit without a goodbye frame — the kernel must survive
    // a dead socket (session lease expiry + in-flight ledger) unaided.
    return 0;
  }

  // phase2: a row that went pending while the socket was down must arrive
  // on this fresh session (replay-after-reconnect).
  const replayed = await session.nextDelivery(args.timeoutMs);
  emit("replayed", { deliveryId: replayed.deliveryId, workId: replayed.workId ?? null });

  const receipt = await linkFetch(live, `/api/link/v1/deliveries/${replayed.deliveryId}/receipt`, {
    method: "POST",
    body: JSON.stringify({
      status: "accepted",
      processedAt: new Date().toISOString().replace(/\.\d{3}Z$/, "Z"),
    }),
  });
  if (!receipt.ok) {
    throw new Error(`receipt failed: ${receipt.status} ${await receipt.text()}`);
  }

  let redelivered: string | null = null;
  if (args.expectRedeliver) {
    // The un-acked phase-1 delivery rides the in-flight lease; once it
    // expires the pump re-offers it on the live socket.
    const again = await session.nextDelivery(args.timeoutMs).catch(() => null);
    redelivered = again?.deliveryId ?? null;
  }

  session.socket.close();
  await session.closed;
  emit("summary", {
    phase: "phase2",
    replayed: replayed.deliveryId,
    acked: true,
    redelivered,
    expectedRedeliver: args.expectRedeliver ?? null,
  });
  return 0;
}

main().then(
  (code) => process.exit(code),
  (error: unknown) => {
    emit("error", { message: String(error instanceof Error ? error.message : error) });
    process.exit(1);
  },
);
