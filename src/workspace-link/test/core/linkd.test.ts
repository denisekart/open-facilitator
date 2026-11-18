// ofac-linkd's entrypoint as an importable unit (the E2E tier spawns the real
// binary; this tier injects the world): main()'s locked exit-code mapping
// (0 clean, 2 config, 3 protocol mismatch, 4 enrollment refused, 1 kernel-not-
// ready), the USAGE answer, and run()'s boot law — manifest read + ownerTeam
// gate, the identity in --state-dir, the first-boot enroll, capabilities
// publication over the session token, and a ready handshake over the fake hub.
// No live hub, no spawned process.
import { mkdtempSync, rmSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { main, run } from "../../src/bin/linkd.js";
import type { LinkdConfig, LinkLogger, WelcomeFrame } from "../../src/index.js";
import { fakes, liveHub, resetFakes, type FakeHub } from "./fake-signalr.js";
import { manifest as manifestFixture } from "./fixtures.js";

vi.mock("@microsoft/signalr", () => import("./fake-signalr.js").then((m) => m.fakeSignalr));

const BASE_URL = "https://kernel.test";

const WELCOME: WelcomeFrame = {
  kind: "welcome",
  sessionId: "sess-linkd",
  leaseEpoch: 1,
  maxInFlight: 16,
  heartbeatSeconds: 30,
  protocolVersion: "v1",
};

const silent: LinkLogger = { debug() {}, info() {}, warn() {}, error() {} };

let dirs: string[] = [];
let written: { stdout: string[]; stderr: string[] };

function tempDir(): string {
  const dir = mkdtempSync(join(tmpdir(), "ofac-linkd-"));
  dirs.push(dir);
  return dir;
}

interface Call {
  url: string;
  init: RequestInit;
}

/**
 * The kernel linkd talks to through plain `fetch`: challenge, proof,
 * capabilities, and enroll when a secret is configured. `enrolled: false`
 * makes the first proof answer 401 ("no live enrollment"), which is the only
 * signal that triggers an enroll.
 */
function installKernel(options: { enrolled: boolean; problemStatus?: number }): Call[] {
  const calls: Call[] = [];
  const state = { proofs: 0, challenges: 0 };
  let enrolledNow = options.enrolled;
  const json = (status: number, body: unknown): Response =>
    new Response(JSON.stringify(body), {
      status,
      headers: { "content-type": "application/json" },
    });
  const fetchImpl = (async (url: unknown, init?: RequestInit) => {
    const route = String(url).replace(`${BASE_URL}/api/link/v1/`, "").split("?")[0]!;
    calls.push({ url: String(url), init: init ?? {} });
    switch (route) {
      case "challenge": {
        state.challenges += 1;
        return json(200, {
          nonce: `1.${state.challenges}.r`,
          publicKeyRequired: true,
          algorithm: "ed25519",
          expiresAt: new Date(Date.now() + 30_000).toISOString(),
        });
      }
      case "proof": {
        state.proofs += 1;
        if (!enrolledNow) {
          return json(options.problemStatus ?? 401, {
            detail: "No live enrollment for this endpoint.",
          });
        }
        return json(200, {
          sessionToken: `jwt-${state.proofs}`,
          expiresAt: new Date(Date.now() + 300_000).toISOString(),
          seq: state.proofs,
        });
      }
      case "capabilities":
        return new Response(null, { status: 204 });
      case "deliveries":
        return json(200, []); // the http-poll keeper's long-poll claim: nothing queued
      default:
        enrolledNow = true; // an enroll lands: later proofs answer
        return json(201, {
          enrollmentId: "enr-1",
          trustMode: "isolated-executor",
          endpointKeyId: `sha256:${"ab".repeat(32)}`,
          seq: 1,
        });
    }
  }) as unknown as typeof fetch;
  vi.stubGlobal("fetch", fetchImpl);
  return calls;
}

/** A config over a fresh state dir with a valid manifest. */
function configFor(overrides: Partial<LinkdConfig> = {}): LinkdConfig {
  const stateDir = tempDir();
  const manifestPath = join(stateDir, "manifest.json");
  writeFileSync(manifestPath, JSON.stringify(manifestFixture("team-alpha")));
  return {
    url: BASE_URL,
    endpointId: "worker-1",
    team: "team-alpha",
    manifestPath,
    trustMode: "isolated-executor",
    role: "child",
    transport: "signalr",
    stateDir,
    statePath: join(stateDir, "ledger.sqlite3"),
    mode: "echo",
    ...overrides,
  };
}

const RUN_ARGS = ["run", "--url", BASE_URL, "--endpoint", "worker-1", "--team", "team-alpha"];

function stderrText(): string {
  return written.stderr.join("");
}

function stdoutText(): string {
  return written.stdout.join("");
}

/** Drive a `main()` boot to ready and park, then signal a clean shutdown. */
async function mainWithSigterm(args: string[]): Promise<number> {
  const done = main(args);
  await vi.waitFor(() => expect(stdoutText()).toContain("linkd ready"));
  process.emit("SIGTERM");
  return done;
}

beforeEach(() => {
  resetFakes();
  dirs = [];
  written = { stdout: [], stderr: [] };
  vi.spyOn(process.stdout, "write").mockImplementation(((chunk: string) => {
    written.stdout.push(String(chunk));
    return true;
  }) as typeof process.stdout.write);
  vi.spyOn(process.stderr, "write").mockImplementation(((chunk: string) => {
    written.stderr.push(String(chunk));
    return true;
  }) as typeof process.stderr.write);
  // The hub answers every server method; after hello the client parks, so no
  // further hub traffic happens during a boot.
  fakes.defaults.set("Hello", WELCOME);
  fakes.defaults.set("Envelope", { status: "accepted" });
  fakes.defaults.set("Heartbeat", true);
});

afterEach(() => {
  vi.unstubAllGlobals();
  vi.restoreAllMocks();
  for (const dir of dirs) rmSync(dir, { recursive: true, force: true });
});

describe("main — the locked exit-code contract", () => {
  it("prints USAGE and exits clean on --help", async () => {
    expect(await main(["--help"])).toBe(0);
    expect(stdoutText()).toContain("ofac-linkd run --url");
    expect(stdoutText()).toContain("Exit codes: 0 clean, 2 config error");
  });

  it("maps a config error to exit 2 (bad positional, missing flag, bad enum)", async () => {
    expect(await main(["walk", "--url", BASE_URL])).toBe(2);
    expect(stderrText()).toContain("linkd config error");
    expect(stderrText()).toContain("first positional must be 'run'");

    expect(await main(["run"])).toBe(2);
    expect(stderrText()).toContain("missing --url");

    const stateDir = tempDir();
    expect(
      await main([
        ...RUN_ARGS,
        "--manifest",
        join(stateDir, "m.json"),
        "--transport",
        "carrier-pigeon",
      ]),
    ).toBe(2);
    expect(stderrText()).toContain("unknown --transport 'carrier-pigeon'");
  });

  it("an unenrolled endpoint with no code exits 4 (a refusal, not a retry)", async () => {
    installKernel({ enrolled: false });
    const config = configFor();
    expect(
      await main([...RUN_ARGS, "--manifest", config.manifestPath, "--state-dir", config.stateDir]),
    ).toBe(4);
    expect(stderrText()).toContain("linkd enrollment refused");
  });

  it("a 5xx at boot exits 1 — a kernel that is not ready is the restart loop's problem", async () => {
    installKernel({ enrolled: false, problemStatus: 503 });
    const config = configFor();
    const code = await main([
      ...RUN_ARGS,
      "--manifest",
      config.manifestPath,
      "--state-dir",
      config.stateDir,
    ]);
    expect(code).toBe(1);
    expect(stderrText()).toContain("linkd enrollment deferred");
  });

  it("boots to ready over the fake hub, publishes capabilities, and exits 0 on SIGTERM", async () => {
    const calls = installKernel({ enrolled: true });
    const config = configFor();
    const code = await mainWithSigterm([
      ...RUN_ARGS,
      "--manifest",
      config.manifestPath,
      "--state-dir",
      config.stateDir,
      "--mode",
      "stub",
    ]);

    expect(code).toBe(0);
    // The kernel saw challenge → proof → capabilities and NO enroll: the
    // endpoint was already enrolled, so the code stays spent.
    // The socket hello carries its own channel proof, so a second challenge
    // follows the capabilities POST — and still NO enroll: the endpoint was
    // already enrolled, so the code stays spent.
    expect(calls.map((c) => c.url)).toEqual([
      `${BASE_URL}/api/link/v1/challenge`,
      `${BASE_URL}/api/link/v1/proof`,
      `${BASE_URL}/api/link/v1/capabilities`,
      `${BASE_URL}/api/link/v1/challenge`,
    ]);
    const capCall = calls[2]!;
    expect((capCall.init.headers as Record<string, string>)["authorization"]).toBe("Bearer jwt-1");
    // The hub got a hello naming this endpoint.
    const hello = liveHub().invokes.find((i) => i.method === "Hello");
    expect((hello?.arg as { endpointId?: string } | undefined)?.endpointId).toBe("worker-1");
    expect(stdoutText()).toContain("linkd ready endpointId=worker-1 epoch=1 transport=signalr");
    // A clean shutdown says goodbye and stops the socket.
    const hub: FakeHub = liveHub();
    expect(hub.invokes.some((i) => i.method === "Goodbye")).toBe(true);
    expect(hub.stops).toBeGreaterThanOrEqual(1);
  });

  it("refuses a manifest whose ownerTeam is not --team (exit 2, no network)", async () => {
    const calls = installKernel({ enrolled: true });
    const config = configFor();
    writeFileSync(config.manifestPath, JSON.stringify(manifestFixture("team-beta")));
    expect(
      await main([...RUN_ARGS, "--manifest", config.manifestPath, "--state-dir", config.stateDir]),
    ).toBe(2);
    expect(stderrText()).toContain("manifest ownerTeam 'team-beta'");
    expect(calls).toHaveLength(0);
  });

  it("an unreadable manifest is a config error (exit 2)", async () => {
    installKernel({ enrolled: true });
    const stateDir = tempDir();
    expect(
      await main([
        ...RUN_ARGS,
        "--manifest",
        join(stateDir, "missing.json"),
        "--state-dir",
        stateDir,
      ]),
    ).toBe(2);
    expect(stderrText()).toContain("cannot read manifest");
  });
});

describe("run — the supervisor handle", () => {
  it("enrolls on first boot when a secret is configured, then proves", async () => {
    const calls = installKernel({ enrolled: false });
    const handle = await run(configFor({ secret: "enroll-dev", mode: "stub" }), silent);
    expect((await handle.ready).leaseEpoch).toBe(1);
    // linkd enrolls FIRST when a secret is configured (it is the boot that has
    // nothing on file), then proves, publishes, and hello-proves the socket.
    expect(calls.map((c) => c.url)).toEqual([
      `${BASE_URL}/api/link/v1/enroll`,
      `${BASE_URL}/api/link/v1/challenge`,
      `${BASE_URL}/api/link/v1/proof`,
      `${BASE_URL}/api/link/v1/capabilities`,
      `${BASE_URL}/api/link/v1/challenge`, // the hello's channel proof
    ]);
    expect(stderrText()).toContain("linkd enrolled endpointKeyId=");
    const enrollBody = JSON.parse(String(calls[0]!.init.body)) as Record<string, unknown>;
    expect(enrollBody).toMatchObject({
      token: "enroll-dev",
      endpointId: "worker-1",
      team: "team-alpha",
      role: "child",
      keyAlgorithm: "ed25519",
    });
    expect(String(enrollBody["instanceId"])).toMatch(/^lnk-[0-9a-f]{16}$/);
    await handle.shutdown();
    expect(handle.client.stats.reconnects).toBe(0);
  });

  it("the http-poll transport still mints its session over the hub keeper", async () => {
    installKernel({ enrolled: true });
    const handle = await run(configFor({ transport: "http-poll" }), silent);
    await handle.ready;
    // HttpPollTransport keeps a hidden keeper socket: hello rode the hub.
    expect(liveHub().invokes.some((i) => i.method === "Hello")).toBe(true);
    await handle.shutdown();
  });

  it("a null welcome (no common protocol version) rejects ready", async () => {
    installKernel({ enrolled: true });
    fakes.defaults.set("Hello", null);
    const handle = await run(configFor(), silent);
    await expect(handle.ready).rejects.toThrow(/no-compatible-protocol-version/);
    await handle.shutdown();
  });
});
