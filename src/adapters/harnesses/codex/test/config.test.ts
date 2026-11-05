// Config law matrix: every named-key error, XOR transports, env-overlay
// precedence, and the Codex-specific keys (model, extraArgs, interactivity,
// promptTemplate, askQuestion). YAML text goes in, WorkerConfigError (or a
// good config) comes out — no filesystem beyond an injected manifest.
import { describe, expect, it } from "vitest";
import { WorkerConfigError, parseWorkerConfig } from "../src/config.js";

const MANIFEST = {
  apiVersion: "facilitator.framework/v1",
  kind: "CapabilityManifest",
  ownerTeam: "team-alpha",
  capabilities: [],
};

const BASE = `
apiVersion: facilitator.framework/v1
kind: HarnessAdapter
childWorkspaceId: gw.team-alpha/codex
transport:
  socket:
    path: ./child.sock
manifestFile: ./capabilities.yaml
kernelUrl: http://kernel:8080
`;

function parse(
  yaml: string,
  extra: { env?: Record<string, string | undefined> } = {},
): ReturnType<typeof parseWorkerConfig> {
  return parseWorkerConfig(yaml, {
    baseDir: "/worker",
    env: extra.env ?? {},
    readManifest: () => MANIFEST,
  });
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
    expect(config.childWorkspaceId).toBe("gw.team-alpha/codex");
    expect(config.transport.socketPath).toBe("/worker/child.sock");
    expect(config.workDir).toBe("/worker/.ofac-worker");
    expect(config.interactivity).toBe("mcp");
    expect(config.extraArgs).toEqual([]);
    expect(config.heartbeatSeconds).toBe(30);
    expect(config.restartBackoffMs).toEqual([100, 250, 500, 1000, 2000, 5000]);
    expect(config.logLevel).toBe("info");
    expect(config.model).toBeUndefined();
    expect(config.askQuestion).toBeUndefined();
    expect(config.promptTemplate).toBeUndefined();
  });

  it("model: optional string", () => {
    expect(parse(`${BASE}model: gpt-5-codex\n`).model).toBe("gpt-5-codex");
    expectFail(`${BASE}model: 7\n`, "model");
  });

  it("extraArgs: verbatim string list; non-strings name the entry", () => {
    const config = parse(`${BASE}extraArgs:\n  - -c\n  - sandbox_mode="read-only"\n`);
    expect(config.extraArgs).toEqual(["-c", 'sandbox_mode="read-only"']);
    expectFail(`${BASE}extraArgs: nope\n`, "extraArgs");
    expectFail(`${BASE}extraArgs:\n  - 5\n`, "extraArgs[0]");
  });

  it("interactivity: 'mcp' or 'off' only", () => {
    expect(parse(`${BASE}interactivity: off\n`).interactivity).toBe("off");
    expectFail(`${BASE}interactivity: telepathy\n`, "interactivity");
  });

  it("promptTemplate: must carry the {objective} placeholder", () => {
    expect(parse(`${BASE}promptTemplate: "do {objective} now"\n`).promptTemplate).toBe(
      "do {objective} now",
    );
    expectFail(`${BASE}promptTemplate: no placeholder\n`, "promptTemplate");
  });

  it("askQuestion: optional string; non-empty or it names the key", () => {
    expect(parse(`${BASE}askQuestion: which framework?\n`).askQuestion).toBe("which framework?");
    expectFail(`${BASE}askQuestion: 7\n`, "askQuestion");
    expectFail(`${BASE}askQuestion: ""\n`, "askQuestion");
    expectFail(`${BASE}askQuestion:\n  nested: true\n`, "askQuestion");
  });

  it("rejects unknown keys, naming them", () => {
    expectFail(`${BASE}sidecar: yes\n`, "file: unknown key 'sidecar'");
    expectFail(
      "childWorkspaceId: gw.t/x\nmanifestFile: ./m.yaml\ntransport:\n  socket:\n    path: ./c.sock\n    mode: fancy\n",
      "transport.socket: unknown key 'mode'",
    );
  });

  it("childWorkspaceId law: needs a namespace prefix and a legal name", () => {
    expectFail(
      "childWorkspaceId: codex\ntransport:\n  socket:\n    path: ./c.sock\nmanifestFile: ./m.yaml\n",
      "childWorkspaceId",
    );
    expectFail(
      "childWorkspaceId: gw.team-alpha/UPPER\ntransport:\n  socket:\n    path: ./c.sock\nmanifestFile: ./m.yaml\n",
      "childWorkspaceId",
    );
  });

  it("transport XOR: socket or tcp, never both, never neither", () => {
    const tcp = parse(
      "childWorkspaceId: gw.t/cx\nmanifestFile: ./m.yaml\nkernelUrl: http://k:1\ntransport:\n  tcp:\n    port: 9412\n",
    );
    expect(tcp.transport.host).toBe("127.0.0.1");
    expect(tcp.transport.port).toBe(9412);
    expectFail(
      "childWorkspaceId: gw.t/cx\nmanifestFile: ./m.yaml\nkernelUrl: http://k:1\ntransport:\n  socket:\n    path: ./c.sock\n  tcp:\n    port: 1\n",
      "mutually exclusive",
    );
    expectFail(
      "childWorkspaceId: gw.t/cx\nmanifestFile: ./m.yaml\nkernelUrl: http://k:1\ntransport: {}\n",
      "exactly one of socket/tcp",
    );
    expectFail(
      "childWorkspaceId: gw.t/cx\nmanifestFile: ./m.yaml\nkernelUrl: http://k:1\ntransport:\n  tcp:\n    host: h\n",
      "port",
    );
  });

  it("kernelUrl is required unless the env overlay supplies it", () => {
    expectFail(
      "childWorkspaceId: gw.t/cx\nmanifestFile: ./m.yaml\ntransport:\n  socket:\n    path: ./c.sock\n",
      "kernelUrl",
    );
    const viaEnv = parse(
      "childWorkspaceId: gw.t/cx\nmanifestFile: ./m.yaml\ntransport:\n  socket:\n    path: ./c.sock\n",
      { env: { OFAC_KERNEL_URL: "http://env-kernel:9" } },
    );
    expect(viaEnv.kernelUrl).toBe("http://env-kernel:9");
  });

  it("env overlays: OFAC_CHILD_SECRET beats the file token; OFAC_ADAPTER_LOG sets the level", () => {
    const config = parse(`${BASE}token: file-token\n`, {
      env: { OFAC_CHILD_SECRET: "env-secret", OFAC_ADAPTER_LOG: "debug" },
    });
    expect(config.token).toBe("env-secret");
    expect(config.logLevel).toBe("debug");
  });

  it("kernelToken rides its own overlay", () => {
    const config = parse(`${BASE}kernelToken: file-k\n`, { env: { OFAC_KERNEL_TOKEN: "env-k" } });
    expect(config.kernelToken).toBe("env-k");
  });

  it("restartBackoffMs: bounded integers; [] disables auto-restart", () => {
    expect(parse(`${BASE}restartBackoffMs: []\n`).restartBackoffMs).toEqual([]);
    expectFail(`${BASE}restartBackoffMs:\n  - -1\n`, "restartBackoffMs[0]");
    expectFail(`${BASE}restartBackoffMs:\n  - 700000\n`, "restartBackoffMs[0]");
  });

  it("heartbeatSeconds: bounded integer", () => {
    expect(parse(`${BASE}heartbeatSeconds: 5\n`).heartbeatSeconds).toBe(5);
    expectFail(`${BASE}heartbeatSeconds: 0\n`, "heartbeatSeconds");
    expectFail(`${BASE}heartbeatSeconds: 99999\n`, "heartbeatSeconds");
  });

  it("bad YAML names the file", () => {
    expectFail("\tnot: [valid", "file");
  });

  it("apiVersion/kind are validated when present", () => {
    expectFail(`${BASE}apiVersion: wrong/v2\n`, "apiVersion");
    expectFail(`${BASE}kind: NotAdapter\n`, "kind");
  });
});
