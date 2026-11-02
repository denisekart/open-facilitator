// The adapter's durable state on better-sqlite3, alongside (not inside) the
// SDK's SqliteLedger which owns the delivery ledger in the SAME file:
//   posts       — the idempotency law: a command's idempotencyKey
//                 maps to the provider object it created; replay answers with
//                 the stored id without touching Mattermost.
//   channels    — channel-type cache (MM WS events carry no channel type).
//   bound_roots — thread roots the adapter participated in (the outbound
//                 posts' root_id): the bound-thread classifier.
//   kv          — cursor state (last_seen create_at for startup replay).
import { chmodSync, existsSync } from "node:fs";
import Database from "better-sqlite3";

const SCHEMA_VERSION = 1;

const DDL = `
CREATE TABLE IF NOT EXISTS posts (
  idempotency_key TEXT PRIMARY KEY,
  provider_object_id TEXT NOT NULL,
  channel_id    TEXT NOT NULL,
  root_id       TEXT NOT NULL,
  kind          TEXT NOT NULL CHECK (kind IN ('post','reaction')),
  created_at    INTEGER NOT NULL
);
CREATE TABLE IF NOT EXISTS channels (
  channel_id TEXT PRIMARY KEY,
  channel_type TEXT NOT NULL CHECK (channel_type IN ('O','P','D'))
);
CREATE TABLE IF NOT EXISTS bound_roots (
  root_id TEXT PRIMARY KEY,
  channel_id TEXT NOT NULL,
  seen_at INTEGER NOT NULL
);
CREATE TABLE IF NOT EXISTS kv (
  key TEXT PRIMARY KEY,
  value TEXT NOT NULL
);
`;

export interface PostLedgerEntry {
  idempotencyKey: string;
  providerObjectId: string;
  channelId: string;
  rootId: string;
  kind: "post" | "reaction";
  createdAt: number;
}

interface PostRow {
  idempotency_key: string;
  provider_object_id: string;
  channel_id: string;
  root_id: string;
  kind: "post" | "reaction";
  created_at: number;
}

export type ChannelType = "O" | "P" | "D";

const POST_SELECT =
  "SELECT idempotency_key, provider_object_id, channel_id, root_id, kind, created_at FROM posts WHERE ";

function toEntry(row: PostRow): PostLedgerEntry {
  return {
    idempotencyKey: row.idempotency_key,
    providerObjectId: row.provider_object_id,
    channelId: row.channel_id,
    rootId: row.root_id,
    kind: row.kind,
    createdAt: row.created_at,
  };
}

export class AdapterState {
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
        `adapter state: unsupported schema version ${version} (this build writes ${SCHEMA_VERSION})`,
      );
    }
  }

  /** The replay-law lookup: the stored result of a previously executed command. */
  findPost(idempotencyKey: string): PostLedgerEntry | undefined {
    const row = this.db.prepare(`${POST_SELECT}idempotency_key = ?`).get(idempotencyKey) as
      PostRow | undefined;
    return row === undefined ? undefined : toEntry(row);
  }

  /** The update law: prove the adapter created this provider object before editing it. */
  findPostByObjectId(providerObjectId: string): PostLedgerEntry | undefined {
    const row = this.db
      .prepare(`${POST_SELECT}provider_object_id = ? AND kind = 'post'`)
      .get(providerObjectId) as PostRow | undefined;
    return row === undefined ? undefined : toEntry(row);
  }

  recordPost(entry: PostLedgerEntry): void {
    this.db
      .prepare(
        "INSERT INTO posts (idempotency_key, provider_object_id, channel_id, root_id, kind, created_at) VALUES (?, ?, ?, ?, ?, ?) ON CONFLICT(idempotency_key) DO NOTHING",
      )
      .run(
        entry.idempotencyKey,
        entry.providerObjectId,
        entry.channelId,
        entry.rootId,
        entry.kind,
        entry.createdAt,
      );
    if (entry.kind === "post" && entry.rootId !== "") {
      this.markBoundRoot(entry.rootId, entry.channelId, entry.createdAt);
    }
  }

  cacheChannelType(channelId: string, channelType: ChannelType): void {
    this.db
      .prepare(
        "INSERT INTO channels (channel_id, channel_type) VALUES (?, ?) ON CONFLICT(channel_id) DO UPDATE SET channel_type = excluded.channel_type",
      )
      .run(channelId, channelType);
  }

  channelType(channelId: string): ChannelType | undefined {
    const row = this.db
      .prepare("SELECT channel_type FROM channels WHERE channel_id = ?")
      .get(channelId) as { channel_type: ChannelType } | undefined;
    return row?.channel_type;
  }

  markBoundRoot(rootId: string, channelId: string, seenAt: number): void {
    this.db
      .prepare(
        "INSERT INTO bound_roots (root_id, channel_id, seen_at) VALUES (?, ?, ?) ON CONFLICT(root_id) DO NOTHING",
      )
      .run(rootId, channelId, seenAt);
  }

  boundRootIds(): Set<string> {
    const rows = this.db.prepare("SELECT root_id FROM bound_roots").all() as {
      root_id: string;
    }[];
    return new Set(rows.map((r) => r.root_id));
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

  close(): void {
    this.db.close();
  }
}
