// The omp `--mode rpc` client: spawn argv (no shell), speak NDJSON over
// stdin/stdout per the live v18.12.6 probe:
// - first stdout frame: {type:"ready",...};
// - requests: {id,type:"prompt"|"get_state"|"abort"} and {type:"steer",message};
// - prompt ack: {id,type:"response",command:"prompt",success:true} —
//   success:true IS the accept (no disposition field in v18.12.6);
// - get_state ack carries data.sessionId;
// - the event stream: agent_start/turn_start/message_update/message_end/
//   turn_end/agent_end, terminated by {type:"prompt_result",status}.
// Every stdout frame is appended verbatim to the transcript buffer (the
// harness uploads it as an artifact on completion).
import { spawn, type ChildProcessWithoutNullStreams } from "node:child_process";

export interface OmpRpcOptions {
  bin: string;
  args: readonly string[];
  cwd: string;
  env?: Readonly<Record<string, string | undefined>> | undefined;
  /** Every parsed stdout frame, in order (ready included). */
  onFrame?: ((frame: Record<string, unknown>) => void) | undefined;
}

export interface OmpExit {
  readonly code: number | null;
  readonly signal: NodeJS.Signals | null;
  readonly stderrTail: string;
}

type Frame = Record<string, unknown>;

export class OmpRpcError extends Error {
  constructor(message: string) {
    super(message);
    this.name = "OmpRpcError";
  }
}

export class OmpRpcSession {
  readonly transcript: string[] = [];
  readonly child: ChildProcessWithoutNullStreams;
  private readonly pending = new Map<string, (frame: Frame) => void>();
  private readonly readyWaiters: (() => void)[] = [];
  private readonly exit: Promise<OmpExit>;
  private stderrBuf = "";
  private stdoutBuf = "";
  private requestSeq = 0;
  private closed = false;
  private readySeen = false;

  private constructor(private readonly opts: OmpRpcOptions) {
    this.child = spawn(opts.bin, [...opts.args], {
      cwd: opts.cwd,
      env: { ...process.env, ...(opts.env ?? {}) } as NodeJS.ProcessEnv,
      stdio: ["pipe", "pipe", "pipe"],
    });
    const { promise, resolve, reject } = Promise.withResolvers<OmpExit>();
    this.exit = promise;
    this.child.stdout.setEncoding("utf8");
    this.child.stdout.on("data", (chunk: string) => this.onStdout(chunk));
    this.child.stderr.setEncoding("utf8");
    this.child.stderr.on("data", (chunk: string) => {
      this.stderrBuf = (this.stderrBuf + chunk).slice(-65_536); // bounded; tailed at failure
    });
    this.child.once("close", (code, signal) => {
      this.closed = true;
      // A dead child never answers: fail every parked request honestly.
      for (const wake of this.pending.values())
        wake({ type: "response", success: false, error: "omp exited" });
      this.pending.clear();
      resolve({ code, signal, stderrTail: this.stderrBuf.slice(-2048) });
    });
    this.child.once("error", (error) => {
      this.closed = true;
      reject(error);
    });
    // Park rejections until a caller awaits (spawn errors must not be
    // unhandled while the run still ends honestly via events).
    this.exit.catch(() => undefined);
  }

  static start(options: OmpRpcOptions): OmpRpcSession {
    return new OmpRpcSession(options);
  }

  /** Resolves with the honest exit record; rejects only on spawn failure. */
  waitExit(): Promise<OmpExit> {
    return this.exit;
  }

  get exited(): boolean {
    return this.closed;
  }

  private onStdout(chunk: string): void {
    this.stdoutBuf += chunk;
    for (;;) {
      const nl = this.stdoutBuf.indexOf("\n");
      if (nl < 0) return;
      const line = this.stdoutBuf.slice(0, nl).trim();
      this.stdoutBuf = this.stdoutBuf.slice(nl + 1);
      if (line === "") continue;
      this.transcript.push(line);
      let frame: Frame;
      try {
        frame = JSON.parse(line) as Frame;
      } catch {
        continue; // foreign noise on stdout: transcripted, never fatal
      }
      const id = typeof frame["id"] === "string" ? frame["id"] : undefined;
      if (frame["type"] === "response" && id !== undefined) {
        this.pending.get(id)?.(frame);
        this.pending.delete(id);
      }
      if (frame["type"] === "ready" && !this.readySeen) {
        this.readySeen = true;
        for (const waiter of this.readyWaiters.splice(0)) waiter();
      }
      this.opts.onFrame?.(frame);
    }
  }

  private sendJson(value: Frame): void {
    this.child.stdin.write(`${JSON.stringify(value)}\n`);
  }

  /** Resolves on the FIRST `ready` frame (the handshake law). */
  waitReady(timeoutMs: number): Promise<void> {
    if (this.readySeen) return Promise.resolve();
    const { promise, resolve, reject } = Promise.withResolvers<void>();
    const timer = setTimeout(
      () => reject(new OmpRpcError(`omp rpc ready frame not seen within ${String(timeoutMs)}ms`)),
      timeoutMs,
    );
    timer.unref?.();
    this.readyWaiters.push(() => {
      clearTimeout(timer);
      resolve();
    });
    return promise;
  }

  /** Send a prompt; resolves on the accepted response, rejects otherwise. */
  prompt(message: string): Promise<void> {
    return this.request("prompt", { message }).then(() => undefined);
  }

  /** Fire-and-forget live steering. */
  steer(message: string): void {
    if (this.closed) return;
    this.sendJson({ type: "steer", message });
  }

  /** Cooperative abort (the harness escalates to a signal on its own grace). */
  abort(): void {
    if (this.closed) return;
    this.sendJson({ type: "abort" });
  }

  /** get_state → data.sessionId (the native session id to persist). */
  async getStateSessionId(timeoutMs = 10_000): Promise<string | undefined> {
    const frame = await this.request("get_state", {}, timeoutMs).catch(() => null);
    const data = frame?.["data"] as Record<string, unknown> | undefined;
    return typeof data?.["sessionId"] === "string" ? data["sessionId"] : undefined;
  }

  /** Cooperative shutdown: closing stdin lets a well-behaved CLI exit. */
  closeStdin(): void {
    const stdin = this.child.stdin;
    if (!stdin.destroyed && !stdin.writableEnded) stdin.end();
  }

  kill(signal: NodeJS.Signals = "SIGTERM"): void {
    if (!this.child.killed) this.child.kill(signal);
  }

  private request(command: string, extra: Frame, timeoutMs = 30_000): Promise<Frame> {
    this.requestSeq += 1;
    const id = `ofac-${String(this.requestSeq)}`;
    const { promise, resolve, reject } = Promise.withResolvers<Frame>();
    const timer = setTimeout(() => {
      this.pending.delete(id);
      reject(new OmpRpcError(`omp ${command} response not seen within ${String(timeoutMs)}ms`));
    }, timeoutMs);
    timer.unref?.();
    this.pending.set(id, (frame) => {
      clearTimeout(timer);
      if (frame["success"] === true) resolve(frame);
      else
        reject(
          new OmpRpcError(
            `omp ${command} refused: ${JSON.stringify(frame["error"] ?? frame).slice(0, 512)}`,
          ),
        );
    });
    this.sendJson({ id, type: command, ...extra });
    return promise;
  }
}
