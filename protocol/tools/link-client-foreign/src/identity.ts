// The foreign client's Ed25519 identity: generated in memory, optionally
// persisted so a later phase can prove the PREVIOUS key stopped working.
//
// Nothing here is imported from the kernel or the workspace fleet — the point
// of this artifact is that a foreign implementation reaches the same wire by
// reading only protocol/openapi/link-v1.yaml and protocol/json-schema. Ed25519
// itself is a platform primitive (node:crypto); what this file hand-rolls is
// the DER framing around the raw 32-byte key and the `sha256:` fingerprint law.
import { createHash, createPrivateKey, createPublicKey, randomBytes, sign, verify, type KeyObject } from "node:crypto";

/** PKCS#8 prefix of a bare 32-byte Ed25519 private key seed (RFC 8410). */
const PKCS8_PREFIX = Buffer.from("302e020100300506032b657004220420", "hex");

/** SPKI prefix of a bare 32-byte Ed25519 public key (RFC 8410). */
const SPKI_PREFIX = Buffer.from("302a300506032b6570032100", "hex");

export interface IdentityFile {
  v: 1;
  keyId: string;
  algorithm: "ed25519";
  publicKey: string;
  seed: string;
}

export interface EndpointIdentity {
  /** `sha256:` + lowercase hex of the raw 32-byte public key — the fingerprint the kernel stores. */
  keyId: string;
  /** Raw public key, base64url without padding (43 chars) — what enroll takes. */
  publicKeyBase64Url: string;
  /** Ed25519 signature over the raw bytes. */
  sign: (payload: Uint8Array) => Buffer;
  /** The serializable form (persisted so a later process can re-prove with it). */
  toFile: () => IdentityFile;
}

function privateKeyFromSeed(seed: Buffer): KeyObject {
  return createPrivateKey({ key: Buffer.concat([PKCS8_PREFIX, seed]), format: "der", type: "pkcs8" });
}

function rawPublicOf(seed: Buffer): Buffer {
  const spki = createPublicKey(privateKeyFromSeed(seed)).export({ format: "der", type: "spki" });
  return Buffer.from(spki.subarray(SPKI_PREFIX.length));
}

function fingerprintOf(rawPublicKey: Buffer): string {
  return "sha256:" + createHash("sha256").update(rawPublicKey).digest("hex");
}

function build(seed: Buffer): EndpointIdentity {
  const raw = rawPublicOf(seed);
  const publicKeyBase64Url = raw.toString("base64url");
  const keyId = fingerprintOf(raw);
  return {
    keyId,
    publicKeyBase64Url,
    sign: (payload) => sign(null, Buffer.from(payload), privateKeyFromSeed(seed)),
    toFile: () => ({ v: 1, keyId, algorithm: "ed25519", publicKey: publicKeyBase64Url, seed: seed.toString("base64url") }),
  };
}

/** A fresh throwaway identity (32 random bytes of seed, nothing on disk). */
export function generateIdentity(): EndpointIdentity {
  return build(randomBytes(32));
}

/** Rebuild an identity from a persisted file — used to show the retired key is really dead. */
export function identityFromFile(file: IdentityFile): EndpointIdentity {
  if (file?.v !== 1 || file.algorithm !== "ed25519" || typeof file.seed !== "string") {
    throw new Error("identity file is not a v1 ed25519 identity");
  }

  const seed = Buffer.from(file.seed, "base64url");
  if (seed.length !== 32) {
    throw new Error("identity file seed is not 32 bytes");
  }

  const identity = build(seed);
  if (identity.publicKeyBase64Url !== file.publicKey || identity.keyId !== file.keyId) {
    throw new Error("identity file header disagrees with its key material");
  }

  return identity;
}

/** Verify a detached Ed25519 signature against a base64url raw public key. */
export function verifySignature(publicKeyBase64Url: string, payload: Uint8Array, signature: Uint8Array): boolean {
  const raw = Buffer.from(publicKeyBase64Url, "base64url");
  if (raw.length !== 32 || signature.length !== 64) {
    return false;
  }

  try {
    return verify(
      null,
      Buffer.from(payload),
      createPublicKey({ key: Buffer.concat([SPKI_PREFIX, raw]), format: "der", type: "spki" }),
      Buffer.from(signature),
    );
  } catch {
    return false;
  }
}
