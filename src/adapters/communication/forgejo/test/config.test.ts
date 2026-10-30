// Config law: missing required keys name themselves; the token resolves
// direct-or-file with fail-closed on every unreadable shape; the tenant is the
// forge host.
import { mkdirSync, mkdtempSync, rmSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { describe, expect, it } from "vitest";
import {
  AdapterConfigError,
  deriveTenant,
  parseEnvConfig,
  resolveBotToken,
} from "../src/config.js";

const base = {
  OFAC_KERNEL_URL: "http://facilitator:8080",
  OFAC_ENROLL_SECRET: "enroll-dev",
  OFAC_ENDPOINT_ID: "forgejo-adapter",
  OFAC_TEAM: "team-alpha",
  FORGEJO_URL: "http://forgejo:3000",
  FORGEJO_TOKEN: "tok",
  FORGEJO_WEBHOOK_SECRET: "hook",
  FORGEJO_BOT_USER: "ofac-bot",
};

describe("parseEnvConfig", () => {
  it("parses the full environment with defaults", () => {
    const config = parseEnvConfig({ ...base });
    expect(config.endpointId).toBe("forgejo-adapter");
    expect(config.workLabels).toEqual(["ofac:work"]);
    expect(config.port).toBe(8067);
    expect(config.hookPath).toBe("/hooks/forgejo");
    expect(config.providerInstanceId).toBe("local-git");
  });

  it("names the missing key on every required omission", () => {
    for (const key of Object.keys(base)) {
      if (key === "FORGEJO_TOKEN") continue; // optional (decision 6)
      const env = { ...base };
      delete env[key as keyof typeof base];
      expect(() => parseEnvConfig(env)).toThrow(AdapterConfigError);
      try {
        parseEnvConfig(env);
      } catch (error) {
        expect((error as Error).message).toContain(key);
      }
    }
  });

  it("parses without any token (inbound-capable cold boot)", () => {
    const env: Record<string, string | undefined> = { ...base, FORGEJO_TOKEN: undefined };
    expect(parseEnvConfig(env).botToken).toBeUndefined();
  });

  it("splits and trims the work-label list", () => {
    const config = parseEnvConfig({ ...base, FORGEJO_WORK_LABELS: " ofac:work , team:task," });
    expect(config.workLabels).toEqual(["ofac:work", "team:task"]);
  });

  it("rejects a junk log level and port", () => {
    expect(() => parseEnvConfig({ ...base, OFAC_ADAPTER_LOG: "chatty" })).toThrow(
      /OFAC_ADAPTER_LOG/,
    );
    expect(() => parseEnvConfig({ ...base, PORT: "99999" })).toThrow(/PORT/);
  });
});

describe("resolveBotToken", () => {
  it("prefers the literal, else reads the file, else absent = no token", () => {
    expect(resolveBotToken({ FORGEJO_TOKEN: " tok " })).toBe("tok");
    const dir = mkdtempSync(join(tmpdir(), "ofac-fj-cfg-"));
    try {
      const file = join(dir, "token");
      writeFileSync(file, "file-tok\n");
      expect(resolveBotToken({ FORGEJO_TOKEN_FILE: file })).toBe("file-tok");
      writeFileSync(file, "  ");
      expect(() => resolveBotToken({ FORGEJO_TOKEN_FILE: file })).toThrow(/empty/);
      // NOT-YET-PROVISIONED (ENOENT) is the cold-boot state, not an error;
      // a file present but unreadable (here: a directory) fails closed.
      expect(resolveBotToken({ FORGEJO_TOKEN_FILE: join(dir, "missing") })).toBeUndefined();
      mkdirSync(join(dir, "blocked"));
      expect(() => resolveBotToken({ FORGEJO_TOKEN_FILE: join(dir, "blocked") })).toThrow(
        /unreadable/,
      );
    } finally {
      rmSync(dir, { recursive: true, force: true });
    }
  });
});

describe("deriveTenant", () => {
  it("is the forge hostname, port-free (kernel section parity)", () => {
    expect(deriveTenant("http://git.localhost:3000/")).toBe("git.localhost");
    expect(() => deriveTenant("not a url")).toThrow(/FORGEJO_URL/);
  });
});
