// Test seams for the pinned-key law: the shipped in-RAM ChildKeyStore (so
// listener tests need no ledger file) plus a child identity to pin and prove
// with.
import { mkdtempSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { loadOrCreateIdentity, type EndpointIdentity } from "@ofac/workspace-link";
import { memoryChildKeyStore, type ChildKeyRow } from "../../src/index.js";

/** The shipped store already records pins in order — the seam is the name. */
export const memoryKeyStore = memoryChildKeyStore;

/** A child identity, as a child's keystore would hold it (fresh stateDir). */
export function childIdentity(): EndpointIdentity {
  return loadOrCreateIdentity(mkdtempSync(join(tmpdir(), "gw-lsn-id-")));
}

/** The ledger row shape a seeded pin takes. */
export function seededKey(childId: string, identity: EndpointIdentity): ChildKeyRow {
  return {
    childId,
    keyId: identity.keyId,
    publicKey: identity.publicKeyBase64Url,
    algorithm: "ed25519",
    pinnedAt: new Date().toISOString(),
    rotatedAt: null,
  };
}
