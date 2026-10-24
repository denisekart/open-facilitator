// The durable linkd configuration: argv parsing + the env fallback for the
// enrollment secret (argv secrets are visible in `ps`; the env var is not).
// `bin/linkd.ts` and the SDK's public surface both consume this.
import { parseArgs } from "node:util";
import type { TrustMode } from "@ofac/protocol";

export interface LinkdConfig {
  url: string;
  token?: string;
  secret?: string;
  endpointId: string;
  team: string;
  manifestPath: string;
  trustMode: TrustMode;
  transport: "signalr" | "http-poll";
  statePath: string;
  mode: "echo" | "stub";
}

export class ConfigError extends Error {}

/**
 * Parse linkd CLI argv. `env` (default `process.env`) is consulted BEFORE
 * `--secret`: OFAC_ENROLL_SECRET keeps the enrollment secret out of the
 * process argument list.
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
      token: { type: "string" },
      secret: { type: "string" },
      endpoint: { type: "string" },
      team: { type: "string" },
      manifest: { type: "string" },
      "trust-mode": { type: "string", default: "isolated-executor" },
      transport: { type: "string", default: "signalr" },
      state: { type: "string" },
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
  if (values.token === undefined && secret === undefined) missing("token | secret");
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
  return {
    url,
    ...(values.token !== undefined ? { token: values.token } : {}),
    ...(secret !== undefined ? { secret } : {}),
    endpointId: values.endpoint ?? missing("endpoint"),
    team: values.team ?? missing("team"),
    manifestPath: values.manifest ?? missing("manifest"),
    trustMode,
    transport,
    statePath: values.state ?? "ofac-linkd.sqlite3",
    mode,
  };
}
