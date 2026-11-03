// Env-only adapter config (the provider config law): every key
// names its consumer; missing required keys fail closed with a named error,
// never a silent default. The provider tenant is the forge host: forgejo
// webhooks carry no instance-wide tenant id, and the site origin IS the
// provider tenant boundary (deriveTenant, mattermost precedent).
import { readFileSync } from "node:fs";

export class AdapterConfigError extends Error {
  constructor(key: string, message: string) {
    super(`${key}: ${message}`);
    this.name = "AdapterConfigError";
  }
}

export interface AdapterConfig {
  /** Kernel base URL (link plane: enroll / capabilities / events / hub). */
  kernelUrl: string;
  /** Mode-(a) enrollment secret (dev literal `enroll-dev`). */
  enrollSecret: string;
  /** Logical link endpoint id this adapter enrolls as (e.g. "forgejo-adapter"). */
  endpointId: string;
  /** Kernel team roster slug the enrollment binds to. */
  team: string;
  /** Stable identity of this provider deployment inside the reference shape. */
  providerInstanceId: string;
  /** Forgejo base URL (REST v1). */
  forgejoUrl: string;
  /** Bot account token: every provider read/write rides it. Source:
   *  `FORGEJO_TOKEN` or the file named by `FORGEJO_TOKEN_FILE` (compose: the
   *  init one-shot mints the PAT and writes the file 0444-atomic). Optional
   *  (decision 6): the adapter is ALWAYS inbound-capable; with no token the
   *  webhook edge + event publish ride on, and reply commands answer
   *  `unsupported` with a reason — that is also how `init` cold-boots before
   *  the file exists. */
  botToken?: string | undefined;
  /** Webhook HMAC secret — the same `.env` key the webhook registration
   *  sends; verifies X-Forgejo-Signature BEFORE normalize. */
  webhookSecret: string;
  /** Comma list of labels that mark an issue as a Work trigger (decision 3:
   *  the ADAPTER owns the label policy, the kernel reads the workTrigger flag). */
  workLabels: string[];
  /** Exact-match echo identity: events authored by this login are dropped. */
  botUser: string;
  /** SQLite state file: comment ledger (idempotency law). */
  statePath: string;
  /** GET /healthz listen port (the webhook edge lives on the kernel sink). */
  port: number;
  logLevel: "debug" | "info" | "warn" | "error";
}

function required(env: Record<string, string | undefined>, key: string): string {
  const value = env[key];
  if (value === undefined || value === "") {
    throw new AdapterConfigError(key, "required and unset");
  }

  return value;
}

/** `FORGEJO_TOKEN` wins; else read the file named by `FORGEJO_TOKEN_FILE`
 *  (one-shot-provisioned). A file that is ABSENT (not yet provisioned —
 *  cold-boot init writes it, an inbound-only run just waits) yields
 *  undefined; a file present but unreadable/empty fails closed naming the
 *  key (the token-absent law above). */
export function resolveBotToken(env: Record<string, string | undefined>): string | undefined {
  const direct = env["FORGEJO_TOKEN"];
  if (direct !== undefined && direct !== "") {
    return direct.trim();
  }

  const file = env["FORGEJO_TOKEN_FILE"];
  if (file === undefined || file === "") {
    return undefined;
  }

  let text: string;
  try {
    text = readFileSync(file, "utf8");
  } catch (error) {
    if ((error as { code?: string }).code === "ENOENT") return undefined;
    throw new AdapterConfigError("FORGEJO_TOKEN_FILE", `unreadable file ${file}`);
  }

  const token = text.trim();
  if (token === "") {
    throw new AdapterConfigError("FORGEJO_TOKEN_FILE", `empty token file ${file}`);
  }

  return token;
}

const LEVELS: Record<string, true> = { debug: true, info: true, warn: true, error: true };

export function parseEnvConfig(env: Record<string, string | undefined>): AdapterConfig {
  const logLevel = env["OFAC_ADAPTER_LOG"] ?? "info";
  if (!(logLevel in LEVELS)) {
    throw new AdapterConfigError("OFAC_ADAPTER_LOG", `unknown level '${logLevel}'`);
  }

  const portRaw = env["PORT"] ?? env["OFAC_HEALTH_PORT"];
  const port = portRaw === undefined ? 8067 : Number(portRaw);
  if (!Number.isInteger(port) || port <= 0 || port > 65535) {
    throw new AdapterConfigError("PORT", `'${portRaw}' is not a port`);
  }

  return {
    kernelUrl: required(env, "OFAC_KERNEL_URL"),
    enrollSecret: required(env, "OFAC_ENROLL_SECRET"),
    endpointId: required(env, "OFAC_ENDPOINT_ID"),
    team: required(env, "OFAC_TEAM"),
    providerInstanceId: env["OFAC_PROVIDER_INSTANCE_ID"] ?? "local-git",
    forgejoUrl: required(env, "FORGEJO_URL"),
    botToken: resolveBotToken(env),
    webhookSecret: required(env, "FORGEJO_WEBHOOK_SECRET"),
    workLabels: (env["FORGEJO_WORK_LABELS"] ?? "ofac:work")
      .split(",")
      .map((label) => label.trim())
      .filter((label) => label !== ""),
    botUser: required(env, "FORGEJO_BOT_USER"),
    statePath: env["OFAC_STATE_PATH"] ?? "./ofac-forgejo-adapter.sqlite3",
    port,
    logLevel: logLevel as AdapterConfig["logLevel"],
  };
}

/** Provider tenant = the forge host (webhook payloads carry no wider scope). */
export function deriveTenant(forgejoUrl: string): string {
  let url: URL;
  try {
    url = new URL(forgejoUrl);
  } catch {
    throw new AdapterConfigError("FORGEJO_URL", `'${forgejoUrl}' is not a URL`);
  }

  return url.hostname;
}
