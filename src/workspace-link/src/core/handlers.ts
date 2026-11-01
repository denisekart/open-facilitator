// Built-in handlers (Plan 04 locked decision): enough to run §26.1's
// client-side items end-to-end without a harness. Plan 06's deterministic
// worker replaces them; Plan 05 composes createEchoHandler/createStubHandler.
import type { EnvelopeCommand, WorkAssignCommandV1Data } from "@ofac/protocol";
import { buildEvent } from "./envelope.js";
import type { LinkHandler } from "./types.js";

export interface EchoHandlerOptions {
  /** CloudEvents source URN for the published events (e.g. linkd/<endpointId>). */
  source: string;
}

/**
 * On `work.assign.v1`: answer accepted, then publish `work.progress.v1` and
 * `assignment.completed.v1` (minimal ResultManifest, zero artifacts) through
 * the context so outbound-event wiring is exercised too.
 */
export function createEchoHandler(options: EchoHandlerOptions): LinkHandler {
  return {
    async onCommand(cmd, ctx) {
      if (cmd.type !== "org.facilitator.command.work.assign.v1") {
        return { kind: "unsupported", reason: `echo handles work.assign only, got ${cmd.type}` };
      }
      const data = cmd.data as unknown as WorkAssignCommandV1Data;
      const source = options.source;
      const subject = cmd.subject;
      await ctx.publishEvent(
        buildEvent("org.facilitator.event.work.progress.v1", {
          source,
          subject,
          data: {
            workId: data.workId,
            assignmentId: data.assignmentId,
            executionGeneration: data.executionGeneration ?? 1,
            progressNote: "linkd echo: assignment received.",
          },
        }),
      );
      await ctx.publishEvent(
        buildEvent("org.facilitator.event.assignment.completed.v1", {
          source,
          subject,
          data: {
            workId: data.workId,
            assignmentId: data.assignmentId,
            executionId: `exec-${ctx.deliveryId}`,
            executionGeneration: data.executionGeneration ?? 1,
            resultManifest: { manifestId: `man-${ctx.deliveryId}`, outputs: [] },
          },
        }),
      );
      return { kind: "accepted" };
    },
  };
}

/** Accept-everything command: the receipt-only proof of the pipeline. */
export function createStubHandler(): LinkHandler {
  return {
    async onCommand(cmd: EnvelopeCommand) {
      return cmd.type.startsWith("org.facilitator.command.")
        ? { kind: "accepted" }
        : { kind: "unsupported", reason: `not a command: ${cmd.type}` };
    },
  };
}
