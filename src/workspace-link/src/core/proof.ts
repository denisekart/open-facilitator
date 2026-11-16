// Proof-of-possession construction: the detached EdDSA JWS (RFC 8037 subset)
// the kernel verifies at /api/link/v1/proof and on the hello frame. Clients own
// their own implementation of the WIRE (this file is the reference one — the
// kernel hand-rolls the same shape, and link-client-foreign hand-rolls its
// own); the point is that the payload claim names and the signing input are
// identical everywhere.
import { verifyWithPublicKey, type EndpointIdentity } from "./keystore.js";

export interface ProofFields {
  /** Endpoint identity (`iss`). */
  endpointId: string;
  /** Method/channel verb: "POST" for the HTTP plane, "WS" for the socket. */
  htm: string;
  /** Request URI or channel URI, exactly as this client sees it. */
  htu: string;
  /** Kernel challenge nonce. */
  nonce: string;
  /** Replay fence to claim (>= the last one the kernel saw). */
  seq: number;
  /** Revocation epoch the challenge was issued under. */
  epoch: number;
  /** Channel-binding hash; "" when the transport exposes no exporter. */
  cbh?: string;
  /** Freshness id; one use per proof. Defaults to a random 128-bit id. */
  jti?: string;
  /** Seconds from now until expiry (default 30). */
  ttlSeconds?: number;
}

function b64u(data: Buffer): string {
  return data.toString("base64url");
}

function nowEpochSeconds(): number {
  return Math.floor(Date.now() / 1000);
}

/**
 * Build the detached JWS: `b64u(header).b64u(payload).b64u(signature)` where
 * the signature is Ed25519 over the ASCII bytes of `b64u(header) + "." +
 * b64u(payload)`.
 */
export function buildProof(identity: EndpointIdentity, fields: ProofFields): string {
  const issuedAt = nowEpochSeconds();
  const expiresAt = issuedAt + (fields.ttlSeconds ?? 30);
  const header = b64u(Buffer.from(JSON.stringify({ alg: "EdDSA", typ: "ofac-proof+jwt" })));
  const payload = b64u(
    Buffer.from(
      JSON.stringify({
        iss: fields.endpointId,
        sub: identity.keyId,
        jti: fields.jti ?? crypto.randomUUID(),
        htm: fields.htm,
        htu: fields.htu,
        iat: issuedAt,
        nbf: issuedAt - 5,
        exp: expiresAt,
        nonce: fields.nonce,
        seq: fields.seq,
        epoch: fields.epoch,
        cbh: fields.cbh ?? "",
      }),
    ),
  );
  const signature = identity.sign(Buffer.from(`${header}.${payload}`, "ascii"));
  return `${header}.${payload}.${b64u(signature)}`;
}

/**
 * The rotation handshake: BOTH proofs sign the same
 * `{oldKid, newKid, nonce, seq}` payload — mutual agreement, not takeover.
 */
export function buildRotationProof(
  identity: EndpointIdentity,
  fields: { oldKid: string; newKid: string; nonce: string; seq: number },
): string {
  const header = b64u(Buffer.from(JSON.stringify({ alg: "EdDSA", typ: "ofac-proof+jwt" })));
  const payload = b64u(Buffer.from(JSON.stringify(fields)));
  const signature = identity.sign(Buffer.from(`${header}.${payload}`, "ascii"));
  return `${header}.${payload}.${b64u(signature)}`;
}

/** The gateway<->child channel's hello proof (htm WS, htu the gateway channel). */
/** The structural read of a detached proof: what any verifier sees before crypto. */
export interface ProofClaims {
  header: Record<string, unknown>;
  payload: Record<string, unknown>;
  signingInput: Buffer;
  signature: Buffer;
  iss: string;
  sub: string;
  jti: string;
  htm: string;
  htu: string;
  nonce: string;
  seq: number;
  epoch: number;
  cbh: string;
  iat: number;
  exp: number;
}

/** Split + decode a detached proof without trusting it; null on any malformation. */
export function parseProof(proof: string): ProofClaims | null {
  const parts = proof.split(".");
  if (parts.length !== 3 || parts.some((p) => p.length === 0 || !/^[A-Za-z0-9_-]+$/.test(p))) {
    return null;
  }
  const signature = Buffer.from(parts[2]!, "base64url");
  if (signature.length !== 64) {
    return null;
  }
  let header: unknown;
  let payload: unknown;
  try {
    header = JSON.parse(Buffer.from(parts[0]!, "base64url").toString("utf8"));
    payload = JSON.parse(Buffer.from(parts[1]!, "base64url").toString("utf8"));
  } catch {
    return null;
  }
  if (
    typeof header !== "object" ||
    header === null ||
    typeof payload !== "object" ||
    payload === null
  ) {
    return null;
  }
  const h = header as Record<string, unknown>;
  if (h["alg"] !== "EdDSA") {
    return null;
  }
  const p = payload as Record<string, unknown>;
  const str = (k: string): string | null => (typeof p[k] === "string" ? (p[k] as string) : null);
  const num = (k: string): number | null =>
    typeof p[k] === "number" && Number.isFinite(p[k]) ? (p[k] as number) : null;
  const iss = str("iss");
  const sub = str("sub");
  const jti = str("jti");
  const htm = str("htm");
  const htu = str("htu");
  const nonce = str("nonce");
  const cbh = str("cbh");
  const seq = num("seq");
  const epoch = num("epoch");
  const iat = num("iat");
  const exp = num("exp");
  if (
    iss === null ||
    sub === null ||
    jti === null ||
    htm === null ||
    htu === null ||
    nonce === null ||
    cbh === null ||
    seq === null ||
    epoch === null ||
    iat === null ||
    exp === null
  ) {
    return null;
  }
  return {
    header: h,
    payload: p,
    signingInput: Buffer.from(`${parts[0]}.${parts[1]}`, "ascii"),
    signature,
    iss,
    sub,
    jti,
    htm,
    htu,
    nonce,
    seq,
    epoch,
    cbh,
    iat,
    exp,
  };
}

export interface VerifyProofOptions {
  /** Claims as the verifier sees this request (exact match, socket schemes folded). */
  endpointId: string;
  htm: string;
  htu: string;
  /** Public key the proof must be signed by. */
  publicKeyBase64Url: string;
  /** Accepted `cbh` values; when omitted, any cbh passes (non-TLS transport). */
  cbh?: string | undefined;
  /** Seconds of clock slack (default 5). */
  skewSeconds?: number;
  /** Reject a proof expiring before `nowSeconds + minRemainingSeconds`. */
  nowSeconds?: number;
}

export type ProofRejection =
  "malformed" | "signature" | "issuer" | "channel" | "channel-binding" | "clock";

/**
 * Verify a detached proof against a known public key. This is the endpoint-side
 * mirror of the kernel's verifier — the gateway needs it because it is a
 * mini-kernel for its own fleet. Nonces/fences are the CALLER's business: a
 * verifier that has no challenge store (the gateway child channel) checks
 * signature + claims + clock + `jti` freshness and nothing else.
 */
export function verifyProof(
  proof: string,
  options: VerifyProofOptions,
): { ok: true; claims: ProofClaims } | { ok: false; reason: ProofRejection } {
  const claims = parseProof(proof);
  if (claims === null) {
    return { ok: false, reason: "malformed" };
  }
  if (!verifyWithPublicKey(options.publicKeyBase64Url, claims.signingInput, claims.signature)) {
    return { ok: false, reason: "signature" };
  }
  if (claims.iss !== options.endpointId) {
    return { ok: false, reason: "issuer" };
  }
  if (claims.htm !== options.htm || foldScheme(claims.htu) !== foldScheme(options.htu)) {
    return { ok: false, reason: "channel" };
  }
  if (options.cbh !== undefined && claims.cbh !== options.cbh) {
    return { ok: false, reason: "channel-binding" };
  }
  const now = options.nowSeconds ?? Math.floor(Date.now() / 1000);
  const skew = options.skewSeconds ?? 5;
  if (claims.iat - skew > now || claims.exp + skew < now) {
    return { ok: false, reason: "clock" };
  }
  return { ok: true, claims };
}

/** ws/wss fold to the transport they ride, so either spelling of the channel matches. */
function foldScheme(htu: string): string {
  if (htu.startsWith("ws://")) return `http://${htu.slice(5)}`;
  if (htu.startsWith("wss://")) return `https://${htu.slice(6)}`;
  return htu;
}

export function signProofForGateway(
  identity: EndpointIdentity,
  fields: {
    childWorkspaceId: string;
    htu: string;
    nonce: string;
    seq?: number;
    epoch?: number;
    cbh?: string;
  },
): string {
  return buildProof(identity, {
    endpointId: fields.childWorkspaceId,
    htm: "WS",
    htu: fields.htu,
    nonce: fields.nonce,
    seq: fields.seq ?? 0,
    epoch: fields.epoch ?? 0,
    cbh: fields.cbh ?? "",
  });
}
