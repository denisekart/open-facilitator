// forgejo-adapter run|init — env-only config (Plan 23 step 3): parse → run →
// park until SIGINT/SIGTERM. Exit codes (the linkd idiom): 0 clean ·
// 1 runtime failure · 2 config error.
import { createConsoleLogger } from "@ofac/workspace-link";
import { AdapterConfigError, parseEnvConfig } from "../config.js";
import { run, runInit, type RunHandle } from "../index.js";

const USAGE = `forgejo-adapter run | forgejo-adapter init

run env: OFAC_KERNEL_URL OFAC_ENROLL_SECRET OFAC_ENDPOINT_ID OFAC_TEAM
     FORGEJO_URL FORGEJO_TOKEN|FORGEJO_TOKEN_FILE FORGEJO_WEBHOOK_SECRET
     FORGEJO_BOT_USER (required)
     OFAC_PROVIDER_INSTANCE_ID (default local-git) FORGEJO_WORK_LABELS
     (default ofac:work) OFAC_STATE_PATH (default ./ofac-forgejo-adapter.sqlite3)
     PORT|OFAC_HEALTH_PORT (default 8067: GET /healthz only) OFAC_ADAPTER_LOG
init env: run env plus
     DEMO_FORGEJO_ADMIN_USER DEMO_FORGEJO_ADMIN_PASSWORD DEMO_FORGEJO_ADMIN_EMAIL
     FORGEJO_INIT_ORG (default demo) FORGEJO_INIT_REPO (default demo/widgets)
     FORGEJO_HOOK_URL (default <OFAC_KERNEL_URL>/api/ingress/webhooks/forgejo)
Exit codes: 0 clean, 1 runtime, 2 config error.`;

export async function main(argv: readonly string[], env: NodeJS.ProcessEnv): Promise<number> {
  const command = argv[0];
  if (command !== "run" && command !== "init") {
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

  if (command === "init") {
    const adminUser = env["DEMO_FORGEJO_ADMIN_USER"] ?? "ofac-admin";
    const adminPassword = env["DEMO_FORGEJO_ADMIN_PASSWORD"];
    if (adminPassword === undefined || adminPassword === "") {
      process.stderr.write("config error: DEMO_FORGEJO_ADMIN_PASSWORD: required and unset\n");
      return 2;
    }

    try {
      const report = await runInit({
        forgejoUrl: config.forgejoUrl,
        adminUser,
        adminPassword,
        adminEmail: env["DEMO_FORGEJO_ADMIN_EMAIL"] ?? `${adminUser}@ofac.local`,
        botUser: config.botUser,
        botEmail: env["DEMO_FORGEJO_BOT_EMAIL"] ?? `${config.botUser}@ofac.local`,
        org: env["FORGEJO_INIT_ORG"] ?? "demo",
        repo: env["FORGEJO_INIT_REPO"] ?? "demo/widgets",
        sampleTitle: env["FORGEJO_INIT_ISSUE_TITLE"] ?? "Seed: summarize the repository layout",
        workLabel: config.workLabels[0] ?? "ofac:work",
        codingIssueTitle: env["FORGEJO_INIT_CODING_ISSUE_TITLE"] ?? "Seed: add a changelog entry",
        codingLabel: env["FORGEJO_INIT_CODING_LABEL"] ?? "ofac:coding",
        hookUrl: env["FORGEJO_HOOK_URL"] ?? `${config.kernelUrl}/api/ingress/webhooks/forgejo`,
        webhookSecret: config.webhookSecret,
        botTokenFile: env["FORGEJO_TOKEN_FILE"] ?? config.statePath + ".token",
      });
      process.stdout.write(`${JSON.stringify(report)}\n`);
      return 0;
    } catch (error) {
      logger.error("init failed", { error: String(error) });
      return 1;
    }
  }

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
