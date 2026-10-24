// Outbox drain (Step 6 law): after every welcome, push buffered outbound
// events to the transport ONE AT A TIME — serial sends are the ordering law,
// never parallelize. A transport failure stops the drain (still partitioned);
// a rejection drops the row (the server's answer is final).
import type { EnvelopeEvent } from "@ofac/protocol";
import type { Ledger } from "./ledger.js";
import type { LinkLogger, LinkTransport } from "./types.js";

export class OutboxDrain {
  constructor(
    private readonly deps: {
      ledger: Ledger;
      transport: LinkTransport;
      logger: LinkLogger;
      onUnauthorized(error: unknown): Promise<void>;
    },
  ) {}

  async drain(): Promise<void> {
    for (;;) {
      const batch = this.deps.ledger.unsentEvents(16);
      if (batch.length === 0) return;
      for (const row of batch) {
        try {
          const ack = await this.deps.transport.envelope(row.envelope as EnvelopeEvent);
          if (ack.status !== "rejected") this.deps.ledger.markEventSent(row.id);
          else this.deps.logger.warn("buffered event rejected; dropping", { eventId: row.id });
        } catch (error) {
          await this.deps.onUnauthorized(error);
          return; // still partitioned — retry after the next welcome
        }
      }
    }
  }
}
