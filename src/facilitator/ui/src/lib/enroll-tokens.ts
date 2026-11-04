import type { EnrollmentTokenRow } from "./types.js";

/** Client-side status of a minted enrollment token (precedence: revoked > expired > consumed > fresh). */
export type EnrollmentTokenStatus = "revoked" | "expired" | "consumed" | "fresh";

/**
 * Derive the token row's lifecycle status. `now` is the comparison instant
 * (injectable so tests pin expiry boundaries); a missing/unparseable
 * expiresAt never counts as expired.
 */
export function enrollmentTokenStatus(
  row: EnrollmentTokenRow,
  now: Date = new Date(),
): EnrollmentTokenStatus {
  if (row.revokedAt !== null) {
    return "revoked";
  }
  const expires = Date.parse(row.expiresAt);
  if (Number.isFinite(expires) && expires <= now.getTime()) {
    return "expired";
  }
  return row.consumedAt !== null ? "consumed" : "fresh";
}

/** The mint-card label for a status row (consumed carries the bound endpoint). */
export function enrollmentStatusLabel(row: EnrollmentTokenRow, now?: Date): string {
  const status = enrollmentTokenStatus(row, now);
  return status === "consumed" ? `consumed → ${row.consumedEndpointId ?? "?"}` : status;
}
