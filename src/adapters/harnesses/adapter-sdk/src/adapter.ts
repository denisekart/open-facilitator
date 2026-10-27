// createAdapter: connectChild + the lifecycle law. The adapter is thin by
// design (S:422: the native harness stays authoritative); everything in
// here is transport honesty:
//
// - every `command` frame gets exactly one `reply` (the locked reply kinds),
//   answered from the execution registry — never from optimistic memory;
// - async capability law: `start` replies accepted immediately and the
//   harness run executes detached;
// - restart is the adapter's job (06 left it as the caller's debt): a dead
//   socket re-`connectChild`s on the backoff schedule, the registry
//   survives, so the Gateway's re-register probe gets `running` for runs
//   that actually survived and `unknown` for runs that didn't — the
//   Gateway's lost/reconcile law does the rest;
// - the SDK publishes ONE terminal event ever: `assignment.failed.v1`, and
//   only when `onExecute` returned failed or threw (05 law: the harness
//   publishes its own completion).
import { connectChild } from "@ofac/gateway";
import type { ChildConnection, ChildReply, CommandFrame } from "@ofac/gateway";
import type { EnvelopeCommand, EnvelopeEvent } from "@ofac/protocol";
import { buildEvent, createConsoleLogger } from "@ofac/workspace-link";
import type { LinkLogger } from "@ofac/workspace-link";
import { ExecutionRegistry, type RegistryEntry } from "./registry.js";
import type {
  AdapterHandle,
  AdapterStatus,
  CreateAdapterOptions,
  RunContext,
  RunOutcome,
} from "./types.js";

export const ADAPTER_PACKAGE = "@ofac/adapter";

/** Locked default restart schedule (cap-loop forever on the last entry). */
export const DEFAULT_RESTART_BACKOFF_MS: readonly number[] = [100, 250, 500, 1000, 2000, 5000];

const PROGRESS_TYPE = "org.facilitator.event.work.progress.v1";
const FAILED_TYPE = "org.facilitator.event.assignment.failed.v1";
const CHILD_NAME_RE = /^[a-z0-9]([a-z0-9-]*[a-z0-9])?$/;
const CHILD_ID_MAX = 128;

type AdapterEvent = "connected" | "disconnected" | "goaway";

interface RunRecord {
  readonly entry: RegistryEntry;
  readonly ctx: RunContext;
  readonly controller: AbortController;
  steered: ((directive: string) => void) | undefined;
}

function validateChildWorkspaceId(id: string): void {
  const slash = id.indexOf("/");
  const name = slash < 0 ? "" : id.slice(slash + 1);
  if (slash <= 0 || name.length === 0 || name.length > CHILD_ID_MAX || !CHILD_NAME_RE.test(name)) {
    throw new Error(
      `invalid childWorkspaceId '${id}': must be '<gatewayId>/<name>' with name matching ^[a-z0-9]([a-z0-9-]*[a-z0-9])?$ (<=${CHILD_ID_MAX})`,
    );
  }
}

/** 06's dedupe identity: the assign envelope's commandId (required by its schema). */
function commandIdOf(command: EnvelopeCommand): string {
  const id = (command.data as Record<string, unknown>)["commandId"];
  return typeof id === "string" && id.length > 0 ? id : "";
}

class Adapter implements AdapterHandle {
  private readonly registry = new ExecutionRegistry();
  private readonly runs = new Map<string, RunRecord>();
  private readonly listeners = new Map<AdapterEvent, ((info?: unknown) => void)[]>();
  private readonly readyPromise = Promise.withResolvers<void>();
  private readonly woken = Promise.withResolvers<void>();
  private readonly restartMs: readonly number[];
  private readonly log: LinkLogger;
  private connection: ChildConnection | null = null;
  private generation = 0;
  private closedLocally = false;
  private firstAttempt = true;
  private readonly loop: Promise<void>;

  constructor(private readonly opts: CreateAdapterOptions) {
    this.restartMs = opts.restartBackoffMs ?? DEFAULT_RESTART_BACKOFF_MS;
    this.log = opts.logger ?? createConsoleLogger();
    this.loop = this.runLoop();
    // ready() rejections must not surface as unhandled rejections when the
    // caller never awaits them (the restart loop keeps its own counsel).
    this.readyPromise.promise.catch(() => undefined);
  }

  ready(): Promise<void> {
    return this.readyPromise.promise;
  }

  status(): AdapterStatus {
    return {
      connected: this.connection?.connected ?? false,
      childGeneration: this.generation,
      activeExecutions: this.registry.runningIds(),
    };
  }

  async close(): Promise<void> {
    this.closedLocally = true;
    this.woken.resolve();
    await this.connection?.close();
    await this.loop;
  }

  readonly events = {
    on: (event: AdapterEvent, cb: (info?: unknown) => void): void => {
      const list = this.listeners.get(event) ?? [];
      list.push(cb);
      this.listeners.set(event, list);
    },
  };

  private emit(event: AdapterEvent, info?: unknown): void {
    for (const cb of this.listeners.get(event) ?? []) cb(info);
  }

  /** The restart loop: connect → serve until the socket dies → backoff → repeat. */
  private async runLoop(): Promise<void> {
    const transport = this.opts.transport;
    let attempt = 0;
    for (;;) {
      if (this.closedLocally) break;
      try {
        const conn = await connectChild({
          childWorkspaceId: this.opts.childWorkspaceId,
          token: this.childToken(),
          manifest: this.opts.manifest,
          ...("socketPath" in transport
            ? { path: transport.socketPath }
            : { port: transport.port, host: transport.host }),
          heartbeatSeconds: this.opts.heartbeatSeconds ?? 30,
          onCommand: (frame) => this.onCommand(frame),
          onNotice: (notice) => this.emit("goaway", notice),
        });
        this.connection = conn;
        this.generation += 1;
        this.firstAttempt = false;
        this.readyPromise.resolve();
        this.log.info("adapter connected", {
          childId: this.opts.childWorkspaceId,
          childGeneration: this.generation,
        });
        this.emit("connected", { childGeneration: this.generation });
        attempt = 0;
        await conn.waitClosed();
        if (!this.closedLocally) this.emit("disconnected", { childGeneration: this.generation });
      } catch (error) {
        // Pre-ack death or a register denial (message: "register denied:
        // <reason>"). Denial law (locked): log honestly, keep the schedule —
        // a restarting Gateway briefly denies races; never exit silently.
        this.log.error("adapter connect failed", {
          childId: this.opts.childWorkspaceId,
          reason: error instanceof Error ? error.message : String(error),
        });
        if (this.firstAttempt && this.restartMs.length === 0) {
          // Auto-restart disabled: one attempt is the whole contract, and
          // ready() carries the honest reason (the binary maps exit codes).
          this.firstAttempt = false;
          this.readyPromise.reject(error instanceof Error ? error : new Error(String(error)));
        }
      }
      if (this.closedLocally || this.restartMs.length === 0) break;
      const delay = this.restartMs[Math.min(attempt, this.restartMs.length - 1)] ?? 0;
      attempt += 1;
      await this.sleep(delay);
    }
  }

  /** Ref'd on purpose: while the loop sleeps between attempts the adapter IS
   * the only reason a parked worker process is alive — an unref'd timer let a
   * spawn-before-Gateway worker's event loop drain mid-backoff and exit 0. */
  private sleep(ms: number): Promise<void> {
    return new Promise((resolve) => {
      const timer = setTimeout(resolve, ms);
      this.woken.promise.then(() => {
        clearTimeout(timer);
        resolve();
      });
    });
  }

  // ── command handling (the locked reply law) ──────────────────────────────

  private async onCommand(frame: CommandFrame): Promise<ChildReply> {
    try {
      return this.handle(frame);
    } catch (error) {
      this.log.error("adapter handler failed", {
        reason: error instanceof Error ? error.message : String(error),
      });
      return { kind: "rejected", reason: "adapter handler failed" };
    }
  }

  private handle(frame: CommandFrame): ChildReply {
    const message = frame.message;
    switch (message.kind) {
      case "start": {
        const { executionId, connectionGeneration, command } = message;
        const commandId = commandIdOf(command) || executionId;
        const existing =
          this.registry.find(executionId) ?? this.registry.findByCommandId(commandId);
        if (existing !== undefined) {
          // 06's dedupe law: the same command never runs twice.
          return { kind: "accepted", executionId };
        }
        const entry = this.registry.track(executionId, commandId);
        const controller = new AbortController();
        const record: RunRecord = {
          entry,
          controller,
          steered: undefined,
          ctx: this.makeContext(entry, command, controller, connectionGeneration),
        };
        this.runs.set(executionId, record);
        void this.execute(record);
        return { kind: "accepted", executionId };
      }
      case "steer": {
        const entry = this.registry.find(message.executionId);
        const record = this.runs.get(message.executionId);
        if (entry === undefined) {
          return { kind: "unknown", executionId: message.executionId };
        }
        if (record === undefined) {
          // Settled inside the terminal window: the execution is known but
          // no longer steerable — honest `unsupported`, never a lie.
          return { kind: "unsupported" };
        }
        if (record.steered === undefined && this.opts.hooks.onSteer === undefined) {
          // S:436: unsupported steering is REPORTED, never reinterpreted.
          return { kind: "unsupported" };
        }
        const directive = directiveOf(message.command);
        entry.steers.push({ directive, at: Date.now() });
        record.steered?.(directive);
        this.opts.hooks.onSteer?.(directive, record.ctx);
        return { kind: "accepted", executionId: message.executionId };
      }
      case "stop": {
        const entry = this.registry.find(message.executionId);
        const record = this.runs.get(message.executionId);
        if (entry === undefined) {
          return { kind: "unknown", executionId: message.executionId };
        }
        // Idempotent stop: a settled execution is stopped; a live one gets
        // the cooperative abort + the harness's onCancel.
        if (record !== undefined) {
          record.controller.abort();
          this.opts.hooks.onCancel?.(message.reason, record.ctx);
        }
        return { kind: "accepted", executionId: message.executionId };
      }
      case "status": {
        const entry = this.registry.find(message.executionId);
        if (entry === undefined) {
          return { kind: "unknown", executionId: message.executionId };
        }
        return { kind: "status", executionId: message.executionId, state: entry.state };
      }
      default:
        return { kind: "unsupported" };
    }
  }

  /** The child link token, resolved per use (OFAC_CHILD_SECRET overlay law). */
  private childToken(): string {
    return this.opts.token ?? process.env["OFAC_CHILD_SECRET"] ?? "";
  }

  private makeContext(
    entry: RegistryEntry,
    command: EnvelopeCommand,
    controller: AbortController,
    executionGeneration: number,
  ): RunContext {
    const data = command.data as Record<string, unknown>;
    const workId = String(data["workId"] ?? "");
    const assignmentId = String(data["assignmentId"] ?? "");
    const traceparent = command.traceparent;
    const relay = (env: Parameters<RunContext["publishEvent"]>[0]): void => this.relay(env);
    const childWorkspaceId = this.opts.childWorkspaceId;
    const runs = this.runs;
    const hooks = this.opts.hooks;
    const ctx: RunContext = {
      ...(this.opts.kernel !== undefined
        ? { kernel: { baseUrl: this.opts.kernel.baseUrl, token: this.childToken() } }
        : {}),
      executionId: entry.executionId,
      executionGeneration,
      command,
      signal: controller.signal,
      get sessionRef(): string | undefined {
        return entry.sessionRef;
      },
      set sessionRef(ref: string | undefined) {
        // The persistence seam: the registry is the v1 home of the ref
        // (ChildReply carries no field for it); a plugged store mirrors it.
        entry.sessionRef = ref;
        if (ref !== undefined) void hooks.sessionStore?.set(entry.executionId, ref);
        else void hooks.sessionStore?.delete(entry.executionId);
      },
      publishEvent(env) {
        relay(env);
      },
      publishProgress(note) {
        relay(
          buildEvent(PROGRESS_TYPE, {
            source: `urn:workspace:${childWorkspaceId}`,
            subject: assignmentId,
            data: {
              workId,
              assignmentId,
              executionGeneration: ctx.executionGeneration,
              progressNote: note,
            },
            ...(traceparent !== undefined ? { traceparent } : {}),
          }),
        );
      },
      setSteered(cb) {
        const record = runs.get(entry.executionId);
        if (record !== undefined) record.steered = cb;
      },
    };
    return ctx;
  }

  private async execute(record: RunRecord): Promise<void> {
    const { entry, ctx, controller } = record;
    const hooks = this.opts.hooks;
    let outcome: RunOutcome;
    try {
      outcome = await hooks.onExecute(ctx);
    } catch (error) {
      outcome = {
        kind: "failed",
        reason: error instanceof Error ? error.message : String(error),
      };
    }
    this.runs.delete(entry.executionId);
    if (entry.sessionRef !== undefined) void hooks.sessionStore?.delete(entry.executionId);
    if (controller.signal.aborted) {
      // Cancelled: the accepted stop reply already settled the Gateway row
      // (06's onInteraction law). Drop the entry, publish NOTHING — no
      // double terminal event, no lie about the outcome.
      this.registry.drop(entry.executionId);
      return;
    }
    if (outcome.kind === "completed") {
      // The harness published its own assignment.completed.v1 (05 law).
      this.registry.settle(entry.executionId, "completed");
      return;
    }
    this.publishFailed(ctx, entry, outcome.reason);
    this.registry.settle(entry.executionId, "failed");
  }

  private publishFailed(ctx: RunContext, entry: RegistryEntry, reason: string): void {
    const data = ctx.command.data as Record<string, unknown>;
    const workId = String(data["workId"] ?? "");
    const assignmentId = String(data["assignmentId"] ?? "");
    this.relay(
      buildEvent(FAILED_TYPE, {
        source: `urn:workspace:${this.opts.childWorkspaceId}`,
        subject: `work/${workId}/assignment/${assignmentId}`,
        data: {
          workId,
          assignmentId,
          executionId: entry.executionId,
          executionGeneration: ctx.executionGeneration,
          reason,
          retryable: true,
        },
        ...(ctx.command.traceparent !== undefined ? { traceparent: ctx.command.traceparent } : {}),
      }),
    );
  }

  private relay(env: EnvelopeEvent): void {
    const conn = this.connection;
    if (conn === null || !conn.connected) {
      // The socket is gone: the event has no route until the next register.
      // Honest drop (the Gateway's lease + reconcile law settles the row) —
      // never a silent queue-and-hope.
      this.log.warn("adapter-event-dropped", {
        childId: this.opts.childWorkspaceId,
        type: env.type,
        reason: "socket closed",
      });
      return;
    }
    void conn.publishEvent(env).catch((error: unknown) => {
      this.log.warn("adapter-event-publish-failed", {
        reason: error instanceof Error ? error.message : String(error),
      });
    });
  }
}

function directiveOf(command: EnvelopeCommand): string {
  const directive = (command.data as Record<string, unknown> | undefined)?.["directive"];
  return typeof directive === "string" ? directive : "";
}

/**
 * Attach a harness to a Gateway: registers, then serves the lifecycle
 * forever (restart-backoffed). `ready()` gates the caller's "online" line.
 * With `restartBackoffMs: []` the contract is one-shot: a denied/failed
 * first register rejects `ready()` with `register denied: <reason>` (the
 * binary maps exit codes); otherwise the loop retries honestly and
 * `ready()` resolves whenever a register-ack lands.
 */
export async function createAdapter(opts: CreateAdapterOptions): Promise<AdapterHandle> {
  validateChildWorkspaceId(opts.childWorkspaceId);
  return new Adapter(opts);
}
