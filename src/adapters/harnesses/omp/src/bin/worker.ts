// ofac-omp-worker — the omp harness worker binary: config →
// createAdapter (sdk invocation) → ready line → park until signal.
//
// Harness options (env, NOT config keys): OFAC_GIT_CLONE_URL (the full
// clone URL), OFAC_GIT_BRANCH (optional branch), OFAC_GIT_TOKEN (the PAT,
// read AT EVERY RUN — rotation law). The coding-worker example assembles
// the URL from a provider origin + a token FILE; the omp adapter takes the
// URL whole (frozen Track-B contract), same scrub-everywhere law. The CLI
// itself: OFAC_OMP_BIN (default `omp`) + OFAC_OMP_ARGS (JSON string array,
// spliced before every forced flag) — the fake-CLI seam the e2e tier rides.
//
// Exit codes (the Gateway bin idiom): 0 clean · 1 runtime failure ·
// 2 config error · 3 register-denied-then-exhausted (restartBackoffMs: []).
import { parseArgs } from "node:util";
import { createConsoleLogger } from "@ofac/workspace-link";
import type { LinkLogger } from "@ofac/workspace-link";
import { createAdapter } from "@ofac/adapter";
import { WorkerConfigError, loadWorkerConfigFile, type WorkerConfig } from "../config.js";
import { createOmpHooks } from "../harness.js";
import { FileSessionStore } from "../session-store.js";

const USAGE = `ofac-omp-worker run --config <path.to/omp-harness.yaml>

The worker attaches to its team Gateway over the child wire (unix socket or
127.0.0.1 TCP), registers coding.omp, and for every assignment spawns
'omp --mode rpc --no-ui --auto-approve' (agentic mode FORCED) against a
cloned workspace, relaying ask_user questions through the Facilitator to
the live run. Clone inputs (env): OFAC_GIT_CLONE_URL, OFAC_GIT_BRANCH,
OFAC_GIT_TOKEN. CLI seam (env): OFAC_OMP_BIN + OFAC_OMP_ARGS (JSON array)
stand in for a real omp. Env overlay: OFAC_CHILD_SECRET wins over the
file's token; OFAC_ADAPTER_LOG sets the log level (debug|info|warn|error);
WORKER_CONFIG supplies the --config path when the flag is absent.
Exit codes: 0 clean, 1 runtime, 2 config error, 3 register denied (auto-restart off).`;

const LOG_LEVELS: Record<string, true> = { debug: true, info: true, warn: true, error: true };

export interface WorkerHandle {
  ready: Promise<void>;
  stop(): Promise<void>;
}

/** The CLI seam: argv prefix for the harness binary (fake-CLI e2e rides it). */
function parseOmpArgs(raw: string | undefined): readonly string[] {
  if (raw === undefined || raw === "") return [];
  try {
    const parsed: unknown = JSON.parse(raw);
    if (Array.isArray(parsed) && parsed.every((entry) => typeof entry === "string")) {
      return parsed as string[];
    }
  } catch {
    /* a malformed seam is simply ignored: the default CLI stands */
  }
  return [];
}

export async function bootWorker(config: WorkerConfig, log: LinkLogger): Promise<WorkerHandle> {
  const cloneUrl = process.env["OFAC_GIT_CLONE_URL"];
  const cloneBranch = process.env["OFAC_GIT_BRANCH"];
  const cloneToken = process.env["OFAC_GIT_TOKEN"];
  const handle = await createAdapter({
    childWorkspaceId: config.childWorkspaceId,
    ...(config.token !== undefined ? { token: config.token } : {}),
    kernel: {
      baseUrl: config.kernelUrl,
      ...(config.kernelToken !== undefined ? { kernelToken: config.kernelToken } : {}),
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
    hooks: createOmpHooks({
      childWorkspaceId: config.childWorkspaceId,
      workDir: config.workDir,
      stepMs: config.stepMs,
      extraArgs: config.extraArgs,
      interactivity: config.interactivity,
      ...(config.model !== undefined ? { model: config.model } : {}),
      ...(config.askQuestion !== undefined ? { askQuestion: config.askQuestion } : {}),
      ...(config.promptTemplate !== undefined ? { promptTemplate: config.promptTemplate } : {}),
      invocation: {
        command: process.env["OFAC_OMP_BIN"] ?? "omp",
        prefixArgs: parseOmpArgs(process.env["OFAC_OMP_ARGS"]),
      },
      ...(cloneUrl !== undefined
        ? {
            clone: {
              url: cloneUrl,
              ...(cloneBranch !== undefined ? { branch: cloneBranch } : {}),
              ...(cloneToken !== undefined ? { token: cloneToken } : {}),
            },
          }
        : {}),
      sessionStore: new FileSessionStore(config.workDir),
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
    process.stderr.write(`missing --config (or WORKER_CONFIG)\n\n${USAGE}\n`);
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
  const level = config.logLevel in LOG_LEVELS ? config.logLevel : "info";
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
