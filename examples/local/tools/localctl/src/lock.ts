// The env lockfile: `envs/<env>.lock.json` — the image refs every render
// resolved, plus the old→new history `localctl upgrade` appends. Committed
// (git-checked): a change to it is a reviewable digest roll.
import { existsSync, readFileSync, writeFileSync } from "node:fs";
import path from "node:path";
import { fail } from "./util.js";

export interface LockEntry {
  image: string;
  resolved: string;
  from: string;
  timestamp: string;
}

export interface Lockfile {
  resolved: Record<string, string>;
  history: LockEntry[];
}

export type ImageKey = "kernel" | "node" | "worker";

export function lockPath(exampleDir: string, envName: string): string {
  return path.join(exampleDir, "envs", `${envName}.lock.json`);
}

export function loadLock(exampleDir: string, envName: string): Lockfile {
  const file = lockPath(exampleDir, envName);
  if (!existsSync(file)) return { resolved: {}, history: [] };
  const parsed: unknown = JSON.parse(readFileSync(file, "utf8"));
  if (parsed === null || typeof parsed !== "object" || Array.isArray(parsed))
    fail(`${file}:? expected an object`);
  const j = parsed as Record<string, unknown>;
  const resolved = j["resolved"];
  const history = j["history"];
  return {
    resolved:
      resolved !== null && typeof resolved === "object" && !Array.isArray(resolved)
        ? (resolved as Record<string, string>)
        : {},
    history: Array.isArray(history) ? (history as LockEntry[]) : [],
  };
}

export function saveLock(exampleDir: string, envName: string, lock: Lockfile): void {
  writeFileSync(lockPath(exampleDir, envName), `${JSON.stringify(lock, null, 2)}\n`);
}
