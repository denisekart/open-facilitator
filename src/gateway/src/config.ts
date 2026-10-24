// Gateway config: `.facilitator/gateway.yaml` (spec §8.4 layout) parsed +
// validated field-by-field. The spec is silent on the key set, so THIS file
// is the lock (Plan 06 extends it with a `children:` block, at which point a
// YAML-schema file may replace the hand-rolled validator). Unknown top-level
// keys are config errors — fail loud on typos rather than silently ignoring
// an identity or credential.
import { existsSync, readFileSync } from "node:fs";
import { join, resolve } from "node:path";
import { parse } from "yaml";

export const GATEWAY_API_VERSION = "facilitator.framework/v1";

/** Server whitelist (enroll-request.v1.json); embedded reality is shared-runtime. */
export const TRUST_MODES = [
  "shared-runtime",
  "separate-process-same-host",
  "isolated-executor",
] as const;
export type TrustMode = (typeof TRUST_MODES)[number];

export type ChildMode = "echo" | "stub";

export interface GatewayConfig {
  apiVersion: typeof GATEWAY_API_VERSION;
  kind: "Gateway";
  /** Link endpointId — the `gw.<slug>` namespace is a client-side convention. */
  gatewayId: string;
  /** ownerTeam; the kernel answers 403 team-mismatch on a wrong slug. */
  team: string;
  facilitatorUrl: string;
  /** Present ⇒ the Gateway enrolls itself and rotates on 401. */
  enrollmentSecret?: string | undefined;
  /** Pre-minted token; refresh only rotates when a secret is ALSO set. */
  linkToken?: string | undefined;
  trustMode: TrustMode;
  /** Ledger + token live here; created 0o700 when missing. */
  stateDir: string;
  child: {
    name: string;
    mode: ChildMode;
    /** Optional YAML `{capabilities: [...]}` overriding the builtin manifest. */
    manifestFile?: string | undefined;
  };
  diagnostics: {
    /** 0 ⇒ diagnostics disabled; >0 binds 127.0.0.1:port (fail-fast on clash). */
    port: number;
  };
  /** Business workspace.heartbeat cadence (transport heartbeat is the SDK's). */
  heartbeatSeconds: number;
}

export class GatewayConfigError extends Error {
  constructor(key: string, message: string) {
    super(`${key}: ${message}`);
    this.name = "GatewayConfigError";
  }
}

export interface ParseConfigOptions {
  /** Env overlay (OFAC_ENROLL_SECRET / OFAC_LINK_TOKEN win over the file). */
  env?: Record<string, string | undefined>;
  /** Test seam for the stored-token escape hatch (default: fs existence). */
  hasStoredToken?: (stateDir: string) => boolean;
}

const GATEWAY_ID_RE = /^gw\.[a-z0-9]([a-z0-9-]*[a-z0-9])?$/;
const TOP_LEVEL_KEYS: Record<string, true> = {
  apiVersion: true,
  kind: true,
  gatewayId: true,
  team: true,
  facilitatorUrl: true,
  enrollmentSecret: true,
  linkToken: true,
  trustMode: true,
  stateDir: true,
  child: true,
  diagnostics: true,
  heartbeatSeconds: true,
};
const CHILD_KEYS: Record<string, true> = { name: true, mode: true, manifestFile: true };
const DIAGNOSTICS_KEYS: Record<string, true> = { port: true };

function fail(key: string, message: string): never {
  throw new GatewayConfigError(key, message);
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

function requireString(doc: Record<string, unknown>, key: string, label = key): string {
  const value = doc[key];
  if (typeof value !== "string" || value.length === 0) fail(label, "must be a non-empty string");
  return value;
}

function optionalString(
  doc: Record<string, unknown>,
  key: string,
  label = key,
): string | undefined {
  const value = doc[key];
  if (value === undefined || value === null) return undefined;
  if (typeof value !== "string" || value.length === 0) fail(label, "must be a non-empty string");
  return value;
}

function requireInt(
  doc: Record<string, unknown>,
  key: string,
  min: number,
  max: number,
  label = key,
): number {
  const value = doc[key];
  if (typeof value !== "number" || !Number.isInteger(value) || value < min || value > max) {
    fail(label, `must be an integer in ${min}..${max}`);
  }
  return value;
}

/** `<stateDir>/token` written by enrollment; a restart may mount it without a secret. */
export function tokenFilePath(stateDir: string): string {
  return join(stateDir, "token");
}

/** Parse + validate the YAML text; every failure names the offending key. */
export function parseConfig(yamlText: string, options: ParseConfigOptions = {}): GatewayConfig {
  const env = options.env ?? process.env;
  let doc: unknown;
  try {
    doc = parse(yamlText);
  } catch (error) {
    fail("yaml", `cannot parse: ${error instanceof Error ? error.message : String(error)}`);
  }
  const root = asRecord(doc, "(root)");
  rejectUnknown(root, TOP_LEVEL_KEYS, "(root)");

  const apiVersion = requireString(root, "apiVersion");
  if (apiVersion !== GATEWAY_API_VERSION) {
    fail("apiVersion", `must be '${GATEWAY_API_VERSION}'`);
  }
  const kind = requireString(root, "kind");
  if (kind !== "Gateway") fail("kind", "must be 'Gateway'");

  const gatewayId = requireString(root, "gatewayId");
  if (!GATEWAY_ID_RE.test(gatewayId)) {
    fail("gatewayId", "must match ^gw\\.[a-z0-9]([a-z0-9-]*[a-z0-9])?$");
  }
  const team = requireString(root, "team");
  const facilitatorUrl = requireString(root, "facilitatorUrl");
  if (!/^https?:\/\/\S+$/.test(facilitatorUrl)) {
    fail("facilitatorUrl", "must be an http(s) URL");
  }

  const trustMode = requireString(root, "trustMode");
  if (!(TRUST_MODES as readonly string[]).includes(trustMode)) {
    fail("trustMode", `must be one of ${TRUST_MODES.join(", ")}`);
  }

  const stateDir = resolve(requireString(root, "stateDir"));

  const childDoc = asRecord(root["child"] ?? {}, "child");
  rejectUnknown(childDoc, CHILD_KEYS, "child");
  const childName = requireString(childDoc, "name", "child.name");
  const mode = requireString(childDoc, "mode", "child.mode");
  if (mode !== "echo" && mode !== "stub") fail("child.mode", "must be 'echo' or 'stub'");
  const manifestFile = optionalString(childDoc, "manifestFile", "child.manifestFile");

  const diagDoc = asRecord(root["diagnostics"] ?? { port: 0 }, "diagnostics");
  rejectUnknown(diagDoc, DIAGNOSTICS_KEYS, "diagnostics");
  const port =
    diagDoc["port"] === undefined ? 0 : requireInt(diagDoc, "port", 0, 65535, "diagnostics.port");

  const heartbeatSeconds =
    root["heartbeatSeconds"] === undefined
      ? 30
      : requireInt(root, "heartbeatSeconds", 1, 3600);

  // Env overlay: the env is the deployment-time truth (ps-visible argv/file
  // is the weaker carrier), same law as linkd's OFAC_* handling.
  const enrollmentSecret = env["OFAC_ENROLL_SECRET"] ?? optionalString(root, "enrollmentSecret");
  const linkToken = env["OFAC_LINK_TOKEN"] ?? optionalString(root, "linkToken");
  const hasStoredToken =
    options.hasStoredToken ?? ((dir: string) => existsSync(tokenFilePath(dir)));
  if (linkToken === undefined && enrollmentSecret === undefined && !hasStoredToken(stateDir)) {
    fail(
      "enrollmentSecret",
      `neither linkToken/OFAC_LINK_TOKEN nor enrollmentSecret/OFAC_ENROLL_SECRET is set and no token file exists in '${stateDir}'`,
    );
  }

  return {
    apiVersion: GATEWAY_API_VERSION,
    kind: "Gateway",
    gatewayId,
    team,
    facilitatorUrl,
    ...(enrollmentSecret !== undefined ? { enrollmentSecret } : {}),
    ...(linkToken !== undefined ? { linkToken } : {}),
    trustMode: trustMode as TrustMode,
    stateDir,
    child: {
      name: childName,
      mode: mode as ChildMode,
      ...(manifestFile !== undefined ? { manifestFile } : {}),
    },
    diagnostics: { port },
    heartbeatSeconds,
  };
}

/** Read + parse a config file (CLI entry; tests drive parseConfig directly). */
export function loadConfigFile(path: string, options: ParseConfigOptions = {}): GatewayConfig {
  let text: string;
  try {
    text = readFileSync(path, "utf8");
  } catch {
    fail("config", `cannot read '${path}'`);
  }
  return parseConfig(text, options);
}
