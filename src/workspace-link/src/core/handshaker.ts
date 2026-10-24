// Hello construction, self-validation, and welcome ingestion: the handshake
// cluster. A null welcome is a TERMINAL policy answer (no common protocol
// version) — the caller must not retry it, so this module raises the typed
// ProtocolMismatchError instead of a transport error.
import { randomBytes } from "node:crypto";
import { validateControlFrame } from "@ofac/protocol";
import { LinkError, ProtocolMismatchError } from "./errors.js";
import type { LinkEventBus } from "./link-event-bus.js";
import type { Ledger } from "./ledger.js";
import type { HelloFrame, LinkLogger, LinkSession, LinkTransport, WelcomeFrame } from "./types.js";

export class Handshaker {
  constructor(
    private readonly deps: {
      transport: LinkTransport;
      ledger: Ledger;
      endpointId: string;
      capabilityManifestDigest: string;
      logger: LinkLogger;
      bus: LinkEventBus;
      /** Applied to the client's window/heartbeat inputs before `ready`. */
      onReady(welcome: WelcomeFrame): void;
    },
  ) {}

  /** Connect + hello; resolves once the welcome landed and state is `ready`. */
  async connectAndHello(
    hasSession: boolean,
    onUnauthorized: (error: unknown) => Promise<void>,
  ): Promise<LinkSession> {
    const { transport, bus } = this.deps;
    bus.setState(hasSession ? "reconnecting" : "connecting");
    try {
      await transport.connect();
    } catch (error) {
      await onUnauthorized(error);
      throw error;
    }
    bus.setState("handshaking");
    const lastKnownAck = this.deps.ledger.processedDeliveries(1)[0];
    const hello: HelloFrame = {
      kind: "hello",
      protocolVersions: ["v1"],
      supportedTransports: transport.supportedTransports ?? ["signalr"],
      endpointId: this.deps.endpointId,
      // Transient per attempt (S:1246): fresh random hex on every hello.
      instanceId: `lnk-${randomBytes(8).toString("hex")}`,
      capabilityManifestDigest: this.deps.capabilityManifestDigest,
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
      await onUnauthorized(error);
      throw error;
    }
    if (welcome === null) {
      // Null welcome after a no-compatible-protocol-version goodbye is a
      // TERMINAL policy answer: never retry, surface a typed error.
      throw new ProtocolMismatchError();
    }
    const session: LinkSession = {
      sessionId: welcome.sessionId,
      leaseEpoch: welcome.leaseEpoch,
      maxInFlight: welcome.maxInFlight,
      heartbeatSeconds: welcome.heartbeatSeconds,
      protocolVersion: welcome.protocolVersion,
    };
    this.deps.onReady(welcome);
    bus.setState("ready");
    this.deps.logger.info("link session ready", {
      endpointId: this.deps.endpointId,
      epoch: welcome.leaseEpoch,
      sessionId: welcome.sessionId,
    });
    bus.emitWelcome(session);
    return session;
  }
}
