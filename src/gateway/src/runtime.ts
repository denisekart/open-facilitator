// GatewayRuntime: the team-side composition over @ofac/workspace-link.
// Startup order (mirrors linkd's template, extended with the reconcile law):
// enroll-or-mount token → capabilities POST (embedded fleet-of-one; fleet
// modes POST on child churn, never at boot) → ledgers → transport → gateway
// handler → child listener (shared|standalone; bound BEFORE the link session
// opens so a command the kernel pushes on session-open meets its child, not
// a boot-window `child-disconnected`: a restart leg's re-hydrated
// assign must never die in that window) → start() → first welcome ⇒
// RECONCILE → ready line → heartbeats.
//
// Stamp law: a binding row pins TWO generations —
// connectionGeneration is the leaseEpoch of the session that accepted the
// command (the freshness fence: a frame recorded under a dead
// connection is not forwarded), executionGeneration is the assign
// envelope's assignment generation, the value every event this execution
// publishes carries so the kernel's fence matches the assignment row.
// The childGeneration is the child link's fence at insert time (embedded:
// 0 — no transport, no re-register). A delivery frame can be dispatched
// before start()'s welcome continuation lands, so onAssign AWAITS the first
// welcome rather than reading a possibly-null session — an unstamped
// (epoch 0) row would make the reconcile generation-freshness law lie.
//
// Routing law: work.assign = capability lookup over the fleet —
// nobody advertises ⇒ unsupported (no row, mirror of the policy-deny
// no-row law); the advertised child is offline ⇒ rejected child-disconnected
// (honest, retryable upstream); quota full ⇒ rejected child-busy; then
// policy → binding row → child start → receipt follows the child's answer.
// steer/cancel route to the row's child (bound-child law); with no row the
// embedded fleet-of-one passes through (05) and a fleet rejects no-binding.
import { createHash, randomBytes, randomUUID } from "node:crypto";
import { join } from "node:path";
import { parse } from "yaml";
import { chmodSync, mkdirSync, readFileSync, writeFileSync } from "node:fs";
import type {
  AssignmentInputCommandV1Data,
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
  oidcClientCredentialsTokenProvider,
  type CommandOutcome,
  type LinkClient,
  type LinkHandler,
  type LinkLogger,
  type LinkStats,
  type LinkTokenProvider,
  type LinkTransport,
} from "@ofac/workspace-link";
import { SignalrTransport, SqliteLedger, postCapabilities } from "@ofac/workspace-link/node";
import type { GatewayConfig, GatewayMode } from "./config.js";
import { tokenFilePath } from "./config.js";
import { EmbeddedChild, builtinChildManifest, newExecutionId, type ChildReply } from "./child.js";
import {
  BindingConflictError,
  ExecutionLedger,
  type BindingRow,
  type GatewayPaths,
  type PendingRow,
} from "./ledger.js";
import {
  DefaultPolicy,
  TeamPolicy,
  type PolicyAction,
  type PolicyContext,
  type PolicyEvaluator,
  type PolicyVerdict,
} from "./policy.js";
import { loadPolicyFile, type LoadedPolicy, type PolicyRow } from "./policy-file.js";
import { auditPolicy } from "./audit.js";
import { defaultClaimsDeps, extractClaims, type ActorClaims } from "./claims.js";
import { computeActionDigest } from "@ofac/protocol";
import {
  ChildFleet,
  ChildLink,
  ChildTransportError,
  EmbeddedMember,
  digestOf,
  type ChildStatusRow,
  type FleetMember,
} from "./child-link.js";
import { startChildListener, type ChildListener } from "./listen.js";

const ASSIGN = "org.facilitator.command.work.assign.v1";
const STEER = "org.facilitator.command.work.steer.v1";
const CANCEL = "org.facilitator.command.work.cancel.v1";
const ASSIGNMENT_INPUT = "org.facilitator.command.assignment.input.v1";
const NUDGE = "org.facilitator.command.work.nudge.v1";
const COMPLETED = "org.facilitator.event.assignment.completed.v1";
const FAILED = "org.facilitator.event.assignment.failed.v1";
const HEARTBEAT = "org.facilitator.event.workspace.heartbeat.v1";
const APPROVAL_REQUIRED = "org.facilitator.event.approval.required.v1";
const APPROVAL_REVOKED = "org.facilitator.event.approval.revoked.v1";
const APPROVAL_CONSUME = "org.facilitator.command.approval.consume.v1";

/** Kernel ProblemDetails types the consume path branches on. */
const ERROR_BASE = "https://errors.openfacilitator.org/v1/";

/** Union re-POST debounce: child churn settles before the kernel sees it. */
const UNION_POST_DEBOUNCE_MS = 1000;

/**
 * Boot-race hold: the kernel pushes a session's queued commands the instant
 * the link opens, but a same-container socket child dials the child listener
 * ~100 ms later. A command routed to a known-offline child waits this long
 * for a registration before answering honestly. The hold only ever delays a
 * `child-disconnected` rejection; it never accepts what a rejection would.
 */
const CHILD_ONLINE_GRACE_MS = 5000;

export type GatewayRuntimeState = "starting" | "ready" | "draining" | "closed";

export interface GatewayStatus {
  gatewayId: string;
  state: GatewayRuntimeState;
  mode: GatewayMode;
  reconciled: boolean;
  session?: { leaseEpoch: number; sessionId: string; state: string } | undefined;
  executions: { running: number; completed: number; failed: number; lost: number };
  /** Confirm-held assignments awaiting a decision (incl. consumed-executing). */
  awaitingApproval: number;
  bindings: {
    assignmentId: string;
    executionId: string;
    state: string;
    connectionGeneration: number;
    executionGeneration: number;
    childId: string;
    childGeneration: number;
  }[];
  /** Every known child (live + durable offline), childWorkspaceId-sorted. */
  children: ChildStatusRow[];
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
  /** Override the policy seam (defaults to TeamPolicy from a policy file, else DefaultPolicy). */
  policy?: PolicyEvaluator;
  /** Override actor-claim extraction (unit tests inject workload-only claims). */
  claimsOf?: ((actorEvidenceRef: string) => Promise<ActorClaims>) | undefined;
  /** Boot-race hold over an offline route; default CHILD_ONLINE_GRACE_MS.
   *  Tests pin the honest rejection fast with a small value. */
  childOnlineGraceMs?: number | undefined;
}

/** One policy decision + everything the audit line cites about it. */
interface PolicyDecision {
  readonly verdict: PolicyVerdict;
  readonly ctx: PolicyContext;
  readonly workId?: string | undefined;
  readonly assignmentId?: string | undefined;
  readonly evidenceRef?: string | undefined;
  /** The row behind a `confirm` verdict (its actorAnyOf seeds eligibleActors). */
  readonly matchedRow: PolicyRow | null;
}

/** Live view of the fleet's advertised descriptors (online first, offline honest). */
class FleetCapabilities implements ReadonlyMap<string, CapabilityDescriptor> {
  constructor(private readonly fleet: ChildFleet) {}
  get(key: string): CapabilityDescriptor | undefined {
    return this.fleet.descriptorOf(key);
  }
  has(key: string): boolean {
    return this.fleet.descriptorOf(key) !== undefined;
  }
  // Iteration is not part of the DefaultPolicy seam.
  forEach(): void {
    /* unsupported view */
  }
  get size(): number {
    return 0;
  }
  [Symbol.iterator](): MapIterator<[string, CapabilityDescriptor]> {
    return [][Symbol.iterator]() as MapIterator<[string, CapabilityDescriptor]>;
  }
  entries(): MapIterator<[string, CapabilityDescriptor]> {
    return [][Symbol.iterator]() as MapIterator<[string, CapabilityDescriptor]>;
  }
  keys(): MapIterator<string> {
    return [][Symbol.iterator]() as MapIterator<string>;
  }
  values(): MapIterator<CapabilityDescriptor> {
    return [][Symbol.iterator]() as MapIterator<CapabilityDescriptor>;
  }
}

/** The team Gateway: one endpoint, a fleet of children, the execution truth. */
export class GatewayRuntime {
  /** Embedded mode only: the in-process child's id ("" in fleet modes). */
  readonly childWorkspaceId: string;

  private readonly config: GatewayConfig;
  private readonly deps: CreateGatewayRuntimeDeps;
  private readonly logger: LinkLogger;
  private readonly paths: GatewayPaths;
  private readonly fleet: ChildFleet;
  private readonly embedded: EmbeddedChild | null;
  private readonly policy: PolicyEvaluator;
  /** The loaded policy document (null ⇒ DefaultPolicy fallback). */
  private readonly loadedPolicy: LoadedPolicy | null;
  private readonly claimsOf: (actorEvidenceRef: string) => Promise<ActorClaims>;
  private readonly capabilityByExecution = new Map<string, string>();
  private client: LinkClient | null = null;
  private listener: ChildListener | null = null;
  private heartbeatTimer: ReturnType<typeof setInterval> | undefined;
  private unionTimer: ReturnType<typeof setTimeout> | undefined;
  private tokenProvider: LinkTokenProvider | null = null;
  /** Reconcile polling: the OIDC CC provider (undefined ⇒ no REST plane). */
  private workloadToken: LinkTokenProvider | null = null;
  /** Digest of the manifest bytes the kernel last accepted (hello carries it). */
  private currentDigest: string;
  private _state: GatewayRuntimeState = "starting";
  private _reconciled = false;
  private childError = false;

  constructor(config: GatewayConfig, deps: CreateGatewayRuntimeDeps = {}) {
    this.config = config;
    this.deps = deps;
    this.logger = deps.logger ?? createConsoleLogger();
    mkdirSync(config.stateDir, { recursive: true, mode: 0o700 });
    this.paths = deps.ledgerFactory?.(config.stateDir) ?? defaultLedgers(config.stateDir);
    this.fleet = new ChildFleet(this.logger);
    for (const entry of config.children ?? []) {
      this.fleet.entries.set(`${config.gatewayId}/${entry.name}`, entry);
    }
    // Durable fleet shadow: generations persist, every child starts offline.
    this.fleet.seed(this.paths.execution.children());
    if (config.mode === "embedded" && config.child !== undefined) {
      const childManifest = loadChildManifest(config, this.logger);
      this.embedded = new EmbeddedChild({
        childWorkspaceId: `${config.gatewayId}/${config.child.name}`,
        mode: config.child.mode,
        manifest: childManifest,
        ...(deps.handlerFactory !== undefined ? { handler: deps.handlerFactory() } : {}),
        logger: this.logger,
      });
      this.childWorkspaceId = this.embedded.childWorkspaceId;
      this.fleet.register(new EmbeddedMember(this.childWorkspaceId, childManifest, this.embedded));
    } else {
      this.embedded = null;
      this.childWorkspaceId = "";
    }
    // A configured policy file boots or aborts (malformed ⇒ throw);
    // no file ⇒ today's DefaultPolicy plus one loud warning at start().
    this.loadedPolicy =
      deps.policy === undefined && config.policy !== undefined
        ? loadPolicyFile(config.policy.inline ?? readFileSync(config.policy.path as string, "utf8"))
        : null;
    this.policy =
      deps.policy ??
      (this.loadedPolicy !== null
        ? new TeamPolicy(this.loadedPolicy, new FleetCapabilities(this.fleet))
        : new DefaultPolicy(new FleetCapabilities(this.fleet)));
    this.claimsOf =
      deps.claimsOf ??
      ((ref: string) => extractClaims(ref, defaultClaimsDeps(`workload:${config.gatewayId}`)));
    this.currentDigest = digestOf(this.fleet.unionManifest(config.team));
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

  /** The manifest the kernel holds: live fleet union, ownerTeam overridden. */
  get aggregatedManifest(): CapabilityManifestV1 {
    return this.fleet.unionManifest(this.config.team);
  }

  /**
   * Enroll/mount → capabilities POST (embedded) → connect → reconcile →
   * child listener. Resolves AFTER the reconcile pass with the ready-line
   * parts (the CLI prints the line).
   */
  async start(): Promise<{ leaseEpoch: number; children: number }> {
    if (this.loadedPolicy === null && this.deps.policy === undefined) {
      this.logger.warn(
        "policy: no policy file configured — DefaultPolicy in effect (capability-advertence only)",
      );
    }
    const token = await this.acquireToken();
    this.tokenProvider = token;
    if (this.embedded !== null) {
      // 05 fleet-of-one: the single child's union POSTs before first hello.
      const posted = await postCapabilities(this.aggregatedManifest, {
        baseUrl: this.config.facilitatorUrl,
        getToken: token.getToken,
        ...(this.deps.fetchImpl !== undefined ? { fetchImpl: this.deps.fetchImpl } : {}),
      });
      this.currentDigest = posted.digest;
    }
    // Fleet modes POST NOTHING at boot: hello carries the placeholder until
    // a child registers and the debounced union POST lands.

    const transport =
      this.deps.transportFactory?.(token, this.config) ??
      new SignalrTransport({ baseUrl: this.config.facilitatorUrl, token });
    const client = createLinkClient({
      transport,
      ledger: this.paths.link,
      endpointId: this.config.gatewayId,
      // A getter: a reconnect after child churn carries the CURRENT digest.
      capabilityManifestDigest: () => this.currentDigest,
      token,
      handler: this.gatewayHandler(),
      logger: this.logger,
      eventSource: `urn:workspace:${this.config.gatewayId}`,
    });
    this.client = client;

    if (this.config.childTransport !== undefined) {
      this.listener = await startChildListener(this.config.childTransport, {
        fleet: this.fleet,
        logger: this.logger,
        gatewayId: this.config.gatewayId,
        heartbeatSeconds: this.config.heartbeatSeconds,
        ...(this.config.childSecret !== undefined ? { childSecret: this.config.childSecret } : {}),
        onRegistered: (link) => this.onChildRegistered(link),
        onDisconnected: (childId) => this.onChildDisconnected(childId),
      });
    }
    const session = await client.start();

    // Child events flow live from the publisher law below; a pre-welcome
    // publishEvent buffers durably in the ledger outbox and ships once the
    // session is up (SDK transport law).
    if (this.embedded !== null) {
      this.embedded.setPublisher((env) => this.forwardEvent(env, this.embeddedMember()));
      await this.forwardQueued();
    }

    await this.reconcile();
    await this.reconcileApprovals();
    this._reconciled = true;

    this._state = "ready";
    this.heartbeatTimer = setInterval(
      () => void this.publishHeartbeat("periodic"),
      this.config.heartbeatSeconds * 1000,
    );
    return { leaseEpoch: session.leaseEpoch, children: this.fleet.size };
  }

  /** SIGTERM path: one draining heartbeat, goodbye, close everything. */
  async stop(): Promise<void> {
    this._state = "draining";
    clearInterval(this.heartbeatTimer);
    this.heartbeatTimer = undefined;
    clearTimeout(this.unionTimer);
    this.unionTimer = undefined;
    const client = this.client;
    if (client !== null) {
      await this.publishHeartbeat("shutdown");
      await client.stop("gateway-shutdown");
    }
    if (this.listener !== null) {
      await this.listener.close(); // goaways every socket child
      this.listener = null;
    }
    for (const member of this.fleet.members()) member.goaway("gateway-shutdown");
    await this.embedded?.close();
    // Honest durable state: after this process dies, no child is connected.
    for (const row of this.paths.execution.children()) {
      this.paths.execution.setChildState(row.childId, "offline");
    }
    this.paths.link.close();
    this.paths.execution.close();
    this._state = "closed";
  }

  gatewayStatus(): GatewayStatus {
    const client = this.client;
    const session = client?.session ?? null;
    const runningByChild = new Map<string, number>();
    for (const row of this.paths.execution.byState("running")) {
      runningByChild.set(row.childId, (runningByChild.get(row.childId) ?? 0) + 1);
    }
    return {
      gatewayId: this.config.gatewayId,
      state: this._state,
      mode: this.config.mode,
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
      awaitingApproval: this.paths.execution.pendingCounts().awaitingApproval,
      bindings: this.paths.execution.all().map((row) => ({
        assignmentId: row.assignmentId,
        executionId: row.executionId,
        state: row.state as string,
        connectionGeneration: row.connectionGeneration,
        executionGeneration: row.executionGeneration,
        childId: row.childId,
        childGeneration: row.childGeneration,
      })),
      children: this.fleet.statusRows(runningByChild),
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

  /** Heartbeat `error` latch: set when a child closed abnormally. */
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

  // ── child fleet lifecycle ────────────────────────────────────────────────

  private embeddedMember(): FleetMember {
    const member = this.fleet.member(this.childWorkspaceId);
    if (member === undefined) throw new Error("embedded member vanished");
    return member;
  }

  /**
   * Accepted register-ack: wire the event publisher, persist the fleet
   * shadow (the generation bump IS the fence), re-POST the union, and — for
   * a child the ledger already knows — probe its `running` rows (a child
   * that died and came back forgot its executions; the rows must not lie).
   */
  private onChildRegistered(link: ChildLink): void {
    if (this._state === "closed" || this._state === "draining") return;
    link.setPublisher((env) => this.forwardEvent(env, link));
    for (const env of link.drainEvents()) void this.forwardEvent(env, link);
    const known = this.paths.execution.children().some((row) => row.childId === link.childId);
    this.paths.execution.upsertChild(
      link.childId,
      digestOf(link.manifest),
      JSON.stringify(link.manifest),
      true,
    );
    this.scheduleUnionPost();
    // A pre-ready registration is probed by the boot-time reconcile pass
    // itself (the child is online by then); probing here would double-report
    // a lost execution.
    if (known && this._reconciled) void this.probeChild(link.childId);
    else
      this.logger.info("child registered", {
        childId: link.childId,
        childGeneration: link.generation,
      });
  }

  private onChildDisconnected(childId: string): void {
    if (this._state === "closed" || this._state === "draining") return;
    this.paths.execution.setChildState(childId, "offline");
    this.scheduleUnionPost();
    this.logger.info("child offline", { childId });
  }

  /**
   * Union re-POST on child churn (debounced): the kernel holds ONE manifest
   * per endpoint, so the fleet's total is what travels. A failure retries on
   * the next churn (or a lone retry here) — hello keeps the old digest.
   */
  private scheduleUnionPost(): void {
    if (this.config.mode === "embedded") return; // 05: static manifest, no churn
    clearTimeout(this.unionTimer);
    this.unionTimer = setTimeout(() => {
      this.unionTimer = undefined;
      void this.postUnion();
    }, UNION_POST_DEBOUNCE_MS);
    this.unionTimer.unref?.();
  }

  private async postUnion(): Promise<void> {
    const manifest = this.fleet.unionManifest(this.config.team);
    // An all-offline fleet has no legal manifest (schema: >=1 capability):
    // the kernel keeps the last one; routes answer child-disconnected anyway.
    if (manifest.capabilities.length === 0) return;
    const digest = digestOf(manifest);
    if (digest === this.currentDigest) return; // churn cancelled out
    try {
      const posted = await postCapabilities(manifest, {
        baseUrl: this.config.facilitatorUrl,
        getToken: async () => this.tokenProvider?.getToken() ?? "",
        ...(this.deps.fetchImpl !== undefined ? { fetchImpl: this.deps.fetchImpl } : {}),
      });
      this.currentDigest = posted.digest;
      this.logger.info("capabilities re-posted", {
        capabilities: manifest.capabilities.length,
        digest: posted.digest.slice(0, 18),
      });
    } catch (error) {
      this.logger.warn("capabilities re-post failed (will retry on next churn)", {
        reason: error instanceof Error ? error.message : String(error),
      });
      this.scheduleUnionPost(); // lone retry: the union MUST converge
    }
  }

  /**
   * Re-register probe: the reconnected child's `running` rows are checked
   * against its own memory — `unknown` means it died mid-flight: lost +
   * ONE honest retryable assignment.failed (never re-executed).
   */
  private async probeChild(childId: string): Promise<void> {
    const member = this.fleet.member(childId);
    if (member === undefined) return; // went offline again mid-probe
    for (const row of this.paths.execution.byChildState(childId, "running")) {
      let settled = false;
      try {
        const reply = await member.executor.send({ kind: "status", executionId: row.executionId });
        if (reply.kind === "status" && (reply.state === "completed" || reply.state === "failed")) {
          this.paths.execution.markState(row.executionId, reply.state);
          settled = true;
        } else if (reply.kind === "status") {
          settled = true; // child still runs it: the row stands
        }
      } catch {
        /* transport-dead mid-probe: falls through to lost */
      }
      if (settled) continue;
      await this.reportLost(row, "child-lost-execution");
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
    const workload =
      config.workload === undefined
        ? undefined
        : oidcClientCredentialsTokenProvider(config.workload, deps.fetchImpl ?? fetch);
    // Reconcile polls the kernel REST plane with the SAME workload
    // credential (audience facilitator-api via the realm's audience mapper).
    this.workloadToken = workload ?? null;
    const refresh = async (): Promise<void> => {
      // Mode (b): a fresh OIDC CC JWT per enroll; mode (a): the shared
      // enrollment secret. Neither ⇒ static mount, nothing to rotate.
      const credential = workload !== undefined ? await workload.getToken() : secret;
      if (credential === undefined) return;
      const response = await enroll(
        {
          baseUrl: config.facilitatorUrl,
          token: credential,
          endpointId: config.gatewayId,
          instanceId: `gw-${randomBytes(8).toString("hex")}`,
          trustMode: config.trustMode,
          role: config.role ?? "gateway",
          team: config.team,
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
      if (secret === undefined && workload === undefined) {
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
          case ASSIGNMENT_INPUT:
            return this.onAssignmentInput(cmd);
          case NUDGE: {
            // The NudgeSweeper posts to the owning endpoint; the child wire
            // vocabulary has no nudge frame, so the honest answer is the
            // child's own capability answer — `unsupported` — never a
            // misleading passthrough no-binding. A nudge never fails work:
            // the sweeper just keeps the stall episode open.
            const data = cmd.data as unknown as { assignmentId?: string };
            const row =
              data.assignmentId === undefined
                ? null
                : this.paths.execution.byAssignment(data.assignmentId);
            return {
              kind: "unsupported",
              reason:
                row === null
                  ? "no-execution"
                  : "harness-plane: the child wire carries no nudge frame",
            };
          }
          default:
            return this.passthrough(cmd);
        }
      },
      onDecided: (env) => this.onDecided(env),
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
    const route = this.fleet.route(data.capabilityId);
    if (route === undefined) {
      // Nobody advertises the capability: no row (the policy-deny no-row law
      // mirrored) — a stale kernel route is answered, never executed blind.
      return { kind: "unsupported", reason: `no-child-advertises: ${data.capabilityId}` };
    }
    if (!route.online) {
      // Fleet honesty: the advertiser is known and gone; retry upstream —
      // but a child mid-boot (kernel pushes on session-open, a socket child
      // dials ~100 ms later) gets one bounded hold first, so a re-hydrated
      // assign landing in that window is EXECUTED, not killed by a receipt
      // the kernel would lawfully ack as terminal (restart law).
      if (
        !(await this.fleet.waitUntilOnline(
          route.childId,
          this.deps.childOnlineGraceMs ?? CHILD_ONLINE_GRACE_MS,
        ))
      ) {
        return { kind: "rejected", reason: "child-disconnected" };
      }
    }
    const member = this.fleet.member(route.childId);
    if (member === undefined) return { kind: "rejected", reason: "child-disconnected" };
    const decision = await this.decide("command.forward", cmd, member.childId);
    const epoch = await this.awaitSessionEpoch();
    if (decision.verdict === "confirm") {
      // Confirm hold: pending row + approval.required, accepted receipt; the
      // decided-push (or a reconcile poll) executes or settles it later.
      this.audit(decision, "confirm-required");
      return this.holdForApproval(cmd, deliveryId, decision, member, epoch);
    }
    if (decision.verdict === "deny") {
      // Policy rejection is not an execution: no ledger row.
      this.audit(decision, "policy-denied");
      return { kind: "rejected", reason: `policy-denied: ${data.capabilityId}` };
    }
    this.audit(decision, "policy-allowed");
    // Quota: the live count of running rows routed to this child.
    const maxInFlight = this.fleet.entries.get(member.childId)?.maxInFlight ?? 1;
    if (this.paths.execution.byChildState(member.childId, "running").length >= maxInFlight) {
      return { kind: "rejected", reason: `child-busy: ${member.childId}` };
    }
    const executionId = newExecutionId();
    try {
      this.paths.execution.insert({
        deliveryId,
        dedupeKey: data.commandId,
        gatewayId: this.config.gatewayId,
        childId: member.childId,
        assignmentId: data.assignmentId,
        executionId,
        workId: data.workId,
        connectionGeneration: epoch,
        // B(4): the event stamp rides the ASSIGNMENT generation carried by
        // the envelope (the kernel's fence key), never the link epoch.
        executionGeneration: data.executionGeneration ?? 1,
        childGeneration: member.generation,
      });
    } catch (error) {
      if (error instanceof BindingConflictError) {
        // Same commandId under a new deliveryId: already bound.
        return { kind: "accepted" };
      }
      throw error;
    }
    this.capabilityByExecution.set(executionId, data.capabilityId);
    let reply: ChildReply;
    try {
      reply = await member.executor.send({
        kind: "start",
        executionId,
        executionGeneration: data.executionGeneration ?? 1,
        command: cmd,
      });
    } catch (error) {
      // Transport-dead (disconnect mid-send / reply timeout): the command
      // never demonstrably ran — honest failure, the row carries the reason.
      const reason = error instanceof ChildTransportError ? error.reason : "child-disconnected";
      this.paths.execution.markState(executionId, "failed", reason);
      return { kind: "rejected", reason };
    }
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
    if (row === null || row.state !== "running") {
      // A confirm-held assignment has no binding row yet — cancel
      // revokes the approval, steer has nothing mid-flight to steer.
      const pending = this.paths.execution.pendingByAssignment(data.assignmentId);
      if (pending !== null && pending.state === "awaiting-approval") {
        if (kind === "cancel") {
          await this.revokePending(pending, data.reason ?? "work-cancelled");
          return { kind: "accepted" };
        }
        return { kind: "rejected", reason: "pending-approval" };
      }
      if (this.embedded !== null && row === null) return this.passthrough(cmd);
      return { kind: "rejected", reason: "no-binding" };
    }
    // Bound-child law: a running row decides the target, not the union.
    const member = this.fleet.member(row.childId);
    if (member === undefined) {
      return { kind: "rejected", reason: "child-disconnected" };
    }
    const capabilityId = this.capabilityByExecution.get(row.executionId);
    if (capabilityId !== undefined) {
      const decision = await this.decide("command.forward", cmd, member.childId, capabilityId);
      // 05 law kept: the descriptor forbids it — the child's own answer
      // decides for the embedded fleet-of-one, a fleet refuses honestly.
      // `confirm` has no hold to park an interaction in: it denies here.
      if (decision.verdict === "deny" || decision.verdict === "confirm") {
        this.audit(decision, `policy-${decision.verdict}`);
        if (this.embedded !== null && member === this.embeddedMember())
          return this.passthrough(cmd);
        return {
          kind: "rejected",
          reason:
            decision.verdict === "confirm"
              ? `confirm-not-supported-here: ${capabilityId}`
              : `policy-denied: ${capabilityId}`,
        };
      }
    }
    let reply: ChildReply;
    try {
      reply = await member.executor.send(
        kind === "steer"
          ? { kind: "steer", executionId: row.executionId, command: cmd }
          : { kind: "stop", executionId: row.executionId, reason: data.reason },
      );
    } catch (error) {
      const reason = error instanceof ChildTransportError ? error.reason : "child-disconnected";
      return { kind: "rejected", reason };
    }
    if (kind === "cancel" && reply.kind === "accepted") {
      if (this.fence(row, "cancel") === "fresh") {
        this.paths.execution.markState(row.executionId, "completed");
        this.settleLinkedPending(row.executionId, "completed");
      }
    }
    return childOutcome(reply);
  }

  /**
   * assignment.input bound-child law (mirrors steer/cancel): the running
   * binding row decides the target; a confirm-held assignment has no row yet
   * and refuses `pending-approval`; no row at all refuses `no-binding` (the
   * embedded fleet-of-one passes through). The Gateway-local row STAYS
   * `running` — an input wait is not a state transition here; the child
   * answers `accepted` iff the question was still live (unknown/dropped
   * otherwise). The child reply maps to the receipt via childOutcome.
   */
  private async onAssignmentInput(cmd: EnvelopeCommand): Promise<CommandOutcome> {
    const data = cmd.data as unknown as AssignmentInputCommandV1Data;
    // The schema carries no assignmentId: the envelope subject is the
    // addressing (`work/<workId>/assignment/<assignmentId>`, assign law);
    // a subject that doesn't address an assignment is simply unbound.
    const addressed = /^work\/[^/]+\/assignment\/([^/]+)$/.exec(cmd.subject ?? "");
    const assignmentId = addressed?.[1];
    if (assignmentId === undefined) return { kind: "rejected", reason: "no-binding" };
    const row = this.paths.execution.byAssignment(assignmentId);
    if (row === null || row.state !== "running") {
      const pending = this.paths.execution.pendingByAssignment(assignmentId);
      if (pending !== null && pending.state === "awaiting-approval") {
        // An input can only be answered by a live run: a confirm-held
        // assignment has no child to unblock yet.
        return { kind: "rejected", reason: "pending-approval" };
      }
      if (this.embedded !== null && row === null) return this.passthrough(cmd);
      return { kind: "rejected", reason: "no-binding" };
    }
    const member = this.fleet.member(row.childId);
    if (member === undefined) {
      return { kind: "rejected", reason: "child-disconnected" };
    }
    const capabilityId = this.capabilityByExecution.get(row.executionId);
    if (capabilityId !== undefined) {
      const decision = await this.decide("command.forward", cmd, member.childId, capabilityId);
      if (decision.verdict === "deny" || decision.verdict === "confirm") {
        this.audit(decision, `policy-${decision.verdict}`);
        if (this.embedded !== null && member === this.embeddedMember())
          return this.passthrough(cmd);
        return {
          kind: "rejected",
          reason:
            decision.verdict === "confirm"
              ? `confirm-not-supported-here: ${capabilityId}`
              : `policy-denied: ${capabilityId}`,
        };
      }
    }
    let reply: ChildReply;
    try {
      reply = await member.executor.send({
        kind: "respond",
        executionId: row.executionId,
        requestId: data.requestId,
        text: data.answer,
      });
    } catch (error) {
      const reason = error instanceof ChildTransportError ? error.reason : "child-disconnected";
      return { kind: "rejected", reason };
    }
    // The row stays `running`: the answer unblocks the LIVE run; the child's
    // own terminal event (or a reconcile) settles it later.
    return childOutcome(reply);
  }

  // ── policy decision plumbing ───────────────────────────────────

  /**
   * The whole resource-derivation law: the connector-supplied
   * `inputs.resource` when present, else the capability id itself as the
   * wildcard-capability resource. Finer resources are connector territory.
   */
  static resourceOf(data: Record<string, unknown>, capabilityId: string): string {
    const inputs = data["inputs"];
    if (typeof inputs === "object" && inputs !== null) {
      const resource = (inputs as Record<string, unknown>)["resource"];
      if (typeof resource === "string" && resource.length > 0) return resource;
    }
    return capabilityId;
  }

  private async decide(
    action: PolicyAction,
    cmd: EnvelopeCommand,
    childWorkspaceId: string,
    capabilityIdOverride?: string,
  ): Promise<PolicyDecision> {
    const data = cmd.data as unknown as Record<string, unknown>;
    const capabilityId = capabilityIdOverride ?? String(data["capabilityId"] ?? "");
    const evidenceRef =
      typeof data["actorEvidenceRef"] === "string" ? (data["actorEvidenceRef"] as string) : "";
    const claims = await this.claimsOf(evidenceRef);
    const ctx: PolicyContext = {
      commandType: cmd.type,
      capabilityId,
      childWorkspaceId,
      resource: GatewayRuntime.resourceOf(data, capabilityId),
      claims,
      workloadActor: `workload:${this.config.gatewayId}`,
    };
    const verdict = this.policy.evaluate(action, ctx);
    return {
      verdict,
      ctx,
      workId: typeof data["workId"] === "string" ? data["workId"] : undefined,
      assignmentId: typeof data["assignmentId"] === "string" ? data["assignmentId"] : undefined,
      evidenceRef,
      matchedRow: verdict === "confirm" ? (this.policy.matchRow?.(action, ctx) ?? null) : null,
    };
  }

  private decideEvent(env: EnvelopeEvent, childWorkspaceId: string): PolicyDecision {
    const workloadActor = `workload:${this.config.gatewayId}`;
    const ctx: PolicyContext = {
      commandType: env.type,
      childWorkspaceId,
      resource: "*",
      claims: { subject: workloadActor, groups: [], verified: false },
      workloadActor,
    };
    return { verdict: this.policy.evaluate("event.forward", ctx), ctx, matchedRow: null };
  }

  /** One audit line per decision; long-lived tokens NEVER included. */
  private audit(decision: PolicyDecision, reason: string): void {
    const { ctx } = decision;
    auditPolicy(this.logger, {
      decision: decision.verdict,
      reason,
      ...(decision.workId !== undefined ? { workId: decision.workId } : {}),
      ...(decision.assignmentId !== undefined ? { assignmentId: decision.assignmentId } : {}),
      ...(ctx.capabilityId !== undefined ? { capabilityId: ctx.capabilityId } : {}),
      resource: ctx.resource,
      callerWorkload: ctx.workloadActor,
      gatewayWorkload: `workload:${this.config.gatewayId}`,
      ...(ctx.claims.verified ? { humanSubject: ctx.claims.subject } : {}),
      policyId: this.loadedPolicy?.policyId ?? "default",
      ...(decision.evidenceRef !== undefined ? { evidenceRef: decision.evidenceRef } : {}),
    });
  }

  // ── approval lifecycle ─────────────────────────────────────────

  /**
   * Confirm hold: mint the approval, park a pending row BEFORE
   * publishing (crash ⇒ reconcile, never a lost action), publish
   * approval.required, answer accepted. The decision arrives as a
   * decided-push or via reconcile polling.
   */
  private async holdForApproval(
    cmd: EnvelopeCommand,
    deliveryId: string,
    decision: PolicyDecision,
    member: FleetMember,
    epoch: number,
  ): Promise<CommandOutcome> {
    const data = cmd.data as unknown as WorkAssignCommandV1Data;
    const matched = decision.matchedRow;
    const eligibleActors =
      matched !== null && matched.actorAnyOf.length > 0
        ? [...matched.actorAnyOf]
        : [decision.ctx.claims.subject];
    // The rule a consent decision must be attributable to: its name, or a
    // positional locator when the file left it unnamed. `not-requester` is the
    // rule flag that denies the requesting side as sole consent.
    const ruleId =
      matched === null
        ? undefined
        : (matched.id ?? `row:${this.loadedPolicy?.file.policies.indexOf(matched) ?? -1}`);
    const notRequester = matched?.conditions?.["not-requester"] === true;
    const approvalId = randomUUID();
    const nonce = `n-${randomBytes(24).toString("base64url")}`;
    const timeout = this.config.policy?.confirmTimeoutSeconds ?? 900;
    const createdAt = new Date().toISOString().slice(0, 19).concat("Z");
    const expiresAt = new Date(Date.parse(createdAt) + timeout * 1000)
      .toISOString()
      .slice(0, 19)
      .concat("Z");
    const action: Record<string, unknown> = {
      capabilityId: data.capabilityId,
      resource: decision.ctx.resource,
      assignmentId: data.assignmentId,
    };
    if (data.workId !== undefined) action["workId"] = data.workId;
    // The frozen approval record: the kernel recomputes and compares.
    const record = { approvalId, action, eligibleActors, expiresAt, createdAt };
    const actionDigest = computeActionDigest(record);
    this.paths.execution.insertPending({
      approvalId,
      deliveryId,
      envelope: JSON.stringify(cmd),
      workId: data.workId ?? null,
      assignmentId: data.assignmentId,
      capabilityId: data.capabilityId,
      resource: decision.ctx.resource,
      childId: member.childId,
      approvalDigestRecord: JSON.stringify(record),
      policyId: this.loadedPolicy?.policyId ?? "default",
      requiredAt: createdAt,
      expiresAt,
      epoch,
    });
    const env = buildEvent(APPROVAL_REQUIRED, {
      source: `urn:workspace:${this.config.gatewayId}`,
      subject: `approval/${approvalId}`,
      data: {
        approvalId,
        actionDigest,
        eligibleActors,
        nonce,
        expiresAt,
        createdAt,
        action,
        // Persist-first (law D3): the held command rides to the kernel, which
        // stores it before any notice goes out and hands it back on consume.
        command: cmd,
        // Consent authority inputs: who requested (verified human subject or
        // this workload), and the rule that made consent necessary.
        requestedBy: decision.ctx.claims.subject,
        ...(ruleId !== undefined ? { ruleId } : {}),
        ...(notRequester ? { notRequester: true } : {}),
        ...(data.workId !== undefined ? { workId: data.workId } : {}),
      },
      ...(cmd.traceparent !== undefined ? { traceparent: cmd.traceparent } : {}),
      correlationid: cmd.id,
    });
    try {
      await this.client?.publishEvent(env);
    } catch (error) {
      // The kernel refused the approval itself: nothing can decide it.
      const reason = error instanceof Error ? error.message : String(error);
      this.paths.execution.markPending(approvalId, "failed");
      this.approvalAudit("approval-publish-failed", data.workId, data.assignmentId, approvalId);
      await this.publishAssignmentFailed(
        data.workId,
        data.assignmentId,
        data.executionGeneration ?? 1,
        `approval-publish-failed: ${reason}`,
      );
      return { kind: "rejected", reason: `approval-publish-failed: ${approvalId}` };
    }
    this.approvalAudit("approval-required", data.workId, data.assignmentId, approvalId);
    this.logger.info("approval required", { approvalId, assignmentId: data.assignmentId });
    return { kind: "accepted" };
  }

  /**
   * Decided-push ingest. The receipt is accepted whatever
   * happens here; every settle is ledger-side. A push for an unknown or
   * already-terminal approval is a no-op (late/replayed push); a createdAt
   * mismatch against the stored digest record means the push is NOT for the
   * action this row parked — logged and ignored, never applied.
   */
  private async onDecided(env: EnvelopeEvent): Promise<void> {
    const data = (env.data ?? {}) as Record<string, unknown>;
    const approvalId = String(data["approvalId"] ?? "");
    const decision = String(data["decision"] ?? "");
    const pending = this.paths.execution.getPending(approvalId);
    if (pending === null) {
      this.logger.info("decided-push for unknown approval", { approvalId, decision });
      return;
    }
    if (pending.state !== "awaiting-approval") {
      return; // already settled (reconcile raced the push, or revoke)
    }
    const record = JSON.parse(pending.approvalDigestRecord) as { createdAt: string };
    if (String(data["createdAt"] ?? "") !== record.createdAt) {
      this.logger.warn("decided-push createdAt mismatch (ignored)", { approvalId });
      return;
    }
    if (decision === "approved") {
      await this.consumeAndExecute(pending, String(data["decidedBy"] ?? ""));
      return;
    }
    const state: "expired" | "revoked" | "failed" =
      decision === "expired" ? "expired" : decision === "revoked" ? "revoked" : "failed";
    this.paths.execution.markPending(approvalId, state);
    this.approvalAudit(
      `approval-${decision === "" ? "denied" : decision}`,
      pending.workId,
      pending.assignmentId,
      approvalId,
    );
    await this.publishAssignmentFailed(
      pending.workId,
      pending.assignmentId,
      GatewayRuntime.pendingGeneration(pending),
      `approval-${decision === "" ? "denied" : decision}`,
    );
  }

  /**
   * Approved: re-evaluate current state (a stale policy or a
   * vanished capability denies even after a human approved), consume the
   * approval synchronously, then bind + start like a plain assign.
   */
  private async consumeAndExecute(pending: PendingRow, decidedBy: string): Promise<void> {
    const approvalId = pending.approvalId;
    let cmd = JSON.parse(pending.envelope) as EnvelopeCommand;
    let data = cmd.data as unknown as WorkAssignCommandV1Data;
    const member = this.fleet.member(pending.childId);
    const deny = async (reason: string): Promise<void> => {
      this.paths.execution.markPending(approvalId, "failed");
      this.approvalAudit(
        `approval-exec-${reason}`,
        pending.workId,
        pending.assignmentId,
        approvalId,
      );
      await this.publishAssignmentFailed(
        pending.workId,
        pending.assignmentId,
        GatewayRuntime.pendingGeneration(pending),
        `approval-${reason}`,
      );
    };
    if (member === undefined) {
      await deny("child-offline");
      return;
    }
    const fresh = await this.decide("command.forward", cmd, member.childId);
    if (fresh.verdict === "deny") {
      this.audit(fresh, "policy-deny-on-reaudit");
      await deny("denied-on-reaudit");
      return;
    }
    const maxInFlight = this.fleet.entries.get(member.childId)?.maxInFlight ?? 1;
    if (this.paths.execution.byChildState(member.childId, "running").length >= maxInFlight) {
      await deny("child-busy");
      return;
    }
    const consume = buildEvent(APPROVAL_CONSUME, {
      source: `urn:workspace:${this.config.gatewayId}`,
      subject: `approval/${approvalId}`,
      data: {
        commandId: `${approvalId}:consume`,
        approvalId,
        actorEvidenceRef: `workload:${this.config.gatewayId}`,
      },
      ...(cmd.traceparent !== undefined ? { traceparent: cmd.traceparent } : {}),
      correlationid: cmd.id,
    });
    let rejectedProblem: string | undefined;
    try {
      const ack = await this.client?.sendCommand(consume);
      if (ack !== undefined && ack.status === "rejected") {
        rejectedProblem = String(ack.problem?.type ?? "consume-rejected").replace(ERROR_BASE, "");
      }
      // Persist-first read-back: when the kernel holds the command, what it
      // hands back IS what consent covered — it replaces the local
      // recollection (a divergence is logged, never silently mixed).
      const held = ack?.data?.["envelope"];
      if (held !== undefined && held !== null) {
        const adopted = held as EnvelopeCommand;
        if (JSON.stringify(adopted) !== JSON.stringify(cmd)) {
          this.logger.warn("kernel held envelope diverges from local pending", { approvalId });
          cmd = adopted;
          data = cmd.data as unknown as WorkAssignCommandV1Data;
        }
      }
    } catch (error) {
      // Transport-dead on the synchronous consume: leave the row awaiting;
      // reconcile polling resolves the kernel's answer later.
      this.logger.warn("consume send failed (reconcile will resolve)", {
        approvalId,
        reason: error instanceof Error ? error.message : String(error),
      });
      return;
    }
    if (rejectedProblem !== undefined) {
      // The kernel answered the consume with a typed problem: settle to the
      // problem's own terminal state (revoked/expired), else honest failed.
      // The approval is spent either way — a replay denies (single use).
      const state: "revoked" | "expired" | "failed" =
        rejectedProblem === "approval-revoked"
          ? "revoked"
          : rejectedProblem === "approval-expired"
            ? "expired"
            : "failed";
      this.paths.execution.markPending(approvalId, state);
      this.approvalAudit(
        `approval-${rejectedProblem}`,
        pending.workId,
        pending.assignmentId,
        approvalId,
      );
      await this.publishAssignmentFailed(
        pending.workId,
        pending.assignmentId,
        GatewayRuntime.pendingGeneration(pending),
        rejectedProblem,
      );
      return;
    }
    this.approvalAudit("approval-consumed", pending.workId, pending.assignmentId, approvalId);
    const epoch = this.client?.session?.leaseEpoch ?? 0;
    const executionId = newExecutionId();
    try {
      this.paths.execution.insert({
        deliveryId: pending.deliveryId,
        dedupeKey: data.commandId,
        gatewayId: this.config.gatewayId,
        childId: member.childId,
        assignmentId: data.assignmentId,
        executionId,
        workId: data.workId ?? "",
        connectionGeneration: epoch,
        executionGeneration: data.executionGeneration ?? 1,
        childGeneration: member.generation,
      });
    } catch (error) {
      if (error instanceof BindingConflictError) {
        // A parallel redelivered assign bound it first: the pending is done.
        this.paths.execution.bindPendingExecution(approvalId, executionId);
        this.paths.execution.markPending(approvalId, "completed");
        return;
      }
      throw error;
    }
    this.capabilityByExecution.set(executionId, data.capabilityId);
    this.paths.execution.bindPendingExecution(approvalId, executionId);
    let reply: ChildReply;
    try {
      reply = await member.executor.send({
        kind: "start",
        executionId,
        executionGeneration: data.executionGeneration ?? 1,
        command: cmd,
      });
    } catch (error) {
      const reason = error instanceof ChildTransportError ? error.reason : "child-disconnected";
      this.paths.execution.markState(executionId, "failed", reason);
      this.paths.execution.markPending(approvalId, "failed");
      return;
    }
    const outcome = this.settleStart(executionId, reply);
    if (outcome.kind !== "accepted") this.paths.execution.markPending(approvalId, "failed");
    this.logger.info("approved action executed", {
      approvalId,
      executionId,
      decidedBy,
      outcome: outcome.kind,
    });
  }

  /** Cancel on a pending action: revoke at the kernel, park revoked. */
  private async revokePending(pending: PendingRow, reason: string): Promise<void> {
    this.paths.execution.markPending(pending.approvalId, "revoked");
    try {
      await this.client?.publishEvent(
        buildEvent(APPROVAL_REVOKED, {
          source: `urn:workspace:${this.config.gatewayId}`,
          subject: `approval/${pending.approvalId}`,
          data: {
            approvalId: pending.approvalId,
            revokedBy: `workload:${this.config.gatewayId}`,
            reason,
          },
        }),
      );
    } catch (error) {
      this.logger.warn("approval.revoked publish dropped (row already revoked)", {
        approvalId: pending.approvalId,
        reason: error instanceof Error ? error.message : String(error),
      });
    }
    this.approvalAudit(
      "approval-revoked",
      pending.workId,
      pending.assignmentId,
      pending.approvalId,
    );
  }

  /**
   * Crash recovery for approvals: every non-terminal pending row re-resolves
   * against the kernel ledger (the REST plane, workload credential).
   * pending/absent ⇒ republish (kernel dedupes by approvalId); approved ⇒
   * consume + execute (the kernel's atomic single-use consume is the arbiter
   * of a crash between consume and start); denied/expired/revoked ⇒ settle +
   * ONE honest assignment.failed.
   */
  private async reconcileApprovals(): Promise<void> {
    const now = Date.now();
    for (const pending of [
      ...this.paths.execution.pendingByState("awaiting-approval"),
      ...this.paths.execution.pendingByState("executing"),
    ]) {
      if (pending.state === "awaiting-approval" && Date.parse(pending.expiresAt) <= now) {
        this.paths.execution.markPending(pending.approvalId, "expired");
        this.approvalAudit(
          "approval-expired",
          pending.workId,
          pending.assignmentId,
          pending.approvalId,
        );
        await this.publishAssignmentFailed(
          pending.workId,
          pending.assignmentId,
          GatewayRuntime.pendingGeneration(pending),
          "approval-expired",
        );
        continue;
      }
      if (pending.state === "executing") {
        // Consume committed pre-crash; the binding row tracks the rest —
        // unless there is none, meaning start never shipped.
        if (
          pending.executionId === null ||
          this.paths.execution.byExecution(pending.executionId) === null
        ) {
          this.paths.execution.markPending(pending.approvalId, "failed");
          await this.publishAssignmentFailed(
            pending.workId,
            pending.assignmentId,
            GatewayRuntime.pendingGeneration(pending),
            "approval-execution-lost",
          );
        }
        continue;
      }
      const kernel = await this.pollKernelApproval(pending);
      if (kernel === null) continue; // unreachable kernel: retry next tick
      if (kernel === "approved") {
        await this.consumeAndExecute(pending, "reconcile");
      } else if (kernel === "pending" || kernel === "absent") {
        await this.republishRequired(pending);
      } else if (kernel === "denied") {
        this.paths.execution.markPending(pending.approvalId, "failed");
        await this.publishAssignmentFailed(
          pending.workId,
          pending.assignmentId,
          GatewayRuntime.pendingGeneration(pending),
          "approval-denied",
        );
      } else if (kernel === "expired") {
        this.paths.execution.markPending(pending.approvalId, "expired");
        await this.publishAssignmentFailed(
          pending.workId,
          pending.assignmentId,
          GatewayRuntime.pendingGeneration(pending),
          "approval-expired",
        );
      } else if (kernel === "revoked") {
        this.paths.execution.markPending(pending.approvalId, "revoked");
      } else {
        // Spent by a consume whose follow-through died: the execution never
        // started (no binding row) — honest failure.
        this.paths.execution.markPending(pending.approvalId, "failed");
        await this.publishAssignmentFailed(
          pending.workId,
          pending.assignmentId,
          GatewayRuntime.pendingGeneration(pending),
          "approval-execution-lost",
        );
      }
    }
  }

  /** GET /api/works/{workId}/approvals; null ⇒ kernel unreachable this tick. */
  private async pollKernelApproval(
    pending: PendingRow,
  ): Promise<
    "pending" | "approved" | "denied" | "expired" | "revoked" | "consumed" | "absent" | null
  > {
    if (pending.workId === null || this.workloadToken === null) return null;
    try {
      const token = await this.workloadToken.getToken();
      const response = await (this.deps.fetchImpl ?? fetch)(
        `${this.config.facilitatorUrl}/api/works/${pending.workId}/approvals`,
        { headers: { authorization: `Bearer ${token}` } },
      );
      if (!response.ok) return null;
      const rows = (await response.json()) as { approvalId: string; state: string }[];
      const row = rows.find((r) => r.approvalId === pending.approvalId);
      if (row === undefined) return "absent";
      if (row.state === "pending" || row.state === "approved" || row.state === "denied")
        return row.state;
      if (row.state === "expired") return "expired";
      if (row.state === "revoked") return "revoked";
      return "consumed";
    } catch {
      return null;
    }
  }

  /** Re-publish approval.required (new envelope id; kernel dedupes by approvalId). */
  private async republishRequired(pending: PendingRow): Promise<void> {
    const record = JSON.parse(pending.approvalDigestRecord) as {
      approvalId: string;
      action: unknown;
      eligibleActors: string[];
      expiresAt: string;
      createdAt: string;
    };
    try {
      await this.client?.publishEvent(
        buildEvent(APPROVAL_REQUIRED, {
          source: `urn:workspace:${this.config.gatewayId}`,
          subject: `approval/${pending.approvalId}`,
          data: {
            approvalId: record.approvalId,
            actionDigest: computeActionDigest(record),
            eligibleActors: record.eligibleActors,
            nonce: `n-${randomBytes(24).toString("base64url")}`,
            expiresAt: record.expiresAt,
            createdAt: record.createdAt,
            action: record.action,
            ...(pending.workId !== null ? { workId: pending.workId } : {}),
          },
        }),
      );
      this.logger.info("approval republished (reconcile)", { approvalId: pending.approvalId });
    } catch (error) {
      this.logger.warn("approval republish dropped", {
        approvalId: pending.approvalId,
        reason: error instanceof Error ? error.message : String(error),
      });
    }
  }

  /** The assignment generation a parked assign envelope carried (B(4) event
   * stamp for every failure the Gateway reports before/without a binding row). */
  private static pendingGeneration(pending: PendingRow): number {
    try {
      const data = JSON.parse(pending.envelope) as { data?: { executionGeneration?: number } };
      return data.data?.executionGeneration ?? 1;
    } catch {
      return 1;
    }
  }

  /** ONE honest non-retryable assignment.failed for a settled-without-execution
   * action. B(4): the stamp is the ASSIGNMENT generation the assign envelope
   * carried (the kernel fence key), not the link epoch. */
  private async publishAssignmentFailed(
    workId: string | null,
    assignmentId: string,
    executionGeneration: number,
    reason: string,
  ): Promise<void> {
    if (workId === null) return;
    try {
      await this.client?.publishEvent(
        buildEvent(FAILED, {
          source: `urn:workspace:${this.config.gatewayId}`,
          subject: `work/${workId}/assignment/${assignmentId}`,
          data: {
            workId,
            assignmentId,
            executionGeneration: Math.max(1, executionGeneration),
            reason,
            retryable: false,
          },
        }),
      );
    } catch (error) {
      this.logger.warn("assignment.failed dropped", {
        assignmentId,
        reason: error instanceof Error ? error.message : String(error),
      });
    }
  }

  /** Approval-lifecycle audit lines: same shape, lifecycle reasons. */
  private approvalAudit(
    reason: string,
    workId: string | null,
    assignmentId: string,
    approvalId: string,
  ): void {
    auditPolicy(this.logger, {
      decision:
        reason === "approval-required" ? "confirm" : reason.endsWith("-failed") ? "deny" : "allow",
      reason,
      ...(workId !== null ? { workId } : {}),
      assignmentId,
      resource: "*",
      callerWorkload: `workload:${this.config.gatewayId}`,
      gatewayWorkload: `workload:${this.config.gatewayId}`,
      policyId: this.loadedPolicy?.policyId ?? "default",
      evidenceRef: `approval/${approvalId}`,
    });
  }

  /** Pending rows mirror their binding row's terminal settle. */
  private settleLinkedPending(executionId: string, state: "completed" | "failed"): void {
    const pending = this.paths.execution.pendingByExecution(executionId);
    if (pending !== null && pending.state === "executing") {
      this.paths.execution.markPending(pending.approvalId, state);
    }
  }

  private async passthrough(cmd: EnvelopeCommand): Promise<CommandOutcome> {
    if (this.embedded === null) {
      // Fleet modes have no "the" child: without a row, nothing to route to.
      return { kind: "rejected", reason: "no-binding" };
    }
    // Single-child pass-through: the child's own answer decides the receipt
    // (echo answers `unsupported` off-assign, the stub accepts commands).
    return childOutcome(
      await this.embedded.send({
        kind: "steer",
        executionId: `exec-na-${createHash("sha256").update(cmd.id).digest("hex").slice(0, 12)}`,
        command: cmd,
      }),
    );
  }

  // ── event forwarding + reconcile ─────────────────────────────────────────

  private async forwardQueued(): Promise<void> {
    const member = this.embeddedMember();
    for (const env of this.embedded?.drainEvents() ?? []) {
      await this.forwardEvent(env, member);
    }
  }

  /**
   * Forward the CHILD's event bytes, stamping nothing (fence first).
   * The child-generation fence precedes everything: a frame on a superseded
   * connection changes NOTHING (no settle, no forward).
   */
  private async forwardEvent(env: EnvelopeEvent, source: FleetMember): Promise<void> {
    if (source.generation > 0 && source.generation !== this.fleet.generationOf(source.childId)) {
      this.logger.warn("stale-child-generation", {
        childId: source.childId,
        frameGeneration: source.generation,
      });
      return;
    }
    const decision = this.decideEvent(env, source.childId);
    if (decision.verdict !== "allow") {
      // The silent drop gains its audit line.
      this.audit(decision, `event-${decision.verdict}`);
      return;
    }
    const rawExecution = env.data?.["executionId"];
    const executionId = typeof rawExecution === "string" ? rawExecution : null;
    const row = executionId !== null ? this.paths.execution.byExecution(executionId) : null;
    if (row !== null) {
      if (this.fence(row, `forward ${env.type}`) === "stale") return;
      if (env.type === COMPLETED) {
        this.paths.execution.markState(row.executionId, "completed");
        this.settleLinkedPending(row.executionId, "completed");
      }
      if (env.type === FAILED) {
        this.paths.execution.markState(row.executionId, "failed", reasonOf(env));
        this.settleLinkedPending(row.executionId, "failed");
      }
    }
    await this.client?.publishEvent(env);
  }

  /**
   * Reconcile, right after the FIRST welcome: every `running` row goes
   * to ITS child (embedded / live link); a child that is not here cannot
   * answer — lost (never re-executed, never silently deleted, reported via
   * ONE retryable assignment.failed). Rows of children that reconnect LATER
   * are lost the same way (they will not survive the offline pass below).
   * Redelivered commands cannot race this: the SDK delivery ledger answers
   * `duplicate` without touching bindings.
   */
  private async reconcile(): Promise<void> {
    for (const row of this.paths.execution.byState("running")) {
      const member = this.fleet.member(row.childId);
      if (member === undefined) {
        await this.reportLost(row, "gateway-restart-lost");
        continue;
      }
      let settled = false;
      try {
        const reply = await member.executor.send({ kind: "status", executionId: row.executionId });
        if (reply.kind === "status") {
          // The child's own truth: running/lost rows stand; terminal answers settle.
          if (reply.state === "completed" || reply.state === "failed") {
            this.paths.execution.markState(row.executionId, reply.state);
          }
          settled = true;
        }
      } catch {
        /* transport-dead: falls through to lost */
      }
      if (settled) continue;
      await this.reportLost(row, "gateway-restart-lost");
    }
  }

  /** lost + ONE honest retryable assignment.failed (the Gateway is the authority). */
  private async reportLost(row: BindingRow, reason: string): Promise<void> {
    this.paths.execution.markState(row.executionId, "lost", reason);
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
            reason,
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
      childId: row.childId,
      reason,
    });
  }

  /** Work recorded under a dead connection cannot settle or forward. */
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
  if (config.child?.manifestFile === undefined)
    return builtinChildManifest(config.child?.mode ?? "echo");
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
