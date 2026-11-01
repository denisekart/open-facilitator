// Command dispatch: run the mounted handler for one delivery, map its
// outcome onto a receipt, and flip the ledger BEFORE the receipt ships
// (S:302 — post-crash, the row explains the gap and redelivery lands on
// `duplicate`, terminating the loop).
import { KNOWN_COMMAND_TYPES } from "@ofac/protocol";
import type { EnvelopeCommand, EnvelopeEvent, LinkDeliveryV1, ReceiptV1 } from "@ofac/protocol";
import { nowIso, epochField, type SendReceipt } from "./delivery-pipeline.js";
import type { Ledger } from "./ledger.js";
import type {
  CommandContext,
  CommandOutcome,
  EnvelopeAck,
  LinkHandler,
  LinkLogger,
  LinkStats,
} from "./types.js";
import { DECIDED_EVENT } from "./types.js";

export class CommandDispatcher {
  constructor(
    private readonly deps: {
      handler?: LinkHandler | undefined;
      ledger: Ledger;
      logger: LinkLogger;
      counters: LinkStats;
      endpointId: string;
      sendReceipt: SendReceipt;
      publishEvent(
        env: EnvelopeEvent,
        traceparent?: string,
        causationid?: string,
      ): Promise<EnvelopeAck>;
    },
  ) {}

  async dispatch(delivery: LinkDeliveryV1): Promise<void> {
    const env = delivery.envelope as EnvelopeCommand;
    const correlation = {
      deliveryId: delivery.deliveryId,
      endpointId: this.deps.endpointId,
      workId: typeof env.data?.["workId"] === "string" ? (env.data["workId"] as string) : undefined,
      assignmentId:
        typeof env.data?.["assignmentId"] === "string"
          ? (env.data["assignmentId"] as string)
          : undefined,
    };
    // Plan 12 pre-branch: a kernel-pushed business event (approval.decided)
    // rides the same delivery machinery as a command — classify it BEFORE
    // the command gate, ack it, and let the handler react.
    const outcome: CommandOutcome = !KNOWN_COMMAND_TYPES.includes(env.type)
      ? await this.onDecidedOrUnsupported(env, delivery, correlation)
      : this.deps.handler === undefined
        ? { kind: "unsupported", reason: "no handler mounted" }
        : await this.invokeHandler(env, delivery, correlation);
    await this.complete(delivery, outcome, correlation);
  }

  private async onDecidedOrUnsupported(
    env: EnvelopeCommand,
    delivery: LinkDeliveryV1,
    correlation: Record<string, unknown>,
  ): Promise<CommandOutcome> {
    if (env.type === DECIDED_EVENT && this.deps.handler?.onDecided !== undefined) {
      try {
        await this.routeDecided(env, delivery);
        return { kind: "accepted" };
      } catch (error) {
        this.deps.logger.error("onDecided threw; answering rejected", {
          ...correlation,
          reason: error instanceof Error ? error.message : String(error),
        });
        return { kind: "rejected", reason: `decided handler error: ${String(error)}` };
      }
    }
    return { kind: "unsupported", reason: `unknown command type ${env.type}` };
  }

  private async routeDecided(env: EnvelopeCommand, delivery: LinkDeliveryV1): Promise<void> {
    const ctx = {
      deliveryId: delivery.deliveryId,
      envelope: env,
      signal: new AbortController().signal,
      publishEvent: (e: EnvelopeEvent) => this.deps.publishEvent(e, env.traceparent, env.id),
    };
    // SAFETY: the DECIDED_EVENT type is an event class, not a command — the
    // dispatcher casts every delivery to EnvelopeCommand up front.
    await this.deps.handler?.onDecided?.(env as unknown as EnvelopeEvent, ctx);
  }

  private async invokeHandler(
    env: EnvelopeCommand,
    delivery: LinkDeliveryV1,
    correlation: Record<string, unknown>,
  ): Promise<CommandOutcome> {
    const handler = this.deps.handler;
    if (handler === undefined) return { kind: "unsupported", reason: "no handler mounted" };
    const controller = new AbortController();
    const ctx: CommandContext = {
      deliveryId: delivery.deliveryId,
      envelope: env,
      signal: controller.signal,
      publishEvent: (e) => this.deps.publishEvent(e, env.traceparent, env.correlationid ?? env.id),
    };
    try {
      return await handler.onCommand(env, ctx);
    } catch (error) {
      this.deps.logger.error("handler threw; answering rejected", {
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
    const { ledger, counters, sendReceipt } = this.deps;
    let receipt: ReceiptV1;
    let state: "processed" | "failed" | "unsupported";
    switch (outcome.kind) {
      case "accepted":
        state = "processed";
        counters.processed += 1;
        receipt = { deliveryId: delivery.deliveryId, status: "accepted", processedAt: nowIso() };
        break;
      case "rejected":
        state = "failed";
        counters.failed += 1;
        receipt = { deliveryId: delivery.deliveryId, status: "rejected", reason: outcome.reason };
        break;
      case "unsupported":
        state = "unsupported";
        counters.unsupported += 1;
        receipt = {
          deliveryId: delivery.deliveryId,
          status: "unsupported",
          reason: outcome.reason ?? "capability not supported",
        };
        break;
    }
    // Plan 35 fencing: the receipt echoes the delivered command's execution
    // epoch (when it carried one) so the kernel fences a stale execution.
    receipt = { ...receipt, ...epochField(delivery.envelope) };
    // Ledger flip BEFORE the receipt (S:302).
    ledger.markState(delivery.deliveryId, state);
    await sendReceipt(delivery.deliveryId, receipt, correlation);
  }
}
