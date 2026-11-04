// Shared process/HTTP primitives for localctl. Everything external is a
// argv-spawned child (docker/ssh/scp/tar — never a shell), so quoting bugs
// cannot smuggle themselves in through the roles file.
import { spawn } from "node:child_process";

export class LocalctlError extends Error {}

export function fail(message: string): never {
  throw new LocalctlError(message);
}

export interface RunResult {
  code: number;
  stdout: string;
  stderr: string;
}

export interface RunOpts {
  cwd?: string;
  env?: Record<string, string>;
  /** Print the command line before running (operator visibility). */
  echo?: boolean;
  /** Inherit stdio (interactive-ish legs: logs, compose progress). */
  inherit?: boolean;
  input?: string;
}

export async function run(argv: string[], opts: RunOpts = {}): Promise<RunResult> {
  const [cmd, ...args] = argv;
  if (cmd === undefined) fail("run: empty command");
  if (opts.echo) console.log(`$ ${argv.join(" ")}`);
  const { promise, resolve, reject } = Promise.withResolvers<RunResult>();
  const child = spawn(cmd, args, {
    cwd: opts.cwd,
    env: opts.env === undefined ? process.env : { ...process.env, ...opts.env },
    stdio: opts.inherit === true ? "inherit" : ["pipe", "pipe", "pipe"],
  });
  let stdout = "";
  let stderr = "";
  child.stdout?.on("data", (d: Buffer) => (stdout += d.toString("utf8")));
  child.stderr?.on("data", (d: Buffer) => (stderr += d.toString("utf8")));
  child.on("error", reject);
  child.on("close", (code) => resolve({ code: code ?? -1, stdout, stderr }));
  if (opts.input !== undefined && child.stdin !== null) {
    child.stdin.end(opts.input);
  }
  return await promise;
}

/** run() but a non-zero exit is an error carrying the captured output. */
export async function must(argv: string[], opts: RunOpts = {}): Promise<string> {
  const r = await run(argv, opts);
  if (r.code !== 0) {
    const out = [r.stdout.trim(), r.stderr.trim()].filter((s) => s !== "").join("\n");
    fail(`command failed (${r.code}): ${argv.join(" ")}\n${out}`);
  }
  return r.stdout;
}

export async function sleep(ms: number): Promise<void> {
  await new Promise((res) => setTimeout(res, ms));
}

/** Poll `probe` until it returns a truthy value or the deadline passes. */
export async function poll<T>(
  seconds: number,
  intervalMs: number,
  probe: () => Promise<T | undefined | false | "" | null>,
): Promise<T | undefined> {
  const deadline = Date.now() + seconds * 1000;
  for (;;) {
    const value = await probe();
    if (value) return value;
    if (Date.now() >= deadline) return undefined;
    await sleep(intervalMs);
  }
}

export interface HttpResponse {
  status: number;
  body: string;
}

export async function http(
  method: string,
  url: string,
  opts: { token?: string; json?: unknown; headers?: Record<string, string>; timeoutMs?: number } = {},
): Promise<HttpResponse> {
  const headers: Record<string, string> = { accept: "application/json", ...opts.headers };
  if (opts.token !== undefined) headers.authorization = `Bearer ${opts.token}`;
  let body: string | undefined;
  if (opts.json !== undefined) {
    body = JSON.stringify(opts.json);
    headers["content-type"] = "application/json";
  }
  const response = await fetch(url, {
    method,
    headers,
    ...(body === undefined ? {} : { body }),
    signal: AbortSignal.timeout(opts.timeoutMs ?? 15_000),
  });
  return { status: response.status, body: await response.text() };
}

export async function getJson<T>(url: string, token: string): Promise<T> {
  const r = await http("GET", url, { token });
  if (r.status !== 200) fail(`GET ${url} -> ${r.status}: ${r.body.slice(0, 240)}`);
  return JSON.parse(r.body) as T;
}

export function table(rows: (string | number)[][], headers: string[]): string {
  const all = [headers, ...rows];
  const widths = headers.map((_, i) => Math.max(...all.map((r) => String(r[i] ?? "").length)));
  const line = (r: (string | number)[]): string =>
    r.map((c, i) => String(c).padEnd(widths[i] ?? 0)).join("  ");
  return [line(headers), widths.map((w) => "-".repeat(w)).join("  "), ...rows.map(line)].join("\n");
}
