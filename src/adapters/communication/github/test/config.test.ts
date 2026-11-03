// The config fail-closed law: every required key names itself in the error;
// token-file precedence; defaults for everything OPTIONAL; no silent default
// for anything load-bearing.
import { describe, expect, it } from "vitest";
import { mkdtempSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";
import {
  AdapterConfigError,
  deriveTenant,
  parseEnvConfig,
  resolveBotToken,
} from "../src/config.js";

const dir = mkdtempSync(join(tmpdir(), "gh-config-"));
writeFileSync(join(dir, "tok"), "ghp_file_token\n");

function base(overrides: Record<string, string> = {}): Record<string, string> {
  return {
    OFAC_KERNEL_URL: "http://kernel:8080",
    OFAC_ENROLL_SECRET: "enroll-dev",
    OFAC_ENDPOINT_ID: "github-adapter",
    OFAC_TEAM: "team-alpha",
    GITHUB_TOKEN_FILE: join(dir, "tok"),
    GITHUB_WEBHOOK_SECRET: "wh-secret",
    GITHUB_BOT_USER: "ofac-bot",
    ...overrides,
  };
}

describe("parseEnvConfig", () => {
  it("parses the full env with defaults applied", () => {
    const config = parseEnvConfig(base());
    expect(config.kernelUrl).toBe("http://kernel:8080");
    expect(config.endpointId).toBe("github-adapter");
    expect(config.githubUrl).toBe("https://api.github.com");
    expect(config.botToken).toBe("ghp_file_token");
    expect(config.webhookSecret).toBe("wh-secret");
    expect(config.botUser).toBe("ofac-bot");
    expect(config.workLabels).toEqual(["ofac:work"]);
    expect(config.providerInstanceId).toBe("github-com");
    expect(config.team).toBe("team-alpha");
    expect(config.port).toBe(8068);
    expect(config.logLevel).toBe("info");
  });

  it("accepts an inline token and a custom url/labels/instance/port", () => {
    const config = parseEnvConfig({
      ...base({
        GITHUB_URL: "http://127.0.0.1:9/relative-only",
        GITHUB_TOKEN: "ghp_inline",
        GITHUB_WORK_LABELS: "ofac:work, blocked ",
        OFAC_PROVIDER_INSTANCE_ID: "ghes",
        OFAC_HEALTH_PORT: "9999",
      }),
    });
    expect(config.botToken).toBe("ghp_inline");
    expect(config.githubUrl).toBe("http://127.0.0.1:9/relative-only");
    expect(config.workLabels).toEqual(["ofac:work", "blocked"]);
    expect(config.providerInstanceId).toBe("ghes");
    expect(config.port).toBe(9999);
  });

  it("prefers the inline token over the file", () => {
    const config = parseEnvConfig({ ...base(), GITHUB_TOKEN: "inline" });
    expect(config.botToken).toBe("inline");
  });

  for (const key of [
    "OFAC_KERNEL_URL",
    "OFAC_ENROLL_SECRET",
    "OFAC_TEAM",
    "GITHUB_WEBHOOK_SECRET",
    "GITHUB_BOT_USER",
  ]) {
    it(`fails closed naming ${key}`, () => {
      const env = base();
      delete env[key];
      expect(() => parseEnvConfig(env)).toThrow(new RegExp(`^${key}:`));
    });
  }

  it("an absent token pair yields an inbound-only run", () => {
    const env = base();
    delete env["GITHUB_TOKEN_FILE"];
    expect(parseEnvConfig(env).botToken).toBeUndefined();
  });

  it("rejects a non-numeric port", () => {
    expect(() => parseEnvConfig(base({ OFAC_HEALTH_PORT: "http" }))).toThrow(/^OFAC_HEALTH_PORT:/);
  });
});

describe("resolveBotToken", () => {
  it("returns undefined for an absent token pair", () => {
    expect(resolveBotToken({})).toBeUndefined();
  });
});

describe("deriveTenant", () => {
  it("takes the host as the provider tenant", () => {
    expect(deriveTenant("https://api.github.com")).toBe("api.github.com");
    expect(deriveTenant("http://127.0.0.1:9000/")).toBe("127.0.0.1");
  });

  it("fails closed on garbage", () => {
    expect(() => deriveTenant("not a url")).toThrowError(AdapterConfigError);
  });
});
