// The endpoint keystore: the ONLY Ed25519-on-disk code in the TS fleet, so the
// gateway, adapters and harnesses import it instead of copying it (node:crypto
// has Ed25519 natively).
//
// The keystore law: the stateDir IS the logical instance. No backup, no
// sealing tiers — a plaintext 0600 file inside a directory whose owner is the
// security boundary. Migration is `mv`. Loss of the directory is loss of
// identity: rejoin by re-enrolling with a fresh key.
import { mkdirSync, readFileSync, writeFileSync, existsSync } from "node:fs";
import { join } from "node:path";
import {
  createPrivateKey,
  createPublicKey,
  sign,
  verify,
  randomBytes,
  createHash,
} from "node:crypto";

const PKCS8_PREFIX = Buffer.from("302e020100300506032b657004220420", "hex");
const SPKI_PREFIX = Buffer.from("302a300506032b6570032100", "hex");

export interface EndpointIdentity {
  /** `sha256:` + lowercase hex of the raw 32-byte public key — the same fingerprint the kernel stores. */
  keyId: string;
  /** Raw public key, base64url without padding (43 chars). */
  publicKeyBase64Url: string;
  /** Sign with the private half. */
  sign: (payload: Buffer | Uint8Array) => Buffer;
}

interface IdentityFile {
  v: 1;
  keyId: string;
  algorithm: "ed25519";
  publicKey: string;
  seed: string;
}

function seedToPrivate(seed: Buffer): Buffer {
  return Buffer.concat([PKCS8_PREFIX, seed]);
}

function publicRawOf(seed: Buffer): Buffer {
  const key = createPrivateKey({ key: seedToPrivate(seed), format: "der", type: "pkcs8" });
  const spki = createPublicKey(key).export({ format: "der", type: "spki" });
  return Buffer.from(spki.subarray(SPKI_PREFIX.length));
}

function keyIdOf(rawPublicKey: Buffer): string {
  return "sha256:" + createHash("sha256").update(rawPublicKey).digest("hex");
}

/**
 * Load the identity from `<stateDir>/identity.key`, generating (and 0600-writing)
 * one when the file is missing. A corrupt file THROWS — silently minting a new
 * identity would silently orphan the enrollment the kernel still trusts.
 */
export function loadOrCreateIdentity(stateDir: string): EndpointIdentity {
  const path = join(stateDir, "identity.key");
  if (existsSync(path)) {
    let parsed: IdentityFile;
    try {
      parsed = JSON.parse(readFileSync(path, "utf8")) as IdentityFile;
    } catch {
      throw new Error(
        `keystore: ${path} is not readable JSON — restore it or remove it deliberately (loss of identity is a rejoin, not a silent re-enroll)`,
      );
    }

    if (
      parsed?.v !== 1 ||
      parsed.algorithm !== "ed25519" ||
      typeof parsed.seed !== "string" ||
      typeof parsed.publicKey !== "string"
    ) {
      throw new Error(`keystore: ${path} is not a v1 ed25519 identity file`);
    }

    const seed = Buffer.from(parsed.seed, "base64url");
    if (seed.length !== 32) {
      throw new Error(`keystore: ${path} seed is not 32 bytes`);
    }

    const raw = publicRawOf(seed);
    const publicKeyBase64Url = raw.toString("base64url");
    const keyId = keyIdOf(raw);
    if (parsed.publicKey !== publicKeyBase64Url || parsed.keyId !== keyId) {
      throw new Error(`keystore: ${path} header disagrees with the key material`);
    }

    return {
      keyId,
      publicKeyBase64Url,
      sign: (payload) =>
        sign(
          null,
          Buffer.from(payload),
          createPrivateKey({ key: seedToPrivate(seed), format: "der", type: "pkcs8" }),
        ),
    };
  }

  const seed = randomBytes(32);
  const raw = publicRawOf(seed);
  const identity: IdentityFile = {
    v: 1,
    keyId: keyIdOf(raw),
    algorithm: "ed25519",
    publicKey: raw.toString("base64url"),
    seed: seed.toString("base64url"),
  };
  mkdirSync(stateDir, { recursive: true });
  writeFileSync(path, JSON.stringify(identity) + "\n", { mode: 0o600, flag: "wx" });
  const privateKey = createPrivateKey({ key: seedToPrivate(seed), format: "der", type: "pkcs8" });
  return {
    keyId: identity.keyId,
    publicKeyBase64Url: identity.publicKey,
    sign: (payload) => sign(null, Buffer.from(payload), privateKey),
  };
}

/** Verify a detached signature against a base64url raw public key (gateway pinning side). */
export function verifyWithPublicKey(
  publicKeyBase64Url: string,
  payload: Buffer | Uint8Array,
  signature: Buffer | Uint8Array,
): boolean {
  const raw = Buffer.from(publicKeyBase64Url, "base64url");
  if (raw.length !== 32) {
    return false;
  }

  const spki = Buffer.concat([SPKI_PREFIX, raw]);
  try {
    return verify(
      null,
      Buffer.from(payload),
      createPublicKey({ key: spki, format: "der", type: "spki" }),
      Buffer.from(signature),
    );
  } catch {
    return false;
  }
}

/** The fingerprint of a base64url raw public key (`sha256:` + lowercase hex). */
export function keyIdFromPublicKey(publicKeyBase64Url: string): string {
  return keyIdOf(Buffer.from(publicKeyBase64Url, "base64url"));
}
