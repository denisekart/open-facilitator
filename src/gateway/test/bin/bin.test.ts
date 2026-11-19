// The process main: config → boot → ready line → SIGINT → exit code. The
// Signalr class is swapped for a scripted fake at the module seam (the bin
// builds its own transport), and the kernel plane is the shared fake-kernel
// behind a globalThis.fetch stub. Exit codes are the contract:
// 0 clean · 1 failed-to-reach-ready · 2 config · 3 protocol · 4 refused.
import { mkdtempSync, writeFileSync } from "node:fs";
import { createServer } from "node:net";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import type { WelcomeFrame } from "@ofac/workspace-link";
import { fakeKernel } from "../fake-kernel.js";

/** Scripting knobs the mocked SignalrTransport reads at call time. */
const h = vi.hoisted(() => ({
  welcome: null as WelcomeFrame | null,
  welcomeFails: false,
}));

vi.mock("@ofac/workspace-link/node", async (importOriginal) => {
  const original = await importOriginal<typeof import("@ofac/workspace-link/node")>();
  class FakeSignalr {
    private deliverCbs: ((d: never) => void)[] = [];
    async connect(): Promise<void> {}
    async hello(): Promise<WelcomeFrame | null> {
      if (h.welcomeFails) throw new Error("hub refused");
      return h.welcome;
    }
    async envelope(): Promise<{ status: "accepted"; deliveryId: string }> {
      return { status: "accepted", deliveryId: "inbox-bin" };
    }
    async receipt(): Promise<void> {}
    async heartbeat(): Promise<boolean> {
      return true;
    }
    async goodbye(): Promise<void> {}
    async close(): Promise<void> {}
    onDeliver(cb: (d: never) => void): void {
      this.deliverCbs.push(cb);
    }
    onFlow(): void {}
    onDisconnect(): void {}
  }
  return { ...original, SignalrTransport: FakeSignalr };
});

import { run } from "../../src/bin/gateway.js";

const WELCOME: WelcomeFrame = {
  kind: "welcome",
  sessionId: "sess-bin",
  leaseEpoch: 1,
  maxInFlight: 8,
  heartbeatSeconds: 300,
  protocolVersion: "v1",
};

let kernel: ReturnType<typeof fakeKernel>;
let out: string[];
let envBackup: string | undefined;

function configYaml(dir: string, extra = ""): string {
  return `
apiVersion: facilitator.framework/v1
kind: Gateway
gatewayId: gw.bin-test
team: default
facilitatorUrl: http://kernel.test
enrollmentSecret: enroll-bin
trustMode: shared-runtime
stateDir: ${dir}
child:
  name: echo
  mode: echo
${extra}heartbeatSeconds: 3600
`;
}

function writeConfig(extra = ""): string {
  const dir = mkdtempSync(join(tmpdir(), "gw-bin-"));
  const path = join(dir, "gateway.yaml");
  writeFileSync(path, configYaml(dir, extra));
  return path;
}

beforeEach(() => {
  kernel = fakeKernel();
  vi.stubGlobal("fetch", kernel.fetchImpl);
  out = [];
  vi.spyOn(process.stdout, "write").mockImplementation((chunk) => {
    out.push(String(chunk));
    return true;
  });
  vi.spyOn(process.stderr, "write").mockImplementation((chunk) => {
    out.push(String(chunk));
    return true;
  });
  h.welcome = WELCOME;
  h.welcomeFails = false;
  envBackup = process.env["GATEWAY_CONFIG"];
  delete process.env["GATEWAY_CONFIG"];
});

afterEach(async () => {
  process.removeAllListeners("SIGINT");
  process.removeAllListeners("SIGTERM");
  if (envBackup !== undefined) process.env["GATEWAY_CONFIG"] = envBackup;
  vi.unstubAllGlobals();
  vi.restoreAllMocks();
  await new Promise((r) => setTimeout(r, 20)); // let shutdown legs settle
});

const waitFor = async (what: string, f: () => unknown): Promise<void> => {
  const deadline = Date.now() + 5_000;
  for (;;) {
    if (f() !== false && f() !== undefined && f() !== null) return;
    if (Date.now() > deadline) throw new Error(`timeout: ${what}`);
    await new Promise((r) => setTimeout(r, 10));
  }
};

describe("ofac-gateway run", () => {
  it("boots to a ready line and exits 0 on SIGINT", async () => {
    const path = writeConfig();
    const code = run(["run", "--config", path]);
    await waitFor("ready line", () => out.find((l) => l.includes("gateway ready")));
    expect(out.join("")).toMatch(/gateway ready endpointId=gw\.bin-test epoch=1/);
    // The boot told the kernel the truth: enroll, then the capability POST.
    expect(kernel.calls).toContain("enroll");
    expect(kernel.calls).toContain("capabilities");
    process.emit("SIGINT");
    expect(await code).toBe(0);
  });

  it("GATEWAY_CONFIG supplies the config path when --config is absent", async () => {
    const path = writeConfig();
    process.env["GATEWAY_CONFIG"] = path;
    const code = run(["run"]);
    await waitFor("ready line", () => out.find((l) => l.includes("gateway ready")));
    process.emit("SIGINT");
    expect(await code).toBe(0);
  });

  it("--help prints usage, exit 0", async () => {
    expect(await run(["--help"])).toBe(0);
    expect(out.join("")).toContain("ofac-gateway run --config");
  });

  it("a non-run positional is a config error, exit 2", async () => {
    expect(await run(["serve", "--config", writeConfig()])).toBe(2);
    expect(out.join("")).toContain("must be 'run'");
  });

  it("no config path at all, exit 2", async () => {
    expect(await run(["run"])).toBe(2);
  });

  it("an unknown flag is a parse error, exit 2", async () => {
    expect(await run(["run", "--bogus"])).toBe(2);
  });

  it("a malformed config file maps to exit 2", async () => {
    const dir = mkdtempSync(join(tmpdir(), "gw-bin-bad-"));
    const path = join(dir, "gateway.yaml");
    writeFileSync(path, "apiVersion: x\nkind: Gateway\ngatewayId: gw\n");
    expect(await run(["run", "--config", path])).toBe(2);
    expect(out.join("")).toContain("gateway config error");
  });

  it("an enrollment refusal (4xx) maps to exit 4", async () => {
    // A 403 on the challenge is terminal: repeating it changes nothing.
    const inner = kernel.fetchImpl;
    vi.stubGlobal("fetch", async (url: unknown, init?: RequestInit) =>
      String(url).includes("/challenge")
        ? new Response(
            JSON.stringify({
              type: "about:blank",
              title: "refused",
              status: 403,
              detail: "team unknown",
            }),
            { status: 403, headers: { "content-type": "application/json" } },
          )
        : inner(url as string, init),
    );
    const code = await run(["run", "--config", writeConfig()]);
    expect(code).toBe(4);
    expect(out.join("")).toContain("gateway enrollment refused");
  });

  it("a protocol mismatch (welcome refused) maps to exit 3", async () => {
    h.welcome = null; // the hub answers the hello with nothing it recognizes
    const code = await run(["run", "--config", writeConfig()]);
    expect(code).toBe(3);
    expect(out.join("")).toContain("protocol mismatch");
  });

  it("a failure that is none of the above maps to exit 1", async () => {
    // Diagnostics fail-fast: a port already taken by a foreign listener.
    const clash = createServer();
    await new Promise<void>((r) => clash.listen(0, "127.0.0.1", r));
    const port = (clash.address() as { port: number }).port;
    const code = await run(["run", "--config", writeConfig(`diagnostics:\n  port: ${port}\n`)]);
    expect(code).toBe(1);
    expect(out.join("")).toContain("failed to reach ready");
    await new Promise<void>((r) => clash.close(() => r()));
  });
});
