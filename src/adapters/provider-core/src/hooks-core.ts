// The connector's SDK hooks (provider-agnostic): `work.assign` data carries
// `capabilityId` (the op id) + `inputs`; the op runs against the provider,
// the completion publishes the child's OWN `assignment.completed.v1` with the
// provider object id in an INLINE result manifest (no artifact byte path —
// the connector holds no kernel credential, so no upload happens and the
// event carries no resultManifestRef). An unknown op id or a throw publishes
// `assignment.failed.v1` via the SDK (retryable) — never a silent success.
import type { EnvelopeCommand } from "@ofac/protocol";
import { buildEvent, dataschemaFor } from "@ofac/workspace-link";
import type { LinkLogger } from "@ofac/workspace-link";
import type { RunContext, RunOutcome } from "@ofac/adapter";
import type { OpTable } from "./ops-core.js";

const COMPLETED_TYPE = "org.facilitator.event.assignment.completed.v1";

export interface ConnectorHooks {
  onExecute(ctx: RunContext): Promise<RunOutcome>;
}

function dataOf(command: EnvelopeCommand): Record<string, unknown> {
  return command.data as unknown as Record<string, unknown>;
}

export function createConnectorHooks(opts: {
  ops: OpTable;
  logger: LinkLogger;
  childWorkspaceId: string;
}): ConnectorHooks {
  const { ops, logger } = opts;

  async function onExecute(ctx: RunContext): Promise<RunOutcome> {
    const data = dataOf(ctx.command);
    const capabilityId = typeof data["capabilityId"] === "string" ? data["capabilityId"] : "";
    const run = ops[capabilityId];
    if (run === undefined) {
      return { kind: "failed", reason: `unknown-op:${capabilityId}` };
    }

    const workId = String(data["workId"] ?? ctx.executionId);
    const assignmentId = String(data["assignmentId"] ?? ctx.executionId);
    const inputs = (data["inputs"] ?? {}) as Record<string, unknown>;
    const commandId = typeof data["commandId"] === "string" ? data["commandId"] : ctx.executionId;

    const result = await run(commandId, inputs);
    ctx.publishProgress(`${capabilityId} -> ${result.providerObjectId}`);
    ctx.publishEvent(
      buildEvent(COMPLETED_TYPE, {
        source: `urn:workspace:${opts.childWorkspaceId}`,
        subject: ctx.command.subject,
        ...(ctx.command.traceparent !== undefined ? { traceparent: ctx.command.traceparent } : {}),
        dataschema: dataschemaFor(COMPLETED_TYPE),
        data: {
          workId,
          assignmentId,
          executionId: ctx.executionId,
          executionGeneration: ctx.executionGeneration,
          resultManifest: {
            manifestId: `man-${ctx.executionId}`,
            outputs: [],
            capability: capabilityId,
            summary: `${capabilityId} -> ${result.providerObjectId}`,
            structuredResult: result.structured,
            createdAt: new Date().toISOString(),
          },
        },
      }),
    );
    logger.info("connector-completed", {
      executionId: ctx.executionId,
      capabilityId,
      providerObjectId: result.providerObjectId,
    });
    // Resolving `completed` = the hook published the terminal event itself.
    return { kind: "completed" };
  }

  return { onExecute };
}
