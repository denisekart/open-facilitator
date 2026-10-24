// The Link client: state machine + reconnect supervisor + heartbeat +
// delivery pipeline law (S:301–302) + advisory flow control + outbound-event
// buffering. Transports move bytes; everything durable or ordered lives here.
//
//   idle → connecting → handshaking → ready ⇄ reconnecting → closed
//
// Terminal states: `closed` (clean stop, or ProtocolMismatchError — a null
// welcome is a policy answer, not a network failure, so it is never retried).
import { randomBytes } from "node:crypto";
import {
  KNOWN_COMMAND_TYPES,
  enforceSizeLimit,
  validateControlFrame,
  validateEnvelope,
  validateReceipt,
  type EnvelopeCommand,
  type EnvelopeEvent,
  type LinkDeliveryV1,
  type ReceiptV1,
} from "@ofac/protocol";
import {
  DeliveryRejectedError,
  LinkError,
  ProtocolMismatchError,
  TransportError,
} from "./errors.js";
import { buildEvent, type BuildEventOptions } from "./envelope.js";
import type { Ledger } from "./ledger.js";
import { dedupeKeyOf } from "./ledger.js";
import { createConsoleLogger } from "./logging.js";
import {
  MAX_ENVELOPE_BYTES,
  type CommandContext,
  type CommandOutcome,
  type EnvelopeAck,
  type FlowControlFrame,
  type GoodbyeFrame,
  type HelloFrame,
  type LinkHandler,
  type LinkLogger,
  type LinkState,
  type LinkStats,
  type LinkTokenProvider,
  type LinkTransport,
  type WelcomeFrame,
} from "./types.js";

export interface LinkClientOptions {
  transport: LinkTransport;
  ledger: Ledger;
  endpointId: string;
  /** sha256:<64hex> over the EXACT manifest bytes POSTed to /capabilities. */
  capabilityManifestDigest: string;
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

export interface LinkSession {
  sessionId: string;
  leaseEpoch: number;
  maxInFlight: number;
  heartbeatSeconds: number;
  protocolVersion: string;
}

type ClientEvents = {
  state: (state: LinkState, previous: LinkState) => void;
  error: (error: Error) => void;
  welcome: (session: LinkSession) => void;
};

const DEFAULT_BACKOFF = [1_000, 2_000, 5_000, 10_000, 30_000] as const;

export class LinkClient {
  private _state: LinkState = "idle";
  private _session: LinkSession | null = null;
  private heartbeatTimer: ReturnType<typeof setInterval> | null = null;
  private heartbeatSeconds = 30;
  private window: number;
  private paused = false;
  private inFlight = 0;
  private queue: LinkDeliveryV1[] = [];
  private listeners: { [K in keyof ClientEvents]?: ClientEvents[K][] } = {};
  private connectAttempt = 0;
  private started = false;
  private stopped = false;
  private loopDone: Promise<void> = Promise.resolve();
  private wakeLoop: (() => void) | null = null;
  readonly counters: LinkStats = {
    delivered: 0,
    duplicate: 0,
    processed: 0,
    failed: 0,
    unsupported: 0,
    reconnects: 0,
  };
  private readonly logger: LinkLogger;
  private readonly maxReconnectAttempts: number;
  private readonly backoff: readonly number[];

  constructor(private readonly options: LinkClientOptions) {
    this.logger = options.logger ?? createConsoleLogger();
    this.maxReconnectAttempts = options.maxReconnectAttempts ?? 0;
    this.backoff = options.backoffMs ?? DEFAULT_BACKOFF;
    this.window = 16;
    const t = options.transport;
    t.onDeliver((d) => {
      this.counters.delivered += 1;
      void this.handleDelivery(d);
    });
    t.onFlow((f) => this.onFlowControl(f));
    t.onDisconnect((g) => this.onServerDisconnect(g));
  }

  get state(): LinkState {
    return this._state;
  }

  get session(): LinkSession | null {
    return this._session;
  }

  get stats(): LinkStats {
    return { ...this.counters };
  }

  on<K extends keyof ClientEvents>(event: K, cb: ClientEvents[K]): () => void {
    const list = ((this.listeners[event] as ClientEvents[K][] | undefined) ??= []);
    list.push(cb);
    return () => {
      const kept = (this.listeners[event] as ClientEvents[K][]).filter((f) => f !== cb);
      this.listeners[event] = kept as never;
    };
  }

  private setState(next: LinkState): void {
    if (this._state === next) return;
    const previous = this._state;
    this._state = next;
    this.logger.debug(`link state ${previous} → ${next}`, { endpointId: this.options.endpointId });
    for (const cb of this.listeners.state ?? []) cb(next, previous);
  }

  private emitError(error: Error): void {
    for (const cb of this.listeners.error ?? []) cb(error);
  }

  /** Connect + hello; resolves once `ready` (rejects terminal on mismatch). */
  async start(): Promise<LinkSession> {
    if (this.started) throw new LinkError("already-started", "client already started");
    this.started = true;
    this.loopDone = this.supervisorLoop();
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

  private terminalError: Error | null = null;

  /** Graceful shutdown: goodbye, stop heartbeat, drain nothing (receipts are per-delivery). */
  async stop(reason = "client-shutdown"): Promise<void> {
    this.stopped = true;
    this.stopHeartbeat();
    if (this._state === "ready" || this._state === "handshaking") {
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
    this.setState("closed");
  }

  private async supervisorLoop(): Promise<void> {
    for (;;) {
      if (this.stopped) return;
      try {
        await this.connectAndHello();
        this.connectAttempt = 0;
        this.resumeQueue();
        await this.drainOutbox();
      } catch (error) {
        if (error instanceof ProtocolMismatchError) {
          this.terminalError = error;
          this.setState("closed");
          this.emitError(error);
          return;
        }
        if (this.stopped) return;
        this.connectAttempt += 1;
        this.counters.reconnects += 1;
        if (this.maxReconnectAttempts > 0 && this.connectAttempt > this.maxReconnectAttempts) {
          this.terminalError =
            error instanceof Error
              ? error
              : new LinkError("reconnect-exhausted", "reconnect attempts exhausted");
          this.setState("closed");
          this.emitError(this.terminalError);
          return;
        }
        this.pauseQueue();
        this.setState("reconnecting");
        const delay = this.backoff[Math.min(this.connectAttempt - 1, this.backoff.length - 1)];
        this.logger.warn("link session down; retrying", {
          attempt: this.connectAttempt,
          backoffMs: delay,
          reason: error instanceof Error ? error.message : String(error),
          endpointId: this.options.endpointId,
        });
        await this.sleep(delay);
        continue;
      }
      // Ready: park until the session dies (transport close, heartbeat death,
      // goodbye push) or stop() — either flips state away from ready.
      await this.waitForSessionDeath();
      if (this.stopped) return;
      // A previously good session died (goodbye push / heartbeat refusal):
      // count the reconnect and back off before the re-hello.
      this.connectAttempt += 1;
      this.counters.reconnects += 1;
      this.pauseQueue();
      this.stopHeartbeat();
      const delay = this.backoff[Math.min(this.connectAttempt - 1, this.backoff.length - 1)];
      await this.sleep(delay);
    }
  }

  private async connectAndHello(): Promise<void> {
    const transport = this.options.transport;
    this.setState(this._session ? "reconnecting" : "connecting");
    try {
      await transport.connect();
    } catch (error) {
      await this.maybeRefresh(error);
      throw error;
    }
    this.setState("handshaking");
    const lastKnownAck = this.options.ledger.processedDeliveries(1)[0];
    const hello: HelloFrame = {
      kind: "hello",
      protocolVersions: ["v1"],
      supportedTransports: this.options.transport.supportedTransports ?? ["signalr"],
      endpointId: this.options.endpointId,
      // Transient per attempt (S:1246): fresh random hex on every hello.
      instanceId: `lnk-${randomBytes(8).toString("hex")}`,
      capabilityManifestDigest: this.options.capabilityManifestDigest,
      ...(lastKnownAck !== undefined ? { lastKnownAck } : {}),
    };
    const verdict = validateControlFrame(hello);
    if (!verdict.ok) {
      throw new LinkError(verdict.errorClass, `self-built hello invalid: ${verdict.message}`);
    }
    let welcome: WelcomeFrame | null;
    try {
      welcome = await transport.hello(hello);
    } catch (error) {
      await this.maybeRefresh(error);
      throw error;
    }
    if (welcome === null) {
      // Null welcome after a no-compatible-protocol-version goodbye is a
      // TERMINAL policy answer: never retry, surface a typed error.
      throw new ProtocolMismatchError();
    }
    this._session = {
      sessionId: welcome.sessionId,
      leaseEpoch: welcome.leaseEpoch,
      maxInFlight: welcome.maxInFlight,
      heartbeatSeconds: welcome.heartbeatSeconds,
      protocolVersion: welcome.protocolVersion,
    };
    this.window = welcome.maxInFlight;
    this.heartbeatSeconds = welcome.heartbeatSeconds;
    this.setState("ready");
    this.startHeartbeat();
    this.logger.info("link session ready", {
      endpointId: this.options.endpointId,
      epoch: welcome.leaseEpoch,
      sessionId: welcome.sessionId,
    });
    for (const cb of this.listeners.welcome ?? []) cb(this._session);
  }

  /** On an unauthorized failure, run the token refresh hook (rotation) once. */
  private async maybeRefresh(error: unknown): Promise<void> {
    if (error instanceof TransportError && error.unauthorized && this.options.token.refresh) {
      try {
        await this.options.token.refresh();
        this.logger.info("link token refreshed after 401", { endpointId: this.options.endpointId });
      } catch (refreshError) {
        this.logger.warn("token refresh failed", { reason: String(refreshError) });
      }
    }
  }

  // ── heartbeat ────────────────────────────────────────────────────────────

  private startHeartbeat(): void {
    this.stopHeartbeat();
    this.heartbeatTimer = setInterval(() => {
      if (this._state !== "ready" || !this._session) return;
      const session = this._session;
      void this.options.transport
        .heartbeat({
          kind: "heartbeat",
          sessionId: session.sessionId,
          leaseEpoch: session.leaseEpoch,
        })
        .then((alive) => {
          if (!alive) this.markSessionDead("heartbeat refused (stale epoch or no session row)");
        })
        .catch(() => this.markSessionDead("heartbeat transport failure"));
    }, this.heartbeatSeconds * 1000);
    // Node: don't hold the event loop open for a mere heartbeat.
    (this.heartbeatTimer as unknown as { unref?: () => void }).unref?.();
  }

  private stopHeartbeat(): void {
    if (this.heartbeatTimer !== null) {
      clearInterval(this.heartbeatTimer);
      this.heartbeatTimer = null;
    }
  }

  // ── server pushes ────────────────────────────────────────────────────────

  private onServerDisconnect(goodbye: GoodbyeFrame): void {
    if (goodbye.reason === "no-compatible-protocol-version") {
      // Terminal; connectAndHello's null-return path raises the typed error.
      return;
    }
    this.markSessionDead(`server goodbye: ${goodbye.reason}`);
  }

  private onFlowControl(frame: FlowControlFrame): void {
    // Advisory pacing (docs/protocol.md:196): 0 pauses dispatch, non-zero
    // resumes with that window. The client self-enforces the window either way.
    if (frame.maxInFlight === 0) {
      this.paused = true;
      this.logger.debug("flow_control pause");
    } else {
      this.paused = false;
      this.window = frame.maxInFlight;
      this.logger.debug("flow_control resume", { maxInFlight: frame.maxInFlight });
      this.pumpQueue();
    }
  }

  // ── delivery pipeline (S:301–302) ────────────────────────────────────────

  private async handleDelivery(delivery: LinkDeliveryV1): Promise<void> {
    const correlation = { deliveryId: delivery.deliveryId, endpointId: this.options.endpointId };
    // 1) Validate before persisting: a broken envelope is refused WITHOUT a
    //    ledger row (it is not a delivery we can ever reconcile).
    const bytes = Buffer.byteLength(JSON.stringify(delivery.envelope), "utf8");
    const shape = validateEnvelope(delivery.envelope);
    if (!shape.ok) {
      // Fail-closed taxonomy (S:309): an unknown COMMAND type is `unsupported`
      // and enters the ledger (a redelivery must answer `duplicate`, not
      // re-run the fail-closed path); any other envelope violation is
      // `rejected` with NO ledger row — an envelope we can't parse is not a
      // delivery we can ever reconcile.
      if (shape.errorClass === "unknown-command") {
        this.options.ledger.insertIfNew(
          delivery.deliveryId,
          delivery.envelope,
          dedupeKeyOf(delivery.envelope, KNOWN_COMMAND_TYPES),
        );
        this.options.ledger.markState(delivery.deliveryId, "unsupported");
        this.counters.unsupported += 1;
        await this.sendReceipt(delivery.deliveryId, {
          status: "unsupported",
          reason: `envelope ${shape.errorClass}: ${shape.message}`,
        });
        return;
      }
      this.counters.failed += 1;
      await this.sendReceipt(delivery.deliveryId, {
        status: "rejected",
        reason: `envelope ${shape.errorClass}: ${shape.message}`,
      });
      return;
    }
    if (!enforceSizeLimit(bytes, MAX_ENVELOPE_BYTES)) {
      this.counters.failed += 1;
      await this.sendReceipt(delivery.deliveryId, {
        status: "rejected",
        reason: `envelope exceeds ${MAX_ENVELOPE_BYTES} wire bytes`,
      });
      return;
    }
    // 2) Durable inbox BEFORE any answer (S:301): fresh | duplicate.
    const env = delivery.envelope;
    const dedupe = dedupeKeyOf(env, KNOWN_COMMAND_TYPES);
    if (this.options.ledger.insertIfNew(delivery.deliveryId, env, dedupe) === "duplicate") {
      this.counters.duplicate += 1;
      this.logger.debug("duplicate delivery → duplicate receipt", correlation);
      await this.sendReceipt(delivery.deliveryId, { status: "duplicate", processedAt: now() });
      return;
    }
    // 3) Fresh: events are facts, auto-acked after logging; commands queue
    //    for the handler inside the flow window (bounded by maxQueue).
    const isCommand = env.type.startsWith("org.facilitator.command.");
    if (!isCommand) {
      this.options.ledger.markState(delivery.deliveryId, "processed");
      this.counters.processed += 1;
      this.logger.info("event delivery auto-acked", { ...correlation, type: env.type });
      await this.sendReceipt(delivery.deliveryId, { status: "accepted", processedAt: now() });
      return;
    }
    const maxQueue = this.window * 4;
    if (this.queue.length + this.inFlight >= maxQueue + this.window) {
      // Backpressure is dispatch-delay only — never NAK a valid delivery.
      this.logger.warn("dispatch queue saturated; delaying", {
        ...correlation,
        queued: this.queue.length,
      });
    }
    this.queue.push(delivery);
    this.pumpQueue();
  }

  private async dispatch(delivery: LinkDeliveryV1): Promise<void> {
    const env = delivery.envelope as EnvelopeCommand;
    const correlation = {
      deliveryId: delivery.deliveryId,
      endpointId: this.options.endpointId,
      workId: typeof env.data?.["workId"] === "string" ? (env.data["workId"] as string) : undefined,
      assignmentId:
        typeof env.data?.["assignmentId"] === "string"
          ? (env.data["assignmentId"] as string)
          : undefined,
    };
    const outcome: CommandOutcome = !KNOWN_COMMAND_TYPES.includes(env.type)
      ? { kind: "unsupported", reason: `unknown command type ${env.type}` }
      : this.options.handler === undefined
        ? { kind: "unsupported", reason: "no handler mounted" }
        : await this.invokeHandler(env, delivery, correlation);
    await this.complete(delivery, outcome, correlation);
  }

  private async invokeHandler(
    env: EnvelopeCommand,
    delivery: LinkDeliveryV1,
    correlation: Record<string, unknown>,
  ): Promise<CommandOutcome> {
    const controller = new AbortController();
    const ctx: CommandContext = {
      deliveryId: delivery.deliveryId,
      envelope: env,
      signal: controller.signal,
      publishEvent: (e) => this.publishEvent(e, env.traceparent, env.correlationid ?? env.id),
    };
    try {
      return await this.options.handler!.onCommand(env, ctx);
    } catch (error) {
      this.logger.error("handler threw; answering rejected", {
        ...correlation,
        reason: error instanceof Error ? error.message : String(error),
      });
      return { kind: "rejected", reason: `handler error: ${String(error)}` };
    }
  }

  private async complete(
    delivery: LinkDeliveryV1,
    outcome: CommandOutcome,
    correlation: Record<string, unknown>,
  ): Promise<void> {
    let receipt: ReceiptV1;
    let state: "processed" | "failed" | "unsupported";
    switch (outcome.kind) {
      case "accepted":
        state = "processed";
        this.counters.processed += 1;
        receipt = { deliveryId: delivery.deliveryId, status: "accepted", processedAt: now() };
        break;
      case "rejected":
        state = "failed";
        this.counters.failed += 1;
        receipt = { deliveryId: delivery.deliveryId, status: "rejected", reason: outcome.reason };
        break;
      case "unsupported":
        state = "unsupported";
        this.counters.unsupported += 1;
        receipt = {
          deliveryId: delivery.deliveryId,
          status: "unsupported",
          reason: outcome.reason ?? "capability not supported",
        };
        break;
    }
    // Ledger flip BEFORE the receipt: post-crash, the row explains the gap
    // and redelivery lands on `duplicate`, terminating the loop (S:302).
    this.options.ledger.markState(delivery.deliveryId, state);
    await this.sendReceipt(delivery.deliveryId, receipt, correlation);
  }

  private async sendReceipt(
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
      // answers `duplicate`, the loop terminates (S:302).
      await this.maybeRefresh(error);
      this.logger.warn("receipt send failed (server will redeliver)", {
        ...correlation,
        deliveryId,
        reason: error instanceof Error ? error.message : String(error),
      });
    }
  }

  // ── outbound events ──────────────────────────────────────────────────────

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
        await this.maybeRefresh(error);
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

  /** Convenience: build + publish (linkd handlers use this). */
  async publish(type: string, options: BuildEventOptions): Promise<EnvelopeAck> {
    return this.publishEvent(buildEvent(type, options));
  }

  private async drainOutbox(): Promise<void> {
    for (;;) {
      const batch = this.options.ledger.unsentEvents(16);
      if (batch.length === 0) return;
      for (const row of batch) {
        try {
          const ack = await this.options.transport.envelope(row.envelope as EnvelopeEvent);
          if (ack.status !== "rejected") this.options.ledger.markEventSent(row.id);
          else this.logger.warn("buffered event rejected; dropping", { eventId: row.id });
        } catch (error) {
          await this.maybeRefresh(error);
          return; // still partitioned — retry after the next welcome
        }
      }
    }
  }

  // ── dispatch queue (flow control) ────────────────────────────────────────

  private pauseQueue(): void {
    this.paused = true;
  }

  private resumeQueue(): void {
    this.paused = false;
  }

  private pumpQueue(): void {
    while (!this.paused && this.inFlight < this.window && this.queue.length > 0) {
      const delivery = this.queue.shift()!;
      this.inFlight += 1;
      void this.dispatch(delivery)
        .catch((error) => {
          this.logger.error("delivery pipeline error", { reason: String(error) });
          this.emitError(error instanceof Error ? error : new LinkError("pipeline", String(error)));
        })
        .finally(() => {
          this.inFlight -= 1;
          this.pumpQueue();
        });
    }
  }

  // ── misc ─────────────────────────────────────────────────────────────────

  private waitForSessionDeath(): Promise<void> {
    const { promise, resolve } = Promise.withResolvers<void>();
    const check = () => {
      if (this._state !== "ready" || this.stopped) {
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
    if (this._state !== "ready") return;
    this.logger.warn("link session lost", { reason, endpointId: this.options.endpointId });
    void this.options.transport.close().catch(() => undefined);
    // State change wakes the supervisor via waitForSessionDeath; the loop
    // backoffs and re-hellos. `reconnecting` is set there (after the park
    // unwinds) to keep the transition single-owner.
    this.setState("reconnecting");
  }

  private sleep(ms: number): Promise<void> {
    const { promise, resolve } = Promise.withResolvers<void>();
    const t = setTimeout(resolve, ms);
    (t as unknown as { unref?: () => void }).unref?.();
    return promise;
  }
}

function now(): string {
  return new Date().toISOString();
}

export function createLinkClient(options: LinkClientOptions): LinkClient {
  return new LinkClient(options);
}
