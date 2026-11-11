// Connector config: YAML file (worker-law: unknown keys rejected by name,
// every error names its key) + env overlay. The child speaks ONLY to its
// Gateway — the token is the fleet secret (OFAC_CHILD_SECRET), never a
// kernel credential. The kernel URL exists solely to satisfy the SDK's
// artifact-seam shape; the connector never uploads artifacts (its completed
// event carries an inline result manifest), so no kernel token is taken.
// GitLab access (URL/token/op-flags) is env-only so the supervisor unit +
// compose inject, and no gitlab secret ever lands in a config file. The
// shared skeleton (workspace id, transport, token overlay, backoff) is the
// @ofac/provider-core connector face's `parseSkeleton`; only the gitlab keys
// and the op-domain flags are provider-local.
import { readFileSync } from "node:fs";
import { dirname } from "node:path";
import { parse as parseYaml } from "yaml";
import type { CapabilityManifestV1 } from "@ofac/protocol";
import {
  ConnectorConfigError,
  asRecord,
  connectorManifest as coreManifest,
  fail,
  flagOn,
  parseSkeleton,
  readToken,
  rejectUnknown,
  type ConnectorSkeleton,
  type ConnectorTransport,
  type ParseOptions,
} from "@ofac/provider-core";

export { ConnectorConfigError };
export type { ConnectorSkeleton, ConnectorTransport };

export interface ConnectorFlags {
  /** Gates `vcs.issue.read` + `vcs.issue.comment` + `vcs.issue.label`. */
  readonly issues: boolean;
  /** Gates `vcs.branch.push`. */
  readonly vcs: boolean;
  /** Gates the change-request domain: create/read/comment/review plus the
   *  discussion reply+resolve pair — GitLab has native discussion threads,
   *  so BOTH are advertised (unlike forgejo, whose REST surface lacks them). */
  readonly mrs: boolean;
  /** Gates `vcs.pipeline.trigger`. */
  readonly pipeline: boolean;
}

export interface ConnectorConfig extends ConnectorSkeleton {
  flags: ConnectorFlags;
  gitlabUrl: string;
  /** Resolved bot token (env GITLAB_TOKEN, or the file's first line). */
  gitlabToken: string;
}

const ROOT_KEYS = [
  "childWorkspaceId",
  "team",
  "token",
  "transport",
  "kernelUrl",
  "statePath",
  "heartbeatSeconds",
  "restartBackoffMs",
];

/** The flag→domain map: the core builder flattens and de-duplicates it; a
 *  disabled domain contributes no ids, so it is invisible on the wire and no
 *  row can ever route to it. GitLab's v4 REST carries native discussion
 *  reply (`POST .../discussions/:id`) and resolve (`PUT .../discussions/:id
 *  {resolved}`), so the mrs domain declares the full six-op set — the
 *  connector declares no op it cannot execute. */
export const FLAG_DOMAINS: Readonly<Record<keyof ConnectorFlags, readonly string[]>> = {
  issues: [
    "vcs.issue.read",
    "vcs.issue.comment",
    "vcs.issue.label",
    "vcs.issue.create",
    "vcs.issue.close",
  ],
  vcs: ["vcs.branch.push"],
  mrs: [
    "vcs.change_request.create",
    "vcs.change_request.read",
    "vcs.change_request.comment",
    "vcs.change_request.review",
    "vcs.change_request.merge",
    "vcs.discussion.reply",
    "vcs.discussion.resolve",
  ],
  pipeline: ["vcs.pipeline.trigger"],
};

export function parseConnectorConfig(yamlText: string, options: ParseOptions): ConnectorConfig {
  let doc: unknown;
  try {
    doc = parseYaml(yamlText);
  } catch (error) {
    fail("file", `not parseable YAML: ${(error as Error).message}`);
  }

  const root = asRecord(doc, "file");
  rejectUnknown(root, ROOT_KEYS, "file");
  const skeleton = parseSkeleton(root, options, "gitlab-connector.sqlite3");
  const env = options.env;

  return {
    ...skeleton,
    flags: {
      issues: flagOn(env, "GITLAB_ENABLE_ISSUES"),
      vcs: flagOn(env, "GITLAB_ENABLE_VCS"),
      mrs: flagOn(env, "GITLAB_ENABLE_MRS"),
      pipeline: flagOn(env, "GITLAB_ENABLE_PIPELINE"),
    },
    gitlabUrl:
      env["GITLAB_URL"] ??
      fail("GITLAB_URL", "required (env): the git host the ops execute against"),
    gitlabToken: readToken(env, "GITLAB_TOKEN", "GITLAB_TOKEN_FILE"),
  };
}

export function loadConnectorConfigFile(
  path: string,
  env: Record<string, string | undefined> = process.env,
): ConnectorConfig {
  let text: string;
  try {
    text = readFileSync(path, "utf8");
  } catch (error) {
    throw new ConnectorConfigError("file", `cannot read ${path}: ${(error as Error).message}`);
  }

  return parseConnectorConfig(text, { baseDir: dirname(path), env });
}

/**
 * The advertised manifest: every op id rides a v1 `asynchronous` descriptor;
 * the opt-in flags filter the list — a disabled domain is invisible on the
 * wire, so no row can ever route to it. Config-law error shape: the core's
 * nothing-to-advertise throw re-rides the `ConnectorConfigError("flags", …)`
 * identity.
 */
export function connectorManifest(team: string, flags: ConnectorFlags): CapabilityManifestV1 {
  const domains: Record<string, readonly string[]> = {};
  for (const flag of Object.keys(FLAG_DOMAINS) as (keyof ConnectorFlags)[]) {
    if (flags[flag]) domains[flag] = FLAG_DOMAINS[flag];
  }

  try {
    return coreManifest(team, domains);
  } catch (error) {
    throw new ConnectorConfigError("flags", (error as Error).message);
  }
}
