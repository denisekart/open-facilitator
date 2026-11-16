// The detached EdDSA JWS proof of possession, hand-rolled: this project is the
// conformance suite's cross-language check, so it derives the wire from
// protocol/openapi/link-v1.yaml + protocol/json-schema alone and references no
// kernel type and no workspace module. The claim names, the protected header,
// and the signing input are the contract; everything below is independent of
// how the kernel builds the same shape.
//
// RFC 8037 subset of RFC 7515: `b64u(header).b64u(payload).b64u(sig)` where the
// signature is the 64-byte Ed25519 signature over the ASCII bytes of
// `b64u(header) + "." + b64u(payload)` — a detached JWS, so the payload bytes
// that travel are the b64u segments themselves.
import { randomUUID } from "node:crypto";

import type { EndpointIdentity } from "./identity.js";

/** The protected header every proof carries. */
export const PROOF_HEADER = { alg: "EdDSA", typ: "ofac-proof+jwt" } as const;

export interface ProofFields {
  /** Endpoint identity (`iss`) — the endpointId the kernel knows. */
  endpointId: string;
  /** Method/channel verb: "POST" for the HTTP proof route, "WS" for the socket. */
  htm: string;
  /** Request URI or channel URI, exactly as this client sees it. */
  htu: string;
  /** The kernel's single-use challenge nonce. */
  nonce: string;
  /** Replay fence claimed — the fence the challenge was minted against. */
  seq: number;
  /** Revocation epoch claimed — the epoch the challenge was minted against. */
  epoch: number;
  /** Channel-binding hash; "" when the transport exposes no TLS exporter. */
  cbh?: string;
  /** Freshness id (one use per proof); defaults to a random uuid. */
  jti?: string;
  /** Seconds from issue until expiry (default 30). */
  ttlSeconds?: number;
  /** Override for tests: the `iat` second. */
  issuedAtSeconds?: number;
}

function base64Url(bytes: Uint8Array): string {
  return Buffer.from(bytes).toString("base64url");
}

/**
 * The wire payload of a challenge response: the kernel carries the state a
 * proof may claim inside the opaque nonce, `<epoch>.<seq>.<random>`, so a
 * borrowed or stale nonce cannot drag the fence backwards.
 */
export function parseChallengeNonce(nonce: string): { epoch: number; seq: number } {
  const parts = nonce.split(".");
  const [epoch, seq] = parts;
  if (parts.length !== 3 || !/^\d+$/.test(epoch ?? "") || !/^\d+$/.test(seq ?? "")) {
    throw new Error(`challenge nonce is not \`<epoch>.<seq>.<random>\`: ${nonce}`);
  }

  return { epoch: Number(epoch), seq: Number(seq) };
}

/**
 * Build the detached JWS a keyed endpoint authenticates with. The payload
 * members are exactly the locked set: `iss, sub, jti, htm, htu, iat, nbf, exp,
 * nonce, seq, epoch, cbh`.
 */
export function buildProof(identity: EndpointIdentity, fields: ProofFields): string {
  const issuedAt = fields.issuedAtSeconds ?? Math.floor(Date.now() / 1000);
  const header = base64Url(Buffer.from(JSON.stringify(PROOF_HEADER), "utf8"));
  const payload = base64Url(Buffer.from(JSON.stringify({
    iss: fields.endpointId,
    sub: identity.keyId,
    jti: fields.jti ?? randomUUID(),
    htm: fields.htm,
    htu: fields.htu,
    iat: issuedAt,
    nbf: issuedAt - 5,
    exp: issuedAt + (fields.ttlSeconds ?? 30),
    nonce: fields.nonce,
    seq: fields.seq,
    epoch: fields.epoch,
    cbh: fields.cbh ?? "",
  }), "utf8"));
  const signingInput = Buffer.from(`${header}.${payload}`, "ascii");
  return `${header}.${payload}.${base64Url(identity.sign(signingInput))}`;
}

export interface ParsedProof {
  header: Record<string, unknown>;
  payload: Record<string, unknown>;
  signingInput: string;
  signature: Buffer;
}

/**
 * Structural parse of a detached proof (what this client re-reads of its own
 * output and of any proof it is shown): three non-empty base64url segments,
 * JSON header and payload, a 64-byte signature.
 */
export function parseProof(proof: string): ParsedProof | null {
  const parts = proof.split(".");
  if (parts.length !== 3 || parts.some((part) => part.length === 0 || !/^[A-Za-z0-9_-]+$/.test(part))) {
    return null;
  }

  const signature = Buffer.from(parts[2]!, "base64url");
  if (signature.length !== 64) {
    return null;
  }

  try {
    const header = JSON.parse(Buffer.from(parts[0]!, "base64url").toString("utf8")) as Record<string, unknown>;
    const payload = JSON.parse(Buffer.from(parts[1]!, "base64url").toString("utf8")) as Record<string, unknown>;
    return { header, payload, signingInput: `${parts[0]}.${parts[1]}`, signature };
  } catch {
    return null;
  }
}
