// The step executor: one step = one spawn (pipes, or node-pty when the
// playbook says `tty: true`), always detached into its own process group,
// killed by the codex law (SIGTERM the GROUP, grace, SIGKILL; an aborted
// run publishes nothing). Streams tee twice: raw bytes for artifacts,
// de-ansified rolling tail windows for `promptMarkers`/`expect` (TUI law:
// match a tail window, not scrollback). No new event classes: the human
// surface is progress notes + input.required only.
import { spawn as nodeSpawn, type ChildProcessWithoutNullStreams } from "node:child_process";
import { readFileSync } from "node:fs";
import { isAbsolute, join } from "node:path";
import type { LinkLogger } from "@ofac/workspace-link";
import { DeansifyWindow } from "./ansi.js";
import { interpolate, type InterpolationScope, type StepRecord } from "./interpolate.js";
import type { PlaybookCapability, PlaybookStep, StepAsk } from "./schema.js";

/** Raw-stream artifact budget: the tail that ships as capture bytes. */
export const RAW_TAIL_BYTES = 262_144;
/** Progress notes carry de-ansified, truncated text. */
const PROGRESS_NOTE_MAX = 400;

export type StepOutcome =
  | { readonly kind: "completed"; readonly steps: readonly ExecutedStep[] }
  | { readonly kind: "failed"; readonly reason: string }
  | { readonly kind: "cancelled" };

export interface ExecutedStep {
  readonly name: string;
  readonly exitCode: number;
  readonly answer?: string | undefined;
  /** Bytes to upload (raw stream tail for stdout/stderr, file bytes for
   * `from: file`); the harness owns the artifact leg. */
  readonly capture?:
    | {
        readonly as: string;
        readonly mediaType: string;
        readonly bytes: Buffer;
      }
    | undefined;
}

export interface StepExecutorHooks {
  publishProgress(note: string): void;
  requestInput(ask: StepAsk): Promise<string>;
  signal: AbortSignal;
  logger: LinkLogger;
  /** Extra env every spawn gets (e.g. the shim bridge address). */
  readonly spawnEnv?: Readonly<Record<string, string>> | undefined;
  /** Extra interpolation slots (e.g. shimCommand once seeded). */
  readonly extraScope?: Partial<InterpolationScope> | undefined;
}

export interface StepExecutorOptions {
  readonly workDir: string;
  readonly killGraceMs: number;
}

interface AttemptResult {
  readonly exitCode: number;
  readonly timedOut: boolean;
  readonly cancelled: boolean;
  readonly stdout: Buffer;
  readonly stderr: Buffer;
  readonly outText: string;
  readonly errText: string;
  readonly unmatchedMarkers: readonly string[];
}

function killGroup(child: { readonly pid?: number | undefined }, killGraceMs: number): void {
  const pid = child.pid;
  if (pid === undefined) return; // never booted
  const signalGroup = (signal: NodeJS.Signals): boolean => {
    try {
      process.kill(-pid, signal);
      return true;
    } catch {
      return false; // group already gone
    }
  };
  if (!signalGroup("SIGTERM")) return;
  const timer = setTimeout(() => {
    signalGroup("SIGKILL");
  }, killGraceMs);
  timer.unref();
}

/** SIGTERM the group (same law for a node-pty child, which has a pid). */
function killPty(pty: { readonly pid: number }, killGraceMs: number): void {
  killGroup({ pid: pty.pid }, killGraceMs);
}

function tail(buffer: Buffer, maxBytes: number): Buffer {
  return buffer.byteLength <= maxBytes ? buffer : buffer.subarray(buffer.byteLength - maxBytes);
}

function truncateNote(text: string): string {
  const single = text.replace(/\s+/g, " ").trim();
  return single.length <= PROGRESS_NOTE_MAX ? single : `${single.slice(0, PROGRESS_NOTE_MAX - 1)}…`;
}

/** One spawn attempt of a `run` step. Never throws; every terminal shape
 * (exit, timeout, abort) comes back as an AttemptResult. */
async function attemptStep(
  step: PlaybookStep,
  scope: InterpolationScope,
  hooks: StepExecutorHooks,
  options: StepExecutorOptions,
): Promise<AttemptResult> {
  const run = step.run;
  if (run === undefined) throw new Error(`step '${step.name}' has no run`);
  const where = `step '${step.name}'`;
  const args = run.args.map((arg, index) =>
    interpolate(scope, `${where}.args[${String(index)}]`, arg),
  );
  const env: NodeJS.ProcessEnv = { ...process.env, ...(hooks.spawnEnv ?? {}) };
  for (const [key, value] of Object.entries(run.env ?? {})) {
    env[key] = interpolate(scope, `${where}.run.env.${key}`, value);
  }
  const cwd =
    run.cwd === undefined
      ? options.workDir
      : isAbsolute(run.cwd)
        ? run.cwd
        : join(options.workDir, run.cwd);

  const outWindow = new DeansifyWindow();
  const errWindow = new DeansifyWindow();
  const outRaw: Buffer[] = [];
  const errRaw: Buffer[] = [];
  const markers = step.promptMarkers.map((marker) => ({ ...marker, fired: false }));

  const abort = hooks.signal;
  let timedOut = false;
  let cancelled = false;

  const settle = Promise.withResolvers<void>();
  let done = false;
  const finish = (): void => {
    if (!done) {
      done = true;
      settle.resolve();
    }
  };

  const timeout = setTimeout(() => {
    timedOut = true;
  }, step.timeoutMs);
  timeout.unref?.();

  // TUI law: match the rolling tail window, not full scrollback. Each
  // marker fires once on its first de-ansified match.
  const drainMarkers = (write: (chunk: string) => void, close?: (() => void) | undefined): void => {
    const text = outWindow.text();
    let allFired = markers.length > 0;
    for (const marker of markers) {
      if (marker.fired) continue;
      if (marker.match.test(text)) {
        marker.fired = true;
        write(`${marker.answer}\n`);
      } else {
        allFired = false;
      }
    }
    if (allFired && close !== undefined) close();
  };

  if (step.tty) {
    // PTY is opt-in per step: node-pty loads only when a `tty: true` step
    // actually spawns (the pipes posture never pays the native binding).
    const pty = await import("node-pty");
    const child = pty.spawn(run.command, args, {
      name: "xterm-256color",
      cols: 120,
      rows: 40,
      cwd,
      env: env as Record<string, string>,
    });
    const onAbort = (): void => {
      cancelled = true;
      killPty(child, options.killGraceMs);
    };
    abort.addEventListener("abort", onAbort, { once: true });
    if (step.feed !== undefined) child.write(step.feed);
    child.onData((chunk) => {
      const buf = Buffer.from(chunk, "utf8");
      outRaw.push(buf);
      outWindow.push(chunk);
      drainMarkers((text) => child.write(text));
    });
    const timeoutWatch = setInterval(() => {
      if (timedOut) {
        clearInterval(timeoutWatch);
        killPty(child, options.killGraceMs);
      }
    }, 50);
    timeoutWatch.unref?.();
    // node-pty exposes the status on the onExit payload, not a property.
    let ptyExitCode = -1;
    child.onExit(({ exitCode }) => {
      ptyExitCode = exitCode;
      clearTimeout(timeout);
      abort.removeEventListener("abort", onAbort);
      finish();
    });
    await settle.promise;
    return {
      exitCode: ptyExitCode,
      timedOut,
      cancelled,
      stdout: tail(Buffer.concat(outRaw), RAW_TAIL_BYTES),
      stderr: tail(Buffer.concat(errRaw), RAW_TAIL_BYTES),
      outText: outWindow.text(),
      errText: errWindow.text(),
      unmatchedMarkers: markers.filter((m) => !m.fired && !m.optional).map((m) => m.match.source),
    };
  }

  const stdinMode = step.feed !== undefined || markers.length > 0 ? "pipe" : "ignore";
  const child = nodeSpawn(run.command, args, {
    cwd,
    env,
    stdio: [stdinMode, "pipe", "pipe"],
    // Own process group: cancel SIGTERMs the GROUP — the CLI may fork
    // helpers, and they must die with it.
    detached: true,
  }) as ChildProcessWithoutNullStreams;
  child.once("error", () => undefined); // surfaced via the exit below

  const onAbort = (): void => {
    cancelled = true;
    killGroup(child, options.killGraceMs);
  };
  abort.addEventListener("abort", onAbort, { once: true });

  const endStdin = (): void => {
    if (stdinMode === "pipe" && child.stdin.writable) child.stdin.end();
  };
  if (stdinMode === "pipe") {
    if (step.feed !== undefined) child.stdin.write(step.feed);
    if (markers.length === 0) endStdin();
  }
  child.stdout.on("data", (chunk: Buffer) => {
    outRaw.push(chunk);
    outWindow.push(chunk.toString("utf8"));
    drainMarkers((text) => child.stdin.write(text), endStdin);
  });
  child.stderr.on("data", (chunk: Buffer) => {
    errRaw.push(chunk);
    errWindow.push(chunk.toString("utf8"));
  });
  const timeoutWatch = setInterval(() => {
    if (timedOut) {
      clearInterval(timeoutWatch);
      killGroup(child, options.killGraceMs);
    }
  }, 50);
  timeoutWatch.unref?.();
  child.once("close", () => {
    clearTimeout(timeout);
    abort.removeEventListener("abort", onAbort);
    finish();
  });
  await settle.promise;
  return {
    exitCode: child.exitCode ?? -1,
    timedOut,
    cancelled,
    stdout: tail(Buffer.concat(outRaw), RAW_TAIL_BYTES),
    stderr: tail(Buffer.concat(errRaw), RAW_TAIL_BYTES),
    outText: outWindow.text(),
    errText: errWindow.text(),
    unmatchedMarkers: markers.filter((m) => !m.fired && !m.optional).map((m) => m.match.source),
  };
}

function checkExpect(
  step: PlaybookStep,
  attempt: AttemptResult,
  workDir: string,
): string | undefined {
  // An unmatched non-optional marker fails the step whatever expect says.
  if (attempt.unmatchedMarkers.length > 0) {
    return `prompt marker never seen: ${attempt.unmatchedMarkers.join(", ")}`;
  }
  const expect = step.expect;
  if (expect === undefined)
    return attempt.timedOut ? `timed out after ${String(step.timeoutMs)}ms` : undefined;
  if (attempt.timedOut) return `timed out after ${String(step.timeoutMs)}ms`;
  if (expect.exit !== undefined && attempt.exitCode !== expect.exit) {
    return `exit code ${String(attempt.exitCode)}, expected ${String(expect.exit)}`;
  }
  if (expect.stdout !== undefined && !expect.stdout.test(attempt.outText)) {
    return `stdout did not match /${expect.stdout.source}/`;
  }
  if (expect.stderr !== undefined && !expect.stderr.test(attempt.errText)) {
    return `stderr did not match /${expect.stderr.source}/`;
  }
  if (expect.maxDurationMs !== undefined) {
    // Measured by the caller via the attempt clock; filled in runCapability.
  }
  if (expect.fileExists !== undefined) {
    const path = isAbsolute(expect.fileExists)
      ? expect.fileExists
      : join(workDir, expect.fileExists);
    try {
      readFileSync(path);
    } catch {
      return `expected file '${expect.fileExists}' does not exist`;
    }
  }
  if (expect.fileAbsent !== undefined) {
    const path = isAbsolute(expect.fileAbsent)
      ? expect.fileAbsent
      : join(workDir, expect.fileAbsent);
    try {
      readFileSync(path);
      return `expected-absent file '${expect.fileAbsent}' exists`;
    } catch {
      /* absent as promised */
    }
  }
  return undefined;
}

/** Run one capability's step list against a run. Returns the executed
 * steps (answers + capture bytes) or an honest failure/cancel outcome. */
export async function runCapability(
  capability: PlaybookCapability,
  base: Omit<InterpolationScope, "steps">,
  hooks: StepExecutorHooks,
  options: StepExecutorOptions,
): Promise<StepOutcome> {
  const records = new Map<string, StepRecord>();
  const executed: ExecutedStep[] = [];
  let firstFailure: string | undefined;
  for (const step of capability.steps) {
    const scope: InterpolationScope = { ...base, ...(hooks.extraScope ?? {}), steps: records };
    if (hooks.signal.aborted) return { kind: "cancelled" };
    if (
      step.when !== undefined &&
      (scope.vars[step.when] === undefined || scope.vars[step.when] === "")
    ) {
      // The optional leg's key is absent from this run's vars: the step
      // (and its human question) never happens. Its record carries an
      // empty answer so later declared slots still resolve — an optional
      // leg that never ran honestly contributes no text.
      hooks.logger.info("playbook-step-skipped", { step: step.name, when: step.when });
      records.set(step.name, { answer: "" });
      continue;
    }
    if (step.progress !== undefined) {
      hooks.publishProgress(
        truncateNote(interpolate(scope, `step '${step.name}'.progress`, step.progress)),
      );
    }
    const started = Date.now();
    let answer: string | undefined;
    if (step.ask !== undefined) {
      answer = await hooks.requestInput({
        question: interpolate(scope, `step '${step.name}'.ask.question`, step.ask.question),
        ...(step.ask.choices !== undefined ? { choices: step.ask.choices } : {}),
        ...(step.ask.contextRef !== undefined
          ? {
              contextRef: interpolate(
                scope,
                `step '${step.name}'.ask.contextRef`,
                step.ask.contextRef,
              ),
            }
          : {}),
      });
      if (hooks.signal.aborted) return { kind: "cancelled" };
    }
    let failure: string | undefined;
    let attemptResult: AttemptResult | undefined;
    if (step.run !== undefined) {
      const maxAttempts = step.retry?.attempts ?? 1;
      for (let attempt = 1; ; attempt += 1) {
        attemptResult = await attemptStep(step, scope, hooks, options);
        if (attemptResult.cancelled) return { kind: "cancelled" };
        const durationMs = Date.now() - started;
        failure = checkExpect(step, attemptResult, options.workDir);
        if (
          failure === undefined &&
          step.expect?.maxDurationMs !== undefined &&
          durationMs > step.expect.maxDurationMs
        ) {
          failure = `took ${String(durationMs)}ms, expected <= ${String(step.expect.maxDurationMs)}ms`;
        }
        if (failure === undefined) break;
        const retryableExit =
          step.retry !== undefined && step.retry.retryOn.includes(attemptResult.exitCode);
        if (attempt >= maxAttempts || !retryableExit) break;
        hooks.publishProgress(
          `step '${step.name}' retry ${String(attempt)}/${String(maxAttempts - 1)} after ${failure}`,
        );
        if (step.retry.backoffMs > 0) {
          await new Promise((resolve) => {
            const timer = setTimeout(resolve, step.retry?.backoffMs ?? 0);
            timer.unref?.();
          });
        }
        if (hooks.signal.aborted) return { kind: "cancelled" };
      }
      records.set(step.name, {
        ...(answer !== undefined ? { answer } : {}),
        exitCode: attemptResult.exitCode,
      });
    } else {
      records.set(step.name, { ...(answer !== undefined ? { answer } : {}) });
    }

    if (failure === undefined && step.capture !== undefined && attemptResult !== undefined) {
      const capture = step.capture;
      const bytes =
        capture.from === "stdout"
          ? attemptResult.stdout
          : capture.from === "stderr"
            ? attemptResult.stderr
            : (() => {
                const path = isAbsolute(String(capture.path))
                  ? String(capture.path)
                  : join(options.workDir, String(capture.path));
                return readFileSync(path);
              })();
      executed.push({
        name: step.name,
        exitCode: attemptResult.exitCode,
        ...(answer !== undefined ? { answer } : {}),
        capture: { as: capture.as, mediaType: capture.mediaType, bytes },
      });
    } else {
      executed.push({
        name: step.name,
        exitCode: attemptResult?.exitCode ?? 0,
        ...(answer !== undefined ? { answer } : {}),
      });
    }

    if (failure !== undefined) {
      const reason = `step '${step.name}': ${failure}`;
      if (step.onFail === "continue") {
        firstFailure ??= reason;
        hooks.logger.warn("playbook-step-continued", { step: step.name, failure });
        continue;
      }
      return { kind: "failed", reason };
    }
  }
  if (firstFailure !== undefined) return { kind: "failed", reason: firstFailure };
  return { kind: "completed", steps: executed };
}
