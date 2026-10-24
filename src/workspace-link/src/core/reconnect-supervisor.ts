// The reconnect supervisor: connect + hello, drain the outbox, park until the
// session dies, then back off along the ladder and re-hello. ProtocolMismatch
// is terminal (a policy answer, not a network failure); a bounded
// `maxReconnectAttempts` ends the loop with the last error surfaced. All
// client-side effects (state, counters, heartbeat, queue) ride the hooks.
import { LinkError, ProtocolMismatchError } from "./errors.js";
import type { LinkSession } from "./types.js";
import { DEFAULT_BACKOFF, sleep } from "./timers.js";

export interface SupervisorHooks {
  /** Connect + hello; resolves once `ready`. Raises ProtocolMismatchError terminal. */
  connectAndHello(hasSession: boolean): Promise<LinkSession>;
  /** Park until the ready session dies or the loop is stopped. */
  waitForSessionDeath(): Promise<void>;
  /** Drain buffered outbound events (serial — ordering law). */
  drainOutbox(): Promise<void>;
  /** A session died (goodbye/heartbeat): stop its heartbeat before backing off. */
  onSessionLost(): void;
  setStateReconnecting(): void;
  resumeQueue(): void;
  pauseQueue(): void;
  recordReconnect(): void;
  isStopped(): boolean;
  hasSession(): boolean;
  /** Record the terminal cause, transition to `closed`, surface the error. */
  terminate(error: Error): void;
  logDown(info: { attempt: number; backoffMs: number; reason: string }): void;
}

export class ReconnectSupervisor {
  private attempt = 0;
  private readonly maxAttempts: number;
  private readonly backoff: readonly number[];

  constructor(
    private readonly hooks: SupervisorHooks,
    options: { maxReconnectAttempts?: number; backoffMs?: readonly number[] },
  ) {
    this.maxAttempts = options.maxReconnectAttempts ?? 0;
    this.backoff = options.backoffMs ?? DEFAULT_BACKOFF;
  }

  async run(): Promise<void> {
    for (;;) {
      if (this.hooks.isStopped()) return;
      try {
        await this.hooks.connectAndHello(this.hooks.hasSession());
        this.attempt = 0;
        this.hooks.resumeQueue();
        await this.hooks.drainOutbox();
      } catch (error) {
        if (error instanceof ProtocolMismatchError) {
          this.hooks.terminate(error);
          return;
        }
        if (this.hooks.isStopped()) return;
        this.attempt += 1;
        this.hooks.recordReconnect();
        if (this.maxAttempts > 0 && this.attempt > this.maxAttempts) {
          this.hooks.terminate(
            error instanceof Error
              ? error
              : new LinkError("reconnect-exhausted", "reconnect attempts exhausted"),
          );
          return;
        }
        this.hooks.pauseQueue();
        this.hooks.setStateReconnecting();
        const delay = this.backoff[Math.min(this.attempt - 1, this.backoff.length - 1)];
        this.hooks.logDown({
          attempt: this.attempt,
          backoffMs: delay,
          reason: error instanceof Error ? error.message : String(error),
        });
        await sleep(delay);
        continue;
      }
      // Ready: park until the session dies (transport close, heartbeat death,
      // goodbye push) or stop() — either flips state away from ready.
      await this.hooks.waitForSessionDeath();
      if (this.hooks.isStopped()) return;
      // A previously good session died (goodbye push / heartbeat refusal):
      // count the reconnect and back off before the re-hello.
      this.attempt += 1;
      this.hooks.recordReconnect();
      this.hooks.pauseQueue();
      this.hooks.onSessionLost();
      const delay = this.backoff[Math.min(this.attempt - 1, this.backoff.length - 1)];
      await sleep(delay);
    }
  }
}
