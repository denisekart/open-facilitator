// ofac-gateway — the team Gateway supervisor. `run` boots the runtime:
// config → enroll-or-mount → capabilities → connect → reconcile → serve.
//
// Exit codes (linkd's law): 0 clean · 1 failed-to-reach-ready/shutdown
// failure · 2 config error · 3 protocol mismatch · 4 enrollment rejected.
import { parseArgs } from "node:util";
import { EnrollmentError, ProtocolMismatchError, createConsoleLogger } from "@ofac/workspace-link";
import {
  GatewayConfigError,
  loadConfigFile,
  startDiagnostics,
  type DiagnosticsHandle,
  type GatewayRuntime,
} from "../index.js";
import { createGatewayRuntime } from "../runtime.js";

const USAGE = `ofac-gateway run --config <path.to/gateway.yaml>

Gateway identity/team/facilitator/trustMode/state-dir/child all come from
the YAML file (env OFAC_ENROLL_SECRET / OFAC_LINK_TOKEN override the file's
credentials; GATEWAY_CONFIG supplies the --config path when the flag is
absent). Diagnostics (GET /status, 127.0.0.1) binds when
diagnostics.port > 0.
Exit codes: 0 clean, 2 config error, 3 protocol mismatch, 4 enrollment rejected.`;

export interface RunHandle {
  runtime: GatewayRuntime;
  ready: Promise<void>;
  shutdown(): Promise<void>;
}

export async function run(args: readonly string[]): Promise<number> {
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
    process.stderr.write(`gateway config error: ${(error as Error).message}\n\n${USAGE}\n`);
    return 2;
  }
  if (help) {
    process.stdout.write(`${USAGE}\n`);
    return 0;
  }
  if (positionals[0] !== "run") {
    process.stderr.write(`gateway: first positional must be 'run'\n\n${USAGE}\n`);
    return 2;
  }
  configPath ??= process.env["GATEWAY_CONFIG"];
  if (configPath === undefined) {
    process.stderr.write(
      "gateway config error: --config <path> (or GATEWAY_CONFIG) is required\n\n" + `${USAGE}\n`,
    );
    return 2;
  }

  let handle: RunHandle;
  try {
    const config = loadConfigFile(configPath);
    const logger = createConsoleLogger({
      level: (process.env["OFAC_GATEWAY_LOG"] as "debug" | undefined) ?? "info",
    });
    const runtime = createGatewayRuntime(config, { logger });
    let diagnostics: DiagnosticsHandle | null = null;
    const ready = runtime
      .start()
      .then(async ({ leaseEpoch, children }) => {
        // Diagnostics first: a Gateway that cannot report truth is broken.
        diagnostics = await startDiagnostics(runtime, config.diagnostics.port);
        process.stdout.write(
          `gateway ready endpointId=${config.gatewayId} epoch=${leaseEpoch} children=${children}\n`,
        );
      })
      .catch(async (error: unknown) => {
        await diagnostics?.close().catch(() => undefined);
        throw error;
      });
    handle = {
      runtime,
      ready,
      async shutdown() {
        await diagnostics?.close();
        await runtime.stop();
      },
    };
  } catch (error) {
    return mapStartupError(error);
  }

  try {
    await handle.ready;
  } catch (error) {
    await handle.shutdown().catch(() => undefined);
    return mapStartupError(error);
  }

  // Park until signal (clean 0) or terminal client failure (closed ⇒ 3).
  const signal = Promise.withResolvers<number>();
  const stop = (): void => {
    void handle.shutdown().then(
      () => signal.resolve(0),
      () => signal.resolve(1),
    );
  };
  process.on("SIGINT", stop);
  process.on("SIGTERM", stop);
  const terminal = Promise.withResolvers<number>();
  handle.runtime.linkClient?.on("state", (state) => {
    if (state === "closed") {
      void handle.shutdown().then(
        () => terminal.resolve(3),
        () => terminal.resolve(1),
      );
    }
  });
  return Promise.race([signal.promise, terminal.promise]);
}

function mapStartupError(error: unknown): number {
  if (error instanceof GatewayConfigError) {
    process.stderr.write(`gateway config error: ${error.message}\n`);
    return 2;
  }
  if (error instanceof EnrollmentError) {
    process.stderr.write(`gateway enrollment refused: ${error.message}\n`);
    return 4;
  }
  if (error instanceof ProtocolMismatchError) {
    process.stderr.write(`gateway protocol mismatch: ${error.message}\n`);
    return 3;
  }
  process.stderr.write(`gateway failed to reach ready: ${String(error)}\n`);
  return 1;
}

// Only execute when invoked as a program (importable for tests).
if (
  process.argv[1] !== undefined &&
  import.meta.url === new URL(`file://${process.argv[1]}`).href
) {
  run(process.argv.slice(2)).then((code) => {
    process.exitCode = code;
  });
}
