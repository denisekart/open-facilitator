// Diagnostics tier: real loopback bind (honesty endpoint — a mocked server
// would test nothing), one route, no auth.
import { mkdtempSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { afterEach, describe, expect, it } from "vitest";
import { parseConfig, type GatewayConfig } from "../../src/config.js";
import { createGatewayRuntime, type GatewayRuntime } from "../../src/runtime.js";
import { startDiagnostics, type DiagnosticsHandle } from "../../src/diagnostics.js";

class DeadTransport {
  async connect(): Promise<void> {
    throw new Error("no network in diagnostics tests");
  }
}

function config(): GatewayConfig {
  return parseConfig(
    `
apiVersion: facilitator.framework/v1
kind: Gateway
gatewayId: gw.diag-test
team: default
facilitatorUrl: http://kernel.test
linkToken: tok-diag
trustMode: shared-runtime
stateDir: ${mkdtempSync(join(tmpdir(), "gw-diag-"))}
child:
  name: echo
  mode: echo
`,
    { env: {} },
  );
}

let handle: DiagnosticsHandle | null = null;
let runtime: GatewayRuntime | null = null;

afterEach(async () => {
  if (handle !== null) await handle.close().catch(() => undefined);
  handle = null;
  runtime = null;
});

function bareRuntime(): GatewayRuntime {
  return createGatewayRuntime(config(), {
    transportFactory: () => new DeadTransport() as never,
  });
}

describe("diagnostics endpoint", () => {
  it("port 0 disables the endpoint entirely", async () => {
    runtime = bareRuntime();
    handle = await startDiagnostics(runtime, 0);
    expect(handle).toBeNull();
  });

  it("answers 200 with the honest pre-start status; other routes 404", async () => {
    // No start(): the runtime never connected, yet the endpoint must answer
    // with the honest pre-ready shape (dev-tier honesty).
    runtime = bareRuntime();
    const port = 39_417;
    handle = await startDiagnostics(runtime, port);
    expect(handle?.port).toBe(port);
    const status = await fetch(`http://127.0.0.1:${port}/status`);
    expect(status.status).toBe(200);
    expect(status.headers.get("content-type")).toBe("application/json");
    const body = (await status.json()) as {
      gatewayId: string;
      state: string;
      reconciled: boolean;
      session?: unknown;
    };
    expect(body.gatewayId).toBe("gw.diag-test");
    expect(body.state).toBe("starting");
    expect(body.reconciled).toBe(false);
    expect(body.session).toBeUndefined();

    const missing = await fetch(`http://127.0.0.1:${port}/nonsense`);
    expect(missing.status).toBe(404);
    const post = await fetch(`http://127.0.0.1:${port}/status`, { method: "POST" });
    expect(post.status).toBe(404);
  });

  it("bind failure surfaces as a rejection (fatal at startup)", async () => {
    runtime = bareRuntime();
    handle = await startDiagnostics(runtime, 39_418);
    await expect(startDiagnostics(runtime, 39_418)).rejects.toThrow(/cannot bind/);
  });
});
