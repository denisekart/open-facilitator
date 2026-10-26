// OIDC client-credentials token provider (plan 10, mode-(b) enrollment):
// mints workload tokens straight from the IdP's token endpoint. Enrollment
// only — the kernel answers with an opaque rotating link token, so runtime
// traffic never carries these. Implements LinkTokenProvider so a caller can
// reuse it for any surface that needs a short-lived workload bearer.
import type { LinkTokenProvider } from "./types.js";

export interface OidcClientCredentialsOptions {
  /** IdP token endpoint (e.g. `${issuer}/protocol/openid-connect/token`). */
  tokenUrl: string;
  clientId: string;
  clientSecret: string;
  scope?: string;
  audience?: string;
}

/**
 * POSTs `grant_type=client_credentials` form-encoded, caches the token until
 * `expires_in − 30s` (network-lag margin), and re-fetches on `refresh()` (the
 * 401-rotation seam).
 */
export function oidcClientCredentialsTokenProvider(
  opts: OidcClientCredentialsOptions,
  fetchImpl: typeof fetch = fetch,
  now: () => number = Date.now,
): LinkTokenProvider {
  let cached: { token: string; expiresAtMs: number } | null = null;

  async function mint(): Promise<string> {
    const form = new URLSearchParams({
      grant_type: "client_credentials",
      client_id: opts.clientId,
      client_secret: opts.clientSecret,
    });
    if (opts.scope) form.set("scope", opts.scope);
    if (opts.audience) form.set("audience", opts.audience);
    const response = await fetchImpl(opts.tokenUrl, {
      method: "POST",
      headers: { "content-type": "application/x-www-form-urlencoded" },
      body: form.toString(),
    });
    if (!response.ok) {
      throw new Error(`oidc token endpoint answered ${response.status}`);
    }
    const payload = (await response.json()) as { access_token: string; expires_in?: number };
    const lifetimeMs = Math.max((payload.expires_in ?? 60) - 30, 5) * 1000;
    cached = { token: payload.access_token, expiresAtMs: now() + lifetimeMs };
    return payload.access_token;
  }

  return {
    async getToken() {
      if (cached && now() < cached.expiresAtMs) return cached.token;
      return mint();
    },
    async refresh() {
      cached = null;
      await mint();
    },
  };
}
