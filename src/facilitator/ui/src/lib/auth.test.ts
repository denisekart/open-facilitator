import { describe, expect, it } from "vitest";

import { decodeTokenClaims } from "./auth.svelte.js";

function jwt(payload: unknown): string {
  const b64 = (input: string) =>
    btoa(input).replace(/\+/g, "-").replace(/\//g, "_").replace(/=+$/, "");
  return `${b64('{"alg":"RS256"}')}.${b64(JSON.stringify(payload))}.sig`;
}

describe("decodeTokenClaims", () => {
  it("reads the access-token claims the realm mappers emit", () => {
    const claims = decodeTokenClaims(
      jwt({ realm_access: { roles: ["framework-admin"] }, groups: ["/teams/team-alpha"] }),
    );
    expect(claims.realm_access?.roles).toContain("framework-admin");
    expect(claims.groups).toContain("/teams/team-alpha");
  });

  it("is total on missing or malformed tokens", () => {
    expect(decodeTokenClaims(null)).toEqual({});
    expect(decodeTokenClaims("not-a-jwt")).toEqual({});
    expect(decodeTokenClaims("a.!!!not-base64!!!.c")).toEqual({});
  });
});
