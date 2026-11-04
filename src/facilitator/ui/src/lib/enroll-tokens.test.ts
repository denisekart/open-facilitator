import { describe, expect, it } from "vitest";

import {
  enrollmentStatusLabel,
  enrollmentTokenStatus,
  type EnrollmentTokenStatus,
} from "./enroll-tokens.js";
import type { EnrollmentTokenRow } from "./types.js";

const NOW = new Date("2026-11-04T12:00:00Z");

function row(over: Partial<EnrollmentTokenRow> = {}): EnrollmentTokenRow {
  return {
    id: "0194f0a0-0000-7000-8000-000000000001",
    label: "living-room pc",
    team: "home",
    role: "gateway",
    createdAt: "2026-11-04T11:00:00Z",
    expiresAt: "2026-11-04T12:00:00Z",
    consumedAt: null,
    consumedEndpointId: null,
    revokedAt: null,
    ...over,
  };
}

describe("enrollmentTokenStatus", () => {
  it("is fresh before expiry and before first use", () => {
    expect(
      enrollmentTokenStatus(row({ expiresAt: "2026-11-04T13:00:00Z" }), NOW),
    ).toBe<EnrollmentTokenStatus>("fresh");
  });

  it("is consumed once consumedAt is set", () => {
    expect(
      enrollmentTokenStatus(
        row({
          expiresAt: "2026-11-04T13:00:00Z",
          consumedAt: "2026-11-04T11:30:00Z",
          consumedEndpointId: "gw.pc1",
        }),
        NOW,
      ),
    ).toBe("consumed");
  });

  it("is expired once expiresAt has passed", () => {
    expect(enrollmentTokenStatus(row({ expiresAt: "2026-11-04T11:59:59Z" }), NOW)).toBe("expired");
  });

  it("revoked outranks expired and consumed", () => {
    const revoked = row({
      expiresAt: "2026-11-04T11:00:01Z",
      consumedAt: "2026-11-04T10:30:00Z",
      consumedEndpointId: "gw.pc1",
      revokedAt: "2026-11-04T11:45:00Z",
    });
    expect(enrollmentTokenStatus(revoked, NOW)).toBe("revoked");
  });

  it("a token consumed then expired reads as expired (expired outranks consumed)", () => {
    expect(
      enrollmentTokenStatus(
        row({
          expiresAt: "2026-11-04T11:59:59Z",
          consumedAt: "2026-11-04T10:00:00Z",
          consumedEndpointId: "gw.pc1",
        }),
        NOW,
      ),
    ).toBe("expired");
  });
});

describe("enrollmentStatusLabel", () => {
  it("names the bound endpoint for consumed rows", () => {
    expect(
      enrollmentStatusLabel(
        row({
          expiresAt: "2026-11-04T13:00:00Z",
          consumedAt: "2026-11-04T11:30:00Z",
          consumedEndpointId: "gw.pc1",
        }),
        NOW,
      ),
    ).toBe("consumed → gw.pc1");
  });
});
