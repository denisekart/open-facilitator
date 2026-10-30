// Config law: YAML validated by key name, env overlay wins, forge credential
// resolves direct-or-file with fail-closed on every unreadable shape, and the
// flag filter decides the advertised manifest (decision 5: a disabled domain
// is invisible on the wire).
import { mkdtempSync, rmSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { describe, expect, it } from "vitest";
import { ConnectorConfigError, connectorManifest, parseConnectorConfig } from "../src/config.js";

const dir = mkdtempSync(join(tmpdir(), "ofac-fjc-cfg-"));

function parse(
  yaml: string,
  env: Record<string, string | undefined>,
): ReturnType<typeof parseConnectorConfig> {
  return parseConnectorConfig(yaml, { baseDir: dir, env });
}

const BASE = `
childWorkspaceId: gw.team-alpha/forgejo-connector
team: team-alpha
transport:
  socket:
    path: child.sock
`;

const FORGE_ENV = { FORGEJO_URL: "http://forgejo:3000", FORGEJO_TOKEN: "bot-pat" };

describe("parseConnectorConfig", () => {
  it("parses the minimal shape + defaults", () => {
    const config = parse(BASE, FORGE_ENV);
    expect(config.childWorkspaceId).toBe("gw.team-alpha/forgejo-connector");
    expect(config.transport.socketPath).toBe(join(dir, "child.sock"));
    expect(config.statePath).toBe(join(dir, "forgejo-connector.sqlite3"));
    expect(config.heartbeatSeconds).toBe(30);
    expect(config.flags).toEqual({ issues: false, vcs: false, ci: false });
    expect(config.logLevel).toBe("info");
  });

  it("OFAC_CHILD_SECRET beats the file token; FORGEJO_TOKEN_FILE reads line one", () => {
    const tokenFile = join(dir, "bot-token");
    writeFileSync(tokenFile, "file-pat\nsecond line ignored\n");
    const config = parse(`${BASE}token: file-secret\n`, {
      ...FORGE_ENV,
      FORGEJO_TOKEN: "",
      FORGEJO_TOKEN_FILE: tokenFile,
      OFAC_CHILD_SECRET: "env-secret",
    });
    expect(config.token).toBe("env-secret");
    expect(config.forgejoToken).toBe("file-pat");
    rmSync(tokenFile);
  });

  it("missing required keys name themselves", () => {
    expect(() => parse("team: team-alpha\n", FORGE_ENV)).toThrow(ConnectorConfigError);
    expect(() => parse("team: team-alpha\n", FORGE_ENV)).toThrow(/childWorkspaceId/);
    expect(() => parse(BASE, { FORGEJO_TOKEN: "t" })).toThrow(/FORGEJO_URL/);
    expect(() => parse(BASE, { FORGEJO_URL: "http://x" })).toThrow(
      /FORGEJO_TOKEN\|FORGEJO_TOKEN_FILE/,
    );
    expect(() =>
      parse(BASE, { ...FORGE_ENV, FORGEJO_TOKEN: "", FORGEJO_TOKEN_FILE: join(dir, "absent") }),
    ).toThrow(/cannot read/);
  });

  it("unknown keys and half-set transports fail closed", () => {
    expect(() => parse(`${BASE}surprise: 1\n`, FORGE_ENV)).toThrow(/file\.surprise: unknown key/);
    expect(() =>
      parse(
        "childWorkspaceId: a/b\nteam: t\ntransport:\n  socket:\n    path: s\n  tcp:\n    port: 1\n",
        FORGE_ENV,
      ),
    ).toThrow(/mutually exclusive/);
    expect(() => parse("childWorkspaceId: a/b\nteam: t\ntransport: {}\n", FORGE_ENV)).toThrow(
      /exactly one of socket\/tcp/,
    );
  });

  it("flags gate the manifest; no flags at all is a config-level nothing-to-advertise", () => {
    const manifest = connectorManifest("team-alpha", { issues: true, vcs: false, ci: true });
    expect(manifest.capabilities.map((c) => c.id)).toEqual([
      "vcs.issue.read",
      "vcs.issue.comment",
      "vcs.pipeline.trigger",
    ]);
    expect(() => connectorManifest("team-alpha", { issues: false, vcs: false, ci: false })).toThrow(
      /no op domain enabled/,
    );
    const all = connectorManifest("team-alpha", { issues: true, vcs: true, ci: true });
    expect(all.capabilities).toHaveLength(6);
    expect(all.ownerTeam).toBe("team-alpha");
  });
});
