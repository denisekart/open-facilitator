// Config law matrix: every named-key error, XOR transports, env-overlay
// precedence, stepMs/backoff bounds. YAML text goes in, WorkerConfigError
// (or a good config) comes out — no filesystem beyond an injected manifest.
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
childWorkspaceId: gw.team-alpha/deterministic
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
    expect(config.childWorkspaceId).toBe("gw.team-alpha/deterministic");
    expect(config.transport.socketPath).toBe("/worker/child.sock");
    expect(config.workDir).toBe("/worker/.ofac-worker");
    expect(config.stepMs).toBe(1500);
    expect(config.heartbeatSeconds).toBe(30);
    expect(config.restartBackoffMs).toEqual([100, 250, 500, 1000, 2000, 5000]);
    expect(config.logLevel).toBe("info");
    expect(config.askQuestion).toBeUndefined();
  });

  it("accepts heartbeatSeconds (the reference worker.yaml shape)", () => {
    expect(parse(`${BASE}heartbeatSeconds: 5\n`).heartbeatSeconds).toBe(5);
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
      "childWorkspaceId: deterministic\ntransport:\n  socket:\n    path: ./c.sock\nmanifestFile: ./m.yaml\n",
      "childWorkspaceId",
    );
    expectFail(
      "childWorkspaceId: gw.team-alpha/UPPER\ntransport:\n  socket:\n    path: ./c.sock\nmanifestFile: ./m.yaml\n",
      "childWorkspaceId",
    );
  });

  it("transport XOR: both or neither fail", () => {
    expectFail(
      "childWorkspaceId: gw.t/x\ntransport:\n  socket:\n    path: ./c.sock\n  tcp:\n    port: 8090\nmanifestFile: ./m.yaml\n",
      "transport: socket and tcp are mutually exclusive",
    );
    expectFail(
      "childWorkspaceId: gw.t/x\ntransport: {}\nmanifestFile: ./m.yaml\n",
      "transport: exactly one",
    );
  });

  it("tcp transport carries host+port; absolute socket paths stay absolute", () => {
    const config = parse(
      "childWorkspaceId: gw.t/x\ntransport:\n  tcp:\n    host: 127.0.0.1\n    port: 8090\nmanifestFile: ./m.yaml\nkernelUrl: http://k\n",
    );
    expect(config.transport).toEqual({ host: "127.0.0.1", port: 8090 });
    const absolute = parse(
      "childWorkspaceId: gw.t/x\ntransport:\n  socket:\n    path: /run/c.sock\nmanifestFile: ./m.yaml\nkernelUrl: http://k\n",
    );
    expect(absolute.transport.socketPath).toBe("/run/c.sock");
  });

  it("stepMs bounds 0..60000, integers only", () => {
    expectFail(`${BASE}stepMs: 60001\n`, "stepMs");
    expectFail(`${BASE}stepMs: -1\n`, "stepMs");
    expectFail(`${BASE}stepMs: 1.5\n`, "stepMs");
    expect(parse(`${BASE}stepMs: 0`).stepMs).toBe(0);
  });

  it("restartBackoffMs: [] disables auto-restart; entries bounded", () => {
    expect(parse(`${BASE}restartBackoffMs: []\n`).restartBackoffMs).toEqual([]);
    expectFail(`${BASE}restartBackoffMs: [10, 700000]\n`, "restartBackoffMs[1]");
    expectFail(`${BASE}restartBackoffMs: notalist\n`, "restartBackoffMs");
  });

  it("env overlay: OFAC_CHILD_SECRET wins over the file token; OFAC_ADAPTER_LOG sets the level", () => {
    const withFile = parse(`${BASE}token: file-secret\n`, { env: {} });
    expect(withFile.token).toBe("file-secret");
    const overridden = parse(`${BASE}token: file-secret\n`, {
      env: { OFAC_CHILD_SECRET: "env-secret" },
    });
    expect(overridden.token).toBe("env-secret");
    const logged = parse(BASE, { env: { OFAC_ADAPTER_LOG: "debug" } });
    expect(logged.logLevel).toBe("debug");
  });

  it("kernelUrl: required; file key parses; OFAC_KERNEL_URL overlay wins", () => {
    expectFail(
      "childWorkspaceId: gw.t/x\ntransport:\n  socket:\n    path: ./c.sock\nmanifestFile: ./m.yaml\n",
      "kernelUrl",
    );
    const withFile = parse(BASE, { env: {} });
    expect(withFile.kernelUrl).toBe("http://kernel:8080");
    const overridden = parse(BASE.replace("http://kernel:8080", "http://file:1"), {
      env: { OFAC_KERNEL_URL: "http://env:2" },
    });
    expect(overridden.kernelUrl).toBe("http://env:2");
  });

  it("kernelToken: file key parses; OFAC_KERNEL_TOKEN overlay wins (separate kernel credential)", () => {
    const bare = parse(BASE, { env: {} });
    expect(bare.kernelToken).toBeUndefined();
    const withFile = parse(`${BASE}kernelToken: link-abc\n`, { env: {} });
    expect(withFile.kernelToken).toBe("link-abc");
    const overridden = parse(`${BASE}kernelToken: file-abc\n`, {
      env: { OFAC_KERNEL_TOKEN: "env-abc" },
    });
    expect(overridden.kernelToken).toBe("env-abc");
  });

  it("apiVersion/kind are optional but exact when present", () => {
    expectFail(`${BASE}apiVersion: facilitator.framework/v2\n`, "apiVersion");
    expectFail(`${BASE}kind: Nope\n`, "kind");
  });
});
