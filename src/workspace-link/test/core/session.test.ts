// The session state machine: challenge → detached proof → 5-minute kernel
// session JWT. Covers the caching/renew-ahead window, single-flight coalescing
// (parallel getToken mints once), the 401 refresh transition, proofForChannel
// for the socket handshake, the workload (`oidc`) arm, and every kernel refusal
// mapped onto EnrollmentError. Driven by a scripted fetch — no SignalR.
import { afterEach, describe, expect, it, vi } from "vitest";
import {
  EnrollmentError,
  SessionProvider,
  parseProof,
  type SessionProviderOptions,
} from "../../src/index.js";
import { cleanUp, makeIdentity, responseOf } from "./fixtures.js";

const BASE_URL = "https://kernel.test";
const CHALLENGE_URL = `${BASE_URL}/api/link/v1/challenge`;
const PROOF_URL = `${BASE_URL}/api/link/v1/proof`;

interface ScriptedCall {
  url: string;
  body: Record<string, unknown>;
}

/**
 * A scripted kernel: challenge answers `<epoch>.<seq>.<random>` nonces, the
 * proof endpoint mints a session JWT. `fail` overrides the answer for a route
 * (a Response once, or a call-count of 503s); `hold` parks the proof route on
 * an externally-resolved gate so single-flight can be observed with no
 * wall-clock timer.
 */
function kernel(
  options: {
    calls: ScriptedCall[];
    seqStart?: number;
    epoch?: number;
    sessionTtlSeconds?: number;
    fail?: Record<string, number | Response>;
    hold?: Promise<void>;
  } = { calls: [] },
) {
  const state = { proofs: 0, challenges: 0 };
  const fetchImpl = (async (url: unknown, init?: RequestInit) => {
    const route = String(url) === CHALLENGE_URL ? "challenge" : "proof";
    const body = JSON.parse(String(init?.body ?? "{}")) as Record<string, unknown>;
    options.calls.push({ url: String(url), body });
    const scripted = options.fail?.[route];
    if (typeof scripted === "object") {
      delete options.fail![route];
      return scripted;
    }
    if (typeof scripted === "number" && scripted > 0) {
      options.fail![route] = scripted - 1;
      return new Response("", { status: 503 });
    }
    if (route === "challenge") {
      state.challenges += 1;
      const epoch = options.epoch ?? 7;
      const seq = (options.seqStart ?? 40) + state.challenges;
      return responseOf(
        200,
        JSON.stringify({
          nonce: `${epoch}.${seq}.r${state.challenges}`,
          publicKeyRequired: true,
          algorithm: "ed25519",
          expiresAt: new Date(Date.now() + 30_000).toISOString(),
        }),
      );
    }
    if (options.hold !== undefined) await options.hold;
    state.proofs += 1;
    const ttl = options.sessionTtlSeconds ?? 300;
    return responseOf(
      200,
      JSON.stringify({
        sessionToken: `jwt-${state.proofs}`,
        expiresAt: new Date(Date.now() + ttl * 1000).toISOString(),
        seq: (options.seqStart ?? 40) + state.challenges,
      }),
    );
  }) as unknown as typeof fetch;
  return { fetchImpl, state };
}

const identity = makeIdentity();

function providerFor(
  fetchImpl: typeof fetch,
  extra: Partial<SessionProviderOptions> = {},
): SessionProvider {
  return new SessionProvider({
    baseUrl: BASE_URL,
    endpointId: "gw.team-alpha/node-1",
    identity,
    fetchImpl,
    ...extra,
  });
}

afterEach(() => {
  cleanUp();
});

describe("SessionProvider (class-key: challenge + proof)", () => {
  it("mints through challenge → proof and returns the session JWT", async () => {
    vi.useFakeTimers();
    vi.setSystemTime(1_764_700_000_000);
    const calls: ScriptedCall[] = [];
    const { fetchImpl, state } = kernel({ calls, seqStart: 41, epoch: 7 });
    const provider = providerFor(fetchImpl);

    expect(provider.seq).toBe(0); // nothing minted yet
    expect(await provider.getToken()).toBe("jwt-1");
    expect(provider.seq).toBe(42); // the fence the mint asserted

    expect(calls.map((c) => c.url)).toEqual([CHALLENGE_URL, PROOF_URL]);
    expect(calls[0]!.body).toEqual({ endpointId: "gw.team-alpha/node-1" });

    // The proof is a detached EdDSA proof over the POST channel, and it
    // claims the state the challenge minted inside its nonce.
    const proof = String(calls[1]!.body["proof"]);
    const claims = parseProof(proof)!;
    expect(claims.iss).toBe("gw.team-alpha/node-1");
    expect(claims.htm).toBe("POST");
    expect(claims.htu).toBe(PROOF_URL);
    expect(claims.nonce).toBe("7.42.r1");
    expect(claims.seq).toBe(42);
    expect(claims.epoch).toBe(7);
    expect(state.proofs).toBe(1);
    vi.useRealTimers();
  });

  it("caches until the renew-ahead window, then re-mints", async () => {
    const now = 1_764_700_000_000;
    vi.useFakeTimers();
    vi.setSystemTime(now);
    const calls: ScriptedCall[] = [];
    const { fetchImpl } = kernel({ calls, sessionTtlSeconds: 300 });
    const provider = providerFor(fetchImpl, { renewAheadSeconds: 30 });

    expect(await provider.getToken()).toBe("jwt-1");
    // 300s life − 30s renew-ahead: still cached at +269s.
    vi.setSystemTime(now + 269_000);
    expect(await provider.getToken()).toBe("jwt-1");
    // Past the margin: a full re-prove (fresh challenge, fresh proof).
    vi.setSystemTime(now + 271_000);
    expect(await provider.getToken()).toBe("jwt-2");
    expect(calls.filter((c) => c.url === CHALLENGE_URL)).toHaveLength(2);
    vi.useRealTimers();
  });

  it("coalesces concurrent mints into one challenge/proof pair (single flight)", async () => {
    const calls: ScriptedCall[] = [];
    const gate = Promise.withResolvers<void>();
    const { fetchImpl, state } = kernel({ calls, hold: gate.promise });
    const provider = providerFor(fetchImpl);
    const inFlight = Promise.all([provider.getToken(), provider.getToken(), provider.getToken()]);
    gate.resolve(); // release the scripted kernel
    expect(await inFlight).toEqual(["jwt-1", "jwt-1", "jwt-1"]);
    expect(state.proofs).toBe(1);
    expect(state.challenges).toBe(1);
  });

  it("refresh() drops the cache and re-proves (the 401 answer)", async () => {
    const calls: ScriptedCall[] = [];
    const { fetchImpl, state } = kernel({ calls });
    const provider = providerFor(fetchImpl);
    expect(await provider.getToken()).toBe("jwt-1");
    await provider.refresh();
    expect(await provider.getToken()).toBe("jwt-2");
    expect(state.challenges).toBe(2);
    expect(calls.filter((c) => c.url === PROOF_URL)).toHaveLength(2);
  });

  it("a failed mint leaves no cache, so the next caller retries", async () => {
    const calls: ScriptedCall[] = [];
    const { fetchImpl, state } = kernel({ calls, fail: { proof: 1 } });
    const provider = providerFor(fetchImpl);
    await expect(provider.getToken()).rejects.toThrow(/HTTP 503/);
    expect(await provider.getToken()).toBe("jwt-1");
    expect(state.proofs).toBe(1);
  });

  it("maps a proof refusal onto EnrollmentError with the ProblemDetails", async () => {
    const calls: ScriptedCall[] = [];
    const refusal = responseOf(
      401,
      JSON.stringify({
        type: "https://errors.openfacilitator.org/no-live-enrollment",
        detail: "No live enrollment for this endpoint.",
      }),
    );
    const { fetchImpl } = kernel({ calls, fail: { proof: refusal } });
    const provider = providerFor(fetchImpl);
    const error = await provider.getToken().catch((e: unknown) => e);
    expect(error).toBeInstanceOf(EnrollmentError);
    expect((error as EnrollmentError).status).toBe(401);
    expect((error as EnrollmentError).problem.detail).toMatch(/No live enrollment/);
  });

  it("a challenge refusal says so in the detail", async () => {
    const calls: ScriptedCall[] = [];
    const { fetchImpl } = kernel({ calls, fail: { challenge: responseOf(500, "boom") } });
    const provider = providerFor(fetchImpl);
    const error = await provider.getToken().catch((e: unknown) => e);
    expect((error as EnrollmentError).status).toBe(500);
    expect((error as EnrollmentError).message).toContain("challenge failed");
  });

  it("a challenge nonce that carries no minted state is a schema violation", async () => {
    const calls: ScriptedCall[] = [];
    const { fetchImpl } = kernel({
      calls,
      fail: {
        challenge: responseOf(
          200,
          JSON.stringify({
            nonce: "opaque",
            publicKeyRequired: true,
            algorithm: "ed25519",
            expiresAt: new Date().toISOString(),
          }),
        ),
      },
    });
    const provider = providerFor(fetchImpl);
    const error = await provider.getToken().catch((e: unknown) => e);
    expect((error as EnrollmentError).status).toBe(502);
    expect((error as EnrollmentError).problem.type).toBe("schema-violation");
  });

  it("proofForChannel proves a socket channel without touching the cache", async () => {
    const calls: ScriptedCall[] = [];
    const { fetchImpl, state } = kernel({ calls });
    const provider = providerFor(fetchImpl);
    const proof = await provider.proofForChannel("WS", "wss://kernel.test/hubs/link");
    const claims = parseProof(proof)!;
    expect(claims.htm).toBe("WS");
    expect(claims.htu).toBe("wss://kernel.test/hubs/link");
    expect(state.proofs).toBe(0); // no session minted
    expect(provider.seq).toBe(0);
    expect(calls.map((c) => c.url)).toEqual([CHALLENGE_URL]);
  });

  it("trims a trailing slash off the base URL", async () => {
    const calls: ScriptedCall[] = [];
    const { fetchImpl } = kernel({ calls });
    const provider = new SessionProvider({
      baseUrl: `${BASE_URL}/`,
      endpointId: "gw.team-alpha/node-1",
      identity,
      fetchImpl,
    });
    await provider.getToken();
    expect(calls.map((c) => c.url)).toEqual([CHALLENGE_URL, PROOF_URL]);
  });
});

describe("SessionProvider (class oidc: workload token, no challenge)", () => {
  it("buys a session with one POST and no challenge", async () => {
    const calls: ScriptedCall[] = [];
    const { fetchImpl, state } = kernel({ calls });
    const provider = providerFor(fetchImpl, { workloadToken: async () => "workload-jwt" });
    expect(await provider.getToken()).toBe("jwt-1");
    expect(calls.map((c) => c.url)).toEqual([PROOF_URL]);
    expect(calls[0]!.body).toEqual({ token: "workload-jwt" });
    expect(state.challenges).toBe(0);
  });

  it("presents a fresh workload token on every mint", async () => {
    let issued = 0;
    const calls: ScriptedCall[] = [];
    const { fetchImpl } = kernel({ calls });
    const provider = providerFor(fetchImpl, {
      workloadToken: async () => `workload-${(issued += 1)}`,
    });
    await provider.getToken();
    await provider.refresh();
    expect(calls.map((c) => String(c.body["token"]))).toEqual(["workload-1", "workload-2"]);
  });
});
