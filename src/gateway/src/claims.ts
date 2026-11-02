// Actor-claim extraction (spec §11/§12). The Gateway trusts exactly
// two identity sources on an inbound command: its own workload identity, and
// an `oidc:` actorEvidenceRef whose JWT verifies against the issuer JWKS
// (signature, iss, aud, exp, delegation===true). Everything else — bare
// `identity-evidence:*`, `workload:*`, an unverifiable `oidc:` — yields the
// workload actor only: an untrusted string never buys privilege (same law as
// the kernel's admission gate).
import {
  createRemoteJWKSet,
  decodeJwt,
  jwtVerify,
  type FlattenedJWSInput,
  type GetKeyFunction,
  type JWSHeaderParameters,
  type RemoteJWKSet,
} from "jose";

/** Anything jwtVerify can pull a verification key from (remote or injected). */
export type KeySource = RemoteJWKSet | GetKeyFunction<JWSHeaderParameters, FlattenedJWSInput>;

export interface ActorClaims {
  /** Canonical spelling: `oidc:<iss>|<sub>` when verified, else the workload actor. */
  readonly subject: string;
  readonly groups: readonly string[];
  /** True only when the evidence JWT fully verified; false ⇒ workload-only. */
  readonly verified: boolean;
  /** `preferred_username` of a verified evidence JWT, when present. */
  readonly preferredUsername?: string | undefined;
}

export interface ClaimsDeps {
  /** JWKS key-source seam (tests inject a local resolver; production fetches remote). */
  jwks: (issuer: string) => KeySource;
  audience: string;
  workloadActor: string;
  /** Lifetime tolerance in seconds; mirrors kernel `Link:Evidence:ClockSkewSeconds` (default 30). */
  clockToleranceSeconds?: number;
}

const unverified = (workloadActor: string): ActorClaims => ({
  subject: workloadActor,
  groups: [],
  verified: false,
});

const jwksCache = new Map<string, RemoteJWKSet>();

function remote(issuer: string): RemoteJWKSet {
  let set = jwksCache.get(issuer);
  if (set === undefined) {
    set = createRemoteJWKSet(new URL(`${issuer.replace(/\/$/, "")}/protocol/openid-connect/certs`));
    jwksCache.set(issuer, set);
  }
  return set;
}

/**
 * Resolve the claims an `actorEvidenceRef` vouches for. An unparseable,
 * unverifiable, or non-`oidc:` reference degrades to workload identity only —
 * never an error: the policy then sees a caller with no human behind it.
 */
export async function extractClaims(
  actorEvidenceRef: string,
  deps: ClaimsDeps,
): Promise<ActorClaims> {
  if (!actorEvidenceRef.startsWith("oidc:")) return unverified(deps.workloadActor);
  const jwt = actorEvidenceRef.slice("oidc:".length);
  try {
    const header = decodeJwt(jwt);
    if (typeof header.iss !== "string") return unverified(deps.workloadActor);
    const { payload } = await jwtVerify(jwt, deps.jwks(header.iss), {
      audience: deps.audience,
      algorithms: ["RS256", "ES256"],
      // same lifetime tolerance as the kernel's evidence validator default:
      // the 1s ofac-evidence mints stay verifiable across the decided-push
      // → re-audit leg (an expired-on-arrival token still denies, the
      // tolerance only absorbs clock + in-flight latency).
      clockTolerance: deps.clockToleranceSeconds ?? 30,
    });
    if (payload["delegation"] !== true) return unverified(deps.workloadActor);
    if (typeof payload.iss !== "string" || typeof payload.sub !== "string") {
      return unverified(deps.workloadActor);
    }
    const groups = Array.isArray(payload["groups"])
      ? payload["groups"].filter((g): g is string => typeof g === "string")
      : [];
    const username =
      typeof payload["preferred_username"] === "string"
        ? (payload["preferred_username"] as string)
        : undefined;
    return {
      subject: `oidc:${payload.iss}|${payload.sub}`,
      groups,
      verified: true,
      ...(username !== undefined ? { preferredUsername: username } : {}),
    };
  } catch {
    return unverified(deps.workloadActor);
  }
}

/** Production deps: remote JWKS per issuer, kernel-issued-token audience. */
export function defaultClaimsDeps(workloadActor: string, audience = "facilitator-api"): ClaimsDeps {
  return { jwks: remote, audience, workloadActor, clockToleranceSeconds: 30 };
}
