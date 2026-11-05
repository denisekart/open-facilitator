// The ChildExecutor seam: serializable messages, in-process today. The
// HarnessAdapter contract freezes this vocabulary cross-process and the
// socket transport carries it, so every message is a plain
// JSON-serializable object — no live handles cross the boundary.
//
// EmbeddedChild wraps an SDK LinkHandler (createEchoHandler/createStubHandler
// — the child owns its events; the Gateway forwards the child's bytes and
// NEVER fabricates an assignment.completed). Execution state is derived from
// what the child itself publishes: an `assignment.completed.v1` settles
// `completed`, an `assignment.failed.v1` settles `failed`; a child that
// accepts silently stays `running` (the stub's law, and the e2e's lever).
import { randomBytes } from "node:crypto";
import type {
  CapabilityDescriptor,
  CapabilityManifestV1,
  EnvelopeCommand,
  EnvelopeEvent,
} from "@ofac/protocol";
import {
  createConsoleLogger,
  createEchoHandler,
  createStubHandler,
  type LinkHandler,
  type LinkLogger,
} from "@ofac/workspace-link";
import type { ChildMode } from "./config.js";

export const MANIFEST_SCHEMA_ID =
  "https://schemas.openfacilitator.org/v1/capability-manifest.v1.json";

export type ChildMessage =
  | { kind: "register"; childWorkspaceId: string; manifest: CapabilityManifestV1 }
  | { kind: "start"; executionId: string; executionGeneration: number; command: EnvelopeCommand } // work.assign envelope (B4: assignment generation, not link epoch)
  | { kind: "steer"; executionId: string; command: EnvelopeCommand }
  | { kind: "status"; executionId: string }
  | { kind: "stop"; executionId: string; reason: string } // work.cancel
  | { kind: "respond"; executionId: string; requestId: string; text: string }; // assignment.input

export type ChildReply =
  | { kind: "accepted"; executionId: string }
  | { kind: "rejected"; reason: string }
  | { kind: "unsupported"; reason?: string | undefined }
  | { kind: "status"; executionId: string; state: "running" | "completed" | "failed" | "lost" }
  | { kind: "unknown"; executionId: string }; // no such execution (reconcile answer)

export interface ChildExecutor {
  readonly childWorkspaceId: string;
  register(): Promise<ChildMessage & { kind: "register" }>;
  send(msg: ChildMessage): Promise<ChildReply>;
  /** Child-published events awaiting upstream forwarding. */
  drainEvents(): EnvelopeEvent[];
  close(): Promise<void>;
}

/**
 * The builtin manifest the echo/stub child advertises: the same
 * `research.analyze` v1 asynchronous capability the e2e seeding targets, so
 * a real `work.assign` for it executes end-to-end. `ownerTeam` is the
 * CHILD's own slug — the Gateway's aggregation rewrites it to the team.
 */
export function builtinChildManifest(mode: ChildMode): CapabilityManifestV1 {
  const capability: CapabilityDescriptor = {
    id: "research.analyze",
    version: 1,
    execution: "asynchronous",
    acceptedContexts: ["uri-reference"],
    outputKinds: ["analysis.report"],
    outputMediaTypes: ["text/markdown"],
    supportsSteering: true,
    supportsCancellation: true,
    supportsNudge: false,
  };
  return {
    apiVersion: "facilitator.framework/v1",
    kind: "CapabilityManifest",
    ownerTeam: mode === "echo" ? "child-echo" : "child-stub",
    capabilities: [capability],
  };
}

export interface EmbeddedChildOptions {
  /** e.g. `gw.team-alpha/echo`; CloudEvent source = `urn:workspace:<id>`. */
  childWorkspaceId: string;
  mode: ChildMode;
  /** The child's own manifest (own ownerTeam); surfaced via register(). */
  manifest: CapabilityManifestV1;
  /** Test seam; defaults to the SDK's builtin per mode (no third echo). */
  handler?: LinkHandler | undefined;
  logger?: LinkLogger | undefined;
}

interface ExecutionRecord {
  state: "running" | "completed" | "failed";
}

/**
 * The single embedded child: a command handler behind the message seam.
 * Event forwarding law: the child publishes via the CommandContext we supply;
 * events queue in `drainEvents()` until the runtime mounts a `publisher`
 * (post-welcome), then flow straight out — offline transport-buffering is
 * the SDK's job (client.publishEvent queues to the ledger outbox).
 */
export class EmbeddedChild implements ChildExecutor {
  readonly childWorkspaceId: string;
  readonly mode: ChildMode;

  private readonly handler: LinkHandler;
  private readonly manifest: CapabilityManifestV1;
  private readonly logger: LinkLogger;
  private readonly executions = new Map<string, ExecutionRecord>();
  private readonly queue: EnvelopeEvent[] = [];
  private publisher: ((env: EnvelopeEvent) => Promise<void>) | null = null;
  private closed = false;

  constructor(options: EmbeddedChildOptions) {
    this.childWorkspaceId = options.childWorkspaceId;
    this.mode = options.mode;
    this.manifest = options.manifest;
    this.logger = options.logger ?? createConsoleLogger();
    this.handler =
      options.handler ??
      (options.mode === "echo"
        ? createEchoHandler({ source: `urn:workspace:${options.childWorkspaceId}` })
        : createStubHandler());
  }

  /** Connected after welcome: child events forward instead of queueing. */
  setPublisher(publisher: (env: EnvelopeEvent) => Promise<void>): void {
    this.publisher = publisher;
  }

  async register(): Promise<ChildMessage & { kind: "register" }> {
    return { kind: "register", childWorkspaceId: this.childWorkspaceId, manifest: this.manifest };
  }

  async send(msg: ChildMessage): Promise<ChildReply> {
    if (this.closed) return { kind: "rejected", reason: "child closed" };
    switch (msg.kind) {
      case "register":
        return { kind: "accepted", executionId: "" };
      case "start":
        // Idempotent by law: a second start for a known execution re-dispatches
        // (echo re-publishes its event pair — acceptable for the echo child).
        this.executions.set(msg.executionId, { state: "running" });
        return this.dispatchCommand(msg.command, msg.executionId);
      case "steer":
        return this.dispatchCommand(msg.command, msg.executionId);
      case "status": {
        const record = this.executions.get(msg.executionId);
        if (record === undefined) return { kind: "unknown", executionId: msg.executionId };
        return { kind: "status", executionId: msg.executionId, state: record.state };
      }
      case "respond": {
        // The builtin echo/stub child never publishes an input.required, so an
        // answer for a known execution is honestly `unsupported` (no question
        // to settle); a foreign execution answers `unknown` like every other
        // interaction.
        const record = this.executions.get(msg.executionId);
        if (record === undefined) return { kind: "unknown", executionId: msg.executionId };
        return { kind: "unsupported", reason: "builtin-child-never-asks" };
      }
      case "stop": {
        const record = this.executions.get(msg.executionId);
        if (record === undefined) return { kind: "unknown", executionId: msg.executionId };
        // The embedded echo/stub child has no mid-flight work to abort: a
        // stop settles the execution `completed` (a real adapter answers the
        // child's own truth over the socket).
        record.state = "completed";
        return { kind: "accepted", executionId: msg.executionId };
      }
    }
  }

  drainEvents(): EnvelopeEvent[] {
    return this.queue.splice(0, this.queue.length);
  }

  async close(): Promise<void> {
    this.closed = true;
    this.logger.debug("embedded child closed", { childWorkspaceId: this.childWorkspaceId });
    this.executions.clear();
  }

  private async dispatchCommand(
    command: EnvelopeCommand,
    executionId: string,
  ): Promise<ChildReply> {
    const outcome = await this.handler.onCommand(command, {
      // The embedded child sees the RAW execution token: the builtin echo
      // handler re-publishes `exec-<ctx.deliveryId>`, which then equals the
      // Gateway's `exec-<hex>` binding identity — the forwarded bytes map
      // back to the row without the Gateway stamping anything.
      deliveryId: executionId.replace(/^exec-/, ""),
      envelope: command,
      signal: new AbortController().signal,
      publishEvent: async (env) => {
        this.settleFromEvent(executionId, env);
        if (this.publisher !== null) {
          await this.publisher(env);
        } else {
          this.queue.push(env);
        }
        return { status: "accepted" };
      },
    });
    switch (outcome.kind) {
      case "accepted":
        return { kind: "accepted", executionId };
      case "rejected":
        this.executions.set(executionId, { state: "failed" });
        return { kind: "rejected", reason: outcome.reason };
      case "unsupported":
        this.executions.delete(executionId);
        return outcome.reason === undefined
          ? { kind: "unsupported" }
          : { kind: "unsupported", reason: outcome.reason };
    }
  }

  /**
   * The child owns the facts: only a terminal event the child itself
   * published settles an execution. An accepted command with no terminal
   * event stays `running` — the stub's honest answer for "accepted, still
   * working" (and the SIGKILL-reconcile lever in e2e); a silent
   * onCommand-return never fabricates completion.
   */
  private settleFromEvent(executionId: string, env: EnvelopeEvent): void {
    const record = this.executions.get(executionId);
    if (record === undefined || record.state !== "running") return;
    if (env.type === "org.facilitator.event.assignment.completed.v1") record.state = "completed";
    else if (env.type === "org.facilitator.event.assignment.failed.v1") record.state = "failed";
  }
}

/** Gateway-issued execution ids: opaque, restart-safe identity (binding row truth). */
export function newExecutionId(): string {
  return `exec-${randomBytes(8).toString("hex")}`;
}
