import { readFileSync } from "node:fs";
// Env-only adapter config: every key names its consumer;
// missing required keys fail closed with a named error, never a silent
// default. `deriveTenant` fixes the conversationReference.tenantOrOrganizationId
// as the Mattermost site host — the WS event stream carries no team id, and
// the site origin IS the provider tenant boundary.
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
  /** Logical link endpoint id this adapter enrolls as (e.g. "mm-adapter"). */
  endpointId: string;
  /** Kernel team roster slug the enrollment binds to. */
  team: string;
  /** Stable identity of this provider deployment inside the reference shape. */
  providerInstanceId: string;
  /** Mattermost base URL (REST v4 + WebSocket v4). */
  mmUrl: string;
  /** Bot account token: every provider read/write rides it. Source:
   *  `MATTERMOST_BOT_TOKEN` or the file named by `MATTERMOST_BOT_TOKEN_FILE`
   *  (compose: a provision one-shot mints the PAT and writes the file —
   *  Mattermost token values cannot be pinned client-side). */
  botToken: string;
  /** The mention token that classifies a post as `mention.v1`. */
  botMention: string;
  /** SQLite state file: delivery ledger + post ledger + caches. */
  statePath: string;
  /** Optional GET /healthz server port. */
  healthPort?: number;
  logLevel: "debug" | "info" | "warn" | "error";
}

function required(env: Record<string, string | undefined>, key: string): string {
  const value = env[key];
  if (value === undefined || value.trim() === "") {
    throw new AdapterConfigError(key, "required environment variable missing");
  }
  return value.trim();
}

/** `MATTERMOST_BOT_TOKEN` wins; else read the file named by
 *  `MATTERMOST_BOT_TOKEN_FILE` (one-shot-provisioned). Missing/empty/unreadable
 *  all fail closed naming the offending key. */
export function resolveBotToken(env: Record<string, string | undefined>): string {
  const inline = env["MATTERMOST_BOT_TOKEN"]?.trim();
  if (inline !== undefined && inline !== "") return inline;
  const file = env["MATTERMOST_BOT_TOKEN_FILE"]?.trim();
  if (file === undefined || file === "") {
    throw new AdapterConfigError(
      "MATTERMOST_BOT_TOKEN",
      "required (MATTERMOST_BOT_TOKEN_FILE also unset)",
    );
  }
  let text: string;
  try {
    text = readFileSync(file, "utf8");
  } catch {
    throw new AdapterConfigError("MATTERMOST_BOT_TOKEN_FILE", `'${file}' is not readable`);
  }
  if (text.trim() === "") {
    throw new AdapterConfigError("MATTERMOST_BOT_TOKEN_FILE", `'${file}' is empty`);
  }
  return text.trim();
}

const LEVELS: Record<string, true> = { debug: true, info: true, warn: true, error: true };

export function parseEnvConfig(env: Record<string, string | undefined>): AdapterConfig {
  const healthRaw = env["PORT"] ?? env["OFAC_HEALTH_PORT"];
  let healthPort: number | undefined;
  if (healthRaw !== undefined && healthRaw.trim() !== "") {
    const port = Number(healthRaw);
    if (!Number.isInteger(port) || port < 1 || port > 65535) {
      throw new AdapterConfigError("PORT", `'${healthRaw}' is not a valid port`);
    }
    healthPort = port;
  }
  const level = env["OFAC_ADAPTER_LOG"] ?? "info";
  const mmUrl = required(env, "MATTERMOST_URL");
  // Fail closed on an unparseable site URL — it feeds both the REST base and
  // the tenant identity.
  deriveTenant(mmUrl);
  const config: AdapterConfig = {
    kernelUrl: required(env, "OFAC_KERNEL_URL"),
    enrollSecret: required(env, "OFAC_ENROLL_SECRET"),
    endpointId: required(env, "OFAC_ENDPOINT_ID"),
    team: required(env, "OFAC_TEAM"),
    providerInstanceId: env["OFAC_PROVIDER_INSTANCE_ID"]?.trim() || "local-chat",
    mmUrl,
    botToken: resolveBotToken(env),
    botMention: env["OFAC_BOT_MENTION"]?.trim() || "@facilitator",
    statePath: env["OFAC_STATE_PATH"]?.trim() || "./ofac-mm-adapter.sqlite3",
    logLevel: (LEVELS[level] === true ? level : "info") as AdapterConfig["logLevel"],
  };
  return healthPort === undefined ? config : { ...config, healthPort };
}

/** Provider tenant = the site host (MM events carry no team scope). */
export function deriveTenant(mmUrl: string): string {
  let host: string;
  try {
    host = new URL(mmUrl).hostname;
  } catch {
    throw new AdapterConfigError("MATTERMOST_URL", `'${mmUrl}' is not a valid URL`);
  }
  if (host === "") {
    throw new AdapterConfigError("MATTERMOST_URL", `'${mmUrl}' has no host`);
  }
  return host;
}
