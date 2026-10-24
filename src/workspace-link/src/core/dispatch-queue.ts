// Advisory flow control + bounded dispatch queue: the client self-enforces
// the welcome window regardless of FlowControl hints (0 pauses ALL dispatch;
// non-zero resumes with that window). Overflow never NAKs — backpressure is
// dispatch-delay only.
import type { LinkDeliveryV1 } from "@ofac/protocol";
import type { CommandDispatcher } from "./command-dispatcher.js";
import type { LinkLogger } from "./types.js";

export class DispatchQueue {
  private queue: LinkDeliveryV1[] = [];
  private paused = false;
  private inFlight = 0;
  private window = 16;

  constructor(
    private readonly deps: {
      dispatcher: CommandDispatcher;
      logger: LinkLogger;
      onError(error: unknown): void;
    },
  ) {}

  depth(): number {
    return this.queue.length;
  }

  /** Queue + in-flight exceed window + maxQueue (= window × 4). */
  saturated(): boolean {
    return this.queue.length + this.inFlight >= this.window * 4 + this.window;
  }

  setWindow(maxInFlight: number): void {
    this.window = maxInFlight;
  }

  pause(): void {
    this.paused = true;
  }

  resume(): void {
    this.paused = false;
    this.pump();
  }

  /** Advisory pacing hint (docs/protocol.md:196). */
  onFlowControl(maxInFlight: number): void {
    if (maxInFlight === 0) {
      this.paused = true;
      this.deps.logger.debug("flow_control pause");
    } else {
      this.paused = false;
      this.window = maxInFlight;
      this.deps.logger.debug("flow_control resume", { maxInFlight });
      this.pump();
    }
  }

  enqueue(delivery: LinkDeliveryV1): void {
    this.queue.push(delivery);
    this.pump();
  }

  private pump(): void {
    while (!this.paused && this.inFlight < this.window && this.queue.length > 0) {
      const delivery = this.queue.shift()!;
      this.inFlight += 1;
      void this.deps.dispatcher
        .dispatch(delivery)
        .catch((error: unknown) => {
          this.deps.logger.error("delivery pipeline error", { reason: String(error) });
          this.deps.onError(error);
        })
        .finally(() => {
          this.inFlight -= 1;
          this.pump();
        });
    }
  }
}
