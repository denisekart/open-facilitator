// Config law: YAML validated by key name, env overlay wins, gitlab credential
// resolves direct-or-file with fail-closed on every unreadable shape, and the
// flag filter decides the advertised manifest (a disabled domain is invisible
// on the wire).
import { mkdtempSync, rmSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { afterAll, describe, expect, it } from "vitest";
import {
  ConnectorConfigError,
  connectorManifest,
  parseConnectorConfig,
  type ConnectorConfig,
} from "../src/config.js";

const dir = mkdtempSync(join(tmpdir(), "ofac-glc-cfg-"));

function parse(yaml: string, env: Record<string, string | undefined>): ConnectorConfig {
  return parseConnectorConfig(yaml, { baseDir: dir, env });
}

const BASE = `
childWorkspaceId: gw.team-alpha/gitlab-connector
team: team-alpha
transport:
  socket:
    path: child.sock
`;

const GITLAB_ENV = { GITLAB_URL: "http://gitlab:80", GITLAB_TOKEN: "bot-pat" };

describe("parseConnectorConfig", () => {
  it("parses the minimal shape + defaults", () => {
    const config = parse(BASE, GITLAB_ENV);
    expect(config.childWorkspaceId).toBe("gw.team-alpha/gitlab-connector");
    expect(config.transport.socketPath).toBe(join(dir, "child.sock"));
    expect(config.statePath).toBe(join(dir, "gitlab-connector.sqlite3"));
    expect(config.heartbeatSeconds).toBe(30);
    expect(config.flags).toEqual({ issues: false, vcs: false, mrs: false, pipeline: false });
    expect(config.logLevel).toBe("info");
  });

  it("OFAC_CHILD_SECRET beats the file token; GITLAB_TOKEN_FILE reads line one", () => {
    const file = join(dir, "pat");
    writeFileSync(file, "file-pat\n");
    const config = parse(`${BASE}\ntoken: file-token\n`, {
      GITLAB_URL: "http://gitlab:80",
      GITLAB_TOKEN_FILE: file,
      OFAC_CHILD_SECRET: "overlay-secret",
    });
    expect(config.token).toBe("overlay-secret");
    expect(config.gitlabToken).toBe("file-pat");
  });

  it("missing required keys name themselves", () => {
    expect(() => parse("team: team-alpha\n", GITLAB_ENV)).toThrow(/childWorkspaceId/);
    expect(() => parse(BASE, { GITLAB_TOKEN: "t" })).toThrow(/GITLAB_URL/);
    expect(() => parse(BASE, { GITLAB_URL: "http://x" })).toThrow(/GITLAB_TOKEN/);
    const empty = join(dir, "empty");
    writeFileSync(empty, "");
    expect(() => parse(BASE, { GITLAB_URL: "http://x", GITLAB_TOKEN_FILE: empty })).toThrow(
      /GITLAB_TOKEN_FILE/,
    );
  });

  it("unknown keys and half-set transports fail closed", () => {
    expect(() => parse(`${BASE}\nsurprise: 1\n`, GITLAB_ENV)).toThrow(/surprise/);
    expect(() =>
      parse(
        `childWorkspaceId: c\nteam: t\ntransport:\n  socket:\n    path: s\n  tcp:\n      host: h\n`,
        GITLAB_ENV,
      ),
    ).toThrow(ConnectorConfigError);
  });

  it("flags gate the manifest; no flags at all is a config-level nothing-to-advertise", () => {
    const manifest = connectorManifest("team-alpha", {
      issues: true,
      vcs: false,
      mrs: true,
      pipeline: false,
    });
    const ids = manifest.capabilities.map((c) => c.id);
    expect(ids).toContain("vcs.issue.read");
    expect(ids).toContain("vcs.issue.label");
    expect(ids).toContain("vcs.change_request.create");
    // GitLab natively supports reply+resolve — both ride the mrs domain.
    expect(ids).toContain("vcs.discussion.reply");
    expect(ids).toContain("vcs.discussion.resolve");
    expect(ids).not.toContain("vcs.branch.push");
    expect(ids).not.toContain("vcs.pipeline.trigger");
    expect(() =>
      connectorManifest("team-alpha", { issues: false, vcs: false, mrs: false, pipeline: false }),
    ).toThrow(ConnectorConfigError);
  });
});

afterAll(() => rmSync(dir, { recursive: true, force: true }));
