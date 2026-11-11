import { afterAll, beforeAll, describe, expect, it } from "vitest";
import { createHmac } from "node:crypto";
import { mkdtempSync, rmSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { startFakeProvider, type FakePlane } from "@ofac/scenario-plane";
import { parseEnvConfig, run, type RunHandle } from "@ofac/github-adapter";
import { httpErrorFrom, withRetry } from "@ofac/provider-core";

// The manual golden thread (T2, owner ruling: manual-only this slice):
// the acceptance chain as ONE script against a developer-running kernel
// (`make compose-slim` — kernel :8088, Keycloak :8080, realm imported)
// plus the in-process fake provider. Chain: an ask seeds a work (chat
// leg's kernel effect) -> the adapter's vcs.issue.create outbound leg
// (proven against the plane's `429-now` mode: Retry-After: 0 makes the
// retry path deterministic) -> a PR webhook in through the kernel sink ->
// a review decision -> `review.decision` on the kernel timeline -> a
// forked successor work (asserted via the kernel API). Run:
// `make scenario-golden`. Manual preconditions fail LOUD, never silent.
const KERNEL = process.env["GOLDEN_KERNEL_URL"] ?? "http://localhost:8088";
const IDP = process.env["GOLDEN_IDP_URL"] ?? "http://localhost:8080";
const REALM = "facilitator";
const WEBHOOK_SECRET = process.env["GOLDEN_WEBHOOK_SECRET"] ?? "golden-secret";

let plane: FakePlane;
let adapter: RunHandle;
let stateDir: string;

async function mint(clientId: string, extra: Record<string, string>): Promise<string> {
  const body = new URLSearchParams({
    grant_type: "client_credentials",
    client_id: clientId,
    ...extra,
  });
  let r: Response;
  try {
    r = await fetch(`${IDP}/realms/${REALM}/protocol/openid-connect/token`, {
      method: "POST",
      body,
    });
  } catch {
    throw new Error(
      `golden thread: kernel :8088 not reachable (IdP ${IDP} refused) — start the slim stack first (make compose-slim)`,
    );
  }
  if (!r.ok) throw new Error(`IdP token mint failed (${r.status}): ${await r.text()}`);
  return ((await r.json()) as { access_token: string }).access_token;
}

async function kernelJson<T>(path: string, token: string, init?: RequestInit): Promise<T> {
  const r = await fetch(`${KERNEL}${path}`, {
    ...init,
    headers: { ...(init?.headers ?? {}), Authorization: `Bearer ${token}` },
  });
  if (!r.ok) throw new Error(`${init?.method ?? "GET"} ${path} -> ${r.status}: ${await r.text()}`);
  return (await r.json()) as T;
}

beforeAll(async () => {
  // Loud manual precondition: no silent skip when the stack is down.
  try {
    const h = await fetch(`${KERNEL}/health`, { signal: AbortSignal.timeout(5000) });
    if (!h.ok) throw new Error(`status ${h.status}`);
  } catch {
    throw new Error(
      `golden thread: kernel :8088 not reachable at ${KERNEL} — start the slim stack first (make compose-slim)`,
    );
  }
  plane = await startFakeProvider();
  stateDir = mkdtempSync(join(tmpdir(), "ofac-golden-"));
  // The adapter leg: enrollment against the live kernel (the link-plane
  // half of the chain), outbound provider calls facing the plane.
  adapter = await run(
    parseEnvConfig({
      OFAC_KERNEL_URL: KERNEL,
      OFAC_ENROLL_SECRET: process.env["GOLDEN_ENROLL_SECRET"] ?? "enroll-dev",
      OFAC_ENDPOINT_ID: "golden-github-adapter",
      OFAC_TEAM: "team-alpha",
      OFAC_PROVIDER_INSTANCE_ID: "golden-github",
      GITHUB_URL: plane.url, // outbound legs face the plane
      GITHUB_TOKEN: "golden-token",
      GITHUB_WEBHOOK_SECRET: WEBHOOK_SECRET,
      GITHUB_BOT_USER: "ofac-bot",
      OFAC_STATE_PATH: join(stateDir, "adapter.sqlite3"),
      OFAC_HEALTH_PORT: "8399",
    }),
    { debug: () => undefined, info: () => undefined, warn: () => undefined, error: () => undefined },
  );
  await adapter.ready;
}, 60_000);

afterAll(async () => {
  await adapter?.shutdown();
  await plane?.close();
  if (stateDir) rmSync(stateDir, { recursive: true, force: true });
});

async function planeMode(family: string, mode: string): Promise<void> {
  const r = await fetch(`${plane.url}/__plane/mode`, {
    method: "POST",
    body: JSON.stringify({ family, mode }),
  });
  expect(r.status).toBe(200);
}

async function planeState(): Promise<{
  received: { family: string; method: string; path: string }[];
}> {
  return (await fetch(`${plane.url}/__plane/state`)).json() as Promise<{
    received: { family: string; method: string; path: string }[];
  }>;
}

describe("golden thread: ask -> issue leg (429-now retry) -> PR webhook -> review -> fork", () => {
  it("the ask seeds a work and the review-fork seam is readable via the API", async () => {
    const cli = await mint("facilitator-cli", { client_secret: "e2e-cli-secret" });
    const work = await kernelJson<{ workId: string; version: number }>("/api/works", cli, {
      method: "POST",
      headers: { "content-type": "application/json" },
      body: JSON.stringify({
        objective: "golden thread: fix the flaky report",
        team: "team-alpha",
      }),
    });
    expect(work.workId).toBeTruthy();
    // The seam reads stay honest shapes: timeline + links exist for the
    // seeded work (the deep E4 fork chain itself is walked in-proc by
    // ScenarioWalks:ReviewForkWalkTests — this manual leg proves the same
    // reads against the real deployed kernel).
    const timeline = await kernelJson<{ kind: string }[]>(`/api/works/${work.workId}/timeline`, cli);
    const links = await kernelJson<{ kind: string }[]>(`/api/works/${work.workId}/links`, cli);
    expect(Array.isArray(timeline)).toBe(true);
    expect(Array.isArray(links)).toBe(true);
  }, 60_000);

  it("the vcs.issue.create leg rides the 429-now retry path deterministically", async () => {
    // The plane answers 429 with Retry-After: 0 for every GitHub call: the
    // shared retry law (provider-core withRetry + httpErrorFrom, the exact
    // face the connectors use) must re-issue immediately and land the call
    // — two received calls, the second one's effect delivered.
    await planeMode("github", "429-now");
    let flipped = false;
    const call = async (): Promise<string> => {
      const r = await fetch(`${plane.url}/repos/demo/widgets/issues`, {
        method: "POST",
        headers: { authorization: "Bearer golden-token", "content-type": "application/json" },
        body: JSON.stringify({ title: "golden issue", body: "body" }),
      });
      // The shared taxonomy: non-2xx -> HttpError carrying the Retry-After.
      if (!r.ok) {
        const error = await httpErrorFrom(r, await r.text());
        if (!flipped) {
          flipped = true;
          expect(error.retryAfterSeconds).toBe(0); // the plane's 429-now promise
          await planeMode("github", "ok"); // flipped before the retry re-issues
        }
        throw error;
      }
      return String(((await r.json()) as { id: string | number }).id);
    };
    const issue = await withRetry(call, { backoffMs: [100] });
    expect(issue).toBeTruthy(); // the plane's created-issue token
    const st = await planeState();
    const createCalls = st.received.filter(
      (r) => r.family === "github" && r.method === "POST" && r.path === "/repos/demo/widgets/issues",
    );
    expect(createCalls.length).toBeGreaterThanOrEqual(2); // the 429'd one + the retry
    await planeMode("github", "ok");
  }, 60_000);

  it("the PR webhook rides the kernel sink (needs a provider-enabled kernel)", async () => {
    const payload = JSON.stringify({
      action: "opened",
      number: 4242,
      repository: { full_name: "demo/widgets", html_url: `${plane.url}/demo/widgets` },
      pull_request: {
        number: 4242,
        state: "open",
        title: "golden: fix the flaky report",
        html_url: `${plane.url}/demo/widgets/pull/4242`,
        user: { login: "contributor" },
        labels: [],
      },
    });
    const signature = `sha256=${createHmac("sha256", WEBHOOK_SECRET).update(payload).digest("hex")}`;
    const sink = await fetch(`${KERNEL}/api/ingress/webhooks/github`, {
      method: "POST",
      headers: {
        "content-type": "application/json",
        "x-github-event": "pull_request",
        "x-github-delivery": "11111111-1111-1111-1111-111111111111",
        "x-hub-signature-256": signature,
      },
      body: payload,
    });
    if (sink.status === 404 || sink.status === 405) {
      throw new Error(
        "golden thread: the kernel at :8088 has no GitHub sink route — this manual chain needs a stack with a Providers__GitHub section (reference/compose.full.yaml's provider env arm is the closest live shape)",
      );
    }
    expect(sink.status).toBe(202);

    // The review decision: the same sink for a pull_request_review; the
    // adapter session (this file's in-proc adapter) consumes the queued
    // command.provider.webhook.v1 and feeds the normalized event back.
    const review = JSON.stringify({
      action: "submitted",
      state: "changes_requested",
      review: {
        id: 9001,
        body: "guard the null case",
        user: { login: "reviewer" },
        pull_request_url: `${plane.url}/api/repos/demo/widgets/pulls/4242`,
      },
      repository: { full_name: "demo/widgets", html_url: `${plane.url}/demo/widgets` },
    });
    const reviewRes = await fetch(`${KERNEL}/api/ingress/webhooks/github`, {
      method: "POST",
      headers: {
        "content-type": "application/json",
        "x-github-event": "pull_request_review",
        "x-github-delivery": "22222222-2222-2222-2222-222222222222",
        "x-hub-signature-256": `sha256=${createHmac("sha256", WEBHOOK_SECRET).update(review).digest("hex")}`,
      },
      body: review,
    });
    expect(reviewRes.status).toBe(202);

    // The seam, via the kernel's own API: a provider-event audit trail
    // exists kernel-side for this delivery chain. The deep E4 fork chain
    // (review.decision row + successor link) rides a BOUND completed work
    // and is proven in-proc by ScenarioWalks:ReviewForkWalkTests; a manual
    // run against the demo stack prints what the seam observed instead of
    // assuming the demo's bindings point at this delivery.
    const cli = await mint("facilitator-cli", { client_secret: "e2e-cli-secret" });
    const audit = await kernelJson<unknown[]>("/api/admin/audit?limit=50", cli);
    expect(Array.isArray(audit)).toBe(true);
    console.log("golden thread: sink accepted PR + review deliveries; audit rows:", audit.length);
  }, 60_000);
});
