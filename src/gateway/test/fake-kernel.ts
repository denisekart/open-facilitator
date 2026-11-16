// Fake kernel link plane for gateway/runtime tests: the credential law in
// ~80 lines, so a test can assert the SAME story production tells — enroll pins
// a key, every call after it rides a session JWT bought with a proof, and a
// bearer for an unenrolled endpoint is refused with the kernel's no-existence
// answer. Proofs are parsed, not re-verified: these tests own the wire shape,
// the crypto is workspace-link's own suite's business.
import { parseProof, keyIdFromPublicKey } from "@ofac/workspace-link";

export interface FakeKernel {
  /** Route names hit, in order (`enroll`, `challenge`, `proof`, `capabilities`…). */
  calls: string[];
  /** Every request body, in order (use `bodiesFor` to filter by route). */
  bodies: string[];
  /** Request bodies for one route, in order. */
  bodiesFor(route: string): string[];
  /** Route names hit, without the handshake noise (challenge/proof). */
  business(): string[];
  /** Bearer tokens seen, in order. */
  bearers: string[];
  /** Keys enrolled so far, per endpointId. */
  enrolled: Map<string, string>;
  /** `sha256:` fingerprints the kernel would store, per endpointId. */
  keys: Map<string, string>;
  fetchImpl: typeof fetch;
  /** Bearer seen on the Nth request (0-based). */
  bearerAt(index: number): string;
  /** The session JWT that carried the last capabilities POST ("" if none). */
  sessionBearer(): string;
  /** Refuse the next challenge (simulate a revoked endpoint). */
  denyChallenge(detail?: string): void;
}

export interface FakeKernelOptions {
  seq?: number;
  epoch?: number;
  /**
   * Credential class `oidc` is in play: the enroll body carrying this token
   * binds a workload enrollment, and a proof POST of `{token}` buys a session
   * only once that binding exists.
   */
  workload?: { token: string };
  /** Answer routes the link plane does not own (token, approvals poll…). */
  fallback?: (path: string, init?: RequestInit) => Response | undefined;
}

export function fakeKernel(options: FakeKernelOptions = {}): FakeKernel {
  const calls: string[] = [];
  const bodies: string[] = [];
  const byRoute: Record<string, string[]> = {};
  const bearers: string[] = [];
  const enrolled = new Map<string, string>();
  const keys = new Map<string, string>();
  const sessions = new Set<string>();
  let seq = options.seq ?? 0;
  const epoch = options.epoch ?? 0;
  let workloadEnrolled = false;
  let challengeDenial: string | undefined;
  let lastSession = "";
  const sessionTtl = new Date(Date.now() + 300_000).toISOString();

  const route = (path: string): string =>
    path.split("/api/link/v1/")[1]?.split("?")[0] ?? new URL(path).pathname.replace(/^\//, "");

  const fetchImpl = (async (url: unknown, init?: RequestInit) => {
    const path = String(url);
    const name = route(path);
    const body = init?.body === undefined ? "" : String(init.body);
    const bearer =
      headerOf(init, "authorization")?.replace(/^Bearer /, "") ??
      headerOf(init, "x-ofac-fake-bearer") ??
      "";
    calls.push(name);
    bodies.push(body);
    (byRoute[name] ??= []).push(body);
    bearers.push(bearer);
    const json = (status: number, payload: unknown): Response =>
      new Response(JSON.stringify(payload), {
        status,
        headers: { "content-type": "application/json" },
      });
    const problem = (status: number, detail: string): Response =>
      json(status, { type: "about:blank", title: "refused", status, detail });

    if (name === "enroll") {
      const doc = JSON.parse(body) as {
        endpointId: string;
        publicKey?: string;
        keyAlgorithm?: string;
      };
      if (
        doc.keyAlgorithm !== "ed25519" ||
        (doc.publicKey === undefined && options.workload === undefined)
      ) {
        return problem(
          400,
          "publicKey + keyAlgorithm (class key) or a workload credential are required",
        );
      }
      if (options.workload !== undefined && JSON.parse(body).token === options.workload.token) {
        workloadEnrolled = true;
      }
      seq += 1;
      if (doc.publicKey !== undefined) {
        enrolled.set(doc.endpointId, doc.publicKey);
        keys.set(doc.endpointId, keyIdFromPublicKey(doc.publicKey));
      }
      return json(201, {
        enrollmentId: `enr-${calls.filter((c) => c === "enroll").length}`,
        trustMode: "shared-runtime",
        endpointKeyId: keys.get(doc.endpointId),
        seq,
      });
    }
    if (name === "challenge") {
      const doc = JSON.parse(body) as { endpointId: string };
      if (challengeDenial !== undefined) {
        const detail = challengeDenial;
        challengeDenial = undefined;
        return problem(401, detail);
      }
      if (!enrolled.has(doc.endpointId)) {
        return problem(401, "No live enrollment for this endpoint.");
      }
      void workloadEnrolled;
      return json(200, {
        nonce: `${epoch}.${seq}.${Math.random().toString(36).slice(2)}`,
        publicKeyRequired: true,
        algorithm: "ed25519",
        expiresAt: sessionTtl,
      });
    }
    if (name === "proof") {
      const doc = JSON.parse(body) as { proof?: string; token?: string };
      if (doc.token !== undefined) {
        // Class `oidc`: issuer trust, no nonce. The binding must already exist.
        if (!workloadEnrolled) {
          return problem(401, "no live enrollment for this workload identity");
        }
        seq += 1;
        const workloadSession = `sess-workload-${sessions.size}`;
        sessions.add(workloadSession);
        return json(200, { sessionToken: workloadSession, expiresAt: sessionTtl, seq });
      }
      if (doc.proof === undefined) return problem(400, "proof is required");
      const claims = parseProof(doc.proof);
      if (claims === null) return problem(400, "proof is not a detached JWS");
      if (enrolled.get(claims.iss) === undefined) {
        return problem(401, "No live enrollment for this endpoint.");
      }
      if (claims.htm !== "POST") return problem(401, "htm mismatch");
      seq = Math.max(seq, claims.seq) + 1;
      const sessionToken = `sess-${claims.iss}-${sessions.size}`;
      sessions.add(sessionToken);
      return json(200, { sessionToken, expiresAt: sessionTtl, seq: claims.seq });
    }
    if (name === "capabilities") {
      if (!sessions.has(bearer)) return problem(401, "no such session");
      // What the kernel would store, for tests that assert the POSTed bearer.
      lastSession = bearer;
      return new Response(null, { status: 204 });
    }
    const extra = options.fallback?.(path, init);
    if (extra !== undefined) {
      return extra;
    }
    return new Response("no route", { status: 500 });
  }) as typeof fetch;

  return {
    calls,
    bodies,
    bodiesFor: (name: string) => byRoute[name] ?? [],
    business: () => calls.filter((name) => name !== "challenge" && name !== "proof"),
    bearers,
    enrolled,
    keys,
    bearerAt: (index: number) => bearers[index] ?? "",
    sessionBearer: () => lastSession,
    fetchImpl,
    denyChallenge: (detail = "No live enrollment for this endpoint.") => {
      challengeDenial = detail;
    },
  };
}

function headerOf(init: RequestInit | undefined, name: string): string | undefined {
  const headers = init?.headers;
  if (headers === undefined) return undefined;
  if (headers instanceof Headers) return headers.get(name) ?? undefined;
  if (Array.isArray(headers)) {
    return headers.find(([key]) => key.toLowerCase() === name)?.[1];
  }
  const entry = Object.entries(headers).find(([key]) => key.toLowerCase() === name);
  return entry?.[1] === undefined ? undefined : String(entry[1]);
}
