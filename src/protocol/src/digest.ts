// The shared approval action digest (appendix §7.1–7.2, Plan 11 law D2):
// SHA-256 over the RFC 8785 canonical form of the approval record
// {approvalId, action, eligibleActors, expiresAt, createdAt}, rendered
// "sha256:<64 hex>". Byte-for-byte the mirror of the kernel's
// Kernel/Shared/ApprovalDigest.cs; both harnesses cross-check against
// protocol/conformance-fixtures/canonical/approval-record-1.sha256.
// Producers (Plan 12's Gateway) compute the declared `actionDigest` with
// this helper; the kernel recomputes and compares.
import { createHash } from "node:crypto";

import canonicalize from "canonicalize";

/** The §7.2 record fields the digest covers (nonce is ledger-only, §7.3). */
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
  const canonical = canonicalize({
    approvalId: nfc(record.approvalId),
    action: nfc(record.action),
    eligibleActors: record.eligibleActors.map(nfc),
    expiresAt: record.expiresAt,
    createdAt: record.createdAt,
  });
  return (
    "sha256:" +
    createHash("sha256")
      .update(Buffer.from(canonical as string, "utf8"))
      .digest("hex")
  );
}

// RFC 8785 leaves spelling alone; appendix §7.1 additionally mandates NFC,
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
