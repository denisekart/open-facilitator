// Env-only adapter config (the provider config law): every key named by its
// consumer; missing required keys fail closed with a named error — never a
// silent default. The provider tenant IS the forge host (deriveTenant);
// webhook payloads carry no wider scope. GITLAB_WEBHOOK_TOKEN is the shared
// secret the project webhook sends in `X-Gitlab-Token` — GitLab has no body
// HMAC, the token IS the proof, verified constant-time in the normalizer.
import { readFileSync } from "node:fs";

export class AdapterConfigError extends Error {
  constructor(
    readonly key: string,
    message: string,
  ) {
    super(`[${key}] ${message}`);
    this.name = "AdapterConfigError";
  }
}

export interface AdapterConfig {
  /** Kernel base URL (link plane: enroll / capabilities / events / hub). */
  kernelUrl: string;
  /** Mode-(a) enrollment secret (dev literal `enroll-dev`). */
  enrollSecret: string;
  /** Logical link endpoint id this adapter enrolls as (`gitlab-adapter`). */
  endpointId: string;
  /** Kernel team roster slug the enrollment binds to. */
  team: string;
  /** Stable identity of this provider deployment (reference shape). */
  providerInstanceId: string;
  /** GitLab base URL (REST v4). */
  gitlabUrl: string;
  /** Bot account token: every provider read/write rides it. Source:
   *  `GITLAB_TOKEN` or the file named by `GITLAB_TOKEN_FILE` (the
   *  tools/gitlab/init-gitlab.sh bootstrap mints the PAT and writes the
   *  file). Optional: the adapter is ALWAYS inbound-capable; with no token
   *  the webhook edge + event publish ride on, and reply commands answer
   *  `unsupported` with a reason. A file that is ABSENT yields undefined; a
   *  file present but unreadable/empty fails closed naming the key. */
  botToken?: string | undefined;
  /** Shared webhook token — the same secret the project webhook
   *  registration sends; verifies `X-Gitlab-Token` BEFORE normalize. */
  webhookToken: string;
  /** Comma list of labels that mark an issue as a Work trigger (the ADAPTER
   *  owns the label policy, the kernel reads the workTrigger flag). */
  workLabels: string[];
  /** Exact-match echo identity: events authored by this user are dropped. */
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

/** `GITLAB_TOKEN` wins; else read the file named by `GITLAB_TOKEN_FILE`
 *  (bootstrap-provisioned). Absent file ⇒ undefined (cold boot before the
 *  PAT exists); present-but-empty/unreadable ⇒ fail closed naming the key. */
export function resolveBotToken(env: Record<string, string | undefined>): string | undefined {
  const direct = env["GITLAB_TOKEN"];
  if (direct !== undefined && direct !== "") {
    return direct.trim();
  }

  const file = env["GITLAB_TOKEN_FILE"];
  if (file === undefined || file === "") {
    return undefined;
  }

  let text: string;
  try {
    text = readFileSync(file, "utf8");
  } catch (error) {
    if ((error as { code?: string }).code === "ENOENT") return undefined;
    throw new AdapterConfigError("GITLAB_TOKEN_FILE", `unreadable file ${file}`);
  }

  const token = text.trim();
  if (token === "") {
    throw new AdapterConfigError("GITLAB_TOKEN_FILE", `empty token file ${file}`);
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
  const port = portRaw === undefined ? 8068 : Number(portRaw);
  if (!Number.isInteger(port) || port <= 0 || port > 65535) {
    throw new AdapterConfigError("PORT", `'${portRaw}' is not a port`);
  }

  return {
    kernelUrl: required(env, "OFAC_KERNEL_URL"),
    enrollSecret: required(env, "OFAC_ENROLL_SECRET"),
    endpointId: required(env, "OFAC_ENDPOINT_ID"),
    team: required(env, "OFAC_TEAM"),
    providerInstanceId: env["OFAC_PROVIDER_INSTANCE_ID"] ?? "local-gitlab",
    gitlabUrl: required(env, "GITLAB_URL"),
    botToken: resolveBotToken(env),
    webhookToken: required(env, "GITLAB_WEBHOOK_TOKEN"),
    workLabels: (env["GITLAB_WORK_LABELS"] ?? "ofac:work")
      .split(",")
      .map((label) => label.trim())
      .filter((label) => label !== ""),
    botUser: required(env, "GITLAB_BOT_USER"),
    statePath: env["OFAC_STATE_PATH"] ?? "./ofac-gitlab-adapter.sqlite3",
    port,
    logLevel: logLevel as AdapterConfig["logLevel"],
  };
}

/** Provider tenant = the GitLab host (webhook payloads carry no wider scope). */
export function deriveTenant(gitlabUrl: string): string {
  let url: URL;
  try {
    url = new URL(gitlabUrl);
  } catch {
    throw new AdapterConfigError("GITLAB_URL", `'${gitlabUrl}' is not a URL`);
  }

  return url.hostname;
}
