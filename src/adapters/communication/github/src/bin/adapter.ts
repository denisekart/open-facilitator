// github-adapter run — env-only config: parse → run → park until
// SIGINT/SIGTERM. There is no `init` command: GitHub is unit/e2e-tier only
// (no container to seed), so the fake GitHub's fixtures stand in for seeding.
// Exit codes (the linkd idiom): 0 clean · 1 runtime failure · 2 config error.
import { createConsoleLogger } from "@ofac/workspace-link";
import { AdapterConfigError, parseEnvConfig } from "../config.js";
import { run, type RunHandle } from "../index.js";

const USAGE = `github-adapter run

run env: OFAC_KERNEL_URL OFAC_ENROLL_SECRET OFAC_ENDPOINT_ID OFAC_TEAM
     GITHUB_URL (default https://api.github.com)
     GITHUB_TOKEN|GITHUB_TOKEN_FILE GITHUB_WEBHOOK_SECRET
     GITHUB_BOT_USER (required)
     OFAC_PROVIDER_INSTANCE_ID (default github-com) GITHUB_WORK_LABELS
     (default ofac:work) OFAC_STATE_PATH (default ./ofac-github-adapter.sqlite3)
     PORT|OFAC_HEALTH_PORT (default 8068: GET /healthz only) OFAC_ADAPTER_LOG
Exit codes: 0 clean, 1 runtime, 2 config error.`;

export async function main(argv: readonly string[], env: NodeJS.ProcessEnv): Promise<number> {
  const command = argv[0];
  if (command !== "run") {
    process.stdout.write(`${USAGE}\n`);
    return command === "help" || command === "--help" ? 0 : 2;
  }

  let config;
  try {
    config = parseEnvConfig(env as Record<string, string | undefined>);
  } catch (error) {
    if (error instanceof AdapterConfigError) {
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
    logger.error("adapter failed to start", { error: String(error) });
    return 1;
  }

  const parked = Promise.withResolvers<number>();
  let stopping = false;
  const stop = (): void => {
    if (stopping) return;
    stopping = true;
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
