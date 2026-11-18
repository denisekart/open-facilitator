// The keystore law: `<stateDir>/identity.key` IS the logical instance. Covers
// mint-on-first-use (0600), stable reload, the four corrupt-file refusals
// (silent re-mint would silently orphan the enrollment the kernel still
// trusts), sign/verify round-trips, and the kid fingerprint.
import { afterAll, describe, expect, it } from "vitest";
import { existsSync, readFileSync, rmSync, statSync, writeFileSync } from "node:fs";
import { createHash, randomBytes } from "node:crypto";
import { join } from "node:path";
import { keyIdFromPublicKey, loadOrCreateIdentity, verifyWithPublicKey } from "../../src/index.js";
import { cleanUp, tempDir } from "./fixtures.js";

afterAll(() => {
  cleanUp();
});

describe("loadOrCreateIdentity", () => {
  it("mints on first use: 0600 file, recursive dirs, keyId over the raw public key", () => {
    const dir = join(tempDir(), "nested", "state");
    const identity = loadOrCreateIdentity(dir);
    const path = join(dir, "identity.key");
    expect(existsSync(path)).toBe(true);
    const file = JSON.parse(readFileSync(path, "utf8")) as Record<string, unknown>;
    expect(file["v"]).toBe(1);
    expect(file["algorithm"]).toBe("ed25519");
    expect(file["keyId"]).toBe(identity.keyId);
    expect(file["publicKey"]).toBe(identity.publicKeyBase64Url);
    expect((file["seed"] as string).length).toBe(43); // 32 bytes, base64url
    expect(identity.keyId).toBe(
      `sha256:${createHash("sha256").update(Buffer.from(identity.publicKeyBase64Url, "base64url")).digest("hex")}`,
    );
    expect(identity.publicKeyBase64Url).toHaveLength(43);
    // Owner-only file inside a directory whose owner is the boundary.
    expect(readFileSync(path, { encoding: "utf8" }).endsWith("\n")).toBe(true);
    expect(statSync(path).mode & 0o077).toBe(0);
  });

  it("reloads the same identity across processes (a restart re-proves, never re-mints)", () => {
    const dir = tempDir();
    const first = loadOrCreateIdentity(dir);
    const second = loadOrCreateIdentity(dir);
    expect(second.keyId).toBe(first.keyId);
    expect(second.publicKeyBase64Url).toBe(first.publicKeyBase64Url);
    const payload = Buffer.from("restart re-proves");
    expect(verifyWithPublicKey(first.publicKeyBase64Url, payload, second.sign(payload))).toBe(true);
  });

  it("a removed directory is a rejoin: a fresh key, not a resurrected one", () => {
    const dir = tempDir();
    const first = loadOrCreateIdentity(dir);
    rmSync(join(dir, "identity.key"));
    const second = loadOrCreateIdentity(dir);
    expect(second.keyId).not.toBe(first.keyId);
  });

  const corrupt: [string, string][] = [
    ["unreadable JSON", "not json at all"],
    ["empty file", ""],
  ];
  for (const [name, text] of corrupt) {
    it(`throws on ${name} rather than silently minting a new identity`, () => {
      const dir = tempDir();
      loadOrCreateIdentity(dir);
      writeFileSync(join(dir, "identity.key"), text);
      expect(() => loadOrCreateIdentity(dir)).toThrow(/not readable JSON/);
    });
  }

  const invalid: [string, (file: Record<string, unknown>) => Record<string, unknown>][] = [
    ["a non-v1 file", (f) => ({ ...f, v: 2 })],
    ["a wrong algorithm", (f) => ({ ...f, algorithm: "ecdsa-p256" })],
    ["a missing seed", (f) => ({ ...f, seed: undefined })],
    ["a seed that is not 32 bytes", (f) => ({ ...f, seed: "ab" })],
    [
      "a header that disagrees with the key material",
      (f) => ({ ...f, publicKey: Buffer.alloc(32).toString("base64url") }),
    ],
    [
      "a keyId that disagrees with the key material",
      (f) => ({ ...f, keyId: "sha256:" + "00".repeat(32) }),
    ],
    ["a missing publicKey", (f) => ({ ...f, publicKey: undefined })],
    ["a null file", () => null as unknown as Record<string, unknown>],
  ];
  for (const [name, patch] of invalid) {
    it(`throws on ${name}`, () => {
      const dir = tempDir();
      loadOrCreateIdentity(dir);
      const path = join(dir, "identity.key");
      writeFileSync(
        path,
        JSON.stringify(patch(JSON.parse(readFileSync(path, "utf8")) as Record<string, unknown>)),
      );
      expect(() => loadOrCreateIdentity(dir)).toThrow(
        /ed25519 identity file|seed is not 32 bytes|header disagrees/,
      );
    });
  }
});

describe("sign / verifyWithPublicKey", () => {
  it("round-trips a detached signature and rejects a tampered payload", () => {
    const identity = loadOrCreateIdentity(tempDir());
    const payload = Buffer.from("proof-of-possession");
    const signature = identity.sign(payload);
    expect(signature).toHaveLength(64);
    expect(verifyWithPublicKey(identity.publicKeyBase64Url, payload, signature)).toBe(true);
    expect(
      verifyWithPublicKey(
        identity.publicKeyBase64Url,
        Buffer.from("proof-of-posssesion"),
        signature,
      ),
    ).toBe(false);
    // A signature from another key does not verify.
    const other = loadOrCreateIdentity(tempDir());
    expect(verifyWithPublicKey(other.publicKeyBase64Url, payload, signature)).toBe(false);
  });

  it("accepts Uint8Array inputs and refuses malformed keys without throwing", () => {
    const identity = loadOrCreateIdentity(tempDir());
    const payload = new Uint8Array([1, 2, 3]);
    const signature = new Uint8Array(identity.sign(Buffer.from(payload)));
    expect(verifyWithPublicKey(identity.publicKeyBase64Url, payload, signature)).toBe(true);
    // Not a 32-byte raw key: false, not an exception.
    expect(verifyWithPublicKey("tooshort", payload, signature)).toBe(false);
    expect(verifyWithPublicKey("", payload, signature)).toBe(false);
    // 32 bytes, but a garbage signature length: node:crypto throws inside.
    expect(verifyWithPublicKey(identity.publicKeyBase64Url, payload, Buffer.alloc(7))).toBe(false);
  });
});

describe("keyIdFromPublicKey", () => {
  it("is the fingerprint the kernel stores", () => {
    const identity = loadOrCreateIdentity(tempDir());
    expect(keyIdFromPublicKey(identity.publicKeyBase64Url)).toBe(identity.keyId);
    expect(keyIdFromPublicKey(Buffer.alloc(32).toString("base64url"))).toBe(
      `sha256:${createHash("sha256").update(Buffer.alloc(32)).digest("hex")}`,
    );
    expect(keyIdFromPublicKey(randomBytes(32).toString("base64url"))).toMatch(
      /^sha256:[0-9a-f]{64}$/,
    );
  });
});

describe("rotation (a new identity in a new state dir)", () => {
  it("two keystores carry independent keys, and the rotation proof binds both kids", () => {
    const oldIdentity = loadOrCreateIdentity(tempDir());
    const newIdentity = loadOrCreateIdentity(tempDir());
    expect(oldIdentity.keyId).not.toBe(newIdentity.keyId);
    const payload = Buffer.from(`${oldIdentity.keyId}|${newIdentity.keyId}|nonce`);
    const byOld = oldIdentity.sign(payload);
    const byNew = newIdentity.sign(payload);
    expect(verifyWithPublicKey(oldIdentity.publicKeyBase64Url, payload, byOld)).toBe(true);
    expect(verifyWithPublicKey(newIdentity.publicKeyBase64Url, payload, byNew)).toBe(true);
    // Mutual agreement: neither signature verifies under the other half.
    expect(verifyWithPublicKey(newIdentity.publicKeyBase64Url, payload, byOld)).toBe(false);
    expect(verifyWithPublicKey(oldIdentity.publicKeyBase64Url, payload, byNew)).toBe(false);
    // And the new kid is derivable from the material alone (the kernel's side).
    expect(keyIdFromPublicKey(newIdentity.publicKeyBase64Url)).toBe(newIdentity.keyId);
  });
});
