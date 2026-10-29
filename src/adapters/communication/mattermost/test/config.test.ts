// Env-config law: every required key fails closed by name; defaults are the
// plan's literals; PORT parses into an optional health port.
import { describe, expect, it } from "vitest";
import { mkdtempSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { AdapterConfigError, deriveTenant, parseEnvConfig } from "../src/config.js";

const BASE: Record<string, string> = {
  OFAC_KERNEL_URL: "http://facilitator:8080",
  OFAC_ENROLL_SECRET: "enroll-dev",
  OFAC_ENDPOINT_ID: "mm-adapter",
  OFAC_TEAM: "team-alpha",
  MATTERMOST_URL: "http://chat.localhost:8065",
  MATTERMOST_BOT_TOKEN: "bot-token",
};

describe("parseEnvConfig", () => {
  it("parses the full required set with plan defaults", () => {
    const config = parseEnvConfig({ ...BASE });
    expect(config.kernelUrl).toBe("http://facilitator:8080");
    expect(config.endpointId).toBe("mm-adapter");
    expect(config.team).toBe("team-alpha");
    expect(config.providerInstanceId).toBe("local-chat");
    expect(config.botMention).toBe("@facilitator");
    expect(config.statePath).toBe("./ofac-mm-adapter.sqlite3");
    expect(config.healthPort).toBeUndefined();
    expect(config.logLevel).toBe("info");
  });

  it.each([
    "OFAC_KERNEL_URL",
    "OFAC_ENROLL_SECRET",
    "OFAC_ENDPOINT_ID",
    "OFAC_TEAM",
    "MATTERMOST_URL",
    "MATTERMOST_BOT_TOKEN",
  ])("names the missing key %s", (key) => {
    const env = { ...BASE };
    delete env[key as keyof typeof env];
    expect(() => parseEnvConfig(env)).toThrow(AdapterConfigError);
    expect(() => parseEnvConfig(env)).toThrow(key);
  });

  it("rejects a non-numeric PORT", () => {
    expect(() => parseEnvConfig({ ...BASE, PORT: "not-a-port" })).toThrow("PORT");
  });

  it("accepts PORT and OFAC_HEALTH_PORT, and overrides defaults", () => {
    expect(parseEnvConfig({ ...BASE, PORT: "8066" }).healthPort).toBe(8066);
    const config = parseEnvConfig({
      ...BASE,
      OFAC_HEALTH_PORT: "9000",
      OFAC_BOT_MENTION: "@bot",
      OFAC_PROVIDER_INSTANCE_ID: "prod-chat",
      OFAC_STATE_PATH: "/var/lib/ofac/mm.sqlite3",
      OFAC_ADAPTER_LOG: "debug",
    });
    expect(config.healthPort).toBe(9000);
    expect(config.botMention).toBe("@bot");
    expect(config.providerInstanceId).toBe("prod-chat");
    expect(config.statePath).toBe("/var/lib/ofac/mm.sqlite3");
    expect(config.logLevel).toBe("debug");
  });

  it("an unknown log level degrades to info, never a crash", () => {
    expect(parseEnvConfig({ ...BASE, OFAC_ADAPTER_LOG: "shout" }).logLevel).toBe("info");
  });
});

describe("resolveBotToken (one-shot provisioned token file)", () => {
  const dir = mkdtempSync(join(tmpdir(), "ofac-mm-tok-"));

  it("reads the token from the file when the inline env is unset", () => {
    const file = join(dir, "bot-token");
    writeFileSync(file, "  minted-token\n");
    const env: Record<string, string | undefined> = { ...BASE };
    delete env.MATTERMOST_BOT_TOKEN;
    env.MATTERMOST_BOT_TOKEN_FILE = file;
    expect(parseEnvConfig(env).botToken).toBe("minted-token");
  });

  it("prefers the inline token over the file", () => {
    const file = join(dir, "other-token");
    writeFileSync(file, "from-file");
    expect(parseEnvConfig({ ...BASE, MATTERMOST_BOT_TOKEN_FILE: file }).botToken).toBe("bot-token");
  });

  it("fails closed naming the file when unreadable or empty", () => {
    const empty = join(dir, "empty");
    writeFileSync(empty, "");
    const base: Record<string, string | undefined> = { ...BASE };
    delete base.MATTERMOST_BOT_TOKEN;
    expect(() =>
      parseEnvConfig({ ...base, MATTERMOST_BOT_TOKEN_FILE: join(dir, "missing") }),
    ).toThrow("MATTERMOST_BOT_TOKEN_FILE");
    expect(() => parseEnvConfig({ ...base, MATTERMOST_BOT_TOKEN_FILE: empty })).toThrow(
      "MATTERMOST_BOT_TOKEN_FILE",
    );
  });
});

describe("deriveTenant", () => {
  it("uses the site host as the provider tenant", () => {
    expect(deriveTenant("http://chat.localhost:8065")).toBe("chat.localhost");
    expect(deriveTenant("https://mm.example.com")).toBe("mm.example.com");
  });

  it("fails closed on an unparseable URL", () => {
    expect(() => deriveTenant("not a url")).toThrow("MATTERMOST_URL");
  });
});
