// Gateway config: `.facilitator/gateway.yaml` (spec §8.4 layout) parsed +
// validated field-by-field. The spec is silent on the key set, so THIS file
// is the lock. Config v2 (Plan 06): the deployment `mode`
// (embedded | shared | standalone, spec §9.1), the `children:` fleet block
// and the `childTransport:` block; the 05 embedded path (`child:`) stays
// exactly as shipped — a v1 file (no `mode`) loads as `mode: "embedded"`.
// Unknown top-level keys are config errors — fail loud on typos rather than
// silently ignoring an identity or credential.
import { existsSync, readFileSync } from "node:fs";
import { dirname, join, resolve } from "node:path";
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

/** §9.1 deployment mode; one binary, mode comes from config, not a flag. */
export type GatewayMode = "embedded" | "shared" | "standalone";

/** One entry of the shared/standalone `children:` fleet block. */
export interface ChildEntryConfig {
  /** childWorkspaceId = "<gatewayId>/<name>". */
  name: string;
  /** Expected hello.token; the OFAC_CHILD_SECRET env overlay wins. */
  token?: string | undefined;
  /** Per-child fan-out quota (Gateway-local); int >= 1. */
  maxInFlight: number;
  /** Reply/event timeout before transport-dead; 1..86400. */
  timeoutSeconds: number;
}

/** Local child transport (unix socket primary, localhost TCP same-protocol fallback). */
export interface ChildTransportConfig {
  socket: {
    /** Resolved against stateDir: resolve(stateDir, configuredPath). */
    path: string;
    /** Optional SO_PEERCRED uid pin. */
    allowedUid?: number | undefined;
  };
  /** Optional 127.0.0.1 fallback listener carrying the identical frames. */
  tcp?: { port: number } | undefined;
  /** hello deadline. */
  handshakeTimeoutSeconds: number;
  /** Heartbeat cadences before a child connection is marked offline. */
  leaseMissedBeats: number;
}

export interface GatewayConfig {
  apiVersion: typeof GATEWAY_API_VERSION;
  kind: "Gateway";
  /** Link endpointId — the `gw.<slug>` namespace is a client-side convention. */
  gatewayId: string;
  /** ownerTeam; the kernel answers 403 team-mismatch on a wrong slug. */
  team: string;
  /** Workload role sent at enrollment (plan 10); a `gw.` endpoint must be gateway (kernel 403 role-mismatch). */
  role?: "gateway" | "child" | undefined;
  /** Present ⇒ mode-(b) enrollment: mint an OIDC client-credentials JWT instead of using the enrollment secret. */
  workload?:
    | {
        tokenUrl: string;
        clientId: string;
        clientSecret: string;
      }
    | undefined;
  facilitatorUrl: string;
  /** Present ⇒ the Gateway enrolls itself and rotates on 401. */
  enrollmentSecret?: string | undefined;
  /** Pre-minted token; refresh only rotates when a secret is ALSO set. */
  linkToken?: string | undefined;
  trustMode: TrustMode;
  /** Ledger + token live here; created 0o700 when missing. */
  stateDir: string;
  /** Deployment mode; absent in the file ⇒ "embedded" (v1 back-compat). */
  mode: GatewayMode;
  /** Present iff embedded — 05's single in-process child block. */
  child?:
    | {
        name: string;
        mode: ChildMode;
        /** Optional YAML `{capabilities: [...]}` overriding the builtin manifest. */
        manifestFile?: string | undefined;
      }
    | undefined;
  /** Present iff shared|standalone — the fleet (shared: non-empty). */
  children?: ChildEntryConfig[] | undefined;
  /** Present iff shared|standalone — the local child transport. */
  childTransport?: ChildTransportConfig | undefined;
  /** Env overlay: OFAC_CHILD_SECRET (same law as OFAC_ENROLL_SECRET). */
  childSecret?: string | undefined;
  diagnostics: {
    /** 0 ⇒ diagnostics disabled; >0 binds 127.0.0.1:port (fail-fast on clash). */
    port: number;
  };
  /** Team policy source (Plan 12); absent ⇒ DefaultPolicy fallback + boot warning. */
  policy?:
    | {
        /** YAML text of the policy document. */
        inline?: string | undefined;
        /** Absolute path; a relative one resolved against the config file's dir. */
        path?: string | undefined;
        /** Approval expiry for confirm holds; default 900 s. */
        confirmTimeoutSeconds?: number | undefined;
      }
    | undefined;
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
  /** Env overlay (OFAC_ENROLL_SECRET / OFAC_LINK_TOKEN / OFAC_CHILD_SECRET win over the file). */
  env?: Record<string, string | undefined>;
  /** Test seam for the stored-token escape hatch (default: fs existence). */
  hasStoredToken?: (stateDir: string) => boolean;
  /** Dir `policy.path` resolves against — loadConfigFile passes dirname(config). */
  configDir?: string | undefined;
}

const GATEWAY_ID_RE = /^gw\.[a-z0-9]([a-z0-9-]*[a-z0-9])?$/;
const CHILD_NAME_RE = /^[a-z0-9]([a-z0-9-]*[a-z0-9])?$/;
const CHILD_NAME_MAX = 128;
const GATEWAY_MODES: readonly GatewayMode[] = ["embedded", "shared", "standalone"];
const TOP_LEVEL_KEYS: Record<string, true> = {
  apiVersion: true,
  kind: true,
  gatewayId: true,
  role: true,
  workload: true,
  team: true,
  facilitatorUrl: true,
  enrollmentSecret: true,
  linkToken: true,
  trustMode: true,
  stateDir: true,
  mode: true,
  child: true,
  children: true,
  childTransport: true,
  diagnostics: true,
  heartbeatSeconds: true,
  policy: true,
};
const CHILD_KEYS: Record<string, true> = { name: true, mode: true, manifestFile: true };
/** Builtin-only keys (`mode`, `manifestFile`) are embedded-only, NOT fleet keys. */
const CHILD_ENTRY_KEYS: Record<string, true> = {
  name: true,
  token: true,
  maxInFlight: true,
  timeoutSeconds: true,
};
const CHILD_TRANSPORT_KEYS: Record<string, true> = {
  socket: true,
  tcp: true,
  handshakeTimeoutSeconds: true,
  leaseMissedBeats: true,
};
const CHILD_SOCKET_KEYS: Record<string, true> = { path: true, allowedUid: true };
const CHILD_TCP_KEYS: Record<string, true> = { port: true };
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

function optionalInt(
  doc: Record<string, unknown>,
  key: string,
  min: number,
  max: number,
  fallback: number,
  label = key,
): number {
  return doc[key] === undefined ? fallback : requireInt(doc, key, min, max, label);
}

/** `children:` fleet block; every error names `children[i]` or `children[i].<key>`. */
function parseChildren(list: readonly unknown[]): ChildEntryConfig[] {
  const seen = new Set<string>();
  return list.map((raw, index): ChildEntryConfig => {
    const where = `children[${index}]`;
    const entry = asRecord(raw, where);
    rejectUnknown(entry, CHILD_ENTRY_KEYS, where);
    const name = requireString(entry, "name", `${where}.name`);
    if (!CHILD_NAME_RE.test(name) || name.length > CHILD_NAME_MAX) {
      fail(
        `${where}.name`,
        `must match ^[a-z0-9]([a-z0-9-]*[a-z0-9])?$ and be at most ${CHILD_NAME_MAX} characters`,
      );
    }
    if (seen.has(name)) fail(`${where}.name`, `duplicate child name '${name}'`);
    seen.add(name);
    const token = optionalString(entry, "token", `${where}.token`);
    return {
      name,
      ...(token !== undefined ? { token } : {}),
      maxInFlight: optionalInt(entry, "maxInFlight", 1, 2147483647, 1, `${where}.maxInFlight`),
      timeoutSeconds: optionalInt(
        entry,
        "timeoutSeconds",
        1,
        86400,
        120,
        `${where}.timeoutSeconds`,
      ),
    };
  });
}

/** `childTransport:` block; socket path is resolved relative to stateDir. */
function parseChildTransport(doc: Record<string, unknown>, stateDir: string): ChildTransportConfig {
  rejectUnknown(doc, CHILD_TRANSPORT_KEYS, "childTransport");
  if (doc["socket"] === undefined) fail("childTransport.socket", "required");
  const socketDoc = asRecord(doc["socket"], "childTransport.socket");
  rejectUnknown(socketDoc, CHILD_SOCKET_KEYS, "childTransport.socket");
  const path = requireString(socketDoc, "path", "childTransport.socket.path");
  const allowedUidRaw = socketDoc["allowedUid"];
  let allowedUid: number | undefined;
  if (allowedUidRaw !== undefined && allowedUidRaw !== null) {
    if (
      typeof allowedUidRaw !== "number" ||
      !Number.isInteger(allowedUidRaw) ||
      allowedUidRaw < 0
    ) {
      fail("childTransport.socket.allowedUid", "must be an integer >= 0");
    }
    allowedUid = allowedUidRaw;
  }
  let tcp: { port: number } | undefined;
  if (doc["tcp"] !== undefined) {
    const tcpDoc = asRecord(doc["tcp"], "childTransport.tcp");
    rejectUnknown(tcpDoc, CHILD_TCP_KEYS, "childTransport.tcp");
    tcp = { port: requireInt(tcpDoc, "port", 1, 65535, "childTransport.tcp.port") };
  }
  return {
    socket: {
      path: resolve(stateDir, path),
      ...(allowedUid !== undefined ? { allowedUid } : {}),
    },
    ...(tcp !== undefined ? { tcp } : {}),
    handshakeTimeoutSeconds: optionalInt(
      doc,
      "handshakeTimeoutSeconds",
      1,
      3600,
      5,
      "childTransport.handshakeTimeoutSeconds",
    ),
    leaseMissedBeats: optionalInt(
      doc,
      "leaseMissedBeats",
      1,
      100,
      2,
      "childTransport.leaseMissedBeats",
    ),
  };
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
  // Env overlay: the deployment (kernel) origin rides OFAC_KERNEL_URL when
  // the operator injects it (the elastic worker's baked config names the
  // single-replica service; the replica overlay repoints every leg at the
  // LB via this env — a pin to one replica dies with it).
  const facilitatorUrl = env["OFAC_KERNEL_URL"] ?? requireString(root, "facilitatorUrl");
  if (!/^https?:\/\/\S+$/.test(facilitatorUrl)) {
    fail("facilitatorUrl", "must be an http(s) URL");
  }

  const trustMode = requireString(root, "trustMode");
  if (!(TRUST_MODES as readonly string[]).includes(trustMode)) {
    fail("trustMode", `must be one of ${TRUST_MODES.join(", ")}`);
  }

  // Workload role (plan 10): gateway|child; absent ⇒ the callsite defaults
  // to gateway (the gw. prefix law makes child meaningless here anyway).
  let role: "gateway" | "child" | undefined;
  if (root["role"] !== undefined) {
    const rawRole = requireString(root, "role");
    if (rawRole !== "gateway" && rawRole !== "child") {
      fail("role", "must be 'gateway' or 'child'");
    }
    role = rawRole;
  }

  // Mode-(b) workload block: env overlay wins per-key, same law as
  // OFAC_ENROLL_SECRET (secrets never weaker in env than on disk).
  let workload: GatewayConfig["workload"];
  if (root["workload"] !== undefined) {
    const doc = asRecord(root["workload"], "workload");
    rejectUnknown(doc, { tokenUrl: true, clientId: true, clientSecret: true }, "workload");
    workload = {
      tokenUrl: env["OFAC_OIDC_TOKEN_URL"] ?? requireString(doc, "tokenUrl", "workload.tokenUrl"),
      clientId: env["OFAC_OIDC_CLIENT_ID"] ?? requireString(doc, "clientId", "workload.clientId"),
      clientSecret:
        env["OFAC_OIDC_CLIENT_SECRET"] ??
        requireString(doc, "clientSecret", "workload.clientSecret"),
    };
  } else if (
    env["OFAC_OIDC_TOKEN_URL"] !== undefined ||
    env["OFAC_OIDC_CLIENT_ID"] !== undefined ||
    env["OFAC_OIDC_CLIENT_SECRET"] !== undefined
  ) {
    // Env-only mode-(b): the deployment supplies the whole triple.
    for (const key of ["OFAC_OIDC_TOKEN_URL", "OFAC_OIDC_CLIENT_ID", "OFAC_OIDC_CLIENT_SECRET"]) {
      if (env[key] === undefined) fail("workload", `${key} set without the full OIDC triple`);
    }
    workload = {
      tokenUrl: env["OFAC_OIDC_TOKEN_URL"] as string,
      clientId: env["OFAC_OIDC_CLIENT_ID"] as string,
      clientSecret: env["OFAC_OIDC_CLIENT_SECRET"] as string,
    };
  }

  // Team policy block (Plan 12): exactly one of inline/path; the key present
  // with neither source is a typo, not a fallback.
  let policy: GatewayConfig["policy"];
  if (root["policy"] !== undefined) {
    const doc = asRecord(root["policy"], "policy");
    rejectUnknown(doc, { inline: true, path: true, confirmTimeoutSeconds: true }, "policy");
    const inline = optionalString(doc, "inline", "policy.inline");
    const path = optionalString(doc, "path", "policy.path");
    if ((inline === undefined) === (path === undefined)) {
      fail("policy", "exactly one of inline/path must be set");
    }
    const confirmTimeoutSeconds = optionalInt(
      doc,
      "confirmTimeoutSeconds",
      60,
      86_400,
      900,
      "policy.confirmTimeoutSeconds",
    );
    policy = {
      ...(inline !== undefined ? { inline } : {}),
      ...(path !== undefined ? { path: resolve(options.configDir ?? process.cwd(), path) } : {}),
      confirmTimeoutSeconds,
    };
  }

  const stateDir = resolve(requireString(root, "stateDir"));

  // Deployment mode: absent ⇒ "embedded" — the v1 back-compat law (05 sample
  // files carry no `mode` and must keep parsing).
  let mode: GatewayMode = "embedded";
  if (root["mode"] !== undefined) {
    const rawMode = requireString(root, "mode");
    if (!GATEWAY_MODES.includes(rawMode as GatewayMode)) {
      fail("mode", `must be one of ${GATEWAY_MODES.join(", ")}`);
    }
    mode = rawMode as GatewayMode;
  }
  // trustMode honesty (appendix): an in-process embedded child has no
  // isolation boundary to claim.
  if (mode === "embedded" && trustMode === "isolated-executor") {
    fail("trustMode", "embedded gateway cannot claim isolated-executor");
  }

  const hasChild = root["child"] !== undefined;
  const hasChildren = root["children"] !== undefined;
  const hasTransport = root["childTransport"] !== undefined;
  // Exactly ONE of `child:` / `children:` — the mode picks the block.
  if (hasChild && hasChildren) {
    fail("child", "cannot use 'child' together with 'children'");
  }

  let child: GatewayConfig["child"];
  let children: ChildEntryConfig[] | undefined;
  let childTransport: ChildTransportConfig | undefined;

  if (mode === "embedded") {
    if (hasChildren) fail("children", "not allowed when mode is 'embedded'");
    // embedded = 05 exactly: the in-process child needs no transport.
    if (hasTransport) fail("childTransport", "not allowed when mode is 'embedded'");
    if (!hasChild) fail("child", "required when mode is 'embedded'");
    const childDoc = asRecord(root["child"], "child");
    rejectUnknown(childDoc, CHILD_KEYS, "child");
    const childName = requireString(childDoc, "name", "child.name");
    const childMode = requireString(childDoc, "mode", "child.mode");
    if (childMode !== "echo" && childMode !== "stub")
      fail("child.mode", "must be 'echo' or 'stub'");
    const manifestFile = optionalString(childDoc, "manifestFile", "child.manifestFile");
    child = {
      name: childName,
      mode: childMode as ChildMode,
      ...(manifestFile !== undefined ? { manifestFile } : {}),
    };
  } else {
    if (hasChild) fail("child", `not allowed when mode is '${mode}'`);
    if (hasChildren) {
      const list = root["children"];
      if (!Array.isArray(list)) fail("children", "must be a sequence");
      children = parseChildren(list);
      // standalone may boot with zero children (spec S:330); shared may not.
      if (mode === "shared" && children.length === 0) {
        fail("children", "must be non-empty when mode is 'shared'");
      }
    } else if (mode === "shared") {
      fail("children", "required (non-empty) when mode is 'shared'");
    }
    if (!hasTransport) fail("childTransport", `required when mode is '${mode}'`);
    childTransport = parseChildTransport(
      asRecord(root["childTransport"], "childTransport"),
      stateDir,
    );
  }

  const diagDoc = asRecord(root["diagnostics"] ?? { port: 0 }, "diagnostics");
  rejectUnknown(diagDoc, DIAGNOSTICS_KEYS, "diagnostics");
  const port =
    diagDoc["port"] === undefined ? 0 : requireInt(diagDoc, "port", 0, 65535, "diagnostics.port");

  const heartbeatSeconds =
    root["heartbeatSeconds"] === undefined ? 30 : requireInt(root, "heartbeatSeconds", 1, 3600);

  // Env overlay: the env is the deployment-time truth (ps-visible argv/file
  // is the weaker carrier), same law as linkd's OFAC_* handling.
  const enrollmentSecret = env["OFAC_ENROLL_SECRET"] ?? optionalString(root, "enrollmentSecret");
  const linkToken = env["OFAC_LINK_TOKEN"] ?? optionalString(root, "linkToken");
  const childSecret = env["OFAC_CHILD_SECRET"];
  const hasStoredToken =
    options.hasStoredToken ?? ((dir: string) => existsSync(tokenFilePath(dir)));
  if (
    linkToken === undefined &&
    enrollmentSecret === undefined &&
    workload === undefined &&
    !hasStoredToken(stateDir)
  ) {
    fail(
      "enrollmentSecret",
      `neither linkToken/OFAC_LINK_TOKEN nor enrollmentSecret/OFAC_ENROLL_SECRET is set and no token file exists in '${stateDir}'`,
    );
  }

  return {
    apiVersion: GATEWAY_API_VERSION,
    kind: "Gateway",
    gatewayId,
    ...(role !== undefined ? { role } : {}),
    ...(workload !== undefined ? { workload } : {}),
    team,
    facilitatorUrl,
    ...(enrollmentSecret !== undefined ? { enrollmentSecret } : {}),
    ...(linkToken !== undefined ? { linkToken } : {}),
    trustMode: trustMode as TrustMode,
    stateDir,
    mode,
    ...(child !== undefined ? { child } : {}),
    ...(children !== undefined ? { children } : {}),
    ...(childTransport !== undefined ? { childTransport } : {}),
    ...(childSecret !== undefined ? { childSecret } : {}),
    diagnostics: { port },
    ...(policy !== undefined ? { policy } : {}),
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
  return parseConfig(text, { configDir: dirname(resolve(path)), ...options });
}
