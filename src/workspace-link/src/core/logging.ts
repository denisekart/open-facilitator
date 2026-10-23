// Default console logger with correlation-ID JSON fields (appendix §9 /
// A:281): every line carries whatever of {workId, assignmentId, deliveryId,
// endpointId, reason} the call site supplied.
import type { LinkLogger } from "./types.js";

const LEVELS = { debug: 10, info: 20, warn: 30, error: 40 } as const;

export interface ConsoleLoggerOptions {
  level?: keyof typeof LEVELS;
  stream?: { write(chunk: string): void };
}

export function createConsoleLogger(options: ConsoleLoggerOptions = {}): LinkLogger {
  const threshold = LEVELS[options.level ?? "info"];
  const stream = options.stream ?? process.stderr;
  function emit(level: keyof typeof LEVELS, msg: string, fields?: Record<string, unknown>): void {
    if (LEVELS[level] < threshold) return;
    stream.write(
      `${JSON.stringify({ ts: new Date().toISOString(), level, msg, ...(fields ?? {}) })}\n`,
    );
  }
  return {
    debug: (m, f) => emit("debug", m, f),
    info: (m, f) => emit("info", m, f),
    warn: (m, f) => emit("warn", m, f),
    error: (m, f) => emit("error", m, f),
  };
}

/** Collects lines in memory — used by tests and by linkd's test harness. */
export function createMemoryLogger(): LinkLogger & { lines: string[] } {
  const lines: string[] = [];
  return {
    lines,
    debug: (m, f) => lines.push(JSON.stringify({ level: "debug", msg: m, ...f })),
    info: (m, f) => lines.push(JSON.stringify({ level: "info", msg: m, ...f })),
    warn: (m, f) => lines.push(JSON.stringify({ level: "warn", msg: m, ...f })),
    error: (m, f) => lines.push(JSON.stringify({ level: "error", msg: m, ...f })),
  };
}
