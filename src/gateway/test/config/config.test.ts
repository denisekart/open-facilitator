import { mkdirSync, mkdtempSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { describe, expect, it } from "vitest";

import {
  GatewayConfigError,
  loadConfigFile,
  parseConfig,
  tokenFilePath,
} from "../../src/config.js";

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
  it("rejects a wrong apiVersion", () =>
    expectKey("apiVersion: wrong\nkind: Gateway", "apiVersion"));

  it("rejects a wrong kind", () =>
    expectKey(BASE.replace("kind: Gateway", "kind: Worker"), "kind"));

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

  it("rejects unknown top-level keys", () => expectKey(`${BASE}bogus: 1\n`, "unknown key 'bogus'"));

  it("rejects unknown child/diagnostics keys", () => {
    expectKey(BASE.replace("mode: echo", "mode: echo\n  socket: x"), "unknown key 'socket'");
    expectKey(`${BASE}diagnostics:\n  host: 0.0.0.0\n`, "unknown key 'host'");
  });

  it("errors when neither token nor secret is available", () =>
    expectKey(BASE.replace("enrollmentSecret: enroll-dev\n", ""), "enrollmentSecret"));

  it("malformed YAML is a config error", () => expectKey("apiVersion: [unclosed", "yaml"));
});

const WORKLOAD = `
workload:
  tokenUrl: http://localhost:8081/realms/facilitator/protocol/openid-connect/token
  clientId: gw-team-a
  clientSecret: cc-secret
`;

describe("parseConfig plan-10 identity keys", () => {
  it("parses role and the workload block", () => {
    const config = parseConfig(`${BASE}${WORKLOAD}`, { hasStoredToken: () => false });
    expect(config.workload).toEqual({
      tokenUrl: "http://localhost:8081/realms/facilitator/protocol/openid-connect/token",
      clientId: "gw-team-a",
      clientSecret: "cc-secret",
    });
    expect(config.role).toBeUndefined();
  });

  it("accepts role: gateway and rejects a role off the enum", () => {
    expect(parseConfig(`${BASE}role: gateway\n`, { hasStoredToken: () => false }).role).toBe(
      "gateway",
    );
    expectKey(`${BASE}role: operator\n`, "role");
  });

  it("workload alone satisfies the first-boot credential check", () => {
    const yaml = BASE.replace("enrollmentSecret: enroll-dev\n", "") + WORKLOAD;
    const config = parseConfig(yaml);
    expect(config.workload?.clientId).toBe("gw-team-a");
    expect(config.enrollmentSecret).toBeUndefined();
  });

  it("OFAC_OIDC_* env overlay wins per-key and completes a file block", () => {
    const config = parseConfig(`${BASE}${WORKLOAD}`, {
      env: { OFAC_OIDC_CLIENT_SECRET: "from-env" },
      hasStoredToken: () => false,
    });
    expect(config.workload?.clientSecret).toBe("from-env");
    expect(config.workload?.clientId).toBe("gw-team-a");
  });

  it("env-only workload needs the full triple", () => {
    const yaml = BASE.replace("enrollmentSecret: enroll-dev\n", "");
    expectKey(yaml, "workload", {
      OFAC_OIDC_TOKEN_URL: "http://idp/token",
      OFAC_OIDC_CLIENT_ID: "gw-team-a",
    });
    const config = parseConfig(yaml, {
      env: {
        OFAC_OIDC_TOKEN_URL: "http://idp/token",
        OFAC_OIDC_CLIENT_ID: "gw-team-a",
        OFAC_OIDC_CLIENT_SECRET: "s",
      },
    });
    expect(config.workload?.tokenUrl).toBe("http://idp/token");
  });

  it("rejects unknown workload keys", () => {
    expectKey(`${BASE}${WORKLOAD}  scope: offline_access\n`, "unknown key 'scope'");
  });
});

const SHARED = `
apiVersion: facilitator.framework/v1
kind: Gateway
gatewayId: gw.team-alpha
team: default
facilitatorUrl: http://localhost:8088
enrollmentSecret: enroll-dev
trustMode: shared-runtime
stateDir: /tmp/gw-state
mode: shared
children:
  - name: echo
  - name: stub
    token: child-secret-1
    maxInFlight: 2
    timeoutSeconds: 60
childTransport:
  socket:
    path: ./child.sock
`;

const STANDALONE = `
apiVersion: facilitator.framework/v1
kind: Gateway
gatewayId: gw.team-alpha
team: default
facilitatorUrl: http://localhost:8088
enrollmentSecret: enroll-dev
trustMode: shared-runtime
stateDir: /tmp/gw-state
mode: standalone
childTransport:
  socket:
    path: ./child.sock
  tcp:
    port: 8090
`;

function keyMessage(yaml: string, env: Record<string, string | undefined> = {}): string {
  try {
    parseConfig(yaml, { env, hasStoredToken: () => false });
  } catch (error) {
    expect(error).toBeInstanceOf(GatewayConfigError);
    return (error as Error).message;
  }
  throw new Error("expected a GatewayConfigError");
}

describe("parseConfig mode matrix (config v2)", () => {
  it("loads a v1 file (no mode, child: block) as embedded — 05 back-compat", () => {
    const config = parseConfig(BASE, { hasStoredToken: () => false });
    expect(config.mode).toBe("embedded");
    expect(config.children).toBeUndefined();
    expect(config.childTransport).toBeUndefined();
    expect(config.childSecret).toBeUndefined();
  });

  it("parses a shared fleet with defaults and explicit overrides", () => {
    const config = parseConfig(SHARED, { hasStoredToken: () => false });
    expect(config.mode).toBe("shared");
    expect(config.child).toBeUndefined();
    expect(config.children).toEqual([
      { name: "echo", maxInFlight: 1, timeoutSeconds: 120 },
      { name: "stub", token: "child-secret-1", maxInFlight: 2, timeoutSeconds: 60 },
    ]);
    expect(config.childTransport).toEqual({
      socket: { path: "/tmp/gw-state/child.sock" },
      handshakeTimeoutSeconds: 5,
      leaseMissedBeats: 2,
    });
  });

  it("boots standalone with zero children (spec S:330)", () => {
    const config = parseConfig(STANDALONE, { hasStoredToken: () => false });
    expect(config.mode).toBe("standalone");
    expect(config.children).toBeUndefined();
    expect(config.childTransport?.tcp).toEqual({ port: 8090 });
  });

  it("accepts an explicitly empty children list in standalone", () => {
    const config = parseConfig(`${STANDALONE}children: []\n`, { hasStoredToken: () => false });
    expect(config.children).toEqual([]);
  });

  it("rejects a mode off the three-value whitelist", () =>
    expectKey(SHARED.replace("mode: shared", "mode: hybrid"), "mode"));

  it("rejects both child: and children: blocks", () => {
    expectKey(`${SHARED}child:\n  name: echo\n  mode: echo\n`, "child");
    expectKey(`${BASE}children:\n  - name: x\n`, "child");
  });

  it("rejects embedded with neither block", () =>
    expectKey(BASE.replace("child:\n  name: echo\n  mode: echo\n", ""), "child"));

  it("rejects shared without children (names the key)", () =>
    expectKey(
      SHARED.replace(
        "children:\n  - name: echo\n  - name: stub\n    token: child-secret-1\n    maxInFlight: 2\n    timeoutSeconds: 60\n",
        "",
      ),
      "children",
    ));

  it("rejects shared with an empty children list", () =>
    expectKey(
      SHARED.replace(
        "children:\n  - name: echo\n  - name: stub\n    token: child-secret-1\n    maxInFlight: 2\n    timeoutSeconds: 60\n",
        "children: []\n",
      ),
      "children",
    ));

  it("rejects shared without childTransport (names the key)", () =>
    expectKey(
      SHARED.replace("childTransport:\n  socket:\n    path: ./child.sock\n", ""),
      "childTransport",
    ));

  it("rejects standalone without childTransport (names the key)", () =>
    expectKey(
      STANDALONE.replace(
        "childTransport:\n  socket:\n    path: ./child.sock\n  tcp:\n    port: 8090\n",
        "",
      ),
      "childTransport",
    ));

  it("rejects childTransport in embedded", () =>
    expectKey(`${BASE}childTransport:\n  socket:\n    path: ./child.sock\n`, "childTransport"));

  it("rejects children in embedded", () =>
    expectKey(
      BASE.replace("child:\n  name: echo\n  mode: echo\n", "") + "children: []\n",
      "children",
    ));

  it("rejects child: in shared/standalone", () => {
    expectKey(`${STANDALONE}child:\n  name: echo\n  mode: echo\n`, "child");
    expectKey(`${SHARED}child:\n  name: echo\n  mode: echo\n`, "child");
  });

  it("rejects embedded claiming isolated-executor", () => {
    const message = keyMessage(
      BASE.replace("trustMode: shared-runtime", "trustMode: isolated-executor"),
    );
    expect(message).toBe("trustMode: embedded gateway cannot claim isolated-executor");
  });

  it("rejects an explicit embedded claiming isolated-executor", () =>
    expectKey(
      `${BASE}mode: embedded\n`.replace(
        "trustMode: shared-runtime",
        "trustMode: isolated-executor",
      ),
      "trustMode: embedded gateway cannot claim isolated-executor",
    ));

  it("allows shared and standalone with isolated-executor", () => {
    const shared = SHARED.replace("trustMode: shared-runtime", "trustMode: isolated-executor");
    expect(() => parseConfig(shared, { hasStoredToken: () => false })).not.toThrow();
    const standalone = STANDALONE.replace(
      "trustMode: shared-runtime",
      "trustMode: isolated-executor",
    );
    expect(() => parseConfig(standalone, { hasStoredToken: () => false })).not.toThrow();
  });
});

describe("parseConfig children fleet entries", () => {
  it("rejects a name outside ^[a-z0-9]([a-z0-9-]*[a-z0-9])?$", () => {
    expectKey(SHARED.replace("- name: echo", "- name: Echo"), "children[0].name");
    expectKey(SHARED.replace("- name: echo", "- name: -echo"), "children[0].name");
    expectKey(SHARED.replace("- name: echo", "- name: echo-"), "children[0].name");
  });

  it("rejects a name over 128 characters", () =>
    expectKey(SHARED.replace("- name: echo", `- name: ${"a".repeat(129)}`), "children[0].name"));

  it("rejects duplicate child names by index", () =>
    expectKey(SHARED.replace("- name: stub", "- name: echo"), "children[1].name"));

  it("rejects a missing name", () =>
    expectKey(SHARED.replace("- name: echo", "- maxInFlight: 1"), "children[0].name"));

  it("rejects an empty token", () =>
    expectKey(SHARED.replace("token: child-secret-1", "token: ''"), "children[1].token"));

  it("rejects maxInFlight below 1 and non-integer timeoutSeconds", () => {
    expectKey(SHARED.replace("maxInFlight: 2", "maxInFlight: 0"), "children[1].maxInFlight");
    expectKey(
      SHARED.replace("timeoutSeconds: 60", "timeoutSeconds: 86401"),
      "children[1].timeoutSeconds",
    );
  });

  it("rejects builtin-only keys in a fleet entry", () => {
    expectKey(SHARED.replace("- name: echo", "- name: echo\n    mode: echo"), "children[0]");
    expectKey(
      SHARED.replace("- name: echo", "- name: echo\n    manifestFile: ./c.yaml"),
      "children[0]",
    );
  });

  it("rejects unknown keys in a fleet entry", () =>
    expectKey(SHARED.replace("- name: echo", "- name: echo\n    socket: x"), "children[0]"));

  it("rejects a children block that is not a sequence", () =>
    expectKey(
      SHARED.replace(
        "children:\n  - name: echo\n  - name: stub\n    token: child-secret-1\n    maxInFlight: 2\n    timeoutSeconds: 60\n",
        "children:\n  name: echo\n",
      ),
      "children",
    ));
});

describe("parseConfig childTransport block", () => {
  it("resolves a relative socket path against stateDir", () => {
    const config = parseConfig(SHARED, { hasStoredToken: () => false });
    expect(config.childTransport?.socket.path).toBe("/tmp/gw-state/child.sock");
  });

  it("keeps an absolute socket path as-is", () => {
    const config = parseConfig(
      SHARED.replace("path: ./child.sock", "path: /var/run/ofac/child.sock"),
      { hasStoredToken: () => false },
    );
    expect(config.childTransport?.socket.path).toBe("/var/run/ofac/child.sock");
  });

  it("requires socket.path", () => {
    expectKey(
      SHARED.replace(
        "childTransport:\n  socket:\n    path: ./child.sock\n",
        "childTransport: {}\n",
      ),
      "childTransport.socket",
    );
    expectKey(SHARED.replace("path: ./child.sock", "path: ''"), "childTransport.socket.path");
  });

  it("accepts an allowedUid pin and rejects negatives", () => {
    const config = parseConfig(
      SHARED.replace("path: ./child.sock", "path: ./child.sock\n    allowedUid: 1000"),
      {
        hasStoredToken: () => false,
      },
    );
    expect(config.childTransport?.socket.allowedUid).toBe(1000);
    expectKey(
      SHARED.replace("path: ./child.sock", "path: ./child.sock\n    allowedUid: -1"),
      "childTransport.socket.allowedUid",
    );
  });

  it("bounds tcp.port to 1..65535", () => {
    expectKey(STANDALONE.replace("port: 8090", "port: 0"), "childTransport.tcp.port");
    expectKey(STANDALONE.replace("port: 8090", "port: 70000"), "childTransport.tcp.port");
  });

  it("bounds handshakeTimeoutSeconds and leaseMissedBeats", () => {
    expectKey(
      SHARED.replace("path: ./child.sock", "path: ./child.sock\n  handshakeTimeoutSeconds: 0"),
      "childTransport.handshakeTimeoutSeconds",
    );
    expectKey(
      SHARED.replace("path: ./child.sock", "path: ./child.sock\n  leaseMissedBeats: 101"),
      "childTransport.leaseMissedBeats",
    );
  });

  it("rejects unknown keys in childTransport/socket/tcp", () => {
    expectKey(
      SHARED.replace("path: ./child.sock", "path: ./child.sock\n  host: 0.0.0.0"),
      "childTransport",
    );
    expectKey(
      SHARED.replace("path: ./child.sock", "path: ./child.sock\n    mode: tcp"),
      "childTransport.socket",
    );
    expectKey(
      STANDALONE.replace("port: 8090", "port: 8090\n    host: 0.0.0.0"),
      "childTransport.tcp",
    );
  });
});

describe("parseConfig child secret overlay", () => {
  it("OFAC_CHILD_SECRET mounts the shared child secret", () => {
    const config = parseConfig(SHARED, {
      env: { OFAC_CHILD_SECRET: "env-child" },
      hasStoredToken: () => false,
    });
    expect(config.childSecret).toBe("env-child");
  });

  it("childSecret stays undefined without the env", () => {
    const config = parseConfig(SHARED, { env: {}, hasStoredToken: () => false });
    expect(config.childSecret).toBeUndefined();
  });
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

  it("OFAC_KERNEL_URL repoints facilitatorUrl (the deployment origin wins)", () => {
    const config = parseConfig(BASE, {
      env: { OFAC_KERNEL_URL: "http://facilitator-lb" },
      hasStoredToken: () => false,
    });
    expect(config.facilitatorUrl).toBe("http://facilitator-lb");
  });

  it("a file facilitatorUrl stands when the env is unset", () => {
    const config = parseConfig(BASE, { env: {}, hasStoredToken: () => false });
    expect(config.facilitatorUrl).toBe("http://localhost:8088");
  });
});

describe("parseConfig policy block", () => {
  const withPolicy = (block: string): string => `${BASE}${block}`;

  it("parses policy.inline with the confirm-timeout default", () => {
    const config = parseConfig(withPolicy('policy:\n  inline: "owner: t\\npolicies: []"\n'), {
      hasStoredToken: () => false,
    });
    expect(config.policy?.inline).toContain("owner: t");
    expect(config.policy?.path).toBeUndefined();
    expect(config.policy?.confirmTimeoutSeconds).toBe(900);
  });

  it("resolves policy.path against the config file's dir", () => {
    const dir = stateDir();
    const file = join(dir, "gateway.yaml");
    writeFileSync(file, withPolicy("policy:\n  path: policy.yaml\n  confirmTimeoutSeconds: 600\n"));
    const config = loadConfigFile(file, { hasStoredToken: () => false });
    expect(config.policy?.path).toBe(join(dir, "policy.yaml"));
    expect(config.policy?.confirmTimeoutSeconds).toBe(600);
  });

  it("an absolute policy.path survives untouched", () => {
    const config = parseConfig(withPolicy("policy:\n  path: /etc/ofac/policy.yaml\n"), {
      hasStoredToken: () => false,
      configDir: "/srv",
    });
    expect(config.policy?.path).toBe("/etc/ofac/policy.yaml");
  });

  it("inline+path together or an empty block are config errors", () => {
    expectKey(withPolicy("policy:\n  inline: x\n  path: p.yaml\n"), "policy");
    expectKey(withPolicy("policy: {}\n"), "policy");
    expectKey(withPolicy("policy:\n  typo: 1\n"), "policy");
    expectKey(
      withPolicy("policy:\n  inline: x\n  confirmTimeoutSeconds: 7\n"),
      "policy.confirmTimeoutSeconds",
    );
  });

  it("absent policy key parses to undefined", () => {
    expect(parseConfig(BASE, { hasStoredToken: () => false }).policy).toBeUndefined();
  });
});
