// The config law: every required key fails closed by name; defaults are the
// plan's literals; the TELEGRAM_CHATS allowlist parses signed ints, @names
// and dm:<int> with lowercase [a-z0-9-] labels; a malformed entry, a
// duplicate chat with differing labels, or webhook mode without url/secret
// is an AdapterConfigError (⇒ exit 2 at the bin).
import { mkdtempSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { describe, expect, it } from "vitest";
import { AdapterConfigError, parseChats, parseEnvConfig, resolveBotToken } from "../src/config.js";

const BASE: Record<string, string> = {
  OFAC_KERNEL_URL: "http://facilitator:8080",
  OFAC_ENROLL_SECRET: "enroll-dev",
  OFAC_ENDPOINT_ID: "tg-adapter",
  OFAC_TEAM: "team-alpha",
  TELEGRAM_BOT_TOKEN: "123:abc",
  TELEGRAM_CHATS: "-1001234567890:ofac:coding",
};

describe("parseEnvConfig defaults + required law", () => {
  it("parses the full required set with plan defaults", () => {
    const config = parseEnvConfig({ ...BASE });
    expect(config.kernelUrl).toBe("http://facilitator:8080");
    expect(config.endpointId).toBe("tg-adapter");
    expect(config.team).toBe("team-alpha");
    expect(config.providerInstanceId).toBe("local-telegram");
    expect(config.tenant).toBe("api.telegram.org");
    expect(config.mode).toBe("poll");
    expect(config.webhookListenPort).toBe(8077);
    expect(config.statePath).toBe("./ofac-tg-adapter.sqlite3");
    expect(config.mediaMaxBytes).toBe(20971520);
    expect(config.mediaEnabled).toBe(true);
    expect(config.apiBase).toBe("https://api.telegram.org");
    expect(config.healthPort).toBeUndefined();
    expect(config.logLevel).toBe("info");
    expect(config.chats).toEqual([{ chatId: "-1001234567890", labels: ["ofac", "coding"] }]);
  });

  it.each([
    "OFAC_KERNEL_URL",
    "OFAC_ENROLL_SECRET",
    "OFAC_ENDPOINT_ID",
    "OFAC_TEAM",
    "TELEGRAM_CHATS",
  ])("missing %s fails closed naming the key", (key) => {
    const env = { ...BASE };
    delete env[key];
    expect(() => parseEnvConfig(env)).toThrow(AdapterConfigError);
    try {
      parseEnvConfig(env);
    } catch (error) {
      expect((error as Error).message).toContain(key);
    }
  });

  it("PORT parses into the optional health port", () => {
    expect(parseEnvConfig({ ...BASE, PORT: "9000" }).healthPort).toBe(9000);
    expect(parseEnvConfig({ ...BASE, OFAC_HEALTH_PORT: "9001" }).healthPort).toBe(9001);
    expect(() => parseEnvConfig({ ...BASE, PORT: "nope" })).toThrow(/PORT/);
  });

  it("webhook mode without url/secret is a config error", () => {
    expect(() => parseEnvConfig({ ...BASE, TELEGRAM_MODE: "webhook" })).toThrow(
      /TELEGRAM_WEBHOOK_URL/,
    );
    expect(() =>
      parseEnvConfig({ ...BASE, TELEGRAM_MODE: "webhook", TELEGRAM_WEBHOOK_URL: "https://x/y" }),
    ).toThrow(/TELEGRAM_WEBHOOK_SECRET/);
    const ok = parseEnvConfig({
      ...BASE,
      TELEGRAM_MODE: "webhook",
      TELEGRAM_WEBHOOK_URL: "https://x/y",
      TELEGRAM_WEBHOOK_SECRET: "s3cret",
    });
    expect(ok.mode).toBe("webhook");
    expect(ok.webhookUrl).toBe("https://x/y");
    expect(ok.webhookSecret).toBe("s3cret");
  });

  it("an unknown TELEGRAM_MODE fails closed", () => {
    expect(() => parseEnvConfig({ ...BASE, TELEGRAM_MODE: "both" })).toThrow(/TELEGRAM_MODE/);
  });
});

describe("resolveBotToken", () => {
  it("inline token wins over the file", () => {
    const dir = mkdtempSync(join(tmpdir(), "tg-cfg-"));
    const file = join(dir, "token");
    writeFileSync(file, "file-token\n");
    expect(resolveBotToken({ TELEGRAM_BOT_TOKEN: "inline", TELEGRAM_BOT_TOKEN_FILE: file })).toBe(
      "inline",
    );
    expect(resolveBotToken({ TELEGRAM_BOT_TOKEN_FILE: file })).toBe("file-token");
  });

  it("missing both / empty file fail closed naming the key", () => {
    expect(() => resolveBotToken({})).toThrow(/TELEGRAM_BOT_TOKEN/);
    const dir = mkdtempSync(join(tmpdir(), "tg-cfg-"));
    const file = join(dir, "empty");
    writeFileSync(file, "  ");
    expect(() => resolveBotToken({ TELEGRAM_BOT_TOKEN_FILE: file })).toThrow(
      /TELEGRAM_BOT_TOKEN_FILE/,
    );
    expect(() => resolveBotToken({ TELEGRAM_BOT_TOKEN_FILE: join(dir, "gone") })).toThrow(
      /TELEGRAM_BOT_TOKEN_FILE/,
    );
  });
});

describe("TELEGRAM_CHATS allowlist law", () => {
  it("signed integers, @names and dm:<int> parse, labels lowercase", () => {
    expect(parseChats("-1001234567890:ofac:coding")).toEqual([
      { chatId: "-1001234567890", labels: ["ofac", "coding"] },
    ]);
    expect(parseChats("42")).toEqual([{ chatId: "42", labels: [] }]);
    expect(parseChats("@MyChannel:Team-Coding")).toEqual([
      { chatId: "@mychannel", labels: ["team-coding"] },
    ]);
    expect(parseChats("dm:555002")).toEqual([{ chatId: "dm:555002", labels: [] }]);
    // comma-separated labels continue the previous entry
    expect(parseChats("-100:ofac,coding")).toEqual([
      { chatId: "-100", labels: ["ofac", "coding"] },
    ]);
    // multiple entries
    expect(parseChats("-100:ofac,@channe1,dm:7:dm-label")).toEqual([
      { chatId: "-100", labels: ["ofac"] },
      { chatId: "@channe1", labels: [] },
      { chatId: "dm:7", labels: ["dm-label"] },
    ]);
  });

  it.each([
    "not-a-chat",
    "-100:Bad_Label",
    "-100:ok,also bad",
    "@ab", // @names are 5..32 chars
    "dm:abc",
    "",
  ])("malformed entry '%s' is an AdapterConfigError", (value) => {
    expect(() => parseChats(value)).toThrow(AdapterConfigError);
  });

  it("duplicate chat with differing labels is a config error; identical labels merge", () => {
    expect(() => parseChats("-100:ofac,-100:coding")).toThrow(/duplicate chat/);
    expect(parseChats("-100:ofac,-100:ofac")).toEqual([{ chatId: "-100", labels: ["ofac"] }]);
  });

  it("a leading bare label names no chat", () => {
    expect(() => parseChats("ofac,-100:coding")).toThrow(/names no chat/);
  });
});
