// The Gateway's pinned-key store: the fleet's identity half, one level below
// the kernel's enrollment row. Same law as everywhere else in this release —
// the bootstrap token authorizes ONCE (it pins the key), and from then on
// authentication is a proof of possession by that key. Fingerprints and the
// public key only; a private half never crosses this boundary.
import { createHash } from "node:crypto";
import type { Socket } from "node:net";
import type { TLSSocket } from "node:tls";
import type { ChildKeyRow, ExecutionLedger } from "./ledger.js";

/** The ledger seam the listener needs (injectable so tests need no sqlite). */
export interface ChildKeyStore {
  childKey(childId: string): ChildKeyRow | null;
  pinChildKey(childId: string, keyId: string, publicKey: string, algorithm: string): ChildKeyRow;
}

/** The pinned keys ride the execution ledger, next to the fleet shadow. */
export function keyStoreFromLedger(ledger: ExecutionLedger): ChildKeyStore {
  return {
    childKey: (childId) => ledger.childKey(childId),
    pinChildKey: (childId, keyId, publicKey, algorithm) =>
      ledger.pinChildKey(childId, keyId, publicKey, algorithm),
  };
}

/**
 * In-RAM pinned-key store: the pins live only as long as the holder does.
 * The runtime injects the ledger-backed store (pins survive restarts); test
 * kits and the adapter lifecycle harness pass none and get this — a fake
 * Gateway that forgets pins across listener swaps is exactly what those
 * tests drive, and forgetting is honest when nothing persists them.
 */
export function memoryChildKeyStore(seed: ChildKeyRow[] = []): ChildKeyStore & {
  readonly pins: ChildKeyRow[];
} {
  const rows = new Map<string, ChildKeyRow>(seed.map((row) => [row.childId, row]));
  const pins: ChildKeyRow[] = [];
  return {
    get pins(): ChildKeyRow[] {
      return pins;
    },
    childKey: (childId) => rows.get(childId) ?? null,
    pinChildKey: (childId, keyId, publicKey, algorithm) => {
      const previous = rows.get(childId);
      const now = new Date().toISOString();
      const row: ChildKeyRow = {
        childId,
        keyId,
        publicKey,
        algorithm,
        pinnedAt: previous?.pinnedAt ?? now,
        rotatedAt: previous === undefined ? null : now,
      };
      rows.set(childId, row);
      pins.push(row);
      return row;
    },
  };
}

/**
 * Channel binding for the child transport: the TLS parameters this socket
 * negotiated, `sha256:` + hex — the same claim shape the kernel computes on its
 * leg. A plain unix/TCP socket has no TLS at all, so it is the empty string
 * (which is what the kernel's own `LinkChannelBinding` yields for HTTP, and the
 * reason channel binding is carried-but-not-required in the reference
 * environment).
 */
export function channelBindingOf(socket: Socket): string {
  const tls = socket as Partial<TLSSocket>;
  if (typeof tls.getProtocol !== "function" || tls.getProtocol() === undefined) {
    return "";
  }
  const cipher = typeof tls.getCipher === "function" ? tls.getCipher() : undefined;
  const material = `${tls.getProtocol()}|${cipher?.name ?? "unknown"}`;
  return `sha256:${createHash("sha256").update(material, "utf8").digest("hex")}`;
}

/** True when the socket is a TLS one (remote children: proof-only admission). */
export function isTlsSocket(socket: Socket): boolean {
  const tls = socket as Partial<TLSSocket>;
  return typeof tls.getProtocol === "function" && tls.getProtocol() !== undefined;
}
