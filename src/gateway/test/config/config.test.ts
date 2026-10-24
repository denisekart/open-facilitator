import { mkdirSync, mkdtempSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { describe, expect, it } from "vitest";

import { GatewayConfigError, loadConfigFile, parseConfig, tokenFilePath } from "../../src/config.js";

function stateDir(): string {
  return mkdtempSync(join(tmpdir(), "gw-config-"));
}

const BASE = `
apiVersion: facilitator.framework/v1
kind: Gateway
gatewayId: gw.team-alpha
team: default
facilitatorUrl: http://localhost:8088
enrollmentSecret: enroll-dev
trustMode: shared-runtime
stateDir: /tmp/gw-state
child:
  name: echo
  mode: echo
`;

function expectKey(yaml: string, key: string, env: Record<string, string | undefined> = {}): void {
  expect(() => parseConfig(yaml, { env, hasStoredToken: () => false })).toThrow(GatewayConfigError);
  try {
    parseConfig(yaml, { env, hasStoredToken: () => false });
  } catch (error) {
    expect((error as Error).message).toContain(key);
  }
}

describe("parseConfig happy path", () => {
  it("parses the locked key set with defaults", () => {
    const config = parseConfig(BASE, { hasStoredToken: () => false });
    expect(config.gatewayId).toBe("gw.team-alpha");
    expect(config.team).toBe("default");
    expect(config.trustMode).toBe("shared-runtime");
    expect(config.stateDir).toBe("/tmp/gw-state");
    expect(config.child).toEqual({ name: "echo", mode: "echo" });
    expect(config.diagnostics.port).toBe(0);
    expect(config.heartbeatSeconds).toBe(30);
    expect(config.enrollmentSecret).toBe("enroll-dev");
    expect(config.linkToken).toBeUndefined();
  });

  it("loads from a YAML file (spec §8.4 layout)", () => {
    const file = join(stateDir(), "gateway.yaml");
    writeFileSync(file, BASE);
    expect(loadConfigFile(file, { hasStoredToken: () => false }).gatewayId).toBe("gw.team-alpha");
  });

  it("accepts a stored token file as the credential when no secret/token is configured", () => {
    const dir = stateDir();
    mkdirSync(dir, { recursive: true });
    writeFileSync(tokenFilePath(dir), "tok-abc");
    const yaml = BASE.replace("enrollmentSecret: enroll-dev\n", "").replace(
      "stateDir: /tmp/gw-state",
      `stateDir: ${dir}`,
    );
    const config = parseConfig(yaml);
    expect(config.enrollmentSecret).toBeUndefined();
    expect(config.linkToken).toBeUndefined();
  });
});

describe("parseConfig validation failures name the bad key", () => {
  it("rejects a wrong apiVersion", () => expectKey("apiVersion: wrong\nkind: Gateway", "apiVersion"));

  it("rejects a wrong kind", () => expectKey(BASE.replace("kind: Gateway", "kind: Worker"), "kind"));

  it("rejects a missing required key", () =>
    expectKey(BASE.replace("team: default\n", ""), "team"));

  it("rejects gatewayId outside the gw.<slug> namespace", () =>
    expectKey(BASE.replace("gw.team-alpha", "worker-1"), "gatewayId"));

  it("rejects a gatewayId with a bad slug shape", () => {
    expectKey(BASE.replace("gw.team-alpha", "gw.-alpha"), "gatewayId");
    expectKey(BASE.replace("gw.team-alpha", "gw.alpha-"), "gatewayId");
    expectKey(BASE.replace("gw.team-alpha", "gw.UPPER"), "gatewayId");
  });

  it("rejects a non-http facilitatorUrl", () =>
    expectKey(BASE.replace("http://localhost:8088", "ftp://x"), "facilitatorUrl"));

  it("rejects a trustMode off the server whitelist", () =>
    expectKey(BASE.replace("shared-runtime", "isolated-kernel"), "trustMode"));

  it("rejects a child mode outside echo|stub", () =>
    expectKey(BASE.replace("mode: echo", "mode: real"), "child.mode"));

  it("rejects an out-of-range diagnostics port", () =>
    expectKey(`${BASE}diagnostics:\n  port: 70000\n`, "diagnostics.port"));

  it("rejects unknown top-level keys", () =>
    expectKey(`${BASE}children: []\n`, "unknown key 'children'"));

  it("rejects unknown child/diagnostics keys", () => {
    expectKey(BASE.replace("mode: echo", "mode: echo\n  socket: x"), "unknown key 'socket'");
    expectKey(`${BASE}diagnostics:\n  host: 0.0.0.0\n`, "unknown key 'host'");
  });

  it("errors when neither token nor secret is available", () =>
    expectKey(
      BASE.replace("enrollmentSecret: enroll-dev\n", ""),
      "enrollmentSecret",
    ));

  it("malformed YAML is a config error", () =>
    expectKey("apiVersion: [unclosed", "yaml"));
});

describe("parseConfig env overlay", () => {
  it("OFAC_ENROLL_SECRET overrides the file", () => {
    const config = parseConfig(BASE, {
      env: { OFAC_ENROLL_SECRET: "env-secret" },
      hasStoredToken: () => false,
    });
    expect(config.enrollmentSecret).toBe("env-secret");
  });

  it("OFAC_LINK_TOKEN mounts a pre-minted token without a secret", () => {
    const yaml = BASE.replace("enrollmentSecret: enroll-dev\n", "");
    const config = parseConfig(yaml, {
      env: { OFAC_LINK_TOKEN: "tok-xyz" },
      hasStoredToken: () => false,
    });
    expect(config.linkToken).toBe("tok-xyz");
    expect(config.enrollmentSecret).toBeUndefined();
  });

  it("env secret rescues a file with no credential at all", () => {
    const yaml = BASE.replace("enrollmentSecret: enroll-dev\n", "");
    expect(() =>
      parseConfig(yaml, { env: { OFAC_ENROLL_SECRET: "s" }, hasStoredToken: () => false }),
    ).not.toThrow();
  });
});
