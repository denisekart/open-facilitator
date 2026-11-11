// The Telegram Bot API over plain fetch: the ONLY place that talks to the
// provider. Every method is a POST to `<apiBase>/bot<token>/<method>` (JSON
// bodies; multipart/form-data for the media uploads). Non-ok answers a typed
// TgError naming the call. Everything the listener, handler, and media
// pipeline need is behind the TelegramApi interface so unit tests inject a
// fake (the idempotency-ledger tests must run with zero HTTP).
import type { TgMessage, TgUpdate } from "./normalize.js";

export class TgError extends Error {
  constructor(
    readonly call: string,
    readonly status: number,
    detail?: string,
  ) {
    super(`telegram ${call} failed: HTTP ${status}${detail === undefined ? "" : ` (${detail})`}`);
    this.name = "TgError";
  }
}

export interface TgUser {
  id: number;
  is_bot: boolean;
  username: string;
  first_name?: string;
}

export interface TgFile {
  file_id: string;
  file_unique_id: string;
  file_size?: number;
  file_path?: string;
}

export interface GetUpdatesParams {
  offset?: number;
  limit?: number;
  timeout?: number;
  allowed_updates?: string[];
}

/** One interactive button (Telegram provider law): an inline callback
 *  button or a reply-keyboard button (the prefill law). */
export type InlineKeyboardButton =
  | { text: string; callback_data: string }
  | { text: string };

/** The wire markup: exactly one of the two families is set. */
export interface ReplyMarkup {
  inline_keyboard?: InlineKeyboardButton[][];
  keyboard?: { text: string }[][];
  resize_keyboard?: boolean;
  one_time_keyboard?: boolean;
}

export interface SendMessageParams {
  chat_id: string | number;
  text: string;
  message_thread_id?: number;
  reply_parameters?: { message_id: number; allow_sending_without_reply?: boolean };
  /** Interactive affordances (C7): rows of inline buttons; the client
   *  serializes the markup (a bot cannot author AS the presser, so buttons
   *  carry the prefill law). */
  reply_markup?: ReplyMarkup;
}

export interface SendMediaParams {
  chat_id: string | number;
  /** Existing file_id or multipart-upload bytes (the client decides the transport). */
  media: { fileId: string } | { bytes: Uint8Array; fileName: string };
  caption?: string;
  message_thread_id?: number;
  reply_parameters?: { message_id: number; allow_sending_without_reply?: boolean };
  reply_markup?: ReplyMarkup;
}

export interface SetMessageReactionParams {
  chat_id: string | number;
  message_id: number;
  reaction: { type: "emoji"; emoji: string }[];
}

export interface SetWebhookParams {
  url: string;
  secret_token: string;
  max_connections?: number;
  allowed_updates?: string[];
  drop_pending_updates?: boolean;
}

/** The test seam: everything the adapter does to Telegram. */
export interface TelegramApi {
  getUpdates(params: GetUpdatesParams): Promise<TgUpdate[]>;
  deleteWebhook(params: { drop_pending_updates: boolean }): Promise<void>;
  setWebhook(params: SetWebhookParams): Promise<void>;
  getMe(): Promise<TgUser>;
  getFile(fileId: string): Promise<TgFile>;
  /** GET <apiBase>/file/bot<token>/<filePath> — bots download ≤ 20 MB. */
  downloadFile(filePath: string): Promise<Uint8Array>;
  /** Returns the created Message. */
  sendMessage(params: SendMessageParams): Promise<TgMessage>;
  sendPhoto(params: SendMediaParams): Promise<TgMessage>;
  sendDocument(params: SendMediaParams): Promise<TgMessage>;
  /** Settle a button press (the spinner law: an unanswered callback spins
   *  client-side until timeout). */
  answerCallbackQuery(params: { callback_query_id: string; text?: string }): Promise<void>;
  editMessageText(params: {
    chat_id: string | number;
    message_id: number;
    text: string;
  }): Promise<TgMessage>;
  setMessageReaction(params: SetMessageReactionParams): Promise<void>;
}

interface ApiResponse<T> {
  ok: boolean;
  result?: T;
  description?: string;
  error_code?: number;
}

export class TelegramClient implements TelegramApi {
  private readonly base: string;

  constructor(
    apiBase: string,
    private readonly botToken: string,
    private readonly fetchImpl: typeof fetch = fetch,
  ) {
    this.base = apiBase.replace(/\/+$/, "");
  }

  private url(method: string): string {
    return `${this.base}/bot${this.botToken}/${method}`;
  }

  private async call<T>(method: string, body: Record<string, unknown>): Promise<T> {
    const response = await this.fetchImpl(this.url(method), {
      method: "POST",
      headers: { "content-type": "application/json" },
      body: JSON.stringify(body),
    });
    let payload: ApiResponse<T>;
    try {
      payload = (await response.json()) as ApiResponse<T>;
    } catch {
      payload = { ok: false };
    }
    if (!response.ok || payload.ok !== true || payload.result === undefined) {
      throw new TgError(method, response.status, payload.description);
    }
    return payload.result;
  }

  private async callMultipart<T>(
    method: string,
    fields: Record<string, string>,
    file: { name: string; field: string; bytes: Uint8Array; contentType: string },
  ): Promise<T> {
    const form = new FormData();
    for (const [key, value] of Object.entries(fields)) form.append(key, value);
    form.append(
      file.field,
      new Blob([new Uint8Array(file.bytes)], { type: file.contentType }),
      file.name,
    );
    // multipart/form-data is a plain fetch body: NO content-type header —
    // fetch mints the boundary itself.
    const response = await this.fetchImpl(this.url(method), { method: "POST", body: form });
    let payload: ApiResponse<T>;
    try {
      payload = (await response.json()) as ApiResponse<T>;
    } catch {
      payload = { ok: false };
    }
    if (!response.ok || payload.ok !== true || payload.result === undefined) {
      throw new TgError(method, response.status, payload.description);
    }
    return payload.result;
  }

  async getUpdates(params: GetUpdatesParams): Promise<TgUpdate[]> {
    return await this.call<TgUpdate[]>("getUpdates", prune({ ...params }));
  }

  async deleteWebhook(params: { drop_pending_updates: boolean }): Promise<void> {
    await this.call<boolean>("deleteWebhook", { ...params });
  }

  async setWebhook(params: SetWebhookParams): Promise<void> {
    await this.call<boolean>("setWebhook", prune({ ...params }));
  }

  async getMe(): Promise<TgUser> {
    return await this.call<TgUser>("getMe", {});
  }

  async getFile(fileId: string): Promise<TgFile> {
    return await this.call<TgFile>("getFile", { file_id: fileId });
  }

  async downloadFile(filePath: string): Promise<Uint8Array> {
    const response = await this.fetchImpl(
      `${this.base}/file/bot${this.botToken}/${filePath.replace(/^\/+/, "")}`,
    );
    if (!response.ok) throw new TgError("downloadFile", response.status);
    return new Uint8Array(await response.arrayBuffer());
  }

  async answerCallbackQuery(params: { callback_query_id: string; text?: string }): Promise<void> {
    await this.call<boolean>("answerCallbackQuery", prune({ ...params }));
  }

  async sendMessage(params: SendMessageParams): Promise<TgMessage> {
    // sendMessage text law: ≤ 4096 — the HANDLER chunks; the client fails
    // closed rather than letting Telegram truncate silently.
    // JSON-body law: reply_markup rides as the nested object Telegram's
    // application/json mode takes (multipart would need JSON.stringify -
    // sendMedia does that for its form fields).
    return await this.call<TgMessage>("sendMessage", prune({ ...params }));
  }

  async sendPhoto(params: SendMediaParams): Promise<TgMessage> {
    return await this.sendMedia("photo", params);
  }

  async sendDocument(params: SendMediaParams): Promise<TgMessage> {
    return await this.sendMedia("document", params);
  }

  private async sendMedia(method: string, params: SendMediaParams): Promise<TgMessage> {
    const fields: Record<string, string> = {
      chat_id: String(params.chat_id),
      ...(params.reply_markup === undefined
        ? {}
        : { reply_markup: JSON.stringify(params.reply_markup) }),
      ...(params.caption === undefined ? {} : { caption: params.caption }),
      ...(params.message_thread_id === undefined
        ? {}
        : { message_thread_id: String(params.message_thread_id) }),
      ...(params.reply_parameters === undefined
        ? {}
        : { reply_parameters: JSON.stringify(params.reply_parameters) }),
    };
    if ("fileId" in params.media) {
      return await this.call<TgMessage>(`send${cap(method)}`, {
        ...fields,
        [method]: params.media.fileId,
      });
    }
    return await this.callMultipart<TgMessage>(`send${cap(method)}`, fields, {
      field: method,
      name: params.media.fileName,
      bytes: params.media.bytes,
      contentType: "application/octet-stream",
    });
  }

  async editMessageText(params: {
    chat_id: string | number;
    message_id: number;
    text: string;
  }): Promise<TgMessage> {
    return await this.call<TgMessage>("editMessageText", { ...params });
  }

  async setMessageReaction(params: SetMessageReactionParams): Promise<void> {
    await this.call<boolean>("setMessageReaction", { ...params });
  }
}

function cap(method: string): string {
  return method[0]!.toUpperCase() + method.slice(1);
}

function prune(body: Record<string, unknown>): Record<string, unknown> {
  const out: Record<string, unknown> = {};
  for (const [key, value] of Object.entries(body)) {
    if (value !== undefined) out[key] = value;
  }
  return out;
}
