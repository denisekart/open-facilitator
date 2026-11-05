// The resume ledger: `<workDir>/sessions.json`, a JSON map keyed
// `${executionId}:${executionGeneration}` → `{threadId, meta}` plus an
// `assign:<assignmentId>` pointer to the newest generation's entry. The
// file survives worker restarts (the reconcile law's honest answer for a
// lost child is `unknown` ⇒ lost, so a restart simply stops resuming).
// Writes are atomic (tmp + rename) and the store is the single writer per
// worker process.
import { existsSync, mkdirSync, readFileSync, renameSync, writeFileSync } from "node:fs";
import { dirname } from "node:path";
import type { SessionStore } from "@ofac/adapter";

export interface SessionEntry {
  readonly threadId: string;
  readonly assignmentId: string;
  readonly workId: string;
  readonly generation: number;
  readonly savedAt: string;
}

export interface ResumePointer {
  readonly threadId: string;
  readonly generation: number;
}

/** The harness's own ledger metadata for one saved native thread. */
export type SessionMeta = Omit<SessionEntry, "threadId" | "savedAt">;

export class FileSessionStore implements SessionStore {
  constructor(private readonly path: string) {}

  private read(): Record<string, SessionEntry> {
    try {
      const raw = JSON.parse(readFileSync(this.path, "utf8")) as unknown;
      return typeof raw === "object" && raw !== null ? (raw as Record<string, SessionEntry>) : {};
    } catch {
      return {}; // missing or corrupt: an empty ledger, never a crash
    }
  }

  private writeAll(map: Record<string, SessionEntry>): void {
    mkdirSync(dirname(this.path), { recursive: true, mode: 0o700 });
    const tmp = `${this.path}.tmp`;
    writeFileSync(tmp, JSON.stringify(map, null, 2));
    renameSync(tmp, this.path);
  }

  /** The SessionStore seam: the SDK's law keys plain executionId. */
  get(executionId: string): string | undefined {
    return this.read()[executionId]?.threadId;
  }

  set(executionId: string, threadId: string): void {
    this.save(executionId, threadId, {
      assignmentId: executionId,
      workId: executionId,
      generation: 0,
    });
  }

  delete(executionId: string): void {
    const map = this.read();
    if (!(executionId in map)) return;
    delete map[executionId];
    this.writeAll(map);
  }

  /** `key` is the composite `${executionId}:${generation}` (our own rows). */
  save(key: string, threadId: string, meta: SessionMeta): void {
    const map = this.read();
    const entry: SessionEntry = { threadId, ...meta, savedAt: new Date().toISOString() };
    map[key] = entry;
    map[`assign:${meta.assignmentId}`] = entry; // newest-generation pointer wins
    this.writeAll(map);
  }

  /** The resume lookup: the newest saved generation for this assignment. */
  findLatestForAssignment(assignmentId: string): ResumePointer | undefined {
    const entry = this.read()[`assign:${assignmentId}`];
    if (entry === undefined || typeof entry.threadId !== "string") return undefined;
    return { threadId: entry.threadId, generation: entry.generation };
  }

  /** Test/reconcile inspection. */
  entries(): Readonly<Record<string, SessionEntry>> {
    return this.read();
  }

  exists(): boolean {
    return existsSync(this.path);
  }
}
