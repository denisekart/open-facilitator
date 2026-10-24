// HTTP long-poll transport + hidden SignalR "session keeper".
//
// As-built constraint (Plan 03, LinkEndpoints.cs): GET /deliveries REQUIRES a
// live link_sessions row — minted ONLY by a hub hello — and every successful
// GET refreshes the lease (no HTTP heartbeat endpoint). A pure-HTTP mode would
// need a server session-mint endpoint (future additive OpenAPI change, not in
// this plan), so this transport keeps a hidden SignalR connection that does
// hello + heartbeats; deliveries flow through the frozen HTTP surface.
// Because the keeper socket is a tracked hub connection, the pump MAY push
// Deliver to it instead of leaving rows for the poll (§7.3: any live socket
// claims) — the transport forwards keeper frames into the client pipeline,
// and the ledger dedupes either arrival surface.
//
// Consequence: on 404 session-not-found the keeper reconnects and re-hellos
// (fresh epoch — lease expiry, not TCP, ends a session), and the next GET
// picks up redelivery of anything the in-flight lease released.
import type { EnvelopeObject, LinkDeliveryV1, ReceiptV1, SupportedTransport } from "@ofac/protocol";
import { parseProblem, trimSlash } from "../core/enroll.js";
import { LinkError, TransportError, type ProblemDetails } from "../core/errors.js";
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
  private readonly deliverCbs: ((d: LinkDeliveryV1) => void)[] = [];
  private readonly flowCbs: ((f: FlowControlFrame) => void)[] = [];
  private readonly disconnectCbs: ((g: GoodbyeFrame) => void)[] = [];

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
    // claims for ANY live socket, §7.3). Forwarding keeps both arrival
    // surfaces feeding the same client pipeline — the ledger dedupes, so
    // a push-then-poll race can never double-run a delivery.
    this.keeper.onDeliver((d) => {
      for (const cb of this.deliverCbs) cb(d);
    });
    this.keeper.onFlow((f) => {
      for (const cb of this.flowCbs) cb(f);
    });
    this.keeper.onDisconnect((g) => {
      for (const cb of this.disconnectCbs) cb(g);
    });
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
    const response = await this.http("POST", `/api/link/v1/deliveries/${deliveryId}/receipt`, body);
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
    this.deliverCbs.push(cb);
  }

  onFlow(cb: (f: FlowControlFrame) => void): void {
    this.flowCbs.push(cb);
  }

  onDisconnect(cb: (g: GoodbyeFrame) => void): void {
    this.disconnectCbs.push(cb);
  }

  // ── poll loop ────────────────────────────────────────────────────────────

  private startPolling(): void {
    if (this.pollAbort !== null) return;
    this.pollAbort = new AbortController();
    const signal = this.pollAbort.signal;
    const loop = async (): Promise<void> => {
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
              problem: await safeProblem(response),
            });
          }
          const items = (await response.json()) as LinkDeliveryV1[];
          for (const item of items) {
            for (const cb of this.deliverCbs) cb(item);
          }
          // Immediate re-issue (long-poll semantics).
        } catch (error) {
          if (signal.aborted || this.closed) return;
          if (error instanceof TransportError && error.unauthorized) {
            await this.token.refresh?.();
          }
          // Network hiccup: brief pause, then keep polling — the lease may
          // lapse server-side, and the 404 path re-mints it.
          await sleep(1000, signal);
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
        for (const cb of this.disconnectCbs) {
          cb({ kind: "goodbye", sessionId: "", reason: "no-compatible-protocol-version" });
        }
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

  /** OpenAPI 1.0.1 codes: 202 accepted, 200 duplicate, 4xx rejected + ProblemDetails. */
  private async ackFrom(response: Response): Promise<EnvelopeAck> {
    if (response.status === 202) {
      const payload = (await response.json().catch(() => ({}))) as { deliveryId?: string };
      return {
        status: "accepted",
        ...(payload.deliveryId !== undefined ? { deliveryId: payload.deliveryId } : {}),
      };
    }
    if (response.status === 200) {
      return { status: "duplicate" };
    }
    return { status: "rejected", problem: await safeProblem(response) };
  }
}

async function safeProblem(response: Response): Promise<ProblemDetails> {
  try {
    const text = await response.text();
    return text.length > 0
      ? ({ ...JSON.parse(text), status: response.status } as ProblemDetails)
      : { status: response.status };
  } catch {
    return { status: response.status };
  }
}

function sleep(ms: number, signal: AbortSignal): Promise<void> {
  const { promise, resolve } = Promise.withResolvers<void>();
  const timer = setTimeout(resolve, ms);
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
