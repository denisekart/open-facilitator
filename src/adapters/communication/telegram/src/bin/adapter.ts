// telegram-adapter run — env-only config (plan Step 3): parse → run → park
// until SIGINT/SIGTERM. Exit codes (the linkd idiom): 0 clean · 1 runtime
// failure · 2 config error.
import { createConsoleLogger } from "@ofac/workspace-link";
import { AdapterConfigError, parseEnvConfig } from "../config.js";
import { run, type RunHandle } from "../index.js";

const USAGE = `telegram-adapter run

Env: OFAC_KERNEL_URL OFAC_ENROLL_SECRET OFAC_ENDPOINT_ID OFAC_TEAM
     TELEGRAM_BOT_TOKEN|TELEGRAM_BOT_TOKEN_FILE TELEGRAM_CHATS (required)
     TELEGRAM_MODE (poll|webhook, default poll)
     TELEGRAM_WEBHOOK_URL TELEGRAM_WEBHOOK_SECRET (mode=webhook)
     TELEGRAM_WEBHOOK_LISTEN_PORT (default 8077)
     OFAC_PROVIDER_INSTANCE_ID (default local-telegram) OFAC_TENANT
     (default api.telegram.org) OFAC_BOT_USER_ID TELEGRAM_STATE_PATH
     (default ./ofac-tg-adapter.sqlite3) TELEGRAM_MEDIA_MAX_BYTES
     TELEGRAM_MEDIA_ENABLED TELEGRAM_API_BASE
     PORT|OFAC_HEALTH_PORT (optional GET /healthz) OFAC_ADAPTER_LOG
Exit codes: 0 clean, 1 runtime, 2 config error.`;

export async function main(argv: readonly string[], env: NodeJS.ProcessEnv): Promise<number> {
  if (argv[0] !== "run") {
    process.stdout.write(`${USAGE}\n`);
    return argv[0] === "help" || argv[0] === "--help" ? 0 : 2;
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
