// The durable native-session store: a JSON file at <workDir>/sessions.json
// (0600, rewrite-in-place), keyed by the caller's key — the harness writes
// `<executionId>:<executionGeneration>` so a retry generation can find the
// prior generation's native session id and resume it. Survives worker
// restarts (the file IS the state); the SDK's SessionStore interface is
// satisfied as-is (its executionId parameter is just the key).
import { mkdirSync, readFileSync, renameSync, writeFileSync } from "node:fs";
import { dirname, join } from "node:path";
import type { SessionStore } from "@ofac/adapter";

export class FileSessionStore implements SessionStore {
  private readonly file: string;
  /** Cached parsed map; reload-on-first-use, write-through after. */
  private cache: Record<string, string> | null = null;

  constructor(workDir: string) {
    this.file = join(workDir, "sessions.json");
  }

  private load(): Record<string, string> {
    if (this.cache !== null) return this.cache;
    let parsed: Record<string, string> = {};
    try {
      const raw = JSON.parse(readFileSync(this.file, "utf8")) as unknown;
      if (raw !== null && typeof raw === "object" && !Array.isArray(raw)) {
        for (const [key, value] of Object.entries(raw as Record<string, unknown>)) {
          if (typeof value === "string") parsed[key] = value;
        }
      }
    } catch {
      parsed = {}; // missing/corrupt file: an empty store is the honest read
    }
    this.cache = parsed;
    return parsed;
  }

  private persist(): void {
    mkdirSync(dirname(this.file), { recursive: true, mode: 0o700 });
    const tmp = `${this.file}.tmp`;
    writeFileSync(tmp, JSON.stringify(this.load(), null, 2), { mode: 0o600 });
    renameSync(tmp, this.file);
  }

  get(executionId: string): string | undefined {
    return this.load()[executionId];
  }

  set(executionId: string, sessionRef: string): void {
    this.load()[executionId] = sessionRef;
    this.persist();
  }

  delete(executionId: string): void {
    if (!(executionId in this.load())) return;
    delete this.load()[executionId];
    this.persist();
  }
}
