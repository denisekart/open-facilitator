// The three lines of law: the identity is a file, the code is spent once, and
// every later credential is a session JWT. Covers identityDirFor, the 401
// "no live enrollment" signal, the first-boot enroll→prove loop, the no-code
// refusal, a hard enroll refusal, a transient 5xx boot retry (fake timers —
// the 500ms ladder is advanced, not slept), and the class-`oidc` arm where a
// workload token replaces the code. Scripted fetch throughout: no kernel.
import { afterEach, describe, expect, it, vi } from "vitest";
import { tmpdir } from "node:os";
import {
  EnrollmentError,
  createEndpointCredential,
  identityDirFor,
  isNotEnrolled,
  type EndpointCredentialOptions,
  type LinkLogger,
} from "../../src/index.js";
import { cleanUp, tempDir } from "./fixtures.js";

const BASE_URL = "https://kernel.test";
const enrollUrl = `${BASE_URL}/api/link/v1/enroll`;
const challengeUrl = `${BASE_URL}/api/link/v1/challenge`;
const proofUrl = `${BASE_URL}/api/link/v1/proof`;

interface Call {
  url: string;
  body: Record<string, unknown>;
}

/** A kernel that answers enroll/challenge/proof from a route → handler map. */
function scripted(calls: Call[], handler: (route: string, n: number) => Response): typeof fetch {
  const counts = new Map<string, number>();
  return (async (url: unknown, init?: RequestInit) => {
    const route = String(url).replace(`${BASE_URL}/api/link/v1/`, "");
    const n = (counts.get(route) ?? 0) + 1;
    counts.set(route, n);
    calls.push({
      url: String(url),
      body: JSON.parse(String(init?.body ?? "{}")) as Record<string, unknown>,
    });
    return handler(route, n);
  }) as unknown as typeof fetch;
}

function json(status: number, body: unknown): Response {
  return new Response(JSON.stringify(body), {
    status,
    headers: { "content-type": "application/json" },
  });
}

function enrollOk(identityKeyId: string): Response {
  return json(201, {
    enrollmentId: "enr-1",
    trustMode: "isolated-executor",
    endpointKeyId: identityKeyId,
    seq: 1,
  });
}

function challengeOk(n: number): Response {
  return json(200, {
    nonce: `3.${n}.r${n}`,
    publicKeyRequired: true,
    algorithm: "ed25519",
    expiresAt: new Date(Date.now() + 30_000).toISOString(),
  });
}

function sessionOk(n: number): Response {
  return json(200, {
    sessionToken: `jwt-${n}`,
    expiresAt: new Date(Date.now() + 300_000).toISOString(),
    seq: n,
  });
}

const problem401 = json(401, { detail: "No live enrollment for this endpoint." });

function silentLogger(): LinkLogger {
  return { debug() {}, info() {}, warn() {}, error() {} };
}

afterEach(() => {
  cleanUp();
});

describe("identityDirFor", () => {
  it("maps a state FILE to its directory", () => {
    expect(identityDirFor("/var/lib/linkd/ledger.sqlite3")).toBe("/var/lib/linkd");
    expect(identityDirFor("relative/ledger.sqlite3")).toBe("relative");
  });

  it("gives a disposable :memory: process a private temp keystore (never the cwd)", () => {
    const dir = identityDirFor(":memory:");
    expect(dir.startsWith(tmpdir())).toBe(true);
    expect(dir).toContain("ofac-identity-");
    const empty = identityDirFor("");
    expect(empty).not.toBe("");
    expect(empty).not.toBe(dir); // a fresh per-process home
  });
});

describe("isNotEnrolled", () => {
  it("is the 401 refusal, and only that", () => {
    expect(
      isNotEnrolled(new EnrollmentError(401, { detail: "No live enrollment for this endpoint." })),
    ).toBe(true);
    expect(isNotEnrolled(new EnrollmentError(403, { detail: "team mismatch" }))).toBe(false);
    expect(isNotEnrolled(new Error("fetch failed"))).toBe(false);
    expect(isNotEnrolled("not even an error")).toBe(false);
  });
});

async function credential(
  options: Partial<EndpointCredentialOptions> & Pick<EndpointCredentialOptions, "fetchImpl">,
) {
  return createEndpointCredential({
    stateDir: tempDir(),
    baseUrl: BASE_URL,
    endpointId: "gw.team-alpha/node-1",
    team: "team-alpha",
    trustMode: "isolated-executor",
    logger: silentLogger(),
    ...options,
  });
}

describe("createEndpointCredential", () => {
  it("an enrolled endpoint boots straight into challenge+proof (no enroll)", async () => {
    const calls: Call[] = [];
    const cred = await credential({
      fetchImpl: scripted(calls, (route, n) =>
        route === "challenge" ? challengeOk(n) : sessionOk(n),
      ),
    });
    expect(calls.map((c) => c.url)).toEqual([challengeUrl, proofUrl]);
    expect(calls[0]!.body).toEqual({ endpointId: "gw.team-alpha/node-1" });
    expect(await cred.provider.getToken()).toBe("jwt-1"); // cached, no re-mint
    expect(calls).toHaveLength(2);
    expect(cred.identity.keyId).toMatch(/^sha256:[0-9a-f]{64}$/);
  });

  it("first boot: 401 on the proof spends the code once, then re-proves", async () => {
    const calls: Call[] = [];
    let proofAnswers = 0;
    const cred = await credential({
      enrollSecret: "enroll-dev",
      instanceId: "lnk-fixed",
      role: "gateway",
      fetchImpl: scripted(calls, (route, n) => {
        if (route === "challenge") return challengeOk(n);
        if (route === "proof") {
          proofAnswers += 1;
          return proofAnswers === 1 ? problem401 : sessionOk(n);
        }
        return enrollOk(`sha256:${"ab".repeat(32)}`);
      }),
    });
    expect(calls.map((c) => c.url)).toEqual([
      challengeUrl,
      proofUrl,
      enrollUrl,
      challengeUrl,
      proofUrl,
    ]);
    // The code rides the enroll body, never a bearer header; the audit
    // instanceId is the fixed one (OFAC_INSTANCE_ID law).
    expect(calls[2]!.body).toMatchObject({
      token: "enroll-dev",
      endpointId: "gw.team-alpha/node-1",
      instanceId: "lnk-fixed",
      role: "gateway",
      team: "team-alpha",
      keyAlgorithm: "ed25519",
      publicKey: cred.identity.publicKeyBase64Url,
    });
    expect(await cred.provider.getToken()).toBe("jwt-2");
  });

  it("a first boot with no credential source refuses (a key is only a credential once the kernel has it)", async () => {
    const calls: Call[] = [];
    await expect(
      credential({
        fetchImpl: scripted(calls, (route, n) =>
          route === "challenge" ? challengeOk(n) : problem401,
        ),
      }),
    ).rejects.toThrow(/no enrollment code/);
    expect(calls.map((c) => c.url)).toEqual([challengeUrl, proofUrl]); // never enroll
  });

  it("a hard enroll refusal (403) surfaces, no retry", async () => {
    const calls: Call[] = [];
    const error = await credential({
      enrollSecret: "spent-code",
      fetchImpl: scripted(calls, (route, n) => {
        if (route === "challenge") return challengeOk(n);
        if (route === "proof") return n <= 1 ? problem401 : sessionOk(n);
        return json(403, { type: "team-mismatch", detail: "team roster mismatch" });
      }),
    }).catch((e: unknown) => e);
    expect(error).toBeInstanceOf(EnrollmentError);
    expect((error as EnrollmentError).status).toBe(403);
    expect((error as EnrollmentError).code).toBe("team-mismatch");
    expect(calls.filter((c) => c.url === enrollUrl)).toHaveLength(1);
  });

  it("a transient 5xx during the prove phase waits the ladder out and re-proves (fake timers)", async () => {
    vi.useFakeTimers();
    try {
      const calls: Call[] = [];
      let proofs = 0;
      const pending = credential({
        enrollSecret: "enroll-dev",
        fetchImpl: scripted(calls, (route, n) => {
          if (route === "challenge") return challengeOk(n);
          proofs += 1;
          return proofs === 1 ? new Response("", { status: 503 }) : sessionOk(n);
        }),
      });
      await vi.advanceTimersByTimeAsync(600); // the 500ms first rung
      const cred = await pending;
      expect(await cred.provider.getToken()).toBe("jwt-2");
      expect(calls.filter((c) => c.url === challengeUrl)).toHaveLength(2);
    } finally {
      vi.useRealTimers();
    }
  });

  it("a transport failure with a code on hand is kernel-not-ready, not a crash", async () => {
    vi.useFakeTimers();
    try {
      const calls: Call[] = [];
      let connectionRefused = true;
      let proofs = 0;
      const pending = credential({
        enrollSecret: "enroll-dev",
        fetchImpl: scripted(calls, (route, n) => {
          if (connectionRefused) {
            connectionRefused = false;
            throw new Error("fetch failed: ECONNREFUSED");
          }
          if (route === "challenge") return challengeOk(n);
          if (route === "proof") {
            proofs += 1;
            return proofs === 1 ? problem401 : sessionOk(n);
          }
          return enrollOk("sha256:" + "ab".repeat(32));
        }),
      });
      await vi.advanceTimersByTimeAsync(600); // the 500ms first rung
      const cred = await pending;
      expect(await cred.provider.getToken()).toBe("jwt-2");
      expect(calls.some((c) => c.url === enrollUrl)).toBe(true); // the wait-out worked
    } finally {
      vi.useRealTimers();
    }
  });

  it("class oidc: the workload token buys sessions and enrolls without a code", async () => {
    const calls: Call[] = [];
    let proofs = 0;
    const cred = await credential({
      workloadToken: async () => "workload-jwt",
      instancePrefix: "wkr",
      fetchImpl: scripted(calls, (route, n) => {
        if (route === "proof") {
          proofs += 1;
          return proofs === 1 ? problem401 : sessionOk(n);
        }
        return enrollOk(`sha256:${"ab".repeat(32)}`);
      }),
    });
    // No challenge — issuer trust needs no nonce: proof (401) → enroll → proof.
    expect(calls.map((c) => c.url)).toEqual([proofUrl, enrollUrl, proofUrl]);
    expect(calls[0]!.body).toEqual({ token: "workload-jwt" });
    expect(calls[1]!.body).toMatchObject({ token: "workload-jwt" });
    expect(String(calls[1]!.body["instanceId"])).toMatch(/^wkr-[0-9a-f]{16}$/);
    expect(await cred.provider.getToken()).toBe("jwt-2");
  });

  it("with BOTH a code and a workload token, sessions ride the key (mode (a) pins)", async () => {
    const calls: Call[] = [];
    await credential({
      enrollSecret: "enroll-dev",
      workloadToken: async () => "workload-jwt",
      fetchImpl: scripted(calls, (route, n) =>
        route === "challenge" ? challengeOk(n) : sessionOk(n),
      ),
    });
    // The proof path (challenge + proof), not the token path.
    expect(calls.map((c) => c.url)).toEqual([challengeUrl, proofUrl]);
    expect(String(calls[1]!.body["proof"]).split(".")).toHaveLength(3);
  });

  it("logs the enroll audit line with the endpoint and key", async () => {
    const lines: string[] = [];
    const logger: LinkLogger = {
      debug: () => {},
      info: (msg, fields) => lines.push(JSON.stringify({ msg, ...fields })),
      warn: () => {},
      error: () => {},
    };
    const calls: Call[] = [];
    await credential({
      logger,
      enrollSecret: "enroll-dev",
      fetchImpl: scripted(calls, (route, n) => {
        if (route === "challenge") return challengeOk(n);
        if (route === "proof") return n <= 1 ? problem401 : sessionOk(n);
        return enrollOk(`sha256:${"ab".repeat(32)}`);
      }),
    });
    expect(
      lines.some((l) => l.includes("endpoint key enrolled") && l.includes("gw.team-alpha/node-1")),
    ).toBe(true);
  });
});
