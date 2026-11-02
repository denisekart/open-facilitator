// The delivery pipeline law (S:301–302): validate BEFORE persisting, persist
// BEFORE answering. A broken envelope is refused without a ledger row (it is
// not a delivery we can ever reconcile); an unknown COMMAND type enters the
// ledger as `unsupported` so a redelivery answers `duplicate` instead of
// re-running the fail-closed path; fresh events auto-ack, fresh commands
// queue for the dispatcher inside the flow window.
import { KNOWN_COMMAND_TYPES, enforceSizeLimit, validateEnvelope } from "@ofac/protocol";
import type { LinkDeliveryV1, ReceiptV1 } from "@ofac/protocol";
import { dedupeKeyOf, type Ledger } from "./ledger.js";
import type { DispatchQueue } from "./dispatch-queue.js";
import { DECIDED_EVENT, MAX_ENVELOPE_BYTES, type LinkLogger, type LinkStats } from "./types.js";

/** Answer for one delivery; the client validates + ships it over the transport. */
export type SendReceipt = (
  deliveryId: string,
  partial: Omit<ReceiptV1, "deliveryId">,
  correlation?: Record<string, unknown>,
) => Promise<void>;

export function nowIso(): string {
  return new Date().toISOString();
}

/**
 * Fencing: a receipt for a command whose data carries an execution
 * epoch echoes it, so the kernel can fence a stale execution's own delivery
 * answer. A non-number (malformed/absent) simply omits the field — the
 * receipt schema keeps it optional.
 */
export function epochField(envelope: unknown): { executionGeneration?: number } {
  const data = (envelope as { data?: Record<string, unknown> } | undefined)?.data;
  const generation = data?.["executionGeneration"];
  return typeof generation === "number" && Number.isInteger(generation) && generation >= 1
    ? { executionGeneration: generation }
    : {};
}

export class DeliveryPipeline {
  constructor(
    private readonly deps: {
      ledger: Ledger;
      logger: LinkLogger;
      counters: LinkStats;
      endpointId: string;
      queue: DispatchQueue;
      sendReceipt: SendReceipt;
    },
  ) {}

  async handle(delivery: LinkDeliveryV1): Promise<void> {
    const { ledger, logger, counters, endpointId, queue, sendReceipt } = this.deps;
    const correlation = { deliveryId: delivery.deliveryId, endpointId };
    // 1) Validate before persisting.
    const bytes = Buffer.byteLength(JSON.stringify(delivery.envelope), "utf8");
    const shape = validateEnvelope(delivery.envelope);
    if (!shape.ok) {
      // Fail-closed taxonomy (S:309): an unknown COMMAND type is `unsupported`
      // and enters the ledger (a redelivery must answer `duplicate`, not
      // re-run the fail-closed path); any other envelope violation is
      // `rejected` with NO ledger row — an envelope we can't parse is not a
      // delivery we can ever reconcile.
      if (shape.errorClass === "unknown-command") {
        ledger.insertIfNew(
          delivery.deliveryId,
          delivery.envelope,
          dedupeKeyOf(delivery.envelope, KNOWN_COMMAND_TYPES),
        );
        ledger.markState(delivery.deliveryId, "unsupported");
        counters.unsupported += 1;
        await sendReceipt(delivery.deliveryId, {
          status: "unsupported",
          reason: `envelope ${shape.errorClass}: ${shape.message}`,
          ...epochField(delivery.envelope),
        });
        return;
      }
      counters.failed += 1;
      await sendReceipt(delivery.deliveryId, {
        status: "rejected",
        reason: `envelope ${shape.errorClass}: ${shape.message}`,
        ...epochField(delivery.envelope),
      });
      return;
    }
    if (!enforceSizeLimit(bytes, MAX_ENVELOPE_BYTES)) {
      counters.failed += 1;
      await sendReceipt(delivery.deliveryId, {
        status: "rejected",
        reason: `envelope exceeds ${MAX_ENVELOPE_BYTES} wire bytes`,
        ...epochField(delivery.envelope),
      });
      return;
    }
    // 2) Durable inbox BEFORE any answer (S:301): fresh | duplicate.
    const env = delivery.envelope;
    const dedupe = dedupeKeyOf(env, KNOWN_COMMAND_TYPES);
    if (ledger.insertIfNew(delivery.deliveryId, env, dedupe) === "duplicate") {
      counters.duplicate += 1;
      logger.debug("duplicate delivery → duplicate receipt", correlation);
      await sendReceipt(delivery.deliveryId, {
        status: "duplicate",
        processedAt: nowIso(),
        ...epochField(env),
      });
      return;
    }
    // 3) Fresh: business events are facts, auto-acked after logging;
    //    commands queue for the handler inside the flow window (bounded by
    //    maxQueue). The kernel's approval.decided PUSH is an
    //    event class yet routes as work — a silently auto-acked decision is
    //    a lost approval round-trip.
    const isCommand = env.type.startsWith("org.facilitator.command.");
    if (!isCommand && env.type !== DECIDED_EVENT) {
      ledger.markState(delivery.deliveryId, "processed");
      counters.processed += 1;
      logger.info("event delivery auto-acked", { ...correlation, type: env.type });
      await sendReceipt(delivery.deliveryId, {
        status: "accepted",
        processedAt: nowIso(),
        ...epochField(env),
      });
      return;
    }
    if (queue.saturated()) {
      // Backpressure is dispatch-delay only — never NAK a valid delivery.
      logger.warn("dispatch queue saturated; delaying", {
        ...correlation,
        queued: queue.depth(),
      });
    }
    queue.enqueue(delivery);
  }
}
