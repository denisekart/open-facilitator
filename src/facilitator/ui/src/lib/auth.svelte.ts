import { UserManager, type User } from "oidc-client-ts";

import { makeApiFetch, toApiError, uiBootstrap, type Fetcher } from "./api.js";

/** Realm roles that grant the admin surface (mirrors admin.read/admin.mutate bindings). */
const OPERATOR_ROLES = ["framework-admin", "kernel-operator"];

/** Realm role that grants the AiDeck chat surface (mirrors the chat.* bindings). */
const AI_OPERATOR_ROLES = ["ai-operator"];

/** Realm role that grants enrollment-token mint/revoke (mirrors the enrollment-tokens.manage binding — strictly framework-admin). */
const ENROLLMENT_ADMIN_ROLES = ["framework-admin"];

interface TokenClaims {
  groups?: string[];
  realm_access?: { roles?: string[] };
}

/**
 * Decode an unsigned JWT payload (base64url, no signature check — the kernel
 * validates). The realm's group/role mappers claim into the ACCESS token
 * (`id.token.claim:false`), so UI gating must read the access token: exactly
 * the claims the role gate itself evaluates.
 */
export function decodeTokenClaims(jwt: string | undefined | null): TokenClaims {
  const payload = jwt?.split(".")[1];
  if (!payload) {
    return {};
  }
  try {
    const json = atob(payload.replace(/-/g, "+").replace(/_/g, "/"));
    return JSON.parse(json) as TokenClaims;
  } catch {
    return {};
  }
}

/**
 * OIDC session: authorization-code + PKCE against the
 * public client, access token in memory (oidc-client-ts keeps the user object
 * in sessionStorage), silent renew via prompt=none iframe.
 */
export class AuthSession {
  #manager: UserManager | null = null;
  #renewTimer: ReturnType<typeof setInterval> | undefined;
  user = $state<User | null>(null);
  ready = $state(false);

  get accessToken(): string | null {
    return this.user?.access_token ?? null;
  }

  get roles(): string[] {
    const claims = decodeTokenClaims(this.accessToken);
    return [...(claims.realm_access?.roles ?? []), ...(claims.groups ?? [])];
  }

  get canMutate(): boolean {
    return this.roles.some((role) => OPERATOR_ROLES.includes(role));
  }

  get canManageEnrollmentTokens(): boolean {
    return this.roles.some((role) => ENROLLMENT_ADMIN_ROLES.includes(role));
  }

  get canChat(): boolean {
    return this.roles.some((role) => AI_OPERATOR_ROLES.includes(role));
  }

  get displayName(): string {
    const profile = this.user?.profile as { preferred_username?: string } | undefined;
    return profile?.preferred_username ?? "unknown";
  }

  /** Bootstrap: complete a redirect callback if present, then load the stored session. */
  async init(): Promise<void> {
    const bootstrap = await uiBootstrap();
    this.#manager = new UserManager({
      authority: bootstrap.oidcAuthority,
      client_id: bootstrap.oidcClientId,
      redirect_uri: window.location.origin,
      response_mode: "query",
      scope: "openid profile",
      silent_redirect_uri: window.location.origin,
      automaticSilentRenew: false,
    });
    if (new URLSearchParams(window.location.search).has("code")) {
      this.user = await this.#manager.signinRedirectCallback();
      window.history.replaceState({}, "", "/");
    } else {
      this.user = await this.#manager.getUser();
    }
    this.#scheduleRenew();
    this.ready = true;
  }

  login(): Promise<never> {
    const manager = this.#manager;
    if (!manager) {
      throw new Error("auth not initialised");
    }
    return manager.signinRedirect({ state: window.location.hash || "/" }) as Promise<never>;
  }

  async logout(): Promise<void> {
    const manager = this.#manager;
    if (!manager) {
      return;
    }
    await manager.signoutRedirect();
  }

  /** Silent renew; a failed refresh drops the session (the UI re-shows the login gate). */
  async renew(): Promise<string | null> {
    const manager = this.#manager;
    if (!manager || !this.user) {
      return null;
    }
    try {
      const renewed = await manager.signinSilent();
      this.user = renewed;
      this.#scheduleRenew();
      return renewed?.access_token ?? null;
    } catch {
      this.user = null;
      return null;
    }
  }

  #scheduleRenew(): void {
    clearInterval(this.#renewTimer);
    const expiresIn = (this.user?.expires_at ?? 0) - Date.now() / 1000;
    const delayMs = Math.max(30, expiresIn - 60) * 1000;
    this.#renewTimer = setInterval(() => {
      void this.renew();
    }, delayMs);
  }
}

export const session = new AuthSession();

/** Authenticated fetch for every view: bearer + one silent-renew retry on 401. */
export const apiFetch: Fetcher = makeApiFetch(
  async () => session.accessToken,
  () => session.renew(),
);

export { toApiError };
