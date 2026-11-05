// The git surface of the omp worker: a spawn-based runner (NO shell —
// args arrays only, cwd + env pass-through), so a repo name or a PAT that
// contains shell metacharacters can never interpolate. Copied from
// examples/full/coding-worker/src/git.ts (no cross-package import of an
// example); the omp adapter's law: a full clone URL from OFAC_GIT_CLONE_URL
// (the example assembles it from a provider origin + a token FILE; here the
// URL arrives whole and OFAC_GIT_TOKEN is read AT EVERY RUN, rotation law).
import { spawn } from "node:child_process";

export interface GitRunResult {
  readonly code: number;
  readonly stdout: string;
  readonly stderr: string;
}

export interface GitRunOptions {
  readonly cwd?: string | undefined;
  readonly env?: Readonly<Record<string, string>> | undefined;
  /** Secrets to redact from thrown text (the PAT). */
  readonly scrub?: readonly string[] | undefined;
}

export class GitError extends Error {
  constructor(command: string, detail: string) {
    super(`git ${command} failed: ${detail}`);
    this.name = "GitError";
  }
}

/** Replace every occurrence of each secret with `[redacted]`. */
export function scrubSecrets(text: string, secrets: readonly string[]): string {
  let out = text;
  for (const secret of secrets) {
    if (secret.length === 0) continue;
    out = out.split(secret).join("[redacted]");
  }
  return out;
}

/** Run one tool with an argv array; resolves with the full output, never throws on exit code. */
export function runTool(
  bin: string,
  args: readonly string[],
  options: GitRunOptions = {},
): Promise<GitRunResult> {
  return new Promise((resolve, reject) => {
    const child = spawn(bin, [...args], {
      ...(options.cwd !== undefined ? { cwd: options.cwd } : {}),
      env: {
        ...(options.env ?? process.env),
        // No interactive credential prompts, ever: an auth failure must
        // surface as a non-zero exit, not a hung worker.
        GIT_TERMINAL_PROMPT: "0",
      },
      stdio: ["ignore", "pipe", "pipe"],
    });
    const out: Buffer[] = [];
    const err: Buffer[] = [];
    child.stdout.on("data", (chunk: Buffer) => out.push(chunk));
    child.stderr.on("data", (chunk: Buffer) => err.push(chunk));
    child.on("error", (error) => reject(new GitError(describe(bin, args), error.message)));
    child.on("close", (code) => {
      const stdout = Buffer.concat(out).toString("utf8");
      const stderr = Buffer.concat(err).toString("utf8");
      if (code !== 0) {
        const detail = scrubSecrets(
          stderr.trim() === "" ? `exit code ${String(code)}` : stderr.trim(),
          options.scrub ?? [],
        );
        reject(new GitError(describe(bin, args), detail));
        return;
      }
      resolve({ code, stdout, stderr });
    });
  });
}

function describe(bin: string, args: readonly string[]): string {
  return [bin, ...args].join(" ");
}

/**
 * Inject Basic credentials into an http(s) clone URL; any other transport
 * (local path, ssh, file) is returned verbatim. The PAT is percent-encoded
 * so a token with reserved bytes stays one opaque userinfo component.
 */
export function injectToken(cloneUrl: string, user: string, pat: string): string {
  if (!/^https?:\/\//.test(cloneUrl)) return cloneUrl;
  const scheme = cloneUrl.slice(0, cloneUrl.indexOf("://"));
  const rest = cloneUrl.slice(scheme.length + 3);
  return `${scheme}://${encodeURIComponent(user)}:${encodeURIComponent(pat)}@${rest}`;
}

export async function gitClone(
  url: string,
  dest: string,
  scrub: readonly string[] = [],
): Promise<void> {
  await runTool("git", ["clone", "--", url, dest], { scrub });
}

/**
 * Create-or-reset `<branch>` at the remote branch's tip when one exists
 * (the reconcile path: a re-dispatch continues the pushed lineage), else at
 * the current HEAD.
 */
export async function checkoutBranch(repoDir: string, branch: string): Promise<void> {
  const remote = await runTool(
    "git",
    ["rev-parse", "--verify", "--quiet", `refs/remotes/origin/${branch}`],
    { cwd: repoDir },
  ).catch(() => null);
  if (remote !== null) {
    await runTool("git", ["checkout", "-B", branch, "--track", `origin/${branch}`], {
      cwd: repoDir,
    });
    return;
  }
  await runTool("git", ["checkout", "-B", branch], { cwd: repoDir });
}
