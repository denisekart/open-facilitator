// Launcher config: YAML file (the connector's worker-law:
// unknown keys rejected by name, every error names its key) + env overlay —
// env wins, so compose can pin anything the file says. Every key names its
// consumer; missing required keys fail closed with a named error, never a
// silent default. The allowlists are the approved-image gate: an unset list
// denies EVERYTHING (fail closed), a `prefix/*` entry admits that repository
// prefix, anything else matches exactly.
import { readFileSync } from "node:fs";
import { parse } from "yaml";

export class LauncherConfigError extends Error {
  constructor(key: string, message: string) {
    super(`${key}: ${message}`);
    this.name = "LauncherConfigError";
  }
}

export interface LauncherConfig {
  /** Kernel base URL (link plane: enroll / capabilities / hub). */
  kernelUrl: string;
  /** Mode-(a) enrollment secret; value read from the env var NAMED by the file (never stored in it). */
  enrollSecret: string;
  /** Logical link endpoint id this launcher enrolls as. */
  endpointId: string;
  /** Kernel team roster slug the enrollment binds to. */
  team: string;
  /** Docker Engine API socket (`unix:///var/run/docker.sock` default). */
  dockerHost: string;
  /** SQLite link-ledger path. */
  statePath: string;
  /** stop+rm of exited ofac-labeled containers older than this (GC law). */
  gcMinutes: number;
  /** SIGKILL grace on provision.stop (stop envelopes carry no spec). */
  stopGraceSeconds: number;
  /** Approved-image gate: exact refs plus `prefix/*` wildcards. Empty ⇒ deny all. */
  allowedImages: string[];
  /** Approved-network gate: exact names plus `prefix/*` wildcards. Empty ⇒ deny all. */
  allowedNetworks: string[];
  logLevel: "debug" | "info" | "warn" | "error";
}

const ROOT_KEYS: Record<string, true> = {
  kernelUrl: true,
  enrollSecretEnv: true,
  endpointId: true,
  team: true,
  statePath: true,
  gcMinutes: true,
  stopGraceSeconds: true,
};

function fail(key: string, message: string): never {
  throw new LauncherConfigError(key, message);
}

function str(doc: Record<string, unknown>, key: string): string {
  const v = doc[key];
  if (typeof v !== "string" || v === "") fail(key, "must be a non-empty string");
  return v;
}

export function parseLauncherConfig(
  yamlText: string,
  env: Record<string, string | undefined>,
): LauncherConfig {
  const parsed: unknown = parse(yamlText);
  if (typeof parsed !== "object" || parsed === null || Array.isArray(parsed)) {
    fail("config", "adapter.yaml must be a mapping");
  }

  const doc = parsed as Record<string, unknown>;
  for (const key of Object.keys(doc)) {
    if (!(key in ROOT_KEYS)) fail(key, "unknown key");
  }

  const secretEnvKey = str(doc, "enrollSecretEnv");
  const enrollSecret = env[secretEnvKey];
  if (enrollSecret === undefined || enrollSecret === "") {
    fail(secretEnvKey, "required and unset (named by adapter.yaml enrollSecretEnv)");
  }

  const logLevel = env["OFAC_ADAPTER_LOG"] ?? "info";
  if (logLevel !== "debug" && logLevel !== "info" && logLevel !== "warn" && logLevel !== "error") {
    fail("OFAC_ADAPTER_LOG", `unknown level '${logLevel}'`);
  }

  const gcRaw = env["OFAC_PROV_GC_MINUTES"];
  const gcMinutes =
    gcRaw === undefined || gcRaw === ""
      ? intOr(doc["gcMinutes"], 60, "gcMinutes")
      : intOf(gcRaw, "OFAC_PROV_GC_MINUTES");
  const stopGraceSeconds = intOr(doc["stopGraceSeconds"], 10, "stopGraceSeconds");

  return {
    kernelUrl: str(doc, "kernelUrl"),
    enrollSecret,
    endpointId: env["OFAC_ENDPOINT_ID"] ?? str(doc, "endpointId"),
    team: env["OFAC_TEAM"] ?? str(doc, "team"),
    dockerHost: env["DOCKER_HOST"] ?? "unix:///var/run/docker.sock",
    statePath: env["OFAC_STATE_PATH"] ?? str(doc, "statePath"),
    gcMinutes,
    stopGraceSeconds,
    allowedImages: commaList(env["OFAC_PROV_ALLOWED_IMAGES"]),
    allowedNetworks: commaList(env["OFAC_PROV_ALLOWED_NETWORKS"]),
    logLevel,
  };
}

function intOf(raw: string, key: string): number {
  const n = Number(raw);
  if (!Number.isInteger(n) || n < 0) fail(key, `'${raw}' is not a non-negative integer`);
  return n;
}

function intOr(value: unknown, fallback: number, key: string): number {
  if (value === undefined || value === null) return fallback;
  if (typeof value !== "number" || !Number.isInteger(value) || value < 0) {
    fail(key, "must be a non-negative integer");
  }

  return value;
}

export function commaList(raw: string | undefined): string[] {
  if (raw === undefined) return [];
  return raw
    .split(",")
    .map((entry) => entry.trim())
    .filter((entry) => entry !== "");
}

/** The approved-gate matcher: exact match, or `prefix/*` matching any value
 *  starting with `prefix/`. An empty allowlist denies everything. */
export function allowed(patterns: readonly string[], value: string): boolean {
  return patterns.some((pattern) =>
    pattern.endsWith("/*") ? value.startsWith(pattern.slice(0, -1)) : pattern === value,
  );
}

export function readConfigFile(path: string): string {
  try {
    return readFileSync(path, "utf8");
  } catch {
    fail("--config", `unreadable file ${path}`);
  }
}
