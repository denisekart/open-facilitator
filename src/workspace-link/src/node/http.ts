// HTTP long-poll transport + hidden SignalR "session keeper".
//
// As-built constraint (kernel LinkEndpoints.cs): GET /deliveries REQUIRES a
// live link_sessions row — minted ONLY by a hub hello — and every successful
// GET refreshes the lease (no HTTP heartbeat endpoint). A pure-HTTP mode would
// need a server session-mint endpoint (future additive OpenAPI change), so
// this transport keeps a hidden SignalR connection that does
// hello + heartbeats; deliveries flow through the frozen HTTP surface.
// Because the keeper socket is a tracked hub connection, the pump MAY push
// Deliver to it instead of leaving rows for the poll (any live socket
// claims) — the transport forwards keeper frames into the client pipeline,
// and the ledger dedupes either arrival surface.
//
// Consequence: on 404 session-not-found the keeper reconnects and re-hellos
// (fresh epoch — lease expiry, not TCP, ends a session), and the next GET
// picks up redelivery of anything the in-flight lease released.
import { validateAgainstSchemaId } from "@ofac/protocol";
import type { EnvelopeObject, LinkDeliveryV1, ReceiptV1, SupportedTransport } from "@ofac/protocol";
import { parseProblem, trimSlash } from "../core/enroll.js";
import { LinkError, TransportError } from "../core/errors.js";
import { FrameEmitter } from "../core/frame-emitter.js";
import { DEFAULT_BACKOFF, unrefTimer } from "../core/timers.js";
import type {
  EnvelopeAck,
  FlowControlFrame,
  GoodbyeFrame,
  HeartbeatFrame,
  HelloFrame,
  LinkTransport,
  WelcomeFrame,
} from "../core/types.js";
import { SignalrTransport } from "./signalr.js";

const LINK_DELIVERY_SCHEMA_ID = "https://schemas.openfacilitator.org/v1/link-delivery.v1.json";

export interface HttpPollTransportOptions {
  baseUrl: string;
  /** Long-poll window per GET (server bounds 0..60). */
  waitSeconds?: number;
  /** Max deliveries per GET claim (server bounds 1..64). */
  limit?: number;
  fetchImpl?: typeof fetch;
  /** Inject for tests; defaults to a keeper over the same URL/token. */
  keeper?: LinkTransport;
}

export class HttpPollTransport implements LinkTransport {
  readonly supportedTransports: [SupportedTransport, ...SupportedTransport[]] = [
    "http-poll",
    "signalr",
  ];
  private readonly baseUrl: string;
  private readonly fetch: typeof fetch;
  private readonly waitSeconds: number;
  private readonly limit: number;
  readonly keeper: LinkTransport;
  private pollAbort: AbortController | null = null;
  private pollLoop: Promise<void> = Promise.resolve();
  private helloFrame: HelloFrame | null = null;
  private closed = false;
  private readonly deliver = new FrameEmitter<LinkDeliveryV1>();
  private readonly flow = new FrameEmitter<FlowControlFrame>();
  private readonly disconnect = new FrameEmitter<GoodbyeFrame>();

  constructor(
    options: HttpPollTransportOptions,
    private readonly token: { getToken(): Promise<string>; refresh?(): Promise<void> },
  ) {
    this.baseUrl = trimSlash(options.baseUrl);
    this.fetch = options.fetchImpl ?? fetch;
    this.waitSeconds = options.waitSeconds ?? 25;
    this.limit = Math.min(options.limit ?? 16, 64);
    this.keeper =
      options.keeper ??
      new SignalrTransport({ baseUrl: options.baseUrl, token: this.token as never });
    // The keeper socket is a tracked hub connection, so the server may
    // legitimately push Deliver/FlowControl/Disconnect to it (the pump
    // claims for ANY live socket). Forwarding keeps both arrival
    // surfaces feeding the same client pipeline — the ledger dedupes, so
    // a push-then-poll race can never double-run a delivery.
    this.keeper.onDeliver((d) => this.deliver.emit(d));
    this.keeper.onFlow((f) => this.flow.emit(f));
    this.keeper.onDisconnect((g) => this.disconnect.emit(g));
  }

  async connect(): Promise<void> {
    this.closed = false;
    await this.keeper.connect();
  }

  async hello(frame: HelloFrame): Promise<WelcomeFrame | null> {
    this.helloFrame = frame;
    const welcome = await this.keeper.hello(frame);
    if (welcome !== null) this.startPolling();
    return welcome;
  }

  async envelope(env: EnvelopeObject): Promise<EnvelopeAck> {
    const response = await this.http("POST", "/api/link/v1/events", env);
    return this.ackFrom(response);
  }

  async receipt(r: ReceiptV1): Promise<void> {
    const { deliveryId, ...body } = r;
    const path = `/api/link/v1/deliveries/${encodeURIComponent(deliveryId)}/receipt`;
    const response = await this.http("POST", path, body);
    if (response.status !== 204) {
      throw new LinkError(
        "receipt-failed",
        `receipt POST answered ${response.status}: ${(await response.text()).slice(0, 512)}`,
      );
    }
  }

  async heartbeat(hb: HeartbeatFrame): Promise<boolean> {
    // Keeper socket owns the hub heartbeat; GET /deliveries refreshes the
    // lease too (server rule), so a live poll loop alone sustains liveness.
    return this.keeper.heartbeat(hb);
  }

  async goodbye(gb: GoodbyeFrame): Promise<void> {
    this.stopPolling();
    await this.keeper.goodbye(gb);
  }

  async close(): Promise<void> {
    this.closed = true;
    this.stopPolling();
    await this.keeper.close();
  }

  onDeliver(cb: (d: LinkDeliveryV1) => void): void {
    this.deliver.subscribe(cb);
  }

  onFlow(cb: (f: FlowControlFrame) => void): void {
    this.flow.subscribe(cb);
  }

  onDisconnect(cb: (g: GoodbyeFrame) => void): void {
    this.disconnect.subscribe(cb);
  }

  // ── poll loop ────────────────────────────────────────────────────────────

  private startPolling(): void {
    if (this.pollAbort !== null) return;
    this.pollAbort = new AbortController();
    const signal = this.pollAbort.signal;
    const loop = async (): Promise<void> => {
      let failures = 0;
      while (!signal.aborted && !this.closed) {
        try {
          const response = await this.http(
            "GET",
            `/api/link/v1/deliveries?wait=${this.waitSeconds}&limit=${this.limit}`,
          );
          if (response.status === 404) {
            // session-not-found: keeper died server-side. Reconnect +
            // re-hello (new epoch) so the next GET has a session to read.
            await this.resuscitateKeeper();
            continue;
          }
          if (!response.ok) {
            throw new TransportError(`GET /deliveries answered ${response.status}`, {
              problem: await parseProblem(response),
            });
          }
          failures = 0;
          const items = (await response.json()) as unknown[];
          for (const item of items) {
            // A malformed wrapper would poison the ledger keyed by
            // `undefined` — refuse it at the door, never fan it out.
            const verdict = validateAgainstSchemaId(LINK_DELIVERY_SCHEMA_ID, item);
            if (verdict.ok) this.deliver.emit(item as LinkDeliveryV1);
          }
          // Immediate re-issue (long-poll semantics).
        } catch (error) {
          if (signal.aborted || this.closed) return;
          if (error instanceof TransportError && error.unauthorized) {
            await this.token.refresh?.();
          }
          // Network hiccup: back off along the ladder (reset on the next
          // success) — the lease may lapse server-side, and the 404 path
          // re-mints it.
          const delay = DEFAULT_BACKOFF[Math.min(failures, DEFAULT_BACKOFF.length - 1)];
          failures += 1;
          await sleep(delay, signal);
        }
      }
    };
    this.pollLoop = loop();
  }

  private async resuscitateKeeper(): Promise<void> {
    await this.keeper.close().catch(() => undefined);
    await this.keeper.connect();
    if (this.helloFrame !== null) {
      const welcome = await this.keeper.hello(this.helloFrame);
      if (welcome === null) {
        this.disconnect.emit({
          kind: "goodbye",
          sessionId: "",
          reason: "no-compatible-protocol-version",
        });
        this.stopPolling();
      }
    }
  }

  private stopPolling(): void {
    this.pollAbort?.abort();
    this.pollAbort = null;
  }

  /** Resolves when the poll loop exits (test seam). */
  async settled(): Promise<void> {
    await this.pollLoop;
  }

  private async http(method: "GET" | "POST", path: string, body?: unknown): Promise<Response> {
    const headers: Record<string, string> = {
      authorization: `Bearer ${await this.token.getToken()}`,
    };
    if (body !== undefined) headers["content-type"] = "application/json";
    return this.fetch(`${this.baseUrl}${path}`, {
      method,
      headers,
      ...(body !== undefined ? { body: JSON.stringify(body) } : {}),
    }).catch((error: unknown) => {
      throw new TransportError(`fetch ${method} ${path} failed: ${String(error)}`);
    });
  }

  /** OpenAPI 1.0.1 codes: 202 accepted, 200 duplicate, 4xx rejected + ProblemDetails.
   * The optional in-band `data` payload (e.g. an upload ticket) surfaces verbatim. */
  private async ackFrom(response: Response): Promise<EnvelopeAck> {
    if (response.status === 202 || response.status === 200) {
      const payload = (await response.json().catch(() => ({}))) as {
        deliveryId?: string;
        data?: Record<string, unknown>;
      };
      return {
        status: response.status === 202 ? "accepted" : "duplicate",
        ...(payload.deliveryId !== undefined ? { deliveryId: payload.deliveryId } : {}),
        ...(payload.data !== undefined ? { data: payload.data } : {}),
      };
    }
    return { status: "rejected", problem: await parseProblem(response) };
  }
}

function sleep(ms: number, signal: AbortSignal): Promise<void> {
  const { promise, resolve } = Promise.withResolvers<void>();
  const timer = setTimeout(resolve, ms);
  unrefTimer(timer);
  signal.addEventListener(
    "abort",
    () => {
      clearTimeout(timer);
      resolve();
    },
    { once: true },
  );
  return promise;
}

export { parseProblem };
