// The Link client facade: the state machine + wiring, composed from
// single-responsibility collaborators (bus, handshake, heartbeat, pipeline,
// dispatch, outbox drain, reconnect supervisor). Transports move bytes;
// everything durable or ordered lives here.
//
//   idle → connecting → handshaking → ready ⇄ reconnecting → closed
//
// Terminal states: `closed` (clean stop, or ProtocolMismatchError — a null
// welcome is a policy answer, not a network failure, so it is never retried).
import { validateReceipt } from "@ofac/protocol";
import type { EnvelopeEvent, ReceiptV1 } from "@ofac/protocol";
import type { EnvelopeObject } from "@ofac/protocol";
import { CommandDispatcher } from "./command-dispatcher.js";
import { DeliveryPipeline } from "./delivery-pipeline.js";
import { DispatchQueue } from "./dispatch-queue.js";
import { DeliveryRejectedError, LinkError, TransportError } from "./errors.js";
import { buildEvent, type BuildEventOptions } from "./envelope.js";
import { LinkEventBus, type ClientEvents } from "./link-event-bus.js";
import type { Ledger } from "./ledger.js";
import { createConsoleLogger } from "./logging.js";
import { OutboxDrain } from "./outbox-drain.js";
import { ReconnectSupervisor } from "./reconnect-supervisor.js";
import { Handshaker } from "./handshaker.js";
import { HeartbeatMonitor } from "./heartbeat-monitor.js";
import { TokenRotation } from "./token-rotation.js";
import type {
  EnvelopeAck,
  GoodbyeFrame,
  LinkHandler,
  LinkLogger,
  LinkSession,
  LinkState,
  LinkStats,
  LinkTokenProvider,
  LinkTransport,
  WelcomeFrame,
} from "./types.js";

export type { ClientEvents, LinkSession };

export interface LinkClientOptions {
  transport: LinkTransport;
  ledger: Ledger;
  endpointId: string;
  /** sha256:<64hex> over the EXACT manifest bytes POSTed to /capabilities.
   * A provider is sampled fresh on every hello (a Gateway that re-POSTs a
   * union manifest mid-session wants the NEXT reconnect to carry the new
   * digest; a plain string keeps the historical fixed-value behavior). */
  capabilityManifestDigest: string | (() => string);
  token: LinkTokenProvider;
  handler?: LinkHandler | undefined;
  logger?: LinkLogger;
  /** 0 (default) = retry forever. */
  maxReconnectAttempts?: number;
  /** Reconnect backoff ladder in ms, last entry caps. */
  backoffMs?: readonly number[];
  /** Override for tests: how an outbound event gets its envelope fields. */
  eventSource?: string;
}

export class LinkClient {
  private started = false;
  private stopped = false;
  private loopDone: Promise<void> = Promise.resolve();
  private terminalError: Error | null = null;
  private wakeLoop: (() => void) | null = null;
  private _session: LinkSession | null = null;
  readonly counters: LinkStats = {
    delivered: 0,
    duplicate: 0,
    processed: 0,
    failed: 0,
    unsupported: 0,
    reconnects: 0,
  };
  private readonly logger: LinkLogger;
  private readonly bus: LinkEventBus;
  private readonly rotation: TokenRotation;
  private readonly queue: DispatchQueue;
  private readonly dispatcher: CommandDispatcher;
  private readonly pipeline: DeliveryPipeline;
  private readonly outbox: OutboxDrain;
  private readonly handshaker: Handshaker;
  private readonly heartbeat: HeartbeatMonitor;
  private readonly supervisor: ReconnectSupervisor;

  constructor(private readonly options: LinkClientOptions) {
    this.logger = options.logger ?? createConsoleLogger();
    const { transport, ledger, endpointId } = options;
    this.bus = new LinkEventBus(this.logger, endpointId);
    this.rotation = new TokenRotation({
      token: options.token,
      logger: this.logger,
      endpointId,
    });
    this.dispatcher = new CommandDispatcher({
      ...(options.handler !== undefined ? { handler: options.handler } : {}),
      ledger,
      logger: this.logger,
      counters: this.counters,
      endpointId,
      publishEvent: (e, traceparent, causationid) => this.publishEvent(e, traceparent, causationid),
      sendReceipt: (id, partial, correlation) => this.deliverReceipt(id, partial, correlation),
    });
    const onQueueError = (error: unknown): void => {
      this.bus.emitError(error instanceof Error ? error : new LinkError("pipeline", String(error)));
    };
    this.queue = new DispatchQueue({
      dispatcher: this.dispatcher,
      logger: this.logger,
      onError: onQueueError,
    });
    this.pipeline = new DeliveryPipeline({
      ledger,
      logger: this.logger,
      counters: this.counters,
      endpointId,
      queue: this.queue,
      sendReceipt: (id, partial, correlation) => this.deliverReceipt(id, partial, correlation),
    });
    this.outbox = new OutboxDrain({
      ledger,
      transport,
      logger: this.logger,
      onUnauthorized: (error) => this.rotation.maybeRefresh(error),
    });
    this.handshaker = new Handshaker({
      transport,
      ledger,
      endpointId,
      capabilityManifestDigest: options.capabilityManifestDigest,
      logger: this.logger,
      bus: this.bus,
      onReady: (welcome) => this.onSessionReady(welcome),
    });
    this.heartbeat = new HeartbeatMonitor({
      transport,
      isAlive: () => this.bus.state === "ready",
      sessionId: () => this._session?.sessionId ?? null,
      leaseEpoch: () => this._session?.leaseEpoch ?? 0,
      onDead: (reason) => this.markSessionDead(reason),
    });
    this.supervisor = new ReconnectSupervisor(
      {
        connectAndHello: (hasSession) =>
          this.handshaker.connectAndHello(hasSession, (error) => this.rotation.maybeRefresh(error)),
        waitForSessionDeath: () => this.waitForSessionDeath(),
        drainOutbox: () => this.outbox.drain(),
        onSessionLost: () => this.heartbeat.stop(),
        setStateReconnecting: () => this.bus.setState("reconnecting"),
        resumeQueue: () => this.queue.resume(),
        pauseQueue: () => this.queue.pause(),
        recordReconnect: () => {
          this.counters.reconnects += 1;
        },
        isStopped: () => this.stopped,
        hasSession: () => this._session !== null,
        terminate: (error) => {
          this.terminalError = error;
          this.bus.setState("closed");
          this.bus.emitError(error);
        },
        logDown: (info) => {
          this.logger.warn("link session down; retrying", {
            ...info,
            endpointId,
          });
        },
      },
      {
        ...(options.maxReconnectAttempts !== undefined
          ? { maxReconnectAttempts: options.maxReconnectAttempts }
          : {}),
        ...(options.backoffMs !== undefined ? { backoffMs: options.backoffMs } : {}),
      },
    );
    transport.onDeliver((d) => {
      this.counters.delivered += 1;
      void this.pipeline.handle(d).catch((error: unknown) => onQueueError(error));
    });
    transport.onFlow((f) => this.queue.onFlowControl(f.maxInFlight));
    transport.onDisconnect((g) => this.onServerDisconnect(g));
  }

  get state(): LinkState {
    return this.bus.state;
  }

  get session(): LinkSession | null {
    return this._session;
  }

  get stats(): LinkStats {
    return { ...this.counters };
  }

  on<K extends keyof ClientEvents>(event: K, cb: ClientEvents[K]): () => void {
    return this.bus.on(event, cb);
  }

  /** Connect + hello; resolves once `ready` (rejects terminal on mismatch). */
  async start(): Promise<LinkSession> {
    if (this.started) throw new LinkError("already-started", "client already started");
    this.started = true;
    this.loopDone = this.supervisor.run();
    const { promise, resolve, reject } = Promise.withResolvers<LinkSession>();
    const done = (s: LinkSession) => {
      off();
      resolve(s);
    };
    const fail = (e: Error) => {
      off();
      reject(e);
    };
    const onState = (s: LinkState) => {
      if (s === "ready" && this._session) done(this._session);
      if (s === "closed")
        fail(this.terminalError ?? new LinkError("closed", "closed before ready"));
    };
    const off = () => {
      offState();
      offErr();
    };
    const offState = this.on("state", onState);
    const offErr = this.on("error", fail);
    return promise;
  }

  /** Graceful shutdown: goodbye, stop heartbeat, drain nothing (receipts are per-delivery). */
  async stop(reason = "client-shutdown"): Promise<void> {
    this.stopped = true;
    this.heartbeat.stop();
    if (this.bus.state === "ready" || this.bus.state === "handshaking") {
      const frame: GoodbyeFrame = {
        kind: "goodbye",
        sessionId: this._session?.sessionId ?? "",
        reason,
      };
      try {
        await this.options.transport.goodbye(frame);
      } catch (err) {
        this.logger.warn("goodbye send failed (closing anyway)", { reason: String(err) });
      }
    }
    try {
      await this.options.transport.close();
    } catch {
      /* socket already gone */
    }
    this.wakeLoop?.();
    await this.loopDone.catch(() => undefined);
    this.bus.setState("closed");
  }

  /**
   * Send an outbound CloudEvent. Transport failures BUFFER the envelope in
   * the ledger outbox (durable, dedupe-safe by CloudEvents id) and resolve as
   * `accepted` — the handler's business outcome stands, delivery is deferred.
   */
  async publishEvent(
    env: EnvelopeEvent,
    traceparent?: string,
    causationid?: string,
  ): Promise<EnvelopeAck> {
    const outbound: EnvelopeEvent = {
      ...env,
      ...(traceparent !== undefined && env.traceparent === undefined ? { traceparent } : {}),
      ...(causationid !== undefined && env.causationid === undefined ? { causationid } : {}),
    };
    try {
      const ack = await this.options.transport.envelope(outbound);
      if (ack.status === "rejected") {
        throw new DeliveryRejectedError("rejected", ack.problem);
      }
      return ack;
    } catch (error) {
      if (error instanceof DeliveryRejectedError) throw error;
      if (error instanceof TransportError) {
        await this.rotation.maybeRefresh(error);
        this.options.ledger.queueEvent(outbound.id, outbound);
        this.logger.warn("event buffered for resend", {
          type: outbound.type,
          eventId: outbound.id,
        });
        return { status: "accepted" };
      }
      throw error;
    }
  }

  /**
   * Send a command envelope and return the kernel's answer verbatim.
   * Unlike publishEvent there is NO outbox buffering: a transport failure
   * throws (a fire-and-forget law is wrong for a synchronous consume whose
   * outcome decides an execution), and a rejected ack resolves with the
   * typed problem instead of throwing — the caller branches on status.
   */
  async sendCommand(env: EnvelopeObject): Promise<EnvelopeAck> {
    return await this.options.transport.envelope(env);
  }

  /** Convenience: build + publish (linkd handlers use this). */
  async publish(type: string, options: BuildEventOptions): Promise<EnvelopeAck> {
    return this.publishEvent(buildEvent(type, options));
  }

  // ── internals ────────────────────────────────────────────────────────────

  private onSessionReady(welcome: WelcomeFrame): void {
    this._session = {
      sessionId: welcome.sessionId,
      leaseEpoch: welcome.leaseEpoch,
      maxInFlight: welcome.maxInFlight,
      heartbeatSeconds: welcome.heartbeatSeconds,
      protocolVersion: welcome.protocolVersion,
    };
    this.queue.setWindow(welcome.maxInFlight);
    this.heartbeat.start(welcome.heartbeatSeconds);
  }

  private async deliverReceipt(
    deliveryId: string,
    partial: Omit<ReceiptV1, "deliveryId">,
    correlation: Record<string, unknown> = {},
  ): Promise<void> {
    const receipt: ReceiptV1 = { deliveryId, ...partial };
    const verdict = validateReceipt(receipt);
    if (!verdict.ok) {
      this.logger.error("self-built receipt invalid (dropped)", {
        ...correlation,
        deliveryId,
        reason: verdict.message,
      });
      return;
    }
    try {
      await this.options.transport.receipt(receipt);
    } catch (error) {
      // A dropped receipt is survivable: the server redelivers, our ledger
      // answers `duplicate`, the loop terminates.
      await this.rotation.maybeRefresh(error);
      this.logger.warn("receipt send failed (server will redeliver)", {
        ...correlation,
        deliveryId,
        reason: error instanceof Error ? error.message : String(error),
      });
    }
  }

  private onServerDisconnect(goodbye: GoodbyeFrame): void {
    if (goodbye.reason === "no-compatible-protocol-version") {
      // Terminal; connectAndHello's null-return path raises the typed error.
      return;
    }
    this.markSessionDead(`server goodbye: ${goodbye.reason}`);
  }

  private waitForSessionDeath(): Promise<void> {
    const { promise, resolve } = Promise.withResolvers<void>();
    const check = () => {
      if (this.bus.state !== "ready" || this.stopped) {
        off();
        this.wakeLoop = null;
        resolve();
      }
    };
    const off = this.on("state", check);
    this.wakeLoop = check;
    // Condition-check at entry: a goodbye can land while this iteration is
    // still finishing drainOutbox — parking on a dead session sleeps forever.
    check();
    return promise;
  }

  private markSessionDead(reason: string): void {
    if (this.bus.state !== "ready") return;
    this.logger.warn("link session lost", { reason, endpointId: this.options.endpointId });
    void this.options.transport.close().catch(() => undefined);
    // State change wakes the supervisor via waitForSessionDeath; the loop
    // backoffs and re-hellos. `reconnecting` is set there (after the park
    // unwinds) to keep the transition single-owner.
    this.bus.setState("reconnecting");
  }
}

export function createLinkClient(options: LinkClientOptions): LinkClient {
  return new LinkClient(options);
}
