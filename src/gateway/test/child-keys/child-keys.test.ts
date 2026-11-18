// The pinned-key store seams + the TLS channel-binding helpers: pure unit
// tier (ledger-backed store on a temp file, fake sockets for the TLS probes).
import { mkdtempSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";
import type { Socket } from "node:net";
import { afterEach, describe, expect, it } from "vitest";

import { ExecutionLedger } from "../../src/ledger.js";
import {
  channelBindingOf,
  isTlsSocket,
  keyStoreFromLedger,
  memoryChildKeyStore,
} from "../../src/child-keys.js";

let ledger: ExecutionLedger | null = null;

afterEach(() => {
  ledger?.close();
  ledger = null;
});

function open(): ExecutionLedger {
  ledger = new ExecutionLedger(join(mkdtempSync(join(tmpdir(), "gw-keys-")), "gateway.sqlite3"));
  return ledger;
}

describe("keyStoreFromLedger", () => {
  it("reads and pins through the execution ledger", () => {
    const l = open();
    const store = keyStoreFromLedger(l);
    expect(store.childKey("gw.t/alpha")).toBeNull();
    const pinned = store.pinChildKey("gw.t/alpha", "kid-1", "pk-1", "ed25519");
    expect(pinned).toMatchObject({
      childId: "gw.t/alpha",
      keyId: "kid-1",
      publicKey: "pk-1",
      rotatedAt: null,
    });
    expect(store.childKey("gw.t/alpha")?.keyId).toBe("kid-1");
  });

  it("a re-pin keeps pinnedAt honest, stamps rotatedAt, and surfaces in childKeys()", () => {
    const l = open();
    const store = keyStoreFromLedger(l);
    const first = store.pinChildKey("gw.t/alpha", "kid-1", "pk-1", "ed25519");
    store.pinChildKey("gw.t/beth", "kid-2", "pk-2", "ed25519");
    const rotated = store.pinChildKey("gw.t/alpha", "kid-9", "pk-9", "ed25519");
    expect(rotated.pinnedAt).toBe(first.pinnedAt); // first pin's moment survives
    expect(rotated.rotatedAt).not.toBeNull();
    expect(rotated.keyId).toBe("kid-9");
    expect(l.childKeys().map((r) => r.childId)).toEqual(["gw.t/alpha", "gw.t/beth"]);
    expect(l.childKeys()[0]).toMatchObject({ keyId: "kid-9", rotatedAt: rotated.rotatedAt });
  });
});

describe("memoryChildKeyStore", () => {
  it("records pins in order and re-pins with rotatedAt", () => {
    const store = memoryChildKeyStore();
    expect(store.childKey("gw.t/x")).toBeNull();
    store.pinChildKey("gw.t/x", "kid-a", "pk-a", "ed25519");
    const rotated = store.pinChildKey("gw.t/x", "kid-b", "pk-b", "ed25519");
    expect(rotated.rotatedAt).not.toBeNull();
    expect(store.pins.map((p) => p.keyId)).toEqual(["kid-a", "kid-b"]);
  });
});

describe("TLS socket probes", () => {
  const plain = { } as Socket;
  const tlsSocket = {
    getProtocol: () => "TLSv1.3",
    getCipher: () => ({ name: "TLS_AES_256_GCM_SHA384", version: "TLSv1.3" }),
  } as unknown as Socket;
  const tlsNoCipher = { getProtocol: () => "TLSv1.2" } as unknown as Socket;

  it("a plain socket has no channel binding", () => {
    expect(isTlsSocket(plain)).toBe(false);
    expect(channelBindingOf(plain)).toBe("");
  });

  it("a TLS socket hashes protocol + cipher", () => {
    expect(isTlsSocket(tlsSocket)).toBe(true);
    expect(channelBindingOf(tlsSocket)).toMatch(/^sha256:[0-9a-f]{64}$/);
    // The exact material is law: protocol|cipher, stable per negotiation.
    expect(channelBindingOf(tlsSocket)).toBe(channelBindingOf(tlsSocket));
  });

  it("a TLS socket without a readable cipher records 'unknown'", () => {
    expect(channelBindingOf(tlsNoCipher)).toMatch(/^sha256:[0-9a-f]{64}$/);
    expect(channelBindingOf(tlsNoCipher)).not.toBe(channelBindingOf(tlsSocket));
  });
});
