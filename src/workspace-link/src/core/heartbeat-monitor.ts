// Heartbeat cadence: a single unref'd interval that pings the transport for
// the live session and reports death (refused heartbeat or transport failure)
// through the injected callback. The supervisor owns the reconnect; this
// module only detects.
import { unrefTimer } from "./timers.js";
import type { LinkTransport } from "./types.js";

type IntervalHandle = ReturnType<typeof setInterval>;

export class HeartbeatMonitor {
  private timer: IntervalHandle | null = null;

  constructor(
    private readonly deps: {
      transport: LinkTransport;
      /** False ⇒ treat the session as dead (stale epoch / no session row). */
      isAlive(): boolean;
      sessionId(): string | null;
      leaseEpoch(): number;
      onDead(reason: string): void;
    },
  ) {}

  start(intervalSeconds: number): void {
    this.stop();
    this.timer = setInterval(() => {
      const sessionId = this.deps.sessionId();
      if (!this.deps.isAlive() || sessionId === null) return;
      void this.deps.transport
        .heartbeat({
          kind: "heartbeat",
          sessionId,
          leaseEpoch: this.deps.leaseEpoch(),
        })
        .then((alive) => {
          if (!alive) this.deps.onDead("heartbeat refused (stale epoch or no session row)");
        })
        .catch(() => this.deps.onDead("heartbeat transport failure"));
    }, intervalSeconds * 1000);
    // Node: don't hold the event loop open for a mere heartbeat.
    unrefTimer(this.timer);
  }

  stop(): void {
    if (this.timer !== null) {
      clearInterval(this.timer);
      this.timer = null;
    }
  }
}
