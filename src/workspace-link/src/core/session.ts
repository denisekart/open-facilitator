// The session provider: the only credential flow an endpoint runs after its
// key is enrolled. challenge (unauthenticated, proves nothing) -> detached
// EdDSA proof -> a 5-minute kernel-signed session JWT, refreshed on 401 or
// before expiry. The enrollment itself is NOT a re-auth path: a 401 re-proves,
// it never re-enrolls.
import type { LinkLogger, LinkTokenProvider } from "./types.js";
import { buildProof } from "./proof.js";
import type { EndpointIdentity } from "./keystore.js";
import { EnrollmentError, type ProblemDetails } from "./errors.js";
import { parseProblem, trimSlash } from "./enroll.js";

interface ChallengeResponse {
  nonce: string;
  publicKeyRequired: boolean;
  algorithm: "ed25519";
  expiresAt: string;
}

interface ProofResponse {
  sessionToken: string;
  expiresAt: string;
  seq: number;
}

export interface SessionProviderOptions {
  baseUrl: string;
  endpointId: string;
  identity: EndpointIdentity;
  logger?: LinkLogger;
  fetchImpl?: typeof fetch;
  /** Seconds of slack before `expiresAt` at which the token is renewed (default 30). */
  renewAheadSeconds?: number;
  /**
   * Credential class `oidc`: a workload token source (client-credentials JWT).
   * When set, the session is bought by POSTing that token to the proof route
   * instead of a key proof — the kernel accepts one route, two identities. The
   * identity is still required: it is what a later class flip (a re-enroll that
   * registers a key) needs, and `proofForChannel` still signs with it.
   */
  workloadToken?: () => Promise<string>;
}

/**
 * A `LinkTokenProvider` that hands out kernel session JWTs. `refresh()` re-runs
 * challenge+proof — the enrollment is no longer a re-auth path, and no secret
 * ever comes back over the wire.
 */
export class SessionProvider implements LinkTokenProvider {
  private cached: { token: string; expiresAtMs: number; seq: number } | null = null;
  private inFlight: Promise<string> | null = null;

  constructor(private readonly opts: SessionProviderOptions) {}

  /**
   * Bound, not prototype methods: consumers hand `provider.getToken` around as
   * a detached function (postCapabilities, the transports' refresh hooks), and
   * an unbound method loses `this` — the same reason the OIDC provider in
   * token-rotation.ts has always been a closure object.
   */

  /** The replay fence the last mint asserted (0 before the first session). */
  get seq(): number {
    return this.cached?.seq ?? 0;
  }

  readonly getToken = async (): Promise<string> => {
    const now = Date.now();
    if (
      this.cached !== null &&
      this.cached.expiresAtMs - (this.opts.renewAheadSeconds ?? 30) * 1000 > now
    ) {
      return this.cached.token;
    }

    if (this.inFlight === null) {
      this.inFlight = this.mint().finally(() => {
        this.inFlight = null;
      });
    }

    return this.inFlight;
  };

  /** Drop the cached session and re-prove (the 401 answer). */
  readonly refresh = async (): Promise<void> => {
    this.cached = null;
    await this.getToken();
  };

  /** A proof for a channel that is not this provider's HTTP route (the socket handshake). */
  readonly proofForChannel = async (htm: "WS" | "POST", htu: string): Promise<string> => {
    const { nonce, epoch, seq } = await this.challenge();
    return buildProof(this.opts.identity, {
      endpointId: this.opts.endpointId,
      htm,
      htu,
      nonce,
      seq,
      epoch,
    });
  };

  private async mint(): Promise<string> {
    if (this.opts.workloadToken !== undefined) {
      return this.mintWorkload(await this.opts.workloadToken());
    }
    const { nonce, epoch, seq } = await this.challenge();
    const proof = buildProof(this.opts.identity, {
      endpointId: this.opts.endpointId,
      htm: "POST",
      htu: `${trimSlash(this.opts.baseUrl)}/api/link/v1/proof`,
      nonce,
      seq,
      epoch,
    });
    const fetchImpl = this.opts.fetchImpl ?? fetch;
    const response = await fetchImpl(`${trimSlash(this.opts.baseUrl)}/api/link/v1/proof`, {
      method: "POST",
      headers: { "content-type": "application/json" },
      body: JSON.stringify({ proof }),
    });
    if (response.status !== 200) {
      throw new EnrollmentError(response.status, await parseProblem(response));
    }

    const payload = (await response.json()) as ProofResponse;
    this.cached = {
      token: payload.sessionToken,
      expiresAtMs: Date.parse(payload.expiresAt),
      seq: payload.seq,
    };
    this.opts.logger?.debug("link session minted", {
      endpointId: this.opts.endpointId,
      seq: payload.seq,
    });
    return payload.sessionToken;
  }

  /**
   * Class `oidc`: no challenge (issuer trust needs no nonce), one POST, the
   * same session shape. A fresh JWT per mint is the point — the workload
   * credential is never a long-lived bearer, so a revoked enrollment is the
   * only thing that can stop a reconnect.
   */
  private async mintWorkload(token: string): Promise<string> {
    const fetchImpl = this.opts.fetchImpl ?? fetch;
    const response = await fetchImpl(`${trimSlash(this.opts.baseUrl)}/api/link/v1/proof`, {
      method: "POST",
      headers: { "content-type": "application/json" },
      body: JSON.stringify({ token }),
    });
    if (response.status !== 200) {
      throw new EnrollmentError(response.status, await parseProblem(response));
    }
    const payload = (await response.json()) as ProofResponse;
    this.cached = {
      token: payload.sessionToken,
      expiresAtMs: Date.parse(payload.expiresAt),
      seq: payload.seq,
    };
    this.opts.logger?.debug("link session minted (workload)", {
      endpointId: this.opts.endpointId,
      seq: payload.seq,
    });
    return payload.sessionToken;
  }

  private async challenge(): Promise<{ nonce: string; epoch: number; seq: number }> {
    const fetchImpl = this.opts.fetchImpl ?? fetch;
    const response = await fetchImpl(`${trimSlash(this.opts.baseUrl)}/api/link/v1/challenge`, {
      method: "POST",
      headers: { "content-type": "application/json" },
      body: JSON.stringify({ endpointId: this.opts.endpointId }),
    });
    if (response.status !== 200) {
      const problem: ProblemDetails = await parseProblem(response);
      throw new EnrollmentError(response.status, {
        ...problem,
        detail: `challenge failed: ${problem.detail ?? response.status}`,
      });
    }

    const payload = (await response.json()) as ChallengeResponse;
    // The kernel carries the state a proof may claim inside the opaque
    // nonce: `<epoch>.<seq>.<random>`.
    const [epochPart, seqPart] = payload.nonce.split(".");
    const epoch = Number(epochPart);
    const seq = Number(seqPart);
    if (!Number.isInteger(epoch) || !Number.isInteger(seq)) {
      throw new EnrollmentError(502, {
        type: "schema-violation",
        detail: "challenge nonce does not carry its minted state",
      });
    }

    return { nonce: payload.nonce, epoch, seq };
  }
}
