// The adapter's durable state on better-sqlite3, alongside (not inside) the
// SDK's SqliteLedger which owns the delivery ledger in the SAME file:
//   commands — the idempotency law: a command's idempotencyKey maps to the
//              provider object(s) it created; replay answers with the stored
//              id without touching Telegram.
//   messages — provider message ids the ADAPTER created: the only messages
//              editMessageText may touch, and the seed of boundRootIds.
//   topics   — forum topic ids this adapter's chat participates in as roots:
//              a reply target that names one sends WITH message_thread_id.
//   files    — the media dedupe ledger (file_unique_id → artifactId): an
//              image forwarded twice costs one upload and one ref.
//   kv       — cursor + identity state (getUpdates offset, bot id, cached
//              chat types, seen update_ids, onboarding-notice guards).
import { chmodSync, existsSync } from "node:fs";
import Database from "better-sqlite3";

const SCHEMA_VERSION = 1;

const DDL = `
CREATE TABLE IF NOT EXISTS commands (
  idempotency_key TEXT PRIMARY KEY,
  provider_object_id TEXT NOT NULL,
  chat_id       TEXT NOT NULL,
  root_id       TEXT NOT NULL,
  kind          TEXT NOT NULL CHECK (kind IN ('message','photo','document','reaction','edit')),
  created_at    INTEGER NOT NULL
);
CREATE TABLE IF NOT EXISTS messages (
  provider_object_id TEXT PRIMARY KEY,
  chat_id       TEXT NOT NULL,
  root_id       TEXT NOT NULL,
  created_at    INTEGER NOT NULL
);
CREATE TABLE IF NOT EXISTS topics (
  chat_id TEXT NOT NULL,
  topic_id TEXT NOT NULL,
  seen_at INTEGER NOT NULL,
  PRIMARY KEY (chat_id, topic_id)
);
CREATE TABLE IF NOT EXISTS files (
  file_unique_id TEXT PRIMARY KEY,
  artifact_id   TEXT NOT NULL,
  media_type    TEXT NOT NULL,
  size          INTEGER NOT NULL,
  digest        TEXT NOT NULL,
  seen_at       INTEGER NOT NULL
);
CREATE TABLE IF NOT EXISTS kv (
  key TEXT PRIMARY KEY,
  value TEXT NOT NULL
);
`;

export interface CommandLedgerEntry {
  idempotencyKey: string;
  /** The provider message id(s) created — a JSON array when a chunked reply
   *  or a multi-artifact send produced more than one. */
  providerObjectId: string;
  chatId: string;
  rootId: string;
  kind: "message" | "photo" | "document" | "reaction" | "edit";
  createdAt: number;
}

interface CommandRow {
  idempotency_key: string;
  provider_object_id: string;
  chat_id: string;
  root_id: string;
  kind: CommandLedgerEntry["kind"];
  created_at: number;
}

export type ChatType = "private" | "group" | "supergroup" | "channel";

export interface FileLedgerEntry {
  fileUniqueId: string;
  artifactId: string;
  mediaType: string;
  size: number;
  digest: string;
}

interface FileRow {
  file_unique_id: string;
  artifact_id: string;
  media_type: string;
  size: number;
  digest: string;
}

const COMMAND_SELECT =
  "SELECT idempotency_key, provider_object_id, chat_id, root_id, kind, created_at FROM commands WHERE ";

function toEntry(row: CommandRow): CommandLedgerEntry {
  return {
    idempotencyKey: row.idempotency_key,
    providerObjectId: row.provider_object_id,
    chatId: row.chat_id,
    rootId: row.root_id,
    kind: row.kind,
    createdAt: row.created_at,
  };
}

export class ChatLedger {
  private readonly db: Database.Database;

  constructor(path: string) {
    this.db = new Database(path);
    if (existsSync(path)) chmodSync(path, 0o600);
    this.db.pragma("journal_mode = WAL");
    const version = this.db.pragma("user_version", { simple: true }) as number;
    if (version === 0) {
      this.db.exec(`BEGIN IMMEDIATE;${DDL}PRAGMA user_version = ${SCHEMA_VERSION};COMMIT;`);
    } else if (version !== SCHEMA_VERSION) {
      throw new Error(
        `chat ledger: unsupported schema version ${version} (this build writes ${SCHEMA_VERSION})`,
      );
    }
  }

  /** The replay-law lookup: the stored result of a previously executed command. */
  findCommand(idempotencyKey: string): CommandLedgerEntry | undefined {
    const row = this.db.prepare(`${COMMAND_SELECT}idempotency_key = ?`).get(idempotencyKey) as
      CommandRow | undefined;
    return row === undefined ? undefined : toEntry(row);
  }

  /** The update law: prove the adapter created this provider message before
   *  editing it (mattermost's findPostByObjectId analogue). */
  isAdapterMessage(providerObjectId: string): boolean {
    return (
      this.db
        .prepare("SELECT 1 FROM messages WHERE provider_object_id = ?")
        .get(providerObjectId) !== undefined
    );
  }

  /** Record an outbound provider message under the command's idempotency key
   *  AND into the adapter-authored set (the edit law + bound roots). */
  recordCommand(entry: CommandLedgerEntry): void {
    this.db
      .prepare(
        "INSERT INTO commands (idempotency_key, provider_object_id, chat_id, root_id, kind, created_at) VALUES (?, ?, ?, ?, ?, ?) ON CONFLICT(idempotency_key) DO NOTHING",
      )
      .run(
        entry.idempotencyKey,
        entry.providerObjectId,
        entry.chatId,
        entry.rootId,
        entry.kind,
        entry.createdAt,
      );
    if (entry.kind !== "reaction") {
      for (const id of parseObjectIds(entry.providerObjectId)) {
        this.markAdapterMessage(id, entry.chatId, entry.rootId, entry.createdAt);
      }
    }
    if (entry.rootId !== "" && entry.kind !== "reaction") {
      this.markKvBoundRoot(entry.rootId);
    }
  }

  markAdapterMessage(providerObjectId: string, chatId: string, rootId: string, at: number): void {
    this.db
      .prepare(
        "INSERT INTO messages (provider_object_id, chat_id, root_id, created_at) VALUES (?, ?, ?, ?) ON CONFLICT(provider_object_id) DO NOTHING",
      )
      .run(providerObjectId, chatId, rootId, at);
  }

  /** The topic law: a numeric reply target recorded here is a forum topic,
   *  so an outbound send rides message_thread_id, not reply_parameters. */
  markTopic(chatId: string, topicId: string, at: number): void {
    this.db
      .prepare(
        "INSERT INTO topics (chat_id, topic_id, seen_at) VALUES (?, ?, ?) ON CONFLICT(chat_id, topic_id) DO NOTHING",
      )
      .run(chatId, topicId, at);
  }

  isTopic(chatId: string, topicId: string): boolean {
    return (
      this.db
        .prepare("SELECT 1 FROM topics WHERE chat_id = ? AND topic_id = ?")
        .get(chatId, topicId) !== undefined
    );
  }

  /** Bound roots: adapter-created message ids + recorded topic roots. The
   *  classifier's authority for "the adapter participates here". */
  boundRootIds(): Set<string> {
    const roots = new Set<string>();
    for (const row of this.db.prepare("SELECT provider_object_id FROM messages").all() as {
      provider_object_id: string;
    }[]) {
      roots.add(row.provider_object_id);
    }
    for (const row of this.db.prepare("SELECT topic_id FROM topics").all() as {
      topic_id: string;
    }[]) {
      roots.add(row.topic_id);
    }
    for (const row of this.db.prepare("SELECT root_id FROM commands WHERE root_id <> ''").all() as {
      root_id: string;
    }[]) {
      roots.add(row.root_id);
    }
    for (const row of this.db.prepare("SELECT value FROM kv WHERE key LIKE 'bound:%'").all() as {
      value: string;
    }[]) {
      roots.add(row.value);
    }
    return roots;
  }

  // ── media dedupe ledger ──────────────────────────────────────────────────

  findFile(fileUniqueId: string): FileLedgerEntry | undefined {
    const row = this.db
      .prepare(
        "SELECT file_unique_id, artifact_id, media_type, size, digest FROM files WHERE file_unique_id = ?",
      )
      .get(fileUniqueId) as FileRow | undefined;
    return row === undefined
      ? undefined
      : {
          fileUniqueId: row.file_unique_id,
          artifactId: row.artifact_id,
          mediaType: row.media_type,
          size: row.size,
          digest: row.digest,
        };
  }

  /** Record BEFORE publish: a redelivered update converges without a second
   *  upload. */
  recordFile(entry: FileLedgerEntry): void {
    this.db
      .prepare(
        "INSERT INTO files (file_unique_id, artifact_id, media_type, size, digest, seen_at) VALUES (?, ?, ?, ?, ?, ?) ON CONFLICT(file_unique_id) DO UPDATE SET artifact_id = excluded.artifact_id, media_type = excluded.media_type, size = excluded.size, digest = excluded.digest, seen_at = excluded.seen_at",
      )
      .run(
        entry.fileUniqueId,
        entry.artifactId,
        entry.mediaType,
        entry.size,
        entry.digest,
        Date.now(),
      );
  }

  // ── kv ───────────────────────────────────────────────────────────────────

  markKvBoundRoot(rootId: string): void {
    this.db
      .prepare("INSERT OR IGNORE INTO kv (key, value) VALUES ('bound:' || ?, ?)")
      .run(rootId, rootId);
  }

  getKv(key: string): string | undefined {
    const row = this.db.prepare("SELECT value FROM kv WHERE key = ?").get(key) as
      { value: string } | undefined;
    return row?.value;
  }

  setKv(key: string, value: string): void {
    this.db
      .prepare(
        "INSERT INTO kv (key, value) VALUES (?, ?) ON CONFLICT(key) DO UPDATE SET value = excluded.value",
      )
      .run(key, value);
  }

  /** True the first time, false afterwards: the onboarding-notice guard
   *  (`notice:<chatId>`) and the update_id dedupe use this. */
  setKvOnce(key: string, value: string): boolean {
    const info = this.db
      .prepare("INSERT OR IGNORE INTO kv (key, value) VALUES (?, ?)")
      .run(key, value);
    return info.changes > 0;
  }

  cacheChatType(chatId: string, chatType: ChatType): void {
    this.setKv(`chattype:${chatId}`, chatType);
  }

  chatType(chatId: string): ChatType | undefined {
    return this.getKv(`chattype:${chatId}`) as ChatType | undefined;
  }

  close(): void {
    this.db.close();
  }
}

/** The stored provider_object_id is either a bare id or the JSON array a
 *  chunked/multi-attachment send recorded. */
export function parseObjectIds(stored: string): string[] {
  if (stored.startsWith("[")) {
    try {
      const parsed: unknown = JSON.parse(stored);
      if (Array.isArray(parsed) && parsed.every((v) => typeof v === "string")) {
        return parsed as string[];
      }
    } catch {
      /* fall through: treat as a bare id */
    }
  }
  return [stored];
}
