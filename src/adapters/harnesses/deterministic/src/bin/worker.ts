// ofac-deterministic-worker — the worked-example worker binary: config →
// createAdapter (sdk invocation) → ready line → park until signal.
//
// Exit codes (the Gateway bin idiom): 0 clean · 1 runtime failure ·
// 2 config error · 3 register-denied-then-exhausted (restartBackoffMs: []).
import { parseArgs } from "node:util";
import { createConsoleLogger } from "@ofac/workspace-link";
import type { LinkLogger } from "@ofac/workspace-link";
import { createAdapter } from "@ofac/adapter";
import { WorkerConfigError, loadWorkerConfigFile, type WorkerConfig } from "../config.js";
import { createDeterministicHooks } from "../harness.js";

const USAGE = `ofac-deterministic-worker run --config <path.to/worker.yaml>

The worker attaches to its team Gateway over the child wire (unix socket or
127.0.0.1 TCP), registers with its capability manifest, and runs the
deterministic two-step research script for every assignment. Env overlay:
OFAC_CHILD_SECRET wins over the file's token; OFAC_ADAPTER_LOG sets the log
level (debug|info|warn|error); WORKER_CONFIG supplies the --config path when
the flag is absent.
Exit codes: 0 clean, 1 runtime, 2 config error, 3 register denied (auto-restart off).`;

const LOG_LEVELS = new Set(["debug", "info", "warn", "error"]);

export interface WorkerHandle {
  ready: Promise<void>;
  stop(): Promise<void>;
}

export async function bootWorker(config: WorkerConfig, log: LinkLogger): Promise<WorkerHandle> {
  const handle = await createAdapter({
    childWorkspaceId: config.childWorkspaceId,
    ...(config.token !== undefined ? { token: config.token } : {}),
    kernel: {
      baseUrl: config.kernelUrl,
      ...(config.kernelToken !== undefined ? { token: config.kernelToken } : {}),
    },
    manifest: config.manifest,
    transport:
      config.transport.socketPath !== undefined
        ? { socketPath: config.transport.socketPath }
        : { host: config.transport.host ?? "127.0.0.1", port: config.transport.port ?? 0 },
    descriptor: {
      invocation: "sdk",
      supports: { steer: true, interrupt: true, resume: "restart-with-context" },
    },
    hooks: createDeterministicHooks({
      childWorkspaceId: config.childWorkspaceId,
      workDir: config.workDir,
      stepMs: config.stepMs,
      logger: log,
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
      process.stdout.write(`worker ready childId=${config.childWorkspaceId} ${endpoint}\n`);
    }),
    stop: () => handle.close(),
  };
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
    process.stderr.write(`worker config error: ${(error as Error).message}\n\n${USAGE}\n`);
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
  configPath = configPath ?? process.env["WORKER_CONFIG"];
  if (configPath === undefined) {
    process.stderr.write("missing --config (or WORKER_CONFIG)\n\n".concat(USAGE, "\n"));
    return 2;
  }
  let config: WorkerConfig;
  try {
    config = loadWorkerConfigFile(configPath);
  } catch (error) {
    if (error instanceof WorkerConfigError) {
      process.stderr.write(`worker config error: ${error.message}\n`);
      return 2;
    }
    throw error;
  }
  const level = LOG_LEVELS.has(config.logLevel) ? config.logLevel : "info";
  const log = createConsoleLogger({ level: level as "debug" | "info" | "warn" | "error" });
  let worker: WorkerHandle;
  try {
    worker = await bootWorker(config, log);
    await worker.ready;
  } catch (error) {
    if (error instanceof Error && error.message.startsWith("register denied")) {
      process.stderr.write(`worker: ${error.message}\n`);
      return 3;
    }
    process.stderr.write(`worker runtime error: ${(error as Error).message}\n`);
    return 1;
  }
  // Park until signal (clean 0) — the adapter owns reconnect honesty.
  const parked = Promise.withResolvers<number>();
  const stop = (): void => {
    void worker.stop().then(
      () => parked.resolve(0),
      () => parked.resolve(1),
    );
  };
  process.on("SIGINT", stop);
  process.on("SIGTERM", stop);
  return parked.promise;
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
