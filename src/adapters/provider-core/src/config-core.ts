// Connector config law, provider-agnostic: YAML file (worker-law: unknown
// keys rejected by name, every error names its key) + env overlay. The
// shared skeleton (childWorkspaceId/team/token/transport/kernelUrl/statePath/
// heartbeat/backoff) parses HERE; the provider supplies its own root keys,
// env names, and flags, parsed with the exported primitives. The connector
// child speaks ONLY to its Gateway — the token is the fleet secret
// (OFAC_CHILD_SECRET), never a kernel credential; provider access
// (URL/token/op-flags) is env-only so no provider secret lands in a config file.
import { readFileSync } from "node:fs";
import { dirname, isAbsolute, resolve } from "node:path";
import { parse as parseYaml } from "yaml";

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

/** The shared YAML skeleton every connector config carries. */
export interface ConnectorSkeleton {
  childWorkspaceId: string;
  /** Owner team for the manifest (the Gateway rewrites it on the union POST anyway). */
  team: string;
  token?: string | undefined;
  transport: ConnectorTransport;
  kernelUrl: string;
  /** Absolute path of the connector_actions ledger SQLite file. */
  statePath: string;
  heartbeatSeconds: number;
  /** `[]` disables auto-restart (exit-3 register-denied idiom). */
  restartBackoffMs: readonly number[];
  logLevel: string;
}

export interface ParseOptions {
  baseDir: string;
  env: Record<string, string | undefined>;
}

export function fail(key: string, message: string): never {
  throw new ConnectorConfigError(key, message);
}

export function asRecord(value: unknown, key: string): Record<string, unknown> {
  if (typeof value !== "object" || value === null || Array.isArray(value)) {
    fail(key, "must be a mapping");
  }

  return value as Record<string, unknown>;
}

export function optionalString(doc: Record<string, unknown>, key: string): string | undefined {
  const v = doc[key];
  if (v === undefined || v === null) return undefined;
  if (typeof v !== "string" || v === "") fail(key, "must be a non-empty string");
  return v;
}

export function rejectUnknown(
  doc: Record<string, unknown>,
  allowed: readonly string[],
  where: string,
): void {
  for (const key of Object.keys(doc)) {
    if (!allowed.includes(key)) fail(`${where}.${key}`, "unknown key");
  }
}

export function resolvePath(baseDir: string, p: string, key: string): string {
  if (typeof p !== "string" || p.length === 0) fail(key, "must be a non-empty string");
  return isAbsolute(p) ? p : resolve(baseDir, p);
}

const TRANSPORT_KEYS = ["socket", "tcp"] as const;
const SOCKET_KEYS = ["path"] as const;
const TCP_KEYS = ["host", "port"] as const;

export function parseTransport(doc: Record<string, unknown>, baseDir: string): ConnectorTransport {
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

/** The restart-backoff default the SDK ships; reused verbatim. */
export const DEFAULT_BACKOFF: readonly number[] = [1000, 2000, 5000, 15000, 30000];

export function parseBackoff(value: unknown): readonly number[] {
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

export function optionalInt(
  value: unknown,
  lo: number,
  hi: number,
  key: string,
  dflt: number,
): number {
  if (value === undefined || value === null) return dflt;
  if (typeof value !== "number" || !Number.isInteger(value) || value < lo || value > hi) {
    fail(key, `must be an integer ${lo}..${hi}`);
  }

  return value;
}

/** Opt-in flag law: exactly `true`/`1` enable a domain; anything else (unset,
 *  typo, `false`) disables — a typo'd flag is invisible, never accidental. */
export function flagOn(env: Record<string, string | undefined>, key: string): boolean {
  const v = env[key];
  return v === "true" || v === "1";
}

/** Token law: inline env value, or the FIRST LINE of the file named by the
 *  `_FILE` env twin (compose mounts secrets as files). One of them is
 *  required; an empty file fails closed. */
export function readToken(
  env: Record<string, string | undefined>,
  envKey: string,
  fileKey: string,
): string {
  const inline = env[envKey];
  if (inline !== undefined && inline !== "") return inline;
  const tokenFile = env[fileKey];
  if (tokenFile === undefined || tokenFile === "") {
    fail(`${envKey}|${fileKey}`, "one of them is required (env)");
  }

  let token: string | undefined;
  try {
    token = readFileSync(tokenFile, "utf8").split("\n")[0]?.trim();
  } catch (error) {
    fail(fileKey, `cannot read ${tokenFile}: ${(error as Error).message}`);
  }

  if (token === undefined || token === "") fail(fileKey, `${tokenFile}: empty token`);
  return token;
}

/** Required env string with the standing error shape. */
export function requiredEnv(
  env: Record<string, string | undefined>,
  key: string,
  why: string,
): string {
  const v = env[key];
  if (v === undefined || v === "") fail(key, `required (env): ${why}`);
  return v;
}

/** Parse the shared skeleton after the provider rejected its own root keys. */
export function parseSkeleton(
  root: Record<string, unknown>,
  options: ParseOptions,
  stateFileDefault: string,
): ConnectorSkeleton {
  const childWorkspaceId =
    optionalString(root, "childWorkspaceId") ??
    fail("childWorkspaceId", "required (<gatewayId>/<name>)");
  const team = optionalString(root, "team") ?? fail("team", "required");
  if (root["transport"] === undefined) fail("transport", "required");
  const transport = parseTransport(asRecord(root["transport"], "transport"), options.baseDir);
  const statePath = resolvePath(
    options.baseDir,
    optionalString(root, "statePath") ?? stateFileDefault,
    "statePath",
  );
  const env = options.env;
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
    heartbeatSeconds: optionalInt(root["heartbeatSeconds"], 1, 3600, "heartbeatSeconds", 30),
    restartBackoffMs: parseBackoff(root["restartBackoffMs"]),
    logLevel: env["OFAC_ADAPTER_LOG"] ?? "info",
  };
}

/** Read + parse the YAML document, returning the root mapping (the provider
 *  runs its own rejectUnknown, then parseSkeleton, then its env overlay). */
export function readYamlRoot(path: string): { root: Record<string, unknown>; baseDir: string } {
  let text: string;
  try {
    text = readFileSync(path, "utf8");
  } catch (error) {
    throw new ConnectorConfigError("file", `cannot read ${path}: ${(error as Error).message}`);
  }

  let doc: unknown;
  try {
    doc = parseYaml(text);
  } catch (error) {
    fail("file", `not parseable YAML: ${(error as Error).message}`);
  }

  return { root: asRecord(doc, "file"), baseDir: dirname(path) };
}
