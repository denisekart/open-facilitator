// GatewayRuntime: the team-side composition over @ofac/workspace-link.
// Startup order (mirrors linkd's template, extended with the reconcile law):
// enroll-or-mount token → POST aggregated capabilities → ledgers → transport
// → embedded child → gateway handler → createLinkClient → start() → first
// welcome ⇒ RECONCILE (§6.3) → ready line (CLI prints it) → heartbeats.
//
// Stamp law: a binding row's connectionGeneration is the leaseEpoch of the
// session that accepted the command. A delivery frame can be dispatched
// before start()'s welcome continuation lands (frames flow the moment the
// transport connects), so onAssign AWAITS the first welcome rather than
// reading a possibly-null session — an unstamped (epoch 0) row would make
// the reconcile generation-freshness law lie.
//
// Command routing: work.assign = policy → binding row (running, current
// leaseEpoch as connectionGeneration) → child start → receipt follows the
// child's answer. steer/cancel route to the child when a running binding
// exists and the capability supports the interaction; every other known
// command passes through (single child — the child answers, the SDK's
// outcome mapping ships the receipt). Unknown command types never reach
// this handler: the SDK answers `unsupported` fail-closed (S:309).
import { createHash, randomBytes } from "node:crypto";
import { join } from "node:path";
import { parse } from "yaml";
import { chmodSync, mkdirSync, readFileSync, writeFileSync } from "node:fs";
import type {
  CapabilityDescriptor,
  CapabilityManifestV1,
  EnvelopeCommand,
  EnvelopeEvent,
  WorkAssignCommandV1Data,
  WorkCancelCommandV1Data,
} from "@ofac/protocol";
import {
  EnrollmentError,
  buildEvent,
  createConsoleLogger,
  createLinkClient,
  enroll,
  type CommandOutcome,
  type LinkClient,
  type LinkHandler,
  type LinkLogger,
  type LinkStats,
  type LinkTokenProvider,
  type LinkTransport,
} from "@ofac/workspace-link";
import { SignalrTransport, SqliteLedger, postCapabilities } from "@ofac/workspace-link/node";
import type { GatewayConfig } from "./config.js";
import { tokenFilePath } from "./config.js";
import { EmbeddedChild, builtinChildManifest, newExecutionId, type ChildReply } from "./child.js";
import {
  BindingConflictError,
  ExecutionLedger,
  type BindingRow,
  type GatewayPaths,
} from "./ledger.js";
import { DefaultPolicy, type PolicyEvaluator } from "./policy.js";

const ASSIGN = "org.facilitator.command.work.assign.v1";
const STEER = "org.facilitator.command.work.steer.v1";
const CANCEL = "org.facilitator.command.work.cancel.v1";
const COMPLETED = "org.facilitator.event.assignment.completed.v1";
const FAILED = "org.facilitator.event.assignment.failed.v1";
const HEARTBEAT = "org.facilitator.event.workspace.heartbeat.v1";

export type GatewayRuntimeState = "starting" | "ready" | "draining" | "closed";

export interface GatewayStatus {
  gatewayId: string;
  state: GatewayRuntimeState;
  reconciled: boolean;
  session?: { leaseEpoch: number; sessionId: string; state: string } | undefined;
  executions: { running: number; completed: number; failed: number; lost: number };
  bindings: {
    assignmentId: string;
    executionId: string;
    state: string;
    connectionGeneration: number;
  }[];
  child: { childWorkspaceId: string; mode: string };
  linkStats: LinkStats;
}

export interface CreateGatewayRuntimeDeps {
  /** Defaults to <stateDir>/link.sqlite3 + gateway.sqlite3. */
  ledgerFactory?: (stateDir: string) => GatewayPaths;
  transportFactory?: (token: LinkTokenProvider, config: GatewayConfig) => LinkTransport;
  /** Override the embedded child's command handler (test child variants). */
  handlerFactory?: () => LinkHandler;
  logger?: LinkLogger;
  fetchImpl?: typeof fetch;
  /** Override the policy seam (defaults to DefaultPolicy over the child manifest). */
  policy?: PolicyEvaluator;
}

/** The team Gateway: one endpoint, one embedded child, the execution truth. */
export class GatewayRuntime {
  readonly childWorkspaceId: string;
  /** The POSTed manifest: the child's, verbatim except the team owns it. */
  readonly aggregatedManifest: CapabilityManifestV1;

  private readonly config: GatewayConfig;
  private readonly deps: CreateGatewayRuntimeDeps;
  private readonly logger: LinkLogger;
  private readonly paths: GatewayPaths;
  private readonly child: EmbeddedChild;
  private readonly policy: PolicyEvaluator;
  private readonly capabilityByExecution = new Map<string, string>();
  private client: LinkClient | null = null;
  private heartbeatTimer: ReturnType<typeof setInterval> | null = null;
  private _state: GatewayRuntimeState = "starting";
  private _reconciled = false;
  private childError = false;

  constructor(config: GatewayConfig, deps: CreateGatewayRuntimeDeps = {}) {
    this.config = config;
    this.deps = deps;
    this.logger = deps.logger ?? createConsoleLogger();
    this.childWorkspaceId = `${config.gatewayId}/${config.child.name}`;
    mkdirSync(config.stateDir, { recursive: true, mode: 0o700 });
    this.paths = deps.ledgerFactory?.(config.stateDir) ?? defaultLedgers(config.stateDir);
    const childManifest = loadChildManifest(config, this.logger);
    this.child = new EmbeddedChild({
      childWorkspaceId: this.childWorkspaceId,
      mode: config.child.mode,
      manifest: childManifest,
      ...(deps.handlerFactory !== undefined ? { handler: deps.handlerFactory() } : {}),
      logger: this.logger,
    });
    const capabilities = new Map<string, CapabilityDescriptor>();
    for (const capability of childManifest.capabilities) {
      capabilities.set(capability.id, capability);
    }
    this.policy = deps.policy ?? new DefaultPolicy(capabilities);
    // One manifest per endpoint server-side; ownerTeam MUST equal the stored
    // team slug or POST /capabilities answers 403 team-mismatch.
    this.aggregatedManifest = { ...childManifest, ownerTeam: config.team };
  }

  get state(): GatewayRuntimeState {
    return this._state;
  }

  get reconciled(): boolean {
    return this._reconciled;
  }

  get linkClient(): LinkClient | null {
    return this.client;
  }

  get executionLedger(): ExecutionLedger {
    return this.paths.execution;
  }

  /**
   * Enroll/mount → capabilities POST → connect → reconcile. Resolves AFTER
   * the reconcile pass with the ready-line parts (the CLI prints the line).
   */
  async start(): Promise<{ leaseEpoch: number }> {
    const token = await this.acquireToken();
    const posted = await postCapabilities(this.aggregatedManifest, {
      baseUrl: this.config.facilitatorUrl,
      getToken: token.getToken,
      ...(this.deps.fetchImpl !== undefined ? { fetchImpl: this.deps.fetchImpl } : {}),
    });

    const transport =
      this.deps.transportFactory?.(token, this.config) ??
      new SignalrTransport({ baseUrl: this.config.facilitatorUrl, token });
    const client = createLinkClient({
      transport,
      ledger: this.paths.link,
      endpointId: this.config.gatewayId,
      capabilityManifestDigest: posted.digest,
      token,
      handler: this.gatewayHandler(),
      logger: this.logger,
      eventSource: `urn:workspace:${this.config.gatewayId}`,
    });
    this.client = client;
    const session = await client.start();

    // Child events flow live from here on; anything queued pre-welcome ships too.
    this.child.setPublisher((env) => this.forwardEvent(env));
    await this.forwardQueued();

    await this.reconcile();
    this._reconciled = true;
    this._state = "ready";
    this.heartbeatTimer = setInterval(
      () => void this.publishHeartbeat("periodic"),
      this.config.heartbeatSeconds * 1000,
    );
    return { leaseEpoch: session.leaseEpoch };
  }

  /** SIGTERM path: one draining heartbeat, goodbye, close everything. */
  async stop(): Promise<void> {
    this._state = "draining";
    if (this.heartbeatTimer !== null) {
      clearInterval(this.heartbeatTimer);
      this.heartbeatTimer = null;
    }
    const client = this.client;
    if (client !== null) {
      await this.publishHeartbeat("shutdown");
      await client.stop("gateway-shutdown");
    }
    await this.child.close();
    this.paths.link.close();
    this.paths.execution.close();
    this._state = "closed";
  }

  gatewayStatus(): GatewayStatus {
    const client = this.client;
    const session = client?.session ?? null;
    return {
      gatewayId: this.config.gatewayId,
      state: this._state,
      reconciled: this._reconciled,
      ...(session !== null
        ? {
            session: {
              leaseEpoch: session.leaseEpoch,
              sessionId: session.sessionId,
              state: client?.state ?? "closed",
            },
          }
        : {}),
      executions: this.paths.execution.counts(),
      bindings: this.paths.execution.all().map((row) => ({
        assignmentId: row.assignmentId,
        executionId: row.executionId,
        state: row.state as string,
        connectionGeneration: row.connectionGeneration,
      })),
      child: { childWorkspaceId: this.childWorkspaceId, mode: this.config.child.mode },
      linkStats:
        client?.stats ??
        ({
          delivered: 0,
          duplicate: 0,
          processed: 0,
          failed: 0,
          unsupported: 0,
          reconnects: 0,
        } satisfies LinkStats),
    };
  }

  /** Heartbeat `error` latch: set when the child closed abnormally. */
  markChildError(): void {
    this.childError = true;
  }

  async publishHeartbeat(cause: "periodic" | "shutdown"): Promise<void> {
    const client = this.client;
    if (client === null) return;
    const running = this.paths.execution.counts().running;
    const status =
      this._state === "draining" || this._state === "closed"
        ? "draining"
        : this.childError
          ? "error"
          : running > 0
            ? "busy"
            : "idle";
    try {
      await client.publish(HEARTBEAT, {
        source: `urn:workspace:${this.config.gatewayId}`,
        subject: this.config.gatewayId,
        data: { workspaceId: this.config.gatewayId, status, activeAssignments: running },
      });
    } catch (error) {
      this.logger.warn("heartbeat publish failed", {
        cause,
        reason: error instanceof Error ? error.message : String(error),
      });
    }
  }

  // ── token lifecycle ───────────────────────────────────────────────────────

  /**
   * linkToken/OFAC_LINK_TOKEN mounts a static provider (refresh re-enrolls
   * only when a secret is ALSO configured — linkd's rule); else enroll at
   * startup and persist <stateDir>/token. Rotation kills the old token
   * immediately, so the file write happens BEFORE any retry. Neither secret
   * nor token at first boot (config's hasStoredToken escape hatch lies) ⇒
   * EnrollmentError, which the CLI maps to exit 4.
   */
  private async acquireToken(): Promise<LinkTokenProvider> {
    const { config, deps, logger } = this;
    const secret = config.enrollmentSecret;
    const activeToken = { value: config.linkToken ?? readStoredToken(config.stateDir) ?? "" };
    const refresh = async (): Promise<void> => {
      if (secret === undefined) return; // static mount: nothing to rotate
      const response = await enroll(
        {
          baseUrl: config.facilitatorUrl,
          token: secret,
          endpointId: config.gatewayId,
          instanceId: `gw-${randomBytes(8).toString("hex")}`,
          trustMode: config.trustMode,
        },
        deps.fetchImpl ?? fetch,
      );
      if (response.linkToken === undefined) {
        throw new EnrollmentError(201, { detail: "server issued no linkToken" });
      }
      writeTokenFile(config.stateDir, response.linkToken);
      activeToken.value = response.linkToken;
    };
    if (config.linkToken === undefined) {
      if (secret === undefined) {
        if (activeToken.value === "") {
          throw new EnrollmentError(401, { detail: "no token available at first boot" });
        }
        // Stored-token restart without a secret: static mount, no rotation.
        return { getToken: async () => activeToken.value };
      }
      await refresh(); // enroll at startup, writes the token file
      logger.info("link token enrolled", { endpointId: config.gatewayId });
    }
    return { getToken: async () => activeToken.value, refresh };
  }

  // ── command routing ───────────────────────────────────────────────────────

  private gatewayHandler(): LinkHandler {
    return {
      onCommand: async (cmd, ctx) => {
        switch (cmd.type) {
          case ASSIGN:
            return this.onAssign(cmd, ctx.deliveryId);
          case STEER:
          case CANCEL:
            return this.onInteraction(cmd, cmd.type === STEER ? "steer" : "cancel");
          default:
            return this.passthrough(cmd);
        }
      },
    };
  }

  /**
   * The leaseEpoch of the session behind this command, awaiting the first
   * welcome when a frame beat the handshake continuation. A terminal close
   * without any session falls back to 0 (nothing was ever minted).
   */
  private async awaitSessionEpoch(): Promise<number> {
    const client = this.client;
    if (client === null) return 0;
    if (client.session !== null) return client.session.leaseEpoch;
    const { promise, resolve } = Promise.withResolvers<number>();
    const settle = (epoch: number): void => {
      offWelcome();
      offState();
      resolve(epoch);
    };
    const offWelcome = client.on("welcome", (session) => settle(session.leaseEpoch));
    const offState = client.on("state", (state) => {
      if (state === "closed") settle(client.session?.leaseEpoch ?? 0);
    });
    return promise;
  }

  private async onAssign(cmd: EnvelopeCommand, deliveryId: string): Promise<CommandOutcome> {
    const data = cmd.data as unknown as WorkAssignCommandV1Data;
    if (
      this.policy.evaluate("command.forward", {
        commandType: cmd.type,
        capabilityId: data.capabilityId,
        childWorkspaceId: this.childWorkspaceId,
      }) === "deny"
    ) {
      // Policy rejection is not an execution: no ledger row.
      return { kind: "rejected", reason: `policy-denied: ${data.capabilityId}` };
    }
    const epoch = await this.awaitSessionEpoch();
    const executionId = newExecutionId();
    try {
      this.paths.execution.insert({
        deliveryId,
        dedupeKey: data.commandId,
        gatewayId: this.config.gatewayId,
        childId: this.childWorkspaceId,
        assignmentId: data.assignmentId,
        executionId,
        workId: data.workId,
        connectionGeneration: epoch,
      });
    } catch (error) {
      if (error instanceof BindingConflictError) {
        // Same commandId under a new deliveryId: already bound.
        return { kind: "accepted" };
      }
      throw error;
    }
    this.capabilityByExecution.set(executionId, data.capabilityId);
    const reply = await this.child.send({
      kind: "start",
      executionId,
      connectionGeneration: epoch,
      command: cmd,
    });
    return this.settleStart(executionId, reply);
  }

  private settleStart(executionId: string, reply: ChildReply): CommandOutcome {
    switch (reply.kind) {
      case "accepted":
        return { kind: "accepted" };
      case "rejected":
        this.paths.execution.markState(executionId, "failed", reply.reason);
        return { kind: "rejected", reason: reply.reason };
      case "unsupported":
        this.paths.execution.markState(executionId, "failed", reply.reason ?? "child unsupported");
        return reply.reason === undefined
          ? { kind: "unsupported" }
          : { kind: "unsupported", reason: reply.reason };
      default:
        this.paths.execution.markState(executionId, "failed", "child unknown");
        return { kind: "rejected", reason: "child unknown" };
    }
  }

  private async onInteraction(
    cmd: EnvelopeCommand,
    kind: "steer" | "cancel",
  ): Promise<CommandOutcome> {
    const data = cmd.data as unknown as WorkCancelCommandV1Data;
    const row = this.paths.execution.byAssignment(data.assignmentId);
    const capabilityId = row !== null ? this.capabilityByExecution.get(row.executionId) : undefined;
    const routed =
      row !== null &&
      row.state === "running" &&
      capabilityId !== undefined &&
      this.policy.evaluate("command.forward", {
        commandType: cmd.type,
        capabilityId,
        childWorkspaceId: this.childWorkspaceId,
      }) === "allow";
    if (row === null || capabilityId === undefined || !routed) {
      // No running binding / capability forbids it: single-child
      // pass-through — the child answers, the receipt follows the reply.
      return this.passthrough(cmd);
    }
    const executionId = row.executionId;
    const reply = await this.child.send(
      kind === "steer"
        ? { kind: "steer", executionId, command: cmd }
        : { kind: "stop", executionId, reason: data.reason },
    );
    if (kind === "cancel" && reply.kind === "accepted") {
      if (this.fence(row, "cancel") === "fresh") {
        this.paths.execution.markState(executionId, "completed");
      }
    }
    return childOutcome(reply);
  }

  private async passthrough(cmd: EnvelopeCommand): Promise<CommandOutcome> {
    // Single-child pass-through: the child's own answer decides the receipt
    // (echo answers `unsupported` off-assign, the stub accepts commands).
    return childOutcome(
      await this.child.send({
        kind: "steer",
        executionId: `exec-na-${createHash("sha256").update(cmd.id).digest("hex").slice(0, 12)}`,
        command: cmd,
      }),
    );
  }

  // ── event forwarding + reconcile ─────────────────────────────────────────

  private async forwardQueued(): Promise<void> {
    for (const env of this.child.drainEvents()) {
      await this.forwardEvent(env);
    }
  }

  /** Forward the CHILD's event bytes, stamping nothing (S:329 fence first). */
  private async forwardEvent(env: EnvelopeEvent): Promise<void> {
    if (
      this.policy.evaluate("event.forward", {
        commandType: env.type,
        childWorkspaceId: this.childWorkspaceId,
      }) === "deny"
    ) {
      return;
    }
    const rawExecution = env.data?.["executionId"];
    const executionId = typeof rawExecution === "string" ? rawExecution : null;
    const row = executionId !== null ? this.paths.execution.byExecution(executionId) : null;
    if (row !== null) {
      if (this.fence(row, `forward ${env.type}`) === "stale") return;
      if (env.type === COMPLETED) this.paths.execution.markState(row.executionId, "completed");
      if (env.type === FAILED)
        this.paths.execution.markState(row.executionId, "failed", reasonOf(env));
    }
    await this.client?.publishEvent(env);
  }

  /**
   * §6.3 reconcile, right after the FIRST welcome: query the child for every
   * `running` row; the child's answer is the truth, `unknown` ⇒ lost (never
   * re-executed, never silently deleted, reported via ONE retryable
   * assignment.failed). Redelivered commands cannot race this: the SDK
   * delivery ledger answers `duplicate` without touching bindings.
   */
  private async reconcile(): Promise<void> {
    for (const row of this.paths.execution.byState("running")) {
      const reply = await this.child.send({ kind: "status", executionId: row.executionId });
      if (reply.kind === "status") {
        // The child's own truth: running/lost rows stand; terminal answers settle.
        if (reply.state === "completed" || reply.state === "failed") {
          this.paths.execution.markState(row.executionId, reply.state);
        }
        continue;
      }
      // unknown / transport-dead: lost + ONE honest assignment.failed. The
      // Gateway is the reporting authority — the child published nothing.
      this.paths.execution.markState(row.executionId, "lost", "gateway-restart-lost");
      try {
        await this.client?.publishEvent(
          buildEvent(FAILED, {
            source: `urn:workspace:${this.config.gatewayId}`,
            subject: `work/${row.workId}/assignment/${row.assignmentId}`,
            data: {
              workId: row.workId,
              assignmentId: row.assignmentId,
              executionId: row.executionId,
              // The leaseEpoch the execution ran under is the honest
              // generation (schema minimum is 1; epoch 0 ⇒ pre-session row).
              executionGeneration: Math.max(1, row.connectionGeneration),
              reason: "gateway-restart-lost",
              retryable: true,
            },
          }),
        );
      } catch (error) {
        this.logger.warn("reconcile failed-event dropped", {
          executionId: row.executionId,
          reason: error instanceof Error ? error.message : String(error),
        });
      }
      this.logger.info("reconcile: execution lost", {
        executionId: row.executionId,
        assignmentId: row.assignmentId,
        connectionGeneration: row.connectionGeneration,
      });
    }
  }

  /** S:329: work recorded under a dead connection cannot settle or forward. */
  private fence(row: BindingRow, action: string): "fresh" | "stale" {
    const epoch = this.client?.session?.leaseEpoch ?? 0;
    if (row.connectionGeneration === epoch) return "fresh";
    this.logger.warn("stale-generation", {
      action,
      executionId: row.executionId,
      rowGeneration: row.connectionGeneration,
      currentEpoch: epoch,
    });
    return "stale";
  }
}

function defaultLedgers(stateDir: string): GatewayPaths {
  return {
    // Two concerns, two files: delivery dedupe (SDK law) vs execution truth.
    link: new SqliteLedger(join(stateDir, "link.sqlite3")),
    execution: new ExecutionLedger(join(stateDir, "gateway.sqlite3")),
  };
}

function loadChildManifest(config: GatewayConfig, logger: LinkLogger): CapabilityManifestV1 {
  if (config.child.manifestFile === undefined) return builtinChildManifest(config.child.mode);
  const doc = parse(readFileSync(config.child.manifestFile, "utf8")) as {
    capabilities?: CapabilityManifestV1["capabilities"];
  };
  if (!Array.isArray(doc.capabilities) || doc.capabilities.length === 0) {
    throw new Error(`child.manifestFile '${config.child.manifestFile}' has no capabilities[]`);
  }
  // The Gateway wraps the bare capability items; postCapabilities validates
  // the full manifest against the frozen schema before POST.
  logger.debug("child manifest loaded from file", { file: config.child.manifestFile });
  return {
    apiVersion: "facilitator.framework/v1",
    kind: "CapabilityManifest",
    ownerTeam: `child-${config.child.name}`,
    capabilities: doc.capabilities,
  };
}

function writeTokenFile(stateDir: string, token: string): void {
  const path = tokenFilePath(stateDir);
  writeFileSync(path, token, { mode: 0o600 });
  chmodSync(path, 0o600); // a pre-existing file keeps its old mode otherwise
}

function readStoredToken(stateDir: string): string | null {
  try {
    const value = readFileSync(tokenFilePath(stateDir), "utf8").trim();
    return value.length > 0 ? value : null;
  } catch {
    return null;
  }
}

function childOutcome(reply: ChildReply): CommandOutcome {
  switch (reply.kind) {
    case "accepted":
      return { kind: "accepted" };
    case "rejected":
      return { kind: "rejected", reason: reply.reason };
    case "unsupported":
      return reply.reason === undefined
        ? { kind: "unsupported" }
        : { kind: "unsupported", reason: reply.reason };
    case "unknown":
      return { kind: "unsupported", reason: `no such execution: ${reply.executionId}` };
    case "status":
      return { kind: "unsupported", reason: `execution ${reply.executionId} is ${reply.state}` };
  }
}

function reasonOf(env: EnvelopeEvent): string | null {
  const reason = env.data?.["reason"];
  return typeof reason === "string" ? reason : null;
}

/** Compose a Gateway runtime (enroll/connect happen in `start()`). */
export function createGatewayRuntime(
  config: GatewayConfig,
  deps: CreateGatewayRuntimeDeps = {},
): GatewayRuntime {
  return new GatewayRuntime(config, deps);
}
