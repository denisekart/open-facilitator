// Fleet launcher config — the AWS reference launcher is env-only (a Fargate
// task has no adapter.yaml hand-mount): every setting names its env var, a
// missing required var fails closed by name, never a silent default. The
// rules file (OFAC_RULES_FILE, JSON) is the image-allowlist + network-pinning
// analog: `workloadId -> { taskDefinition, cluster, subnets?, envAllowlist? }`
// — the launcher may ONLY run statically-declared workloads, and an unknown
// workloadId is denied without consulting the wire. An empty rules table is
// valid and denies everything (the fail-closed posture of an unset allowlist).
import { readFileSync } from "node:fs";

export class LauncherConfigError extends Error {
  constructor(key: string, message: string) {
    super(`${key}: ${message}`);
    this.name = "LauncherConfigError";
  }
}

/** One approved fleet workload: exactly this task definition, exactly this
 *  cluster, optionally pinned to these subnets, optionally gating the spec
 *  env NAMES it may receive (values always expand from the launcher's env). */
export interface FleetRule {
  taskDefinition: string;
  cluster: string;
  /** awsvpc network pinning; empty ⇒ the task definition's own placement. */
  subnets: string[];
  /** Spec env NAMES admitted into the RunTask override; undefined ⇒ all. */
  envAllowlist?: readonly string[] | undefined;
}

export type FleetRules = Readonly<Record<string, FleetRule>>;

export interface LauncherConfig {
  /** Kernel base URL (link plane: enroll / capabilities / hub). */
  kernelUrl: string;
  /** Mode-(a) enrollment secret (secret-injected env on the task). */
  enrollToken: string;
  /** Logical link endpoint id this launcher enrolls as (e.g. `launcher.team-alpha`). */
  endpointId: string;
  /** Kernel team roster slug the enrollment + capability manifest bind to. */
  team: string;
  /** Fixed instance id (optional; unset ⇒ a fresh random id per start, the oci law). */
  instanceId?: string | undefined;
  /** SQLite link-ledger path. */
  statePath: string;
  /** The static fleet allowlist (parsed from OFAC_RULES_FILE). */
  rules: FleetRules;
  logLevel: "debug" | "info" | "warn" | "error";
}

const RULE_KEYS: Record<string, true> = {
  taskDefinition: true,
  cluster: true,
  subnets: true,
  envAllowlist: true,
};

/** Spec env name law (mirrors the provision.start schema's env propertyNames). */
const ENV_NAME = /^[A-Z][A-Z0-9_]{0,63}$/;

function fail(key: string, message: string): never {
  throw new LauncherConfigError(key, message);
}

function str(env: Record<string, string | undefined>, key: string): string {
  const v = env[key];
  if (v === undefined || v === "") fail(key, "required and unset");
  return v;
}

function nonEmptyString(value: unknown, key: string): string {
  if (typeof value !== "string" || value === "") {
    fail(key, "must be a non-empty string");
  }
  return value;
}

/** Parse + validate the static fleet rules JSON. Unknown keys are rejected by
 *  name (the connector's config law); every error names its workload + key. */
export function parseRules(jsonText: string): FleetRules {
  let parsed: unknown;
  try {
    parsed = JSON.parse(jsonText);
  } catch {
    fail("OFAC_RULES_FILE", "rules file is not valid JSON");
  }
  if (typeof parsed !== "object" || parsed === null || Array.isArray(parsed)) {
    fail("OFAC_RULES_FILE", "rules file must be a workloadId-keyed mapping");
  }

  const rules: Record<string, FleetRule> = {};
  for (const [workloadId, raw] of Object.entries(parsed as Record<string, unknown>)) {
    const where = `rules.${workloadId}`;
    if (typeof raw !== "object" || raw === null || Array.isArray(raw)) {
      fail(where, "must be a rule object");
    }
    const rule = raw as Record<string, unknown>;
    for (const key of Object.keys(rule)) {
      if (!(key in RULE_KEYS)) fail(`${where}.${key}`, "unknown key");
    }
    rules[workloadId] = {
      taskDefinition: nonEmptyString(rule["taskDefinition"], `${where}.taskDefinition`),
      cluster: nonEmptyString(rule["cluster"], `${where}.cluster`),
      subnets: stringList(rule["subnets"], `${where}.subnets`),
      ...(rule["envAllowlist"] === undefined
        ? {}
        : { envAllowlist: envNameList(rule["envAllowlist"], `${where}.envAllowlist`) }),
    };
  }
  return rules;
}

function stringList(value: unknown, key: string): string[] {
  if (value === undefined) return [];
  if (!Array.isArray(value) || value.some((v) => typeof v !== "string" || v === "")) {
    fail(key, "must be an array of non-empty strings");
  }
  return value as string[];
}

function envNameList(value: unknown, key: string): string[] {
  if (!Array.isArray(value) || value.some((v) => typeof v !== "string" || !ENV_NAME.test(v))) {
    fail(key, "must be an array of env names matching ^[A-Z][A-Z0-9_]{0,63}$");
  }
  return value as string[];
}

/** Env-only config parse; `readFile` is the injectable fs seam (tests read a
 *  fixture string instead of the path OFAC_RULES_FILE names). */
export function parseLauncherConfig(
  env: Record<string, string | undefined>,
  readFile: (path: string) => string = (path) => readFileSync(path, "utf8"),
): LauncherConfig {
  const kernelUrl = str(env, "OFAC_KERNEL_URL");
  const enrollToken = str(env, "OFAC_ENROLL_TOKEN");
  const endpointId = str(env, "OFAC_ENDPOINT_ID");
  const team = str(env, "OFAC_TEAM");
  const rulesFile = str(env, "OFAC_RULES_FILE");

  const instanceId = env["OFAC_INSTANCE_ID"];
  if (
    instanceId !== undefined &&
    instanceId !== "" &&
    !/^[A-Za-z0-9._-]{1,128}$/.test(instanceId)
  ) {
    fail("OFAC_INSTANCE_ID", "must match ^[A-Za-z0-9._-]{1,128}$");
  }

  const logLevel = env["OFAC_ADAPTER_LOG"] ?? "info";
  if (logLevel !== "debug" && logLevel !== "info" && logLevel !== "warn" && logLevel !== "error") {
    fail("OFAC_ADAPTER_LOG", `unknown level '${logLevel}'`);
  }

  let rulesText: string;
  try {
    rulesText = readFile(rulesFile);
  } catch {
    fail("OFAC_RULES_FILE", `unreadable file ${rulesFile}`);
  }

  return {
    kernelUrl,
    enrollToken,
    endpointId,
    team,
    ...(instanceId === undefined || instanceId === "" ? {} : { instanceId }),
    statePath: env["OFAC_STATE_PATH"] ?? "/var/lib/ofac/fleet-launcher.sqlite3",
    rules: parseRules(rulesText),
    logLevel,
  };
}
