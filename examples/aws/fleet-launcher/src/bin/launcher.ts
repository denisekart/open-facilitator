// NON-PRODUCTION REFERENCE ENTRYPOINT (see index.ts): fleet-launcher run —
// env-only config → enroll → capabilities → SignalR → park until
// SIGINT/SIGTERM. Exit codes (the linkd idiom): 0 clean · 1 runtime · 2 config.
import { createConsoleLogger } from "@ofac/workspace-link";
import { LauncherConfigError, parseLauncherConfig } from "../config.js";
import { run, type RunHandle } from "../index.js";

const USAGE = `fleet-launcher run

run env: OFAC_KERNEL_URL (https://<kernelDomain>)
     OFAC_ENROLL_TOKEN (secret-injected env, mode-(a) enrollment secret)
     OFAC_ENDPOINT_ID (e.g. launcher.team-alpha) OFAC_TEAM
     OFAC_RULES_FILE (path to fleet-rules.json: the static workload allowlist)
     OFAC_INSTANCE_ID (optional; unset ⇒ random instanceId per start)
     OFAC_STATE_PATH (default /var/lib/ofac/fleet-launcher.sqlite3)
     OFAC_ADAPTER_LOG (debug|info|warn|error, default info)
     AWS_REGION + task role credentials (default chain; no static keys)
Exit codes: 0 clean, 1 runtime, 2 config error.`;

export async function main(_argv: readonly string[], env: NodeJS.ProcessEnv): Promise<number> {
  if (env["OFAC_FLEET_HELP"] === "1") {
    process.stdout.write(`${USAGE}\n`);
    return 0;
  }

  let config;
  try {
    config = parseLauncherConfig(env as Record<string, string | undefined>);
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
  // A REF'D interval keeps the loop alive past transport blips (every SDK
  // timer is unref'd by design and this process owns no server — after a
  // blip the loop would hold nothing ref'd and Node would exit 0 with the
  // supervisor mid-backoff, the oci bin's daemon law).
  const keepAlive = setInterval(() => undefined, 60_000);
  const stop = (): void => {
    if (stopping) return;
    stopping = true;
    clearInterval(keepAlive);
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
