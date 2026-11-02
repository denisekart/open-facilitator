// oci-launcher run — YAML config + env overlay (the connector's config law,
// env-only secrets) → run → park until SIGINT/SIGTERM. Exit codes (the linkd
// idiom): 0 clean · 1 runtime failure · 2 config error.
import { createConsoleLogger } from "@ofac/workspace-link";
import { LauncherConfigError, parseLauncherConfig, readConfigFile } from "../config.js";
import { run, type RunHandle } from "../index.js";

const USAGE = `oci-launcher run --config <adapter.yaml>

run env: OFAC_ENROLL_SECRET (named by adapter.yaml enrollSecretEnv)
     OFAC_ENDPOINT_ID (default oci-launcher) OFAC_TEAM
     DOCKER_HOST (default unix:///var/run/docker.sock)
     OFAC_PROV_ALLOWED_IMAGES OFAC_PROV_ALLOWED_NETWORKS (comma lists, prefix/* ok;
     unset ⇒ deny everything: the approved gate fails closed)
     OFAC_PROV_GC_MINUTES (default 60) OFAC_STATE_PATH OFAC_ADAPTER_LOG
Exit codes: 0 clean, 1 runtime, 2 config error.`;

export async function main(argv: readonly string[], env: NodeJS.ProcessEnv): Promise<number> {
  if (argv[0] !== "run") {
    process.stdout.write(`${USAGE}\n`);
    return argv[0] === "help" || argv[0] === "--help" ? 0 : 2;
  }

  const configFlag = argv.indexOf("--config");
  const configPath = configFlag === -1 ? undefined : argv[configFlag + 1];
  if (configPath === undefined) {
    process.stderr.write("config error: --config <adapter.yaml> is required\n");
    return 2;
  }

  let config;
  try {
    config = parseLauncherConfig(
      readConfigFile(configPath),
      env as Record<string, string | undefined>,
    );
  } catch (error) {
    if (error instanceof LauncherConfigError) {
      process.stderr.write(`config error: ${error.message}\n`);
      return 2;
    }

    throw error;
  }

  const logger = createConsoleLogger({ level: config.logLevel });

  let handle: RunHandle;
  try {
    handle = await run(config, logger);
  } catch (error) {
    logger.error("launcher failed to start", { error: String(error) });
    return 1;
  }

  const parked = Promise.withResolvers<number>();
  let stopping = false;
  // The daemon's liveness anchor: a REF'D periodic GC timer. Every SDK
  // timer (heartbeat, reconnect backoff) is unref'd by design, and this
  // process owns no server — after a transport blip the loop would hold
  // nothing ref'd and Node would exit 0 with the supervisor mid-backoff.
  // The same pass doubles as the periodic stale-container GC.
  const gcTimer = setInterval(() => {
    void handle.gcOnce().catch((error: unknown) => {
      logger.warn("periodic gc failed", { error: String(error) });
    });
  }, 60_000);
  const stop = (): void => {
    if (stopping) return;
    stopping = true;
    clearInterval(gcTimer);
    void handle.shutdown().then(
      () => parked.resolve(0),
      (error: unknown) => {
        logger.error("shutdown failed", { error: String(error) });
        parked.resolve(1);
      },
    );
  };

  process.on("SIGINT", stop);
  process.on("SIGTERM", stop);
  void handle.ready.catch((error: unknown) => {
    logger.error("runtime failed", { error: String(error) });
    stop();
  });
  return await parked.promise;
}

// Only execute when invoked as a program (importable for tests).
if (
  process.argv[1] !== undefined &&
  import.meta.url === new URL(`file://${process.argv[1]}`).href
) {
  void main(process.argv.slice(2), process.env).then((code) => {
    process.exitCode = code;
  });
}
