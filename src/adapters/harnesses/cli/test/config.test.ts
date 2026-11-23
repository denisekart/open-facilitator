// Config law matrix: every named-key error, XOR transports, env-overlay
// precedence, killGraceMs/defaultTimeoutMs/askUser bounds. YAML text goes
// in, WorkerConfigError (or a good config) comes out — no filesystem.
import { describe, expect, it } from "vitest";
import { WorkerConfigError, parseWorkerConfig, type WorkerConfig } from "../src/config.js";

const BASE = `
apiVersion: facilitator.framework/v1
kind: HarnessAdapter
childWorkspaceId: gw.team-alpha/cli
transport:
  socket:
    path: ./child.sock
playbookFile: ./research.playbook.yaml
kernelUrl: http://kernel:8080
`;

function parse(
  yaml: string,
  extra: { env?: Record<string, string | undefined> } = {},
): WorkerConfig {
  return parseWorkerConfig(yaml, { baseDir: "/worker", env: extra.env ?? {} });
}

function expectFail(yaml: string, key: string, env?: Record<string, string | undefined>): void {
  let error: unknown;
  try {
    parse(yaml, { ...(env !== undefined ? { env } : {}) });
  } catch (caught) {
    error = caught;
  }
  expect(error, `expected failure naming '${key}'`).toBeInstanceOf(WorkerConfigError);
  expect((error as Error).message).toContain(key);
}

describe("worker config law", () => {
  it("parses the base document with defaults", () => {
    const config = parse(BASE);
    expect(config.childWorkspaceId).toBe("gw.team-alpha/cli");
    expect(config.transport.socketPath).toBe("/worker/child.sock");
    expect(config.playbookFile).toBe("/worker/research.playbook.yaml");
    expect(config.workDir).toBe("/worker/.ofac-worker");
    expect(config.killGraceMs).toBe(5000);
    expect(config.defaultTimeoutMs).toBe(600_000);
    expect(config.askUser).toBe("off");
    expect(config.heartbeatSeconds).toBe(30);
    expect(config.restartBackoffMs).toEqual([100, 250, 500, 1000, 2000, 5000]);
    expect(config.logLevel).toBe("info");
  });

  it("playbookFile: required, resolved against the config dir; absolute stands", () => {
    expectFail(BASE.replace("playbookFile: ./research.playbook.yaml\n", ""), "playbookFile");
    expectFail(`${BASE}playbookFile: 7\n`, "playbookFile");
    expect(parse(BASE.replace("./research.playbook.yaml", "/opt/pb.yaml")).playbookFile).toBe(
      "/opt/pb.yaml",
    );
  });

  it("killGraceMs / defaultTimeoutMs: bounded integers with defaults", () => {
    expect(parse(`${BASE}killGraceMs: 250\n`).killGraceMs).toBe(250);
    expectFail(`${BASE}killGraceMs: -1\n`, "killGraceMs");
    expectFail(`${BASE}killGraceMs: 60001\n`, "killGraceMs");
    expect(parse(`${BASE}defaultTimeoutMs: 30000\n`).defaultTimeoutMs).toBe(30_000);
    expectFail(`${BASE}defaultTimeoutMs: 0\n`, "defaultTimeoutMs");
  });

  it("askUser: 'mcp' or 'off' only, default off", () => {
    expect(parse(`${BASE}askUser: mcp\n`).askUser).toBe("mcp");
    expect(parse(`${BASE}askUser: off\n`).askUser).toBe("off");
    expectFail(`${BASE}askUser: shell\n`, "askUser");
  });

  it("rejects unknown keys, naming them", () => {
    expectFail(`${BASE}sidecar: yes\n`, "file: unknown key 'sidecar'");
    expectFail(`${BASE}manifestFile: ./capabilities.yaml\n`, "file: unknown key 'manifestFile'");
    expectFail(
      "childWorkspaceId: gw.t/x\nplaybookFile: ./p.yaml\ntransport:\n  socket:\n    path: ./c.sock\n    mode: fancy\n",
      "transport.socket: unknown key 'mode'",
    );
  });

  it("childWorkspaceId law: needs a namespace prefix and a legal name", () => {
    expectFail(
      "childWorkspaceId: cli\ntransport:\n  socket:\n    path: ./c.sock\nplaybookFile: ./p.yaml\n",
      "childWorkspaceId",
    );
    expectFail(
      "childWorkspaceId: gw.team-alpha/UPPER\ntransport:\n  socket:\n    path: ./c.sock\nplaybookFile: ./p.yaml\n",
      "childWorkspaceId",
    );
  });

  it("transport XOR: both or neither fail", () => {
    expectFail(
      "childWorkspaceId: gw.t/x\nplaybookFile: ./p.yaml\ntransport:\n  socket:\n    path: ./c.sock\n  tcp:\n    port: 1\n",
      "transport",
    );
    expectFail("childWorkspaceId: gw.t/x\nplaybookFile: ./p.yaml\ntransport: {}\n", "transport");
  });

  it("tcp transport parses with host default", () => {
    const config = parse(
      "childWorkspaceId: gw.t/x\nplaybookFile: ./p.yaml\nkernelUrl: http://k:1\ntransport:\n  tcp:\n    port: 7070\n",
    );
    expect(config.transport).toEqual({ host: "127.0.0.1", port: 7070 });
  });

  it("env overlays win: secret, kernel url/token, log level", () => {
    const config = parse(`${BASE}token: file-token\nkernelToken: file-kernel\naskUser: mcp\n`, {
      env: {
        OFAC_CHILD_SECRET: "env-secret",
        OFAC_KERNEL_URL: "http://env-kernel:9",
        OFAC_KERNEL_TOKEN: "env-kernel-token",
        OFAC_ADAPTER_LOG: "debug",
      },
    });
    expect(config.token).toBe("env-secret");
    expect(config.kernelUrl).toBe("http://env-kernel:9");
    expect(config.kernelToken).toBe("env-kernel-token");
    expect(config.logLevel).toBe("debug");
    expect(config.askUser).toBe("mcp");
  });

  it("kernelUrl required: no artifact byte path without an origin", () => {
    expectFail(
      "childWorkspaceId: gw.t/x\nplaybookFile: ./p.yaml\ntransport:\n  socket:\n    path: ./c.sock\n",
      "kernelUrl",
    );
  });

  it("restartBackoffMs: list of bounded ints, [] disables auto-restart", () => {
    expect(parse(`${BASE}restartBackoffMs: [0, 5]\n`).restartBackoffMs).toEqual([0, 5]);
    expect(parse(`${BASE}restartBackoffMs: []\n`).restartBackoffMs).toEqual([]);
    expectFail(`${BASE}restartBackoffMs: [600001]\n`, "restartBackoffMs[0]");
    expectFail(`${BASE}restartBackoffMs: fancy\n`, "restartBackoffMs");
  });

  it("askQuestion: optional string; gates the optional human leg", () => {
    expect(parse(`${BASE}askQuestion: which framework?\n`).askQuestion).toBe("which framework?");
    expect(parse(BASE).askQuestion).toBeUndefined();
    expectFail(`${BASE}askQuestion: 7\n`, "askQuestion");
  });

  it("kind/apiVersion: only the known values", () => {
    expectFail(`${BASE}kind: Nope\n`, "kind");
    expectFail(`${BASE}apiVersion: facilitator.framework/v2\n`, "apiVersion");
  });
});
