// The worker config: a YAML (JSON parses too) file, validated 06-style —
// unknown keys rejected by name, every error names its key. The env
// overlay law mirrors the Gateway's: OFAC_CHILD_SECRET beats the file's
// token; OFAC_ADAPTER_LOG beats the default log level.
import { readFileSync } from "node:fs";
import { dirname, isAbsolute, resolve } from "node:path";
import { parse } from "yaml";
import type { CapabilityManifestV1 } from "@ofac/protocol";

export class WorkerConfigError extends Error {
  constructor(key: string, message: string) {
    super(`${key}: ${message}`);
    this.name = "WorkerConfigError";
  }
}

export interface WorkerTransport {
  readonly socketPath?: string | undefined;
  readonly host?: string | undefined;
  readonly port?: number | undefined;
}

export interface WorkerConfig {
  childWorkspaceId: string;
  /** Env overlay applied: OFAC_CHILD_SECRET wins over the file. */
  token?: string | undefined;
  /** Kernel origin for the artifact byte path (Plan 15); OFAC_KERNEL_URL overlay. Required by the harness at completion. */
  kernelUrl?: string | undefined;
  /**
   * Bearer for the kernel link plane (Plan 15); OFAC_KERNEL_TOKEN overlay.
   * The Gateway-facing `token` is NOT a kernel credential unless it is one
   * (mode-(a) enrollment issues a separate linkToken); unset ⇒ the worker's
   * own `token` is sent verbatim.
   */
  kernelToken?: string | undefined;
  transport: WorkerTransport;
  manifest: CapabilityManifestV1;
  /** Absolute: artifacts land under <workDir>/artifacts/. */
  workDir: string;
  stepMs: number;
  heartbeatSeconds: number;
  restartBackoffMs: readonly number[];
  logLevel: string;
}

export const DEFAULT_RESTART_BACKOFF_MS: readonly number[] = [100, 250, 500, 1000, 2000, 5000];

const CHILD_NAME_RE = /^[a-z0-9]([a-z0-9-]*[a-z0-9])?$/;
const CHILD_ID_MAX = 128;
const TOP_KEYS: Record<string, true> = {
  apiVersion: true,
  kind: true,
  childWorkspaceId: true,
  token: true,
  kernelUrl: true,
  kernelToken: true,
  transport: true,
  manifestFile: true,
  workDir: true,
  stepMs: true,
  heartbeatSeconds: true,
  restartBackoffMs: true,
};
const TRANSPORT_KEYS: Record<string, true> = { socket: true, tcp: true };
const SOCKET_KEYS: Record<string, true> = { path: true };
const TCP_KEYS: Record<string, true> = { host: true, port: true };

function fail(key: string, message: string): never {
  throw new WorkerConfigError(key, message);
}

function asRecord(value: unknown, key: string): Record<string, unknown> {
  if (typeof value !== "object" || value === null || Array.isArray(value)) {
    fail(key, "must be a mapping");
  }
  return value as Record<string, unknown>;
}

function rejectUnknown(
  doc: Record<string, unknown>,
  allowed: Record<string, true>,
  where: string,
): void {
  for (const key of Object.keys(doc)) {
    if (!(key in allowed)) fail(where, `unknown key '${key}'`);
  }
}

function optionalString(doc: Record<string, unknown>, key: string): string | undefined {
  const value = doc[key];
  if (value === undefined || value === null) return undefined;
  if (typeof value !== "string" || value.length === 0) fail(key, "must be a non-empty string");
  return value;
}

function requireInt(doc: Record<string, unknown>, key: string, min: number, max: number): number {
  const value = doc[key];
  if (typeof value !== "number" || !Number.isInteger(value) || value < min || value > max) {
    fail(key, `must be an integer in ${min}..${max}`);
  }
  return value;
}

function optionalInt(
  doc: Record<string, unknown>,
  key: string,
  min: number,
  max: number,
  fallback: number,
): number {
  if (doc[key] === undefined || doc[key] === null) return fallback;
  return requireInt(doc, key, min, max);
}

export interface ParseWorkerOptions {
  /** Base for relative paths (manifestFile/workDir/socket): the config file's dir. */
  baseDir?: string | undefined;
  env?: Record<string, string | undefined> | undefined;
  /** Test seam: read the manifest from an injected fs instead of disk. */
  readManifest?: ((path: string) => unknown) | undefined;
}

export function parseWorkerConfig(
  yamlText: string,
  options: ParseWorkerOptions = {},
): WorkerConfig {
  const env = options.env ?? process.env;
  const baseDir = options.baseDir ?? process.cwd();
  let doc: unknown;
  try {
    doc = parse(yamlText);
  } catch (error) {
    fail("file", `not parseable YAML: ${(error as Error).message}`);
  }
  const root = asRecord(doc, "file");
  rejectUnknown(root, TOP_KEYS, "file");

  const apiVersion = optionalString(root, "apiVersion");
  if (apiVersion !== undefined && apiVersion !== "facilitator.framework/v1") {
    fail("apiVersion", "must be 'facilitator.framework/v1'");
  }
  const kind = optionalString(root, "kind");
  if (kind !== undefined && kind !== "HarnessAdapter") {
    fail("kind", "must be 'HarnessAdapter'");
  }

  const childWorkspaceId = optionalString(root, "childWorkspaceId");
  if (childWorkspaceId === undefined) fail("childWorkspaceId", "required");
  const slash = childWorkspaceId.indexOf("/");
  const name = slash < 0 ? "" : childWorkspaceId.slice(slash + 1);
  if (slash <= 0 || name.length > CHILD_ID_MAX || !CHILD_NAME_RE.test(name)) {
    fail(
      "childWorkspaceId",
      "must be '<gatewayId>/<name>' with name matching ^[a-z0-9]([a-z0-9-]*[a-z0-9])?$ (<=128)",
    );
  }

  const transportDoc = root["transport"];
  if (transportDoc === undefined || transportDoc === null) fail("transport", "required");
  const transport = parseTransport(asRecord(transportDoc, "transport"), baseDir);

  const manifestFile = optionalString(root, "manifestFile");
  if (manifestFile === undefined) fail("manifestFile", "required");
  const manifestPath = resolvePath(baseDir, manifestFile, "manifestFile");
  let manifestRaw: unknown;
  try {
    manifestRaw = options.readManifest?.(manifestPath) ?? parse(readFileSync(manifestPath, "utf8"));
  } catch (error) {
    fail("manifestFile", `cannot read ${manifestPath}: ${(error as Error).message}`);
  }
  const manifest = manifestRaw as CapabilityManifestV1; // schema law: the Gateway validates

  const workDir = resolvePath(
    baseDir,
    optionalString(root, "workDir") ?? "./.ofac-worker",
    "workDir",
  );
  const stepMs = optionalInt(root, "stepMs", 0, 60_000, 1500);
  const heartbeatSeconds = optionalInt(root, "heartbeatSeconds", 1, 3600, 30);
  const restartBackoffMs = parseBackoff(root["restartBackoffMs"]);

  return {
    childWorkspaceId,
    ...(env["OFAC_CHILD_SECRET"] !== undefined
      ? { token: env["OFAC_CHILD_SECRET"] }
      : optionalString(root, "token") !== undefined
        ? { token: optionalString(root, "token") }
        : {}),
    ...(env["OFAC_KERNEL_URL"] !== undefined
      ? { kernelUrl: env["OFAC_KERNEL_URL"] }
      : optionalString(root, "kernelUrl") !== undefined
        ? { kernelUrl: optionalString(root, "kernelUrl") }
        : {}),
    ...(env["OFAC_KERNEL_TOKEN"] !== undefined
      ? { kernelToken: env["OFAC_KERNEL_TOKEN"] }
      : optionalString(root, "kernelToken") !== undefined
        ? { kernelToken: optionalString(root, "kernelToken") }
        : {}),
    transport,
    manifest,
    workDir,
    stepMs,
    heartbeatSeconds,
    restartBackoffMs,
    logLevel: env["OFAC_ADAPTER_LOG"] ?? "info",
  };
}

function resolvePath(baseDir: string, p: string, key: string): string {
  if (typeof p !== "string" || p.length === 0) fail(key, "must be a non-empty string");
  return isAbsolute(p) ? p : resolve(baseDir, p);
}

function parseTransport(doc: Record<string, unknown>, baseDir: string): WorkerTransport {
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
  const port = requireInt(tcpDoc, "port", 1, 65_535);
  return { host, port };
}

function parseBackoff(value: unknown): readonly number[] {
  if (value === undefined || value === null) return DEFAULT_RESTART_BACKOFF_MS;
  if (!Array.isArray(value)) fail("restartBackoffMs", "must be a list of integers");
  if (value.length === 0) return []; // explicit: auto-restart disabled
  return value.map((entry, index) => {
    if (typeof entry !== "number" || !Number.isInteger(entry) || entry < 0 || entry > 600_000) {
      fail(`restartBackoffMs[${index}]`, "must be an integer 0..600000");
    }
    return entry;
  });
}

export function loadWorkerConfigFile(
  path: string,
  options: Omit<ParseWorkerOptions, "baseDir"> = {},
): WorkerConfig {
  let text: string;
  try {
    text = readFileSync(path, "utf8");
  } catch (error) {
    throw new WorkerConfigError("file", `cannot read ${path}: ${(error as Error).message}`);
  }
  return parseWorkerConfig(text, { ...options, baseDir: dirname(path) });
}
