// gitlab-adapter run — env-only config (the provider config law): parse →
// run → park until SIGINT/SIGTERM. Exit codes (the worker idiom): 0 clean ·
// 1 runtime failure · 2 config error. There is no `init` subcommand: the
// on-demand GitLab instance is bootstrapped by tools/gitlab/init-gitlab.sh.
import { createConsoleLogger } from "@ofac/workspace-link";
import { AdapterConfigError, parseEnvConfig } from "../config.js";
import { run, type RunHandle } from "../index.js";

const USAGE = `gitlab-adapter run

run env:
  OFAC_KERNEL_URL OFAC_ENROLL_SECRET OFAC_ENDPOINT_ID OFAC_TEAM
  OFAC_PROVIDER_INSTANCE_ID (default local-gitlab) OFAC_STATE_PATH
  OFAC_HEALTH_PORT (default 8068) OFAC_ADAPTER_LOG
  gitlab env: GITLAB_URL GITLAB_TOKEN|GITLAB_TOKEN_FILE GITLAB_WEBHOOK_TOKEN
              GITLAB_BOT_USER (required) GITLAB_WORK_LABELS
              (default ofac:work)`;

export async function main(
  args: readonly string[],
  env: NodeJS.ProcessEnv = process.env,
): Promise<number> {
  const command = args[0];
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

if (
  process.argv[1] !== undefined &&
  import.meta.url === new URL(`file://${process.argv[1]}`).href
) {
  process.exitCode = await main(process.argv.slice(2));
}
