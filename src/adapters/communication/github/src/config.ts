// Env-only adapter config (the provider config law): every key names its
// consumer; missing required keys fail closed with a named error, never a
// silent default. The provider tenant is the GitHub host: GitHub webhooks
// carry no instance-wide tenant id, and the site origin IS the provider
// tenant boundary (deriveTenant, forgejo/mattermost precedent).
import { readFileSync } from "node:fs";

export class AdapterConfigError extends Error {
  constructor(
    readonly key: string,
    message: string,
  ) {
    super(`${key}: ${message}`);
    this.name = "AdapterConfigError";
  }
}

export interface AdapterConfig {
  /** Kernel base URL (link plane: enroll / capabilities / events / hub). */
  kernelUrl: string;
  /** Mode-(a) enrollment secret (dev literal `enroll-dev`). */
  enrollSecret: string;
  /** Logical link endpoint id this adapter enrolls as ("github-adapter"). */
  endpointId: string;
  /** Kernel team roster slug the enrollment binds to. */
  team: string;
  /** Stable identity of this provider deployment inside the reference shape. */
  providerInstanceId: string;
  /** GitHub REST base URL (default https://api.github.com; e2e points it at
   *  the in-process fake). */
  githubUrl: string;
  /** Bot account token: every provider read rides it (the sha→PR status
   *  resolver is the only adapter-side call). Source: `GITHUB_TOKEN` or the
   *  file named by `GITHUB_TOKEN_FILE` (compose mounts secrets as files);
   *  ABSENT file ⇒ undefined (inbound-only run), present-but-empty fails
   *  closed. */
  botToken?: string | undefined;
  /** Webhook HMAC secret — the same `.env` key the webhook registration
   *  sends; verifies X-Hub-Signature-256 BEFORE normalize. */
  webhookSecret: string;
  /** Comma list of labels that mark an issue as a Work trigger (the ADAPTER
   *  owns the label policy, the kernel reads the workTrigger flag). */
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

/** `GITHUB_TOKEN` wins; else read the file named by `GITHUB_TOKEN_FILE`
 *  (one-shot-provisioned). A file that is ABSENT (not yet provisioned — an
 *  inbound-only run just waits) yields undefined; a file present but
 *  unreadable/empty fails closed naming the key. */
export function resolveBotToken(env: Record<string, string | undefined>): string | undefined {
  const direct = env["GITHUB_TOKEN"];
  if (direct !== undefined && direct !== "") {
    return direct.trim();
  }

  const file = env["GITHUB_TOKEN_FILE"];
  if (file === undefined || file === "") {
    return undefined;
  }

  let text: string;
  try {
    text = readFileSync(file, "utf8");
  } catch (error) {
    if ((error as { code?: string }).code === "ENOENT") return undefined;
    throw new AdapterConfigError("GITHUB_TOKEN_FILE", `unreadable file ${file}`);
  }

  const token = text.trim();
  if (token === "") {
    throw new AdapterConfigError("GITHUB_TOKEN_FILE", `empty token file ${file}`);
  }

  return token;
}

const LEVELS: Record<string, true> = { debug: true, info: true, warn: true, error: true };

export function parseEnvConfig(env: Record<string, string | undefined>): AdapterConfig {
  const logLevel = env["OFAC_ADAPTER_LOG"] ?? "info";
  if (!(logLevel in LEVELS)) {
    throw new AdapterConfigError("OFAC_ADAPTER_LOG", `unknown level '${logLevel}'`);
  }

  const portKey = env["PORT"] === undefined ? "OFAC_HEALTH_PORT" : "PORT";
  const portRaw = env["PORT"] ?? env["OFAC_HEALTH_PORT"];
  const port = portRaw === undefined ? 8068 : Number(portRaw);
  if (!Number.isInteger(port) || port <= 0 || port > 65535) {
    throw new AdapterConfigError(portKey, `'${portRaw}' is not a port`);
  }

  return {
    kernelUrl: required(env, "OFAC_KERNEL_URL"),
    enrollSecret: required(env, "OFAC_ENROLL_SECRET"),
    endpointId: required(env, "OFAC_ENDPOINT_ID"),
    team: required(env, "OFAC_TEAM"),
    providerInstanceId: env["OFAC_PROVIDER_INSTANCE_ID"] ?? "github-com",
    githubUrl: env["GITHUB_URL"] ?? "https://api.github.com",
    botToken: resolveBotToken(env),
    webhookSecret: required(env, "GITHUB_WEBHOOK_SECRET"),
    workLabels: (env["GITHUB_WORK_LABELS"] ?? "ofac:work")
      .split(",")
      .map((label) => label.trim())
      .filter((label) => label !== ""),
    botUser: required(env, "GITHUB_BOT_USER"),
    statePath: env["OFAC_STATE_PATH"] ?? "./ofac-github-adapter.sqlite3",
    port,
    logLevel: logLevel as AdapterConfig["logLevel"],
  };
}

/** Provider tenant = the GitHub host (webhook payloads carry no wider scope). */
export function deriveTenant(githubUrl: string): string {
  let url: URL;
  try {
    url = new URL(githubUrl);
  } catch {
    throw new AdapterConfigError("GITHUB_URL", `'${githubUrl}' is not a URL`);
  }

  return url.hostname;
}
