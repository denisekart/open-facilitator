// Config law, pinned: every missing required env var fails closed naming
// itself; the rules file is the fail-closed allowlist (unknown keys rejected
// by name, taskDefinition/cluster required, envAllowlist names validated);
// OFAC_INSTANCE_ID shape-gated; sane defaults for state path + log level.
import { describe, expect, it } from "vitest";
import {
  LauncherConfigError,
  parseLauncherConfig,
  parseRules,
  type LauncherConfig,
} from "../src/config.js";

const BASE_ENV: Record<string, string | undefined> = {
  OFAC_KERNEL_URL: "https://kernel.example.org",
  OFAC_ENROLL_TOKEN: "sekret",
  OFAC_ENDPOINT_ID: "launcher.team-alpha",
  OFAC_TEAM: "team-alpha",
  OFAC_RULES_FILE: "/opt/ofac/fleet-rules.json",
};

const EMPTY_RULES = () => "{}";

function parse(env: Record<string, string | undefined> = BASE_ENV): LauncherConfig {
  return parseLauncherConfig(env, EMPTY_RULES);
}

describe("parseLauncherConfig (env-only, fail closed)", () => {
  it("parses the full surface", () => {
    const config = parse();
    expect(config.kernelUrl).toBe("https://kernel.example.org");
    expect(config.enrollToken).toBe("sekret");
    expect(config.endpointId).toBe("launcher.team-alpha");
    expect(config.team).toBe("team-alpha");
    expect(config.statePath).toBe("/var/lib/ofac/fleet-launcher.sqlite3");
    expect(config.logLevel).toBe("info");
    expect(config.instanceId).toBeUndefined();
    expect(config.rules).toEqual({});
  });

  it("every missing required var fails by name", () => {
    for (const key of Object.keys(BASE_ENV)) {
      const env = { ...BASE_ENV };
      delete env[key];
      expect(() => parse(env)).toThrow(LauncherConfigError);
      try {
        parse(env);
      } catch (error) {
        expect((error as Error).message).toContain(key);
      }
    }
  });

  it("empty string counts as unset", () => {
    expect(() => parse({ ...BASE_ENV, OFAC_ENROLL_TOKEN: "" })).toThrow(/OFAC_ENROLL_TOKEN/);
  });

  it("OFAC_INSTANCE_ID is shape-gated, empty means unset", () => {
    expect(parse({ ...BASE_ENV, OFAC_INSTANCE_ID: "launcher-fixed-1" }).instanceId).toBe(
      "launcher-fixed-1",
    );
    expect(parse({ ...BASE_ENV, OFAC_INSTANCE_ID: "" }).instanceId).toBeUndefined();
    expect(() => parse({ ...BASE_ENV, OFAC_INSTANCE_ID: "bad id!" })).toThrow(/OFAC_INSTANCE_ID/);
  });

  it("OFAC_ADAPTER_LOG rejects unknown levels", () => {
    expect(() => parse({ ...BASE_ENV, OFAC_ADAPTER_LOG: "verbose" })).toThrow(
      /OFAC_ADAPTER_LOG.*verbose/,
    );
  });

  it("an unreadable rules file fails naming OFAC_RULES_FILE + the path", () => {
    expect(() =>
      parseLauncherConfig(BASE_ENV, () => {
        throw new Error("ENOENT");
      }),
    ).toThrow(/OFAC_RULES_FILE.*unreadable file \/opt\/ofac\/fleet-rules\.json/);
  });
});

describe("parseRules (the static allowlist)", () => {
  it("parses minimal + full rules", () => {
    const rules = parseRules(
      JSON.stringify({
        "gw.team-alpha": {
          taskDefinition: "arn:aws:ecs:eu-west-1:222:task-definition/gw:3",
          cluster: "arn:aws:ecs:eu-west-1:222:cluster/team",
          subnets: ["subnet-1", "subnet-2"],
          envAllowlist: ["OFAC_KERNEL_URL", "TEAM_TOKEN"],
        },
        "worker.x": {
          taskDefinition: "td",
          cluster: "cl",
        },
      }),
    );
    expect(rules["gw.team-alpha"]).toEqual({
      taskDefinition: "arn:aws:ecs:eu-west-1:222:task-definition/gw:3",
      cluster: "arn:aws:ecs:eu-west-1:222:cluster/team",
      subnets: ["subnet-1", "subnet-2"],
      envAllowlist: ["OFAC_KERNEL_URL", "TEAM_TOKEN"],
    });
    // subnets default to empty (the def's own placement), no allowlist ⇒ all spec env admitted
    expect(rules["worker.x"]).toEqual({
      taskDefinition: "td",
      cluster: "cl",
      subnets: [],
    });
  });

  it("rejects non-mappings and non-object rules", () => {
    expect(() => parseRules("[]")).toThrow(/workloadId-keyed mapping/);
    expect(() => parseRules("not json")).toThrow(/not valid JSON/);
    expect(() => parseRules('{"a": 4}')).toThrow(/rules\.a: must be a rule object/);
  });

  it("rejects unknown keys by name", () => {
    expect(() => parseRules('{"a":{"taskDefinition":"t","cluster":"c","fargate":true}}')).toThrow(
      /rules\.a\.fargate: unknown key/,
    );
  });

  it("requires taskDefinition + cluster", () => {
    expect(() => parseRules('{"a":{"cluster":"c"}}')).toThrow(/rules\.a\.taskDefinition/);
    expect(() => parseRules('{"a":{"taskDefinition":"t"}}')).toThrow(/rules\.a\.cluster/);
  });

  it("envAllowlist names must look like env names", () => {
    expect(() =>
      parseRules('{"a":{"taskDefinition":"t","cluster":"c","envAllowlist":["lower"]}}'),
    ).toThrow(/rules\.a\.envAllowlist/);
  });

  it("subnets must be a list of non-empty strings", () => {
    expect(() => parseRules('{"a":{"taskDefinition":"t","cluster":"c","subnets":[""]}}')).toThrow(
      /rules\.a\.subnets/,
    );
  });
});
