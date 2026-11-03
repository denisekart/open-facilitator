// The config fail-closed law: YAML unknown-key rejection naming the key,
// the flag→manifest filtering (a disabled domain is invisible on the wire),
// the token law (inline vs `_FILE` twin), and the nothing-to-advertise
// config error.
import { mkdtempSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { describe, expect, it } from "vitest";
import {
  FLAG_DOMAINS,
  connectorManifest,
  loadConnectorConfigFile,
  parseConnectorConfig,
} from "../src/config.js";

const dir = mkdtempSync(join(tmpdir(), "gh-cfg-"));
writeFileSync(join(dir, "tok"), "ghp_file\n");

function yaml(extra = ""): string {
  return `childWorkspaceId: gw.team-alpha/github
team: team-alpha
kernelUrl: http://kernel:8080
statePath: ./actions.sqlite3
transport:
  socket:
    path: ./child.sock
${extra}`;
}

const env = (overrides: Record<string, string> = {}): Record<string, string> => ({
  GITHUB_URL: "https://api.github.com",
  GITHUB_TOKEN: "ghp_inline",
  GITHUB_ENABLE_ISSUES: "true",
  ...overrides,
});

describe("parseConnectorConfig", () => {
  it("parses the skeleton + env overlay", () => {
    const config = parseConnectorConfig(yaml(), { baseDir: dir, env: env() });
    expect(config.childWorkspaceId).toBe("gw.team-alpha/github");
    expect(config.team).toBe("team-alpha");
    expect(config.transport.socketPath).toBe(join(dir, "child.sock"));
    expect(config.githubUrl).toBe("https://api.github.com");
    expect(config.githubToken).toBe("ghp_inline");
    expect(config.flags).toEqual({
      issues: true,
      vcs: false,
      mrs: false,
      pipeline: false,
    });
    expect(config.statePath).toBe(join(dir, "actions.sqlite3"));
  });

  it("rejects an unknown root key by name", () => {
    expect(() => parseConnectorConfig(yaml("bogus: 1\n"), { baseDir: dir, env: env() })).toThrow(
      /file\.bogus: unknown key/,
    );
  });

  it("reads the token from the file twin's first line", () => {
    const config = parseConnectorConfig(yaml(), {
      baseDir: dir,
      env: env({ GITHUB_TOKEN_FILE: join(dir, "tok") }),
    });
    // The inline GITHUB_TOKEN wins per the token law.
    expect(config.githubToken).toBe("ghp_inline");
    const fileOnly = env({ GITHUB_TOKEN: "" });
    delete fileOnly["GITHUB_TOKEN"];
    expect(
      parseConnectorConfig(yaml(), {
        baseDir: dir,
        env: { ...fileOnly, GITHUB_TOKEN_FILE: join(dir, "tok") },
      }).githubToken,
    ).toBe("ghp_file");
  });

  it("fails closed on a missing url or token", () => {
    const noUrl = env();
    delete noUrl["GITHUB_URL"];
    expect(() => parseConnectorConfig(yaml(), { baseDir: dir, env: noUrl })).toThrow(/GITHUB_URL/);
    const noToken = env();
    delete noToken["GITHUB_TOKEN"];
    expect(() => parseConnectorConfig(yaml(), { baseDir: dir, env: noToken })).toThrow(
      /GITHUB_TOKEN\|GITHUB_TOKEN_FILE/,
    );
  });

  it("loads through the file loader", () => {
    const path = join(dir, "connector.yaml");
    writeFileSync(path, yaml());
    const config = loadConnectorConfigFile(path, env());
    expect(config.githubUrl).toBe("https://api.github.com");
  });
});

describe("connectorManifest", () => {
  it("filters domains by flag", () => {
    const manifest = connectorManifest("team-alpha", {
      issues: true,
      vcs: false,
      mrs: true,
      pipeline: false,
    });
    const ids = manifest.capabilities.map((c) => c.id);
    for (const id of [...FLAG_DOMAINS.issues, ...FLAG_DOMAINS.mrs]) expect(ids).toContain(id);
    expect(ids).not.toContain("vcs.branch.push");
    expect(ids).not.toContain("vcs.pipeline.trigger");
    expect(ids).toContain("vcs.discussion.reply");
    expect(ids).toContain("vcs.discussion.resolve");
    expect(manifest.capabilities.every((c) => c.execution === "asynchronous")).toBe(true);
  });

  it("all-flags-off is a config error, not an empty manifest", () => {
    expect(() =>
      connectorManifest("team-alpha", { issues: false, vcs: false, mrs: false, pipeline: false }),
    ).toThrow(/no op domain enabled/);
  });
});
