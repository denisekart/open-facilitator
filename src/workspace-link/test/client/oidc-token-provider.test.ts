import { describe, expect, it } from "vitest";

import { oidcClientCredentialsTokenProvider } from "../../src/core/oidc-token-provider.js";

function fakeIdp(calls: RequestInit[]): typeof fetch {
  return (async (_url: unknown, init?: RequestInit) => {
    calls.push(init ?? {});
    return new Response(JSON.stringify({ access_token: `jwt-${calls.length}`, expires_in: 120 }), {
      status: 200,
      headers: { "content-type": "application/json" },
    });
  }) as typeof fetch;
}

describe("oidcClientCredentialsTokenProvider", () => {
  it("POSTs form-encoded client_credentials and caches until expiry margin", async () => {
    const calls: RequestInit[] = [];
    let clock = 1_000_000;
    const provider = oidcClientCredentialsTokenProvider(
      { tokenUrl: "http://idp/token", clientId: "cid", clientSecret: "sec" },
      fakeIdp(calls),
      () => clock,
    );

    expect(await provider.getToken()).toBe("jwt-1");
    const form = new URLSearchParams(String(calls[0]?.body));
    expect(form.get("grant_type")).toBe("client_credentials");
    expect(form.get("client_id")).toBe("cid");
    expect(form.get("client_secret")).toBe("sec");

    // 120s lifetime − 30s margin: still cached at +89s.
    clock += 89_000;
    expect(await provider.getToken()).toBe("jwt-1");

    // Past the margin: re-mints.
    clock += 2_000;
    expect(await provider.getToken()).toBe("jwt-2");
  });

  it("refresh() forces a re-mint and forwards scope/audience", async () => {
    const calls: RequestInit[] = [];
    const provider = oidcClientCredentialsTokenProvider(
      { tokenUrl: "http://idp/token", clientId: "cid", clientSecret: "sec", scope: "s", audience: "a" },
      fakeIdp(calls),
    );
    await provider.getToken();
    await provider.refresh!();
    expect(calls).toHaveLength(2);
    const form = new URLSearchParams(String(calls[0]?.body));
    expect(form.get("scope")).toBe("s");
    expect(form.get("audience")).toBe("a");
  });

  it("a non-2xx token response throws (enroll surfaces it as a config-time error)", async () => {
    const provider = oidcClientCredentialsTokenProvider(
      { tokenUrl: "http://idp/token", clientId: "cid", clientSecret: "sec" },
      (async () => new Response("invalid_client", { status: 401 })) as typeof fetch,
    );
    await expect(provider.getToken()).rejects.toThrow("401");
  });
});
