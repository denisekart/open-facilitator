// The exported config primitives in isolation: every rejected shape names its
// key (worker-law), the transport union is exclusive, the token law reads the
// `_FILE` twin's first line, and the env overlay wins over the file. Each
// helper is driven directly — a provider's own parse only ever composes these.
import { mkdtempSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { isAbsolute, join, resolve } from "node:path";
import { describe, expect, it } from "vitest";
import {
  ConnectorConfigError,
  DEFAULT_BACKOFF,
  asRecord,
  fail,
  flagOn,
  optionalInt,
  optionalString,
  parseBackoff,
  parseSkeleton,
  parseTransport,
  readToken,
  readYamlRoot,
  rejectUnknown,
  requiredEnv,
  resolvePath,
  type ConnectorSkeleton,
  type ParseOptions,
} from "../src/index.js";

function dir(): string {
  return mkdtempSync(join(tmpdir(), "provider-core-cfg-"));
}

function skeletonOf(yaml: string, env: Record<string, string | undefined> = {}): ConnectorSkeleton {
  const file = join(dir(), "connector.yaml");
  writeFileSync(file, yaml);
  const { root, baseDir } = readYamlRoot(file);
  return parseSkeleton(root, { baseDir, env }, "connector.sqlite3");
}

const BASE_YAML = `
childWorkspaceId: gw-1/forge
team: team-alpha
transport:
  tcp:
    port: 9999
`;

describe("fail / asRecord / optionalString / rejectUnknown", () => {
  it("fail carries the key in the message", () => {
    expect(() => fail("some.key", "nope")).toThrow(ConnectorConfigError);
    expect(() => fail("some.key", "nope")).toThrow(/^some\.key: nope$/);
  });

  it("asRecord accepts only a non-array object", () => {
    expect(asRecord({ a: 1 }, "root")).toEqual({ a: 1 });
    for (const bad of [null, [], "text", 7, undefined]) {
      expect(() => asRecord(bad, "root")).toThrow(/root: must be a mapping/);
    }
  });

  it("optionalString: absent ⇒ undefined, empty or wrong type ⇒ named failure", () => {
    expect(optionalString({ a: undefined }, "a")).toBeUndefined();
    expect(optionalString({ a: null }, "a")).toBeUndefined();
    expect(optionalString({ a: "v" }, "a")).toBe("v");
    expect(() => optionalString({ a: "" }, "a")).toThrow(/a: must be a non-empty string/);
    expect(() => optionalString({ a: 3 }, "a")).toThrow(/a: must be a non-empty string/);
  });

  it("rejectUnknown names the offending key path", () => {
    expect(() => rejectUnknown({ ok: 1, bad: 2 }, ["ok"], "file")).toThrow(
      /^file\.bad: unknown key$/,
    );
    expect(() => rejectUnknown({ ok: 1 }, ["ok"], "file")).not.toThrow();
  });
});

describe("resolvePath", () => {
  it("absolute passes through, relative resolves against the config dir", () => {
    expect(resolvePath("/etc/conf", "/var/lib/s.db", "statePath")).toBe("/var/lib/s.db");
    const rel = resolvePath("/etc/conf", "s.db", "statePath");
    expect(rel).toBe(resolve("/etc/conf", "s.db"));
    expect(isAbsolute(rel)).toBe(true);
  });

  it("empty or non-string fails with the key", () => {
    expect(() => resolvePath("/etc", "", "statePath")).toThrow(
      /statePath: must be a non-empty string/,
    );
    expect(() => resolvePath("/etc", 5 as unknown as string, "statePath")).toThrow(
      /statePath: must be a non-empty string/,
    );
  });
});

describe("parseTransport", () => {
  it("socket leg: resolves the path, defaults nothing", () => {
    expect(parseTransport({ socket: { path: "/tmp/x.sock" } }, "/base")).toEqual({
      socketPath: "/tmp/x.sock",
    });
    expect(parseTransport({ socket: { path: "x.sock" } }, "/base")).toEqual({
      socketPath: resolve("/base", "x.sock"),
    });
  });

  it("socket leg: every rejected shape names its key", () => {
    expect(() => parseTransport({ socket: 7 }, "/base")).toThrow(
      /transport\.socket: must be a mapping/,
    );
    expect(() => parseTransport({ socket: { nope: 1 } }, "/base")).toThrow(
      /transport\.socket\.nope: unknown key/,
    );
    expect(() => parseTransport({ socket: {} }, "/base")).toThrow(
      /transport\.socket\.path: required/,
    );
    expect(() => parseTransport({ socket: { path: "" } }, "/base")).toThrow(
      /^path: must be a non-empty string$/,
    );
  });

  it("tcp leg: default host, integer port range enforced", () => {
    expect(parseTransport({ tcp: { port: 1 } }, "/base")).toEqual({
      host: "127.0.0.1",
      port: 1,
    });
    expect(parseTransport({ tcp: { host: "0.0.0.0", port: 65_535 } }, "/base")).toEqual({
      host: "0.0.0.0",
      port: 65_535,
    });
    for (const port of [0, -1, 65_536, 80.5, "8080", undefined, null]) {
      expect(() => parseTransport({ tcp: { port } }, "/base")).toThrow(
        /transport\.tcp\.port: must be an integer 1\.\.65535/,
      );
    }
    expect(() => parseTransport({ tcp: "x" }, "/base")).toThrow(
      /transport\.tcp: must be a mapping/,
    );
    expect(() => parseTransport({ tcp: { port: 1, extra: true } }, "/base")).toThrow(
      /transport\.tcp\.extra: unknown key/,
    );
  });

  it("unknown transport key and the empty union fail closed", () => {
    expect(() => parseTransport({ vsock: {} }, "/base")).toThrow(/transport\.vsock: unknown key/);
    expect(() => parseTransport({}, "/base")).toThrow(
      /transport: exactly one of socket\/tcp is required/,
    );
  });
});

describe("parseBackoff", () => {
  it("absent ⇒ SDK default, empty ⇒ disabled", () => {
    expect(parseBackoff(undefined)).toBe(DEFAULT_BACKOFF);
    expect(parseBackoff(null)).toBe(DEFAULT_BACKOFF);
    expect(parseBackoff([])).toEqual([]);
  });

  it("entries are validated positionally", () => {
    expect(parseBackoff([0, 1, 600_000])).toEqual([0, 1, 600_000]);
    expect(() => parseBackoff("fast")).toThrow(/restartBackoffMs: must be a list of integers/);
    expect(() => parseBackoff([1000, "2s"])).toThrow(
      /restartBackoffMs\[1\]: must be an integer 0\.\.600000/,
    );
    expect(() => parseBackoff([-1])).toThrow(/restartBackoffMs\[0\]: must be an integer/);
    expect(() => parseBackoff([600_001])).toThrow(/restartBackoffMs\[0\]: must be an integer/);
    expect(() => parseBackoff([1.5])).toThrow(/restartBackoffMs\[0\]: must be an integer/);
  });
});

describe("optionalInt / flagOn / requiredEnv", () => {
  it("optionalInt: default, in-range pass, every out-of-shape value names its key", () => {
    expect(optionalInt(undefined, 1, 10, "heartbeatSeconds", 30)).toBe(30);
    expect(optionalInt(null, 1, 10, "heartbeatSeconds", 30)).toBe(30);
    expect(optionalInt(5, 1, 10, "heartbeatSeconds", 30)).toBe(5);
    expect(() => optionalInt(0, 1, 10, "hb", 30)).toThrow(/hb: must be an integer 1\.\.10/);
    expect(() => optionalInt(11, 1, 10, "hb", 30)).toThrow(/hb: must be an integer 1\.\.10/);
    expect(() => optionalInt("5", 1, 10, "hb", 30)).toThrow(/hb: must be an integer 1\.\.10/);
    expect(() => optionalInt(2.5, 1, 10, "hb", 30)).toThrow(/hb: must be an integer 1\.\.10/);
  });

  it("flagOn: exactly true/1 enable; a typo is invisible, never accidental", () => {
    expect(flagOn({ F: "true" }, "F")).toBe(true);
    expect(flagOn({ F: "1" }, "F")).toBe(true);
    for (const v of ["false", "0", "yes", "", "TRUE"]) {
      expect(flagOn({ F: v }, "F")).toBe(false);
    }
    expect(flagOn({}, "F")).toBe(false);
  });

  it("requiredEnv: present passes, unset/empty fails with the why", () => {
    expect(requiredEnv({ K: "v" }, "K", "needed")).toBe("v");
    expect(() => requiredEnv({}, "K", "needed")).toThrow(/^K: required \(env\): needed$/);
    expect(() => requiredEnv({ K: "" }, "K", "needed")).toThrow(/^K: required \(env\): needed$/);
  });
});

describe("readToken", () => {
  it("inline env value wins", () => {
    expect(readToken({ TOK: "abc" }, "TOK", "TOK_FILE")).toBe("abc");
  });

  it("the _FILE twin's FIRST line, trimmed", () => {
    const file = join(dir(), "secret");
    writeFileSync(file, "line-one\nline-two\n");
    expect(readToken({ TOK_FILE: file }, "TOK", "TOK_FILE")).toBe("line-one");
  });

  it("empty inline falls through to the file", () => {
    const file = join(dir(), "secret");
    writeFileSync(file, "from-file");
    expect(readToken({ TOK: "", TOK_FILE: file }, "TOK", "TOK_FILE")).toBe("from-file");
  });

  it("neither present fails closed naming both keys", () => {
    expect(() => readToken({}, "TOK", "TOK_FILE")).toThrow(
      /^TOK\|TOK_FILE: one of them is required \(env\)$/,
    );
    expect(() => readToken({ TOK: "", TOK_FILE: "" }, "TOK", "TOK_FILE")).toThrow(
      /^TOK\|TOK_FILE: one of them is required \(env\)$/,
    );
  });

  it("an unreadable or empty token file fails closed naming the file", () => {
    const missing = join(dir(), "gone");
    expect(() => readToken({ TOK_FILE: missing }, "TOK", "TOK_FILE")).toThrow(
      /^TOK_FILE: cannot read .\/?.*gone/,
    );
    const empty = join(dir(), "empty");
    writeFileSync(empty, "");
    expect(() => readToken({ TOK_FILE: empty }, "TOK", "TOK_FILE")).toThrow(
      /TOK_FILE: .*empty token$/,
    );
    const blankLine = join(dir(), "blank-first");
    writeFileSync(blankLine, "\nsecond line");
    expect(() => readToken({ TOK_FILE: blankLine }, "TOK", "TOK_FILE")).toThrow(/empty token$/);
  });
});

describe("parseSkeleton", () => {
  it("required shared keys fail with their own names", () => {
    expect(() => skeletonOf(`team: t\ntransport:\n  tcp:\n    port: 1\n`)).toThrow(
      /^childWorkspaceId: required/,
    );
    expect(() => skeletonOf(`childWorkspaceId: g/c\ntransport:\n  tcp:\n    port: 1\n`)).toThrow(
      /^team: required$/,
    );
    expect(() => skeletonOf(`childWorkspaceId: g/c\nteam: t\n`)).toThrow(/^transport: required$/);
  });

  it("yaml token, kernelUrl and heartbeat overlay rules", () => {
    const config = skeletonOf(`${BASE_YAML}token: file-token\nkernelUrl: http://k:1/kernel\n`);
    expect(config.token).toBe("file-token");
    expect(config.kernelUrl).toBe("http://k:1/kernel");
    expect(config.heartbeatSeconds).toBe(30);
    expect(config.logLevel).toBe("info");
    expect(config.statePath).toMatch(/connector\.sqlite3$/);
    expect(config.restartBackoffMs).toBe(DEFAULT_BACKOFF);
  });

  it("env overlay beats the file for secret, kernel url and log level", () => {
    const config = skeletonOf(`${BASE_YAML}token: file-token\nkernelUrl: http://yaml:1\n`, {
      OFAC_CHILD_SECRET: "env-secret",
      OFAC_KERNEL_URL: "http://env:8080",
      OFAC_ADAPTER_LOG: "debug",
    });
    expect(config.token).toBe("env-secret");
    expect(config.kernelUrl).toBe("http://env:8080");
    expect(config.logLevel).toBe("debug");
  });

  it("an empty-string env secret still counts as set", () => {
    const config = skeletonOf(`${BASE_YAML}token: file-token`, { OFAC_CHILD_SECRET: "" });
    expect(config.token).toBe("");
  });

  it("statePath from the file resolves against the config dir", () => {
    const config = skeletonOf(`${BASE_YAML}statePath: state/a.db\n`);
    expect(config.statePath).toMatch(/state\/a\.db$/);
    expect(isAbsolute(config.statePath)).toBe(true);
  });

  it("an out-of-range heartbeat is named", () => {
    expect(() => skeletonOf(`${BASE_YAML}heartbeatSeconds: 0\n`)).toThrow(
      /heartbeatSeconds: must be an integer 1\.\.3600/,
    );
  });

  it("a provider-parsed file carries the base dir through ParseOptions", () => {
    const baseDir = dir();
    const options: ParseOptions = { baseDir, env: {} };
    const skeleton = parseSkeleton(
      {
        childWorkspaceId: "g/c",
        team: "t",
        transport: { socket: { path: "rel.sock" } },
      },
      options,
      "abs/state.sqlite3",
    );
    expect(skeleton.transport.socketPath).toBe(resolve(baseDir, "rel.sock"));
    expect(skeleton.statePath).toBe(resolve(baseDir, "abs/state.sqlite3"));
    expect(skeleton).not.toHaveProperty("token");
  });
});

describe("readYamlRoot", () => {
  it("an unreadable path fails closed as the file key", () => {
    expect(() => readYamlRoot(join(dir(), "missing.yaml"))).toThrow(
      /^file: cannot read .*missing\.yaml: ENOENT/,
    );
  });

  it("unparseable YAML names the file", () => {
    const file = join(dir(), "bad.yaml");
    writeFileSync(file, "a: [1,\n b");
    expect(() => readYamlRoot(file)).toThrow(/^file: not parseable YAML: /);
  });

  it("a scalar document is not a mapping", () => {
    const file = join(dir(), "scalar.yaml");
    writeFileSync(file, "just-a-string\n");
    expect(() => readYamlRoot(file)).toThrow(/^file: must be a mapping$/);
  });

  it("baseDir is the file's directory", () => {
    const d = dir();
    const file = join(d, "ok.yaml");
    writeFileSync(file, "a: 1\n");
    expect(readYamlRoot(file)).toEqual({ root: { a: 1 }, baseDir: d });
  });
});
