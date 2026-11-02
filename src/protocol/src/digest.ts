// The shared approval action digest (law D2):
// SHA-256 over the RFC 8785 canonical form of the approval record
// {approvalId, action, eligibleActors, expiresAt, createdAt}, rendered
// "sha256:<64 hex>". Byte-for-byte the mirror of the kernel's
// Kernel/Shared/ApprovalDigest.cs; both harnesses cross-check against
// protocol/conformance-fixtures/canonical/approval-record-1.sha256.
// Producers (the Gateway) compute the declared `actionDigest` with
// this helper; the kernel recomputes and compares.
import { createHash } from "node:crypto";

import canonicalize from "canonicalize";

/** The record fields the digest covers (nonce is ledger-only). */
export interface ApprovalDigestRecord {
  approvalId: string;
  /** The exact action payload, digested verbatim (key order irrelevant). */
  action: unknown;
  eligibleActors: string[];
  /** RFC 3339 UTC, second precision (yyyy-MM-ddTHH:mm:ssZ). */
  expiresAt: string;
  /** RFC 3339 UTC, second precision (yyyy-MM-ddTHH:mm:ssZ). */
  createdAt: string;
}

/** Canonicalize (RFC 8785) + SHA-256 the approval record → "sha256:…". */
export function computeActionDigest(record: ApprovalDigestRecord): string {
  return digestCanonical(
    nfc({
      approvalId: record.approvalId,
      action: record.action,
      eligibleActors: record.eligibleActors,
      expiresAt: record.expiresAt,
      createdAt: record.createdAt,
    }),
  );
}

/**
 * The Result Manifest digest: SHA-256 over the
 * RFC 8785 canonical form of the manifest document, NFC-normalized — the
 * same law as `computeActionDigest` over a different document. Producers
 * serialize the manifest WITH `canonicalizeManifest` so the stored bytes
 * equal the canonical form the kernel re-verifies byte-for-byte.
 */
export function computeManifestDigest(manifest: Record<string, unknown>): string {
  return digestCanonical(nfc(manifest));
}

/** The document bytes a producer must store for `computeManifestDigest` to
 *  match: RFC 8785 canonical form, NFC-normalized strings. */
export function canonicalizeManifest(manifest: Record<string, unknown>): string {
  return canonicalize(nfc(manifest)) as string;
}

function digestCanonical(canonicalInput: unknown): string {
  const canonical = canonicalize(canonicalInput);
  return (
    "sha256:" +
    createHash("sha256")
      .update(Buffer.from(canonical as string, "utf8"))
      .digest("hex")
  );
}

// RFC 8785 leaves spelling alone; additionally mandates NFC,
// so the digest of an NFD-encoded payload equals the NFC one — mirrored by
// the .NET helper's Normalize(FormC).
function nfc(value: unknown): unknown {
  if (typeof value === "string") return value.normalize("NFC");
  if (Array.isArray(value)) return value.map(nfc);
  if (value !== null && typeof value === "object") {
    const out: Record<string, unknown> = {};
    for (const [key, inner] of Object.entries(value as Record<string, unknown>)) {
      out[key.normalize("NFC")] = nfc(inner);
    }
    return out;
  }
  return value;
}
