// SignalR transport (reference transport). Mirrors the as-built server
// (LinkHub.cs): server methods Hello/Envelope/Receipt/Heartbeat/Goodbye,
// client-method pushes Deliver/FlowControl/Disconnect. Bearer rides the
// WebSocket upgrade via accessTokenFactory (S:624 — never ?access_token=);
// skipNegotiation + WebSockets matches the server's Plan 03 test-harness
// connect pattern (LinkTestHarness.cs FakeLinkClient). The SDK's own
// supervisor owns re-hello (the server bumps the epoch on every hello
// regardless), so SignalR's automatic reconnect stays OFF.
import {
  HttpTransportType,
  HubConnectionBuilder,
  HubConnectionState,
  LogLevel,
  type HubConnection,
} from "@microsoft/signalr";
import type { EnvelopeObject, LinkDeliveryV1, ReceiptV1 } from "@ofac/protocol";
import { trimSlash } from "../core/enroll.js";
import { TransportError } from "../core/errors.js";
import { FrameEmitter } from "../core/frame-emitter.js";
import type {
  EnvelopeAck,
  FlowControlFrame,
  GoodbyeFrame,
  HeartbeatFrame,
  HelloFrame,
  LinkTokenProvider,
  LinkTransport,
  WelcomeFrame,
} from "../core/types.js";
import type { SupportedTransport } from "@ofac/protocol";

export interface SignalrTransportOptions {
  baseUrl: string;
  token: LinkTokenProvider;
}

export class SignalrTransport implements LinkTransport {
  readonly supportedTransports: [SupportedTransport, ...SupportedTransport[]] = [
    "signalr",
    "http-poll",
  ];
  private readonly deliver = new FrameEmitter<LinkDeliveryV1>();
  private readonly flow = new FrameEmitter<FlowControlFrame>();
  private readonly disconnect = new FrameEmitter<GoodbyeFrame>();
  /** Set when the server pushed a goodbye; distinguishes policy drops from network loss. */
  private lastGoodbye: GoodbyeFrame | null = null;
  private connection: HubConnection;

  constructor(private readonly options: SignalrTransportOptions) {
    this.connection = this.buildConnection();
  }

  get lastServerGoodbye(): GoodbyeFrame | null {
    return this.lastGoodbye;
  }

  private buildConnection(): HubConnection {
    const connection = new HubConnectionBuilder()
      .withUrl(`${trimSlash(this.options.baseUrl)}/hubs/link`, {
        accessTokenFactory: () => this.options.token.getToken(),
        skipNegotiation: true,
        transport: HttpTransportType.WebSockets,
      })
      .configureLogging(LogLevel.None)
      .build();
    // Handlers BEFORE start: the server pumps Deliver frames immediately
    // inside Hello, so registering later races a lost frame.
    connection.on("Deliver", (frame: LinkDeliveryV1) => this.deliver.emit(frame));
    connection.on("FlowControl", (frame: FlowControlFrame) => this.flow.emit(frame));
    connection.on("Disconnect", (frame: GoodbyeFrame) => {
      this.lastGoodbye = frame;
      this.disconnect.emit(frame);
    });
    return connection;
  }

  async connect(): Promise<void> {
    if (this.connection.state === HubConnectionState.Connected) return;
    if (this.connection.state === HubConnectionState.Disconnected) {
      // A stopped SignalR connection cannot restart — rebuild the socket.
      this.connection = this.buildConnection();
    }
    try {
      await this.connection.start();
    } catch (error) {
      throw mapHubError(error);
    }
  }

  async hello(frame: HelloFrame): Promise<WelcomeFrame | null> {
    try {
      // Server null-return convention: LinkHub.Hello returns WelcomeFrame?;
      // the JS client surfaces `null`, and the incompatible-version goodbye
      // arrives through the Disconnect push.
      return await this.connection.invoke<WelcomeFrame | null>("Hello", frame);
    } catch (error) {
      throw mapHubError(error);
    }
  }

  async envelope(env: EnvelopeObject): Promise<EnvelopeAck> {
    try {
      return await this.connection.invoke<EnvelopeAck>("Envelope", env);
    } catch (error) {
      throw mapHubError(error);
    }
  }

  async receipt(r: ReceiptV1): Promise<void> {
    try {
      await this.connection.invoke("Receipt", r);
    } catch (error) {
      throw mapHubError(error);
    }
  }

  async heartbeat(hb: HeartbeatFrame): Promise<boolean> {
    try {
      return await this.connection.invoke<boolean>("Heartbeat", hb);
    } catch (error) {
      throw mapHubError(error);
    }
  }

  async goodbye(gb: GoodbyeFrame): Promise<void> {
    await this.connection.invoke("Goodbye", gb);
  }

  async close(): Promise<void> {
    await this.connection.stop().catch(() => undefined);
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
}

/**
 * Map SignalR failures onto TransportError. 401 detection: HttpError carries
 * `statusCode`; a WebSocket-upgrade auth rejection surfaces as a message
 * containing the status (best-effort matching — the E2E tier proves the path).
 */
export function mapHubError(error: unknown): TransportError {
  const message = error instanceof Error ? error.message : String(error);
  const status =
    typeof error === "object" && error !== null && "statusCode" in error
      ? (error as { statusCode?: number }).statusCode
      : undefined;
  const unauthorized = status === 401 || /\b401\b|unauthorized/i.test(message);
  return new TransportError(message, {
    ...(unauthorized ? { unauthorized: true } : {}),
    ...(status !== undefined ? { problem: { status } } : {}),
  });
}
