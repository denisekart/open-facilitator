// Command dispatch: run the mounted handler for one delivery, map its
// outcome onto a receipt, and flip the ledger BEFORE the receipt ships
// (S:302 — post-crash, the row explains the gap and redelivery lands on
// `duplicate`, terminating the loop).
import { KNOWN_COMMAND_TYPES } from "@ofac/protocol";
import type { EnvelopeCommand, EnvelopeEvent, LinkDeliveryV1, ReceiptV1 } from "@ofac/protocol";
import { nowIso, type SendReceipt } from "./delivery-pipeline.js";
import type { Ledger } from "./ledger.js";
import type {
  CommandContext,
  CommandOutcome,
  EnvelopeAck,
  LinkHandler,
  LinkLogger,
  LinkStats,
} from "./types.js";
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
    const outcome: CommandOutcome = !KNOWN_COMMAND_TYPES.includes(env.type)
      ? { kind: "unsupported", reason: `unknown command type ${env.type}` }
      : this.deps.handler === undefined
        ? { kind: "unsupported", reason: "no handler mounted" }
        : await this.invokeHandler(env, delivery, correlation);
    await this.complete(delivery, outcome, correlation);
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
    // Ledger flip BEFORE the receipt (S:302).
    ledger.markState(delivery.deliveryId, state);
    await sendReceipt(delivery.deliveryId, receipt, correlation);
  }
}
