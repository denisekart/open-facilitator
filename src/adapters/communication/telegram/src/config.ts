// Env-only adapter config: every key names its consumer; missing required
// keys fail closed with a named AdapterConfigError (exit 2 at the bin),
// never a silent default. The chat allowlist is the whole admission law:
// an entry is `chatId[:label[:label]]` where chatId is Telegram's signed
// integer (supergroups are negative), an `@channelusername`, or
// `dm:<userId>`; labels are lowercase `[a-z0-9-]+` (the kernel VerbPattern
// charset and the idToken limit both hold). A duplicate chat id with
// differing labels is a config error — two labels for one chat is always a
// typo, never a merge.
import { readFileSync } from "node:fs";

export class AdapterConfigError extends Error {
  constructor(key: string, message: string) {
    super(`${key}: ${message}`);
    this.name = "AdapterConfigError";
  }
}

/** One admitted chat: the provider chat key + the label names the kernel
 *  routes on (empty list = admitted, no labels ⇒ global routing). */
export interface ChatEntry {
  /** Canonical chat key: the signed integer as a string, `@name`
   *  (lowercased), or `dm:<userId>`. */
  chatId: string;
  labels: string[];
}

export interface AdapterConfig {
  /** Kernel base URL (link plane: enroll / capabilities / events / hub). */
  kernelUrl: string;
  /** Mode-(a) enrollment secret (dev literal `enroll-dev`). */
  enrollSecret: string;
  /** Logical link endpoint id this adapter enrolls as (e.g. "tg-adapter"). */
  endpointId: string;
  /** Kernel team roster slug the enrollment binds to. */
  team: string;
  /** Stable identity of this provider deployment inside the reference shape. */
  providerInstanceId: string;
  /** Bot token: every provider read/write rides it. Source:
   *  `TELEGRAM_BOT_TOKEN` or the file named by `TELEGRAM_BOT_TOKEN_FILE`
   *  (inline wins). */
  botToken: string;
  /** The admitted-chat allowlist (the whole inbound flood control). */
  chats: ChatEntry[];
  /** `poll` (long polling, default) | `webhook`. */
  mode: "poll" | "webhook";
  /** Public HTTPS URL registered via setWebhook (webhook mode). */
  webhookUrl?: string;
  /** `secret_token` compared constant-time against the inbound header. */
  webhookSecret?: string;
  /** Inbound port for POST /webhook (webhook mode only). */
  webhookListenPort: number;
  /** `reference.tenantOrOrganizationId` — the value Providers:Telegram:Tenant
   *  must equal. */
  tenant: string;
  /** Adapter-side bot numeric id for the echo law; default from getMe. */
  botUserId?: string;
  /** SQLite state file: link ledger + chat ledger + offset. */
  statePath: string;
  /** Inbound media ceiling; larger ⇒ recorded skip, never a crash. */
  mediaMaxBytes: number;
  /** false ⇒ media is noted and skipped. */
  mediaEnabled: boolean;
  /** Bot API base (fake-server seam). */
  apiBase: string;
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

function optionalPort(
  env: Record<string, string | undefined>,
  key: string,
  fallback: number,
): number {
  const raw = env[key]?.trim();
  if (raw === undefined || raw === "") return fallback;
  const port = Number(raw);
  if (!Number.isInteger(port) || port < 1 || port > 65535) {
    throw new AdapterConfigError(key, `'${raw}' is not a valid port`);
  }
  return port;
}

/** `TELEGRAM_BOT_TOKEN` wins; else read the file named by
 *  `TELEGRAM_BOT_TOKEN_FILE`. Missing/empty/unreadable all fail closed
 *  naming the offending key. */
export function resolveBotToken(env: Record<string, string | undefined>): string {
  const inline = env["TELEGRAM_BOT_TOKEN"]?.trim();
  if (inline !== undefined && inline !== "") return inline;
  const file = env["TELEGRAM_BOT_TOKEN_FILE"]?.trim();
  if (file === undefined || file === "") {
    throw new AdapterConfigError(
      "TELEGRAM_BOT_TOKEN",
      "required (TELEGRAM_BOT_TOKEN_FILE also unset)",
    );
  }
  let text: string;
  try {
    text = readFileSync(file, "utf8");
  } catch {
    throw new AdapterConfigError("TELEGRAM_BOT_TOKEN_FILE", `'${file}' is not readable`);
  }
  if (text.trim() === "") {
    throw new AdapterConfigError("TELEGRAM_BOT_TOKEN_FILE", `'${file}' is empty`);
  }
  return text.trim();
}

const LABEL = /^[a-z0-9-]+$/;
const CHAT_INT = /^-?\d+$/;
const CHANNEL_NAME = /^@[A-Za-z][A-Za-z0-9_]{4,31}$/;
// `dm:<userId>` optionally followed by `:<label>…`.
const DM_HEAD = /^dm:(-?\d+)(?::([\s\S]*))?$/i;

/** Parse one comma-separated allowlist token: either a chat entry
 *  (`chatId[:label[:label…]]`, labels also writable comma-separated) or a
 *  bare label continuing the previous entry (`-100:ofac,coding`). Throws
 *  AdapterConfigError on anything that is neither. */
function parseChatToken(
  raw: string,
): { chatId: string; labels: string[] } | { labels: [string] } | null {
  const token = raw.trim();
  if (token === "") return null;
  const firstColon = token.indexOf(":");
  const chatPart = firstColon === -1 ? token : token.slice(0, firstColon);
  let labelsRaw: string;
  let chatId: string | undefined;
  const dm = DM_HEAD.exec(token);
  if (dm !== null) {
    chatId = `dm:${dm[1]}`;
    labelsRaw = dm[2] ?? "";
  } else if (CHAT_INT.test(chatPart) || CHANNEL_NAME.test(chatPart)) {
    chatId = chatPart.startsWith("@") ? chatPart.toLowerCase() : chatPart;
    labelsRaw = firstColon === -1 ? "" : token.slice(firstColon + 1);
  } else if (firstColon === -1 && LABEL.test(chatPart)) {
    // Bare label: a continuation of the previous entry's label list. Valid
    // ONLY when a chat precedes it — the caller enforces that.
    return { labels: [chatPart.toLowerCase()] };
  } else {
    throw new AdapterConfigError(
      "TELEGRAM_CHATS",
      `'${token}' is not 'chatId[:label]' — chatId must be a signed integer, @channelusername, or dm:<userId>`,
    );
  }
  const labels: string[] = [];
  for (const label of labelsRaw.split(":")) {
    if (label === "") continue;
    const lower = label.toLowerCase();
    if (!LABEL.test(lower)) {
      throw new AdapterConfigError(
        "TELEGRAM_CHATS",
        `label '${label}' is not [a-z0-9-]+ (kernel idToken + VerbPattern law)`,
      );
    }
    if (!labels.includes(lower)) labels.push(lower);
  }
  return { chatId: chatId!, labels };
}

/** The allowlist law: every entry parses; labels lowercase `[a-z0-9-]+`;
 *  duplicate chat id with differing labels ⇒ error. Empty value ⇒ error
 *  (the variable is required). */
export function parseChats(value: string): ChatEntry[] {
  if (value.trim() === "") {
    throw new AdapterConfigError("TELEGRAM_CHATS", "required environment variable missing");
  }
  const entries: ChatEntry[] = [];
  const byKey = new Map<string, ChatEntry>();
  let last: ChatEntry | undefined;
  for (const raw of value.split(",")) {
    const parsed = parseChatToken(raw);
    if (parsed === null) continue;
    if ("chatId" in parsed) {
      const existing = byKey.get(parsed.chatId);
      if (existing !== undefined) {
        const same =
          existing.labels.length === parsed.labels.length &&
          existing.labels.every((label, i) => label === parsed.labels[i]);
        if (!same) {
          throw new AdapterConfigError(
            "TELEGRAM_CHATS",
            `duplicate chat '${parsed.chatId}' with differing labels`,
          );
        }
        last = existing;
        continue;
      }
      const entry: ChatEntry = { chatId: parsed.chatId, labels: parsed.labels };
      byKey.set(entry.chatId, entry);
      entries.push(entry);
      last = entry;
    } else {
      if (last === undefined) {
        throw new AdapterConfigError("TELEGRAM_CHATS", `label '${parsed.labels[0]}' names no chat`);
      }
      if (!last.labels.includes(parsed.labels[0]!)) last.labels.push(parsed.labels[0]!);
    }
  }
  if (entries.length === 0) {
    throw new AdapterConfigError("TELEGRAM_CHATS", "no valid chat entry parsed");
  }
  return entries;
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
  const modeRaw = env["TELEGRAM_MODE"]?.trim() || "poll";
  if (modeRaw !== "poll" && modeRaw !== "webhook") {
    throw new AdapterConfigError("TELEGRAM_MODE", `'${modeRaw}' is not 'poll' or 'webhook'`);
  }
  const webhookUrl = env["TELEGRAM_WEBHOOK_URL"]?.trim();
  const webhookSecret = env["TELEGRAM_WEBHOOK_SECRET"]?.trim();
  if (modeRaw === "webhook" && (webhookUrl === undefined || webhookUrl === "")) {
    throw new AdapterConfigError("TELEGRAM_WEBHOOK_URL", "required when TELEGRAM_MODE=webhook");
  }
  if (modeRaw === "webhook" && (webhookSecret === undefined || webhookSecret === "")) {
    throw new AdapterConfigError("TELEGRAM_WEBHOOK_SECRET", "required when TELEGRAM_MODE=webhook");
  }
  const mediaMaxRaw = env["TELEGRAM_MEDIA_MAX_BYTES"]?.trim();
  let mediaMaxBytes = 20971520;
  if (mediaMaxRaw !== undefined && mediaMaxRaw !== "") {
    const n = Number(mediaMaxRaw);
    if (!Number.isInteger(n) || n <= 0) {
      throw new AdapterConfigError(
        "TELEGRAM_MEDIA_MAX_BYTES",
        `'${mediaMaxRaw}' is not a positive integer`,
      );
    }
    mediaMaxBytes = n;
  }
  const config: AdapterConfig = {
    kernelUrl: required(env, "OFAC_KERNEL_URL"),
    enrollSecret: required(env, "OFAC_ENROLL_SECRET"),
    endpointId: required(env, "OFAC_ENDPOINT_ID"),
    team: required(env, "OFAC_TEAM"),
    providerInstanceId: env["OFAC_PROVIDER_INSTANCE_ID"]?.trim() || "local-telegram",
    botToken: resolveBotToken(env),
    chats: parseChats(required(env, "TELEGRAM_CHATS")),
    mode: modeRaw,
    webhookListenPort: optionalPort(env, "TELEGRAM_WEBHOOK_LISTEN_PORT", 8077),
    tenant: env["OFAC_TENANT"]?.trim() || "api.telegram.org",
    statePath: env["TELEGRAM_STATE_PATH"]?.trim() || "./ofac-tg-adapter.sqlite3",
    mediaMaxBytes,
    mediaEnabled: (env["TELEGRAM_MEDIA_ENABLED"]?.trim() || "true") !== "false",
    apiBase: env["TELEGRAM_API_BASE"]?.trim() || "https://api.telegram.org",
    logLevel: (LEVELS[level] === true ? level : "info") as AdapterConfig["logLevel"],
    ...(webhookUrl === undefined || webhookUrl === "" ? {} : { webhookUrl }),
    ...(webhookSecret === undefined || webhookSecret === "" ? {} : { webhookSecret }),
    ...(env["OFAC_BOT_USER_ID"]?.trim() === undefined || env["OFAC_BOT_USER_ID"]?.trim() === ""
      ? {}
      : { botUserId: env["OFAC_BOT_USER_ID"]!.trim() }),
  };
  return healthPort === undefined ? config : { ...config, healthPort };
}

/** Provider tenant identity for the event reference (Telegram has no site
 *  host: the api origin IS the tenant boundary). */
export function deriveTenant(env: Record<string, string | undefined>): string {
  return env["OFAC_TENANT"]?.trim() || "api.telegram.org";
}

/** The allowlist lookup the normalizer + handler use: numeric id, @name or
 *  dm:<id> — public chats also match their @username when the entry names one. */
export function admissionForChat(
  chats: readonly ChatEntry[],
  chatId: string,
): { admitted: true; labels: string[] } | null {
  for (const entry of chats) {
    if (entry.chatId === chatId) return { admitted: true, labels: entry.labels };
  }
  return null;
}
