// Connector config: YAML file (worker-law: unknown keys rejected by name,
// every error names its key) + env overlay. Decision 5: this child speaks
// ONLY to its Gateway — the token is the fleet secret (OFAC_CHILD_SECRET),
// never a kernel credential. The kernel URL exists solely to satisfy the
// SDK's artifact-seam shape; the connector never uploads artifacts (its
// completed event carries an inline result manifest), so no kernel token is
// taken. Forge access (URL/token/op-flags) is env-only so the supervisor
// unit + compose inject, and no forge secret ever lands in a config file.
import { readFileSync } from "node:fs";
import { dirname, isAbsolute, resolve } from "node:path";
import { parse } from "yaml";
import type { CapabilityManifestV1 } from "@ofac/protocol";

export class ConnectorConfigError extends Error {
  constructor(key: string, message: string) {
    super(`${key}: ${message}`);
    this.name = "ConnectorConfigError";
  }
}

export interface ConnectorTransport {
  readonly socketPath?: string | undefined;
  readonly host?: string | undefined;
  readonly port?: number | undefined;
}

export interface ConnectorFlags {
  /** Gates `vcs.issue.read` + `vcs.issue.comment`. */
  readonly issues: boolean;
  /** Gates `vcs.branch.push` + `vcs.change_request.create` + `vcs.change_request.comment`. */
  readonly vcs: boolean;
  /** Gates `vcs.pipeline.trigger`. */
  readonly ci: boolean;
}

export interface ConnectorConfig {
  childWorkspaceId: string;
  /** Owner team for the manifest (the Gateway rewrites it on the union POST anyway). */
  team: string;
  token?: string | undefined;
  transport: ConnectorTransport;
  kernelUrl: string;
  /** Absolute path of the connector_actions ledger SQLite file. */
  statePath: string;
  flags: ConnectorFlags;
  forgejoUrl: string;
  /** Resolved bot token (env FORGEJO_TOKEN, or the file's first line). */
  forgejoToken: string;
  heartbeatSeconds: number;
  /** `[]` disables auto-restart (exit-3 register-denied idiom). */
  restartBackoffMs: readonly number[];
  logLevel: string;
}

interface ParseOptions {
  baseDir: string;
  env: Record<string, string | undefined>;
}

const ROOT_KEYS = new Set([
  "childWorkspaceId",
  "team",
  "token",
  "transport",
  "kernelUrl",
  "statePath",
  "heartbeatSeconds",
  "restartBackoffMs",
]);
const TRANSPORT_KEYS = new Set(["socket", "tcp"]);
const SOCKET_KEYS = new Set(["path"]);
const TCP_KEYS = new Set(["host", "port"]);

/** The restart-backoff default the SDK ships; reused verbatim. */
const DEFAULT_BACKOFF: readonly number[] = [1000, 2000, 5000, 15000, 30000];

function fail(key: string, message: string): never {
  throw new ConnectorConfigError(key, message);
}

function asRecord(value: unknown, key: string): Record<string, unknown> {
  if (typeof value !== "object" || value === null || Array.isArray(value)) {
    fail(key, "must be a mapping");
  }

  return value as Record<string, unknown>;
}

function optionalString(doc: Record<string, unknown>, key: string): string | undefined {
  const v = doc[key];
  if (v === undefined || v === null) return undefined;
  if (typeof v !== "string" || v === "") fail(key, "must be a non-empty string");
  return v;
}

function rejectUnknown(doc: Record<string, unknown>, allowed: Set<string>, where: string): void {
  for (const key of Object.keys(doc)) {
    if (!allowed.has(key)) fail(`${where}.${key}`, "unknown key");
  }
}

function resolvePath(baseDir: string, p: string, key: string): string {
  if (typeof p !== "string" || p.length === 0) fail(key, "must be a non-empty string");
  return isAbsolute(p) ? p : resolve(baseDir, p);
}

function parseTransport(doc: Record<string, unknown>, baseDir: string): ConnectorTransport {
  rejectUnknown(doc, TRANSPORT_KEYS, "transport");
  const socket = doc["socket"];
  const tcp = doc["tcp"];
  if (socket !== undefined && tcp !== undefined) {
    fail("transport", "socket and tcp are mutually exclusive (exactly one required)");
  }

  if (socket === undefined && tcp === undefined) {
    fail("transport", "exactly one of socket/tcp is required");
  }

  if (socket !== undefined) {
    const socketDoc = asRecord(socket, "transport.socket");
    rejectUnknown(socketDoc, SOCKET_KEYS, "transport.socket");
    const p = optionalString(socketDoc, "path");
    if (p === undefined) fail("transport.socket.path", "required");
    return { socketPath: resolvePath(baseDir, p, "transport.socket.path") };
  }

  const tcpDoc = asRecord(tcp, "transport.tcp");
  rejectUnknown(tcpDoc, TCP_KEYS, "transport.tcp");
  const host = optionalString(tcpDoc, "host") ?? "127.0.0.1";
  const rawPort = tcpDoc["port"];
  if (
    typeof rawPort !== "number" ||
    !Number.isInteger(rawPort) ||
    rawPort < 1 ||
    rawPort > 65_535
  ) {
    fail("transport.tcp.port", "must be an integer 1..65535");
  }

  return { host, port: rawPort };
}

function parseBackoff(value: unknown): readonly number[] {
  if (value === undefined || value === null) return DEFAULT_BACKOFF;
  if (!Array.isArray(value)) fail("restartBackoffMs", "must be a list of integers");
  if (value.length === 0) return [];
  return value.map((entry, index) => {
    if (typeof entry !== "number" || !Number.isInteger(entry) || entry < 0 || entry > 600_000) {
      fail(`restartBackoffMs[${index}]`, "must be an integer 0..600000");
    }

    return entry;
  });
}

function flagOn(env: Record<string, string | undefined>, key: string): boolean {
  const v = env[key];
  return v === "true" || v === "1";
}

function optionalInt(value: unknown, lo: number, hi: number, key: string, dflt: number): number {
  if (value === undefined || value === null) return dflt;
  if (typeof value !== "number" || !Number.isInteger(value) || value < lo || value > hi) {
    fail(key, `must be an integer ${lo}..${hi}`);
  }

  return value;
}

export function parseConnectorConfig(yamlText: string, options: ParseOptions): ConnectorConfig {
  let doc: unknown;
  try {
    doc = parse(yamlText);
  } catch (error) {
    fail("file", `not parseable YAML: ${(error as Error).message}`);
  }

  const root = asRecord(doc, "file");
  rejectUnknown(root, ROOT_KEYS, "file");
  const childWorkspaceId =
    optionalString(root, "childWorkspaceId") ??
    fail("childWorkspaceId", "required (<gatewayId>/<name>)");
  const team = optionalString(root, "team") ?? fail("team", "required");
  const transportDoc = root["transport"];
  if (transportDoc === undefined) fail("transport", "required");
  const transport = parseTransport(asRecord(transportDoc, "transport"), options.baseDir);
  const statePath = resolvePath(
    options.baseDir,
    optionalString(root, "statePath") ?? "forgejo-connector.sqlite3",
    "statePath",
  );
  const env = options.env;

  const forgejoUrl =
    env["FORGEJO_URL"] ?? fail("FORGEJO_URL", "required (env): the forge the ops execute against");
  const tokenFile = env["FORGEJO_TOKEN_FILE"];
  let forgejoToken: string | undefined = env["FORGEJO_TOKEN"];
  if (forgejoToken === undefined || forgejoToken === "") {
    if (tokenFile === undefined || tokenFile === "") {
      fail("FORGEJO_TOKEN|FORGEJO_TOKEN_FILE", "one of them is required (env)");
    }

    try {
      forgejoToken = readFileSync(tokenFile, "utf8").split("\n")[0]?.trim();
    } catch (error) {
      fail("FORGEJO_TOKEN_FILE", `cannot read ${tokenFile}: ${(error as Error).message}`);
    }
  }

  if (forgejoToken === undefined || forgejoToken === "") {
    fail("FORGEJO_TOKEN_FILE", `${tokenFile ?? ""}: empty token`);
  }

  return {
    childWorkspaceId,
    team,
    ...(env["OFAC_CHILD_SECRET"] !== undefined
      ? { token: env["OFAC_CHILD_SECRET"] }
      : optionalString(root, "token") !== undefined
        ? { token: optionalString(root, "token") }
        : {}),
    transport,
    // Env overlay so compose can point it at the real kernel; the connector
    // never calls it (inline completions), so any well-formed origin is legal.
    kernelUrl:
      env["OFAC_KERNEL_URL"] ?? optionalString(root, "kernelUrl") ?? "http://127.0.0.1:8080",
    statePath,
    flags: {
      issues: flagOn(env, "FORGEJO_ENABLE_ISSUES"),
      vcs: flagOn(env, "FORGEJO_ENABLE_VCS"),
      ci: flagOn(env, "FORGEJO_ENABLE_CI"),
    },
    forgejoUrl,
    forgejoToken,
    heartbeatSeconds: optionalInt(root["heartbeatSeconds"], 1, 3600, "heartbeatSeconds", 30),
    restartBackoffMs: parseBackoff(root["restartBackoffMs"]),
    logLevel: env["OFAC_ADAPTER_LOG"] ?? "info",
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
 * The advertised manifest (decision 5): every op id rides a v1
 * `asynchronous` descriptor; the opt-in flags filter the list — a disabled
 * domain is invisible on the wire, so no row can ever route to it.
 */
export function connectorManifest(team: string, flags: ConnectorFlags): CapabilityManifestV1 {
  const descriptor = (id: string): CapabilityManifestV1["capabilities"][number] => ({
    id,
    version: 1,
    execution: "asynchronous",
    supportsSteering: false,
    supportsCancellation: false,
    supportsNudge: false,
  });
  const capabilities = [
    ...(flags.issues ? [descriptor("vcs.issue.read"), descriptor("vcs.issue.comment")] : []),
    ...(flags.vcs
      ? [
          descriptor("vcs.branch.push"),
          descriptor("vcs.change_request.create"),
          descriptor("vcs.change_request.comment"),
        ]
      : []),
    ...(flags.ci ? [descriptor("vcs.pipeline.trigger")] : []),
  ];
  if (capabilities.length === 0) {
    fail("flags", "no op domain enabled (set FORGEJO_ENABLE_ISSUES/VCS/CI)");
  }

  return {
    apiVersion: "facilitator.framework/v1",
    kind: "CapabilityManifest",
    ownerTeam: team,
    capabilities: capabilities as CapabilityManifestV1["capabilities"],
  };
}
