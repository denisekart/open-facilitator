// Config law: YAML validated by key name (unknown keys rejected), env overlay
// wins (endpoint/team/docker host/GC), the enroll secret travels ONLY by env
// var indirection, the allowlists parse as comma lists, and the approved-gate
// matcher honors exact + `prefix/*` forms with fail-closed on empty.
import { describe, expect, it } from "vitest";
import { LauncherConfigError, allowed, commaList, parseLauncherConfig } from "../src/config.js";

const BASE = `
kernelUrl: http://kernel:9000
enrollSecretEnv: OFAC_ENROLL_SECRET
endpointId: oci-launcher
team: team-alpha
statePath: /var/lib/ofac/oci-launcher.sqlite3
`;

const ENV = { OFAC_ENROLL_SECRET: "enroll-dev" };

describe("parseLauncherConfig", () => {
  it("parses the minimal shape + defaults", () => {
    const config = parseLauncherConfig(BASE, ENV);
    expect(config.kernelUrl).toBe("http://kernel:9000");
    expect(config.enrollSecret).toBe("enroll-dev");
    expect(config.endpointId).toBe("oci-launcher");
    expect(config.team).toBe("team-alpha");
    expect(config.dockerHost).toBe("unix:///var/run/docker.sock");
    expect(config.gcMinutes).toBe(60);
    expect(config.stopGraceSeconds).toBe(10);
    expect(config.allowedImages).toEqual([]);
    expect(config.allowedNetworks).toEqual([]);
    expect(config.logLevel).toBe("info");
  });

  it("env overlay beats the file", () => {
    const config = parseLauncherConfig(BASE, {
      ...ENV,
      OFAC_ENDPOINT_ID: "team-beta-launcher",
      OFAC_TEAM: "team-beta",
      DOCKER_HOST: "unix:///run/user/1000/docker.sock",
      OFAC_PROV_GC_MINUTES: "5",
      OFAC_STATE_PATH: "/tmp/x.sqlite3",
    });
    expect(config.endpointId).toBe("team-beta-launcher");
    expect(config.team).toBe("team-beta");
    expect(config.dockerHost).toBe("unix:///run/user/1000/docker.sock");
    expect(config.gcMinutes).toBe(5);
    expect(config.statePath).toBe("/tmp/x.sqlite3");
  });

  it("missing required keys name themselves", () => {
    expect(() => parseLauncherConfig("team: a\n", ENV)).toThrow(LauncherConfigError);
    expect(() => parseLauncherConfig("enrollSecretEnv: OFAC_ENROLL_SECRET\n", ENV)).toThrow(
      /kernelUrl/,
    );
    expect(() => parseLauncherConfig(BASE, {})).toThrow(/OFAC_ENROLL_SECRET/);
    expect(() => parseLauncherConfig(BASE, { OFAC_ENROLL_SECRET: "" })).toThrow(
      /OFAC_ENROLL_SECRET/,
    );
  });

  it("unknown keys are rejected by name", () => {
    expect(() => parseLauncherConfig(`${BASE}restartPolicy: always\n`, ENV)).toThrow(
      /restartPolicy: unknown key/,
    );
  });

  it("bad numbers and levels fail closed", () => {
    expect(() => parseLauncherConfig(BASE, { ...ENV, OFAC_PROV_GC_MINUTES: "-2" })).toThrow(
      /OFAC_PROV_GC_MINUTES/,
    );
    expect(() => parseLauncherConfig(BASE, { ...ENV, OFAC_ADAPTER_LOG: "loud" })).toThrow(
      /OFAC_ADAPTER_LOG/,
    );
    expect(() => parseLauncherConfig(`${BASE}gcMinutes: soon\n`, ENV)).toThrow(/gcMinutes/);
  });
});

describe("the approved gate", () => {
  it("commaList trims and drops empties", () => {
    expect(commaList(" a , ,b/* ")).toEqual(["a", "b/*"]);
    expect(commaList(undefined)).toEqual([]);
  });

  it("exact matches and prefix/* wildcards; empty denies everything", () => {
    expect(allowed(["alpine:3.20", "worker/*"], "alpine:3.20")).toBe(true);
    expect(allowed(["worker/*"], "worker/demo:1")).toBe(true);
    expect(allowed(["worker/*"], "workerx/demo:1")).toBe(false);
    expect(allowed(["team-*"], "team-alpha")).toBe(false); // `*` alone is NOT a wildcard
    expect(allowed(["team-*"], "team-*")).toBe(true); // …it's a literal
    expect(allowed([], "anything")).toBe(false);
  });
});
