// The durable linkd configuration: argv parsing + the env fallback for the
// enrollment CODE (argv secrets are visible in `ps`; the env var is not).
// `bin/linkd.ts` and the SDK's public surface both consume this.
//
// The state dir is a required concept: the identity key lives in it, and the
// keystore law says the state dir IS the logical instance (no backup, migration
// is `mv`, loss is a rejoin). The delivery ledger lives inside it as sqlite.
import { parseArgs } from "node:util";
import { join } from "node:path";
import type { TrustMode } from "@ofac/protocol";

export interface LinkdConfig {
  url: string;
  /** First-boot-only enrollment code (OFAC_ENROLL_SECRET or --secret). Not a credential. */
  secret?: string;
  endpointId: string;
  team: string;
  manifestPath: string;
  trustMode: TrustMode;
  /** Workload role for enrollment; linkd is a standalone child workspace by default. */
  role: "gateway" | "child";
  transport: "signalr" | "http-poll";
  /** Where identity.key + the delivery ledger live (default ./ofac-link-state). */
  stateDir: string;
  /** The sqlite ledger path — always inside `stateDir`. */
  statePath: string;
  mode: "echo" | "stub";
}

export class ConfigError extends Error {}

/**
 * Parse linkd CLI argv. `env` (default `process.env`) is consulted BEFORE
 * `--secret`: OFAC_ENROLL_SECRET keeps the enrollment code out of the
 * process argument list. There is no token option anymore — the bearer is a
 * 5-minute session JWT the client mints itself from the key in `--state-dir`.
 */
export function parseArgv(
  argv: readonly string[],
  env: Record<string, string | undefined> = process.env,
): LinkdConfig {
  const { values, positionals } = parseArgs({
    args: argv,
    allowPositionals: true,
    options: {
      url: { type: "string" },
      secret: { type: "string" },
      endpoint: { type: "string" },
      team: { type: "string" },
      manifest: { type: "string" },
      "trust-mode": { type: "string", default: "isolated-executor" },
      role: { type: "string", default: "child" },
      transport: { type: "string", default: "signalr" },
      "state-dir": { type: "string" },
      mode: { type: "string", default: "echo" },
      help: { type: "boolean", short: "h" },
    },
  });
  if (values.help || positionals[0] === "--help") {
    throw new ConfigError("help");
  }
  if (positionals[0] !== "run") {
    throw new ConfigError(`first positional must be 'run', got '${positionals[0] ?? ""}'`);
  }
  const missing = (name: string): never => {
    throw new ConfigError(`missing --${name}`);
  };
  const url = values.url ?? missing("url");
  const secret = env["OFAC_ENROLL_SECRET"] ?? values.secret;
  const trustMode = values["trust-mode"] as TrustMode;
  if (!["shared-runtime", "separate-process-same-host", "isolated-executor"].includes(trustMode)) {
    throw new ConfigError(`unknown --trust-mode '${trustMode}'`);
  }
  const transport = values.transport as LinkdConfig["transport"];
  if (!["signalr", "http-poll"].includes(transport)) {
    throw new ConfigError(`unknown --transport '${transport}'`);
  }
  const mode = values.mode as LinkdConfig["mode"];
  if (!["echo", "stub"].includes(mode)) throw new ConfigError(`unknown --mode '${mode}'`);
  const role = values.role as LinkdConfig["role"];
  if (!["gateway", "child"].includes(role)) throw new ConfigError(`unknown --role '${role}'`);
  const stateDir = values["state-dir"] ?? env["OFAC_LINK_STATE_DIR"] ?? "./ofac-link-state";
  return {
    url,
    ...(secret !== undefined ? { secret } : {}),
    endpointId: values.endpoint ?? missing("endpoint"),
    team: values.team ?? missing("team"),
    manifestPath: values.manifest ?? missing("manifest"),
    trustMode,
    role,
    transport,
    stateDir,
    statePath: join(stateDir, "ledger.sqlite3"),
    mode,
  };
}
