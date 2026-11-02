// forgejo-connector run --config <connector.yaml> — the child binary:
// config → ledger → ops → createAdapter (SDK lifecycle, connectChild to the
// Gateway's childTransport socket) → ready line → park until signal.
//
// Exit codes (the worker idiom): 0 clean · 1 runtime failure · 2 config
// error · 3 register-denied-then-exhausted (restartBackoffMs: []).
import { parseArgs } from "node:util";
import { createConsoleLogger } from "@ofac/workspace-link";
import { createAdapter } from "@ofac/adapter";
import {
  ConnectorConfigError,
  connectorManifest,
  loadConnectorConfigFile,
  type ConnectorConfig,
} from "../config.js";
import { ActionLedger } from "../ledger.js";
import { createOps } from "../ops.js";
import { createConnectorHooks } from "../hooks.js";
import { ForgejoClient } from "@ofac/forgejo-adapter";

const USAGE = `forgejo-connector run --config <path.to/connector.yaml>

The connector attaches to its team Gateway over the child wire (unix socket
or 127.0.0.1 TCP), registers the flag-filtered forge op manifest, and
executes privileged forge ops (PRs, comments, branches, commit statuses) for
every assignment the Gateway routes. Env overlay: OFAC_CHILD_SECRET wins
over the file's token; FORGEJO_URL + FORGEJO_TOKEN|FORGEJO_TOKEN_FILE name
the forge + bot credential; FORGEJO_ENABLE_ISSUES/VCS/CI gate the advertised
domains; OFAC_ADAPTER_LOG sets the log level.
Exit codes: 0 clean, 1 runtime, 2 config error, 3 register denied (auto-restart off).`;

export interface ConnectorHandle {
  ready: Promise<void>;
  stop(): Promise<void>;
}

export async function bootConnector(config: ConnectorConfig): Promise<ConnectorHandle> {
  const log = createConsoleLogger({
    level: (["debug", "info", "warn", "error"].includes(config.logLevel)
      ? config.logLevel
      : "info") as "debug" | "info" | "warn" | "error",
  });
  const ledger = new ActionLedger(config.statePath);
  const api = new ForgejoClient(config.forgejoUrl, config.forgejoToken);
  const handle = await createAdapter({
    childWorkspaceId: config.childWorkspaceId,
    ...(config.token !== undefined ? { token: config.token } : {}),
    kernel: { baseUrl: config.kernelUrl },
    manifest: connectorManifest(config.team, config.flags),
    transport:
      config.transport.socketPath !== undefined
        ? { socketPath: config.transport.socketPath }
        : { host: config.transport.host ?? "127.0.0.1", port: config.transport.port ?? 0 },
    descriptor: {
      invocation: "sdk",
      supports: { steer: false, interrupt: false, resume: "restart-with-context" },
    },
    hooks: createConnectorHooks({
      ops: createOps({ api, ledger }),
      logger: log,
      childWorkspaceId: config.childWorkspaceId,
    }),
    heartbeatSeconds: config.heartbeatSeconds,
    restartBackoffMs: config.restartBackoffMs,
    logger: log,
  });
  return {
    ready: handle.ready().then(() => {
      const endpoint =
        config.transport.socketPath !== undefined
          ? `socket=${config.transport.socketPath}`
          : `tcp=${config.transport.host ?? "127.0.0.1"}:${String(config.transport.port ?? 0)}`;
      process.stdout.write(`connector ready childId=${config.childWorkspaceId} ${endpoint}\n`);
    }),
    stop: async () => {
      await handle.close();
      ledger.close();
    },
  };
}

export async function run(
  args: readonly string[],
  env: NodeJS.ProcessEnv = process.env,
): Promise<number> {
  let positionals: string[];
  let configPath: string | undefined;
  let help: boolean;
  try {
    const parsed = parseArgs({
      args: [...args],
      allowPositionals: true,
      options: {
        config: { type: "string" },
        help: { type: "boolean", default: false },
      },
    });
    positionals = parsed.positionals;
    configPath = parsed.values["config"];
    help = parsed.values["help"] ?? false;
  } catch (error) {
    process.stderr.write(`connector config error: ${(error as Error).message}\n\n${USAGE}\n`);
    return 2;
  }

  if (help) {
    process.stdout.write(`${USAGE}\n`);
    return 0;
  }

  if (positionals[0] !== "run") {
    process.stderr.write(`unknown invocation '${positionals[0] ?? ""}'\n\n${USAGE}\n`);
    return 2;
  }

  configPath = configPath ?? env["CONNECTOR_CONFIG"];
  if (configPath === undefined) {
    process.stderr.write(`missing --config (or CONNECTOR_CONFIG)\n\n${USAGE}\n`);
    return 2;
  }

  let config: ConnectorConfig;
  try {
    config = loadConnectorConfigFile(configPath, env as Record<string, string | undefined>);
  } catch (error) {
    if (error instanceof ConnectorConfigError) {
      process.stderr.write(`connector config error: ${error.message}\n`);
      return 2;
    }

    throw error;
  }

  let connector: ConnectorHandle;
  try {
    connector = await bootConnector(config);
    await connector.ready;
  } catch (error) {
    if (error instanceof Error && error.message.startsWith("register denied")) {
      process.stderr.write(`connector: ${error.message}\n`);
      return 3;
    }

    process.stderr.write(`connector runtime error: ${(error as Error).message}\n`);
    return 1;
  }

  // Park until signal (clean 0) — the adapter owns reconnect honesty.
  const parked = Promise.withResolvers<number>();
  const stop = (): void => {
    void connector.stop().then(
      () => parked.resolve(0),
      () => parked.resolve(1),
    );
  };

  process.on("SIGINT", stop);
  process.on("SIGTERM", stop);
  return await parked.promise;
}

// Only execute when invoked as a program (importable for tests).
if (
  process.argv[1] !== undefined &&
  import.meta.url === new URL(`file://${process.argv[1]}`).href
) {
  void run(process.argv.slice(2)).then((code) => {
    process.exitCode = code;
  });
}
