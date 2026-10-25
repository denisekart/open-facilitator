// The deterministic harness: a two-step, zero-randomness research run that
// behaves identically every time (Plan 07's worked example — third-party
// implementers copy this file as the shape of "harness hooks").
//
// Script (locked): progress → sleep → progress → sleep → write report →
// publish assignment.completed.v1 with a result manifest whose digest is
// the sha256 of the bytes actually on disk. Cancel law: an abort during a
// sleep unwinds as cancelled — NO terminal event (the accepted stop reply
// already settled the Gateway row).
import { createHash } from "node:crypto";
import { mkdirSync, writeFileSync } from "node:fs";
import { dirname, join } from "node:path";
import { pathToFileURL } from "node:url";
import { buildEvent } from "@ofac/workspace-link";
import type { LinkLogger } from "@ofac/workspace-link";
import type { RunContext, RunOutcome } from "@ofac/adapter";

export interface DeterministicRunOptions {
  childWorkspaceId: string;
  workDir: string;
  stepMs: number;
  logger: LinkLogger;
}

export interface DeterministicHooks {
  onExecute(ctx: RunContext): Promise<RunOutcome>;
  onSteer(directive: string, ctx: RunContext): void;
}

interface SteerRecord {
  directive: string;
  atMs: number;
}

/** Resolves true when the sleep completed, false when the signal aborted it. */
function cancellableSleep(ms: number, signal: AbortSignal): Promise<boolean> {
  return new Promise((resolve) => {
    if (signal.aborted) {
      resolve(false);
      return;
    }
    const onAbort = (): void => {
      clearTimeout(timer);
      resolve(false);
    };
    const timer = setTimeout(() => {
      signal.removeEventListener("abort", onAbort);
      resolve(true);
    }, ms);
    timer.unref?.();
    signal.addEventListener("abort", onAbort, { once: true });
  });
}

export function createDeterministicHooks(options: DeterministicRunOptions): DeterministicHooks {
  const { childWorkspaceId, workDir, stepMs, logger } = options;
  const steers = new Map<string, SteerRecord[]>();
  const startedAt = new Map<string, number>();

  async function onExecute(ctx: RunContext): Promise<RunOutcome> {
    const data = ctx.command.data as Record<string, unknown>;
    const workId = String(data["workId"] ?? ctx.executionId);
    const assignmentId = String(data["assignmentId"] ?? ctx.executionId);
    const inputs = data["inputs"] as Record<string, unknown> | undefined;
    const objective = typeof inputs?.["objective"] === "string" ? inputs["objective"] : workId;
    const capabilityId = String(data["capabilityId"] ?? "research.analyze");
    const t0 = Date.now();
    startedAt.set(ctx.executionId, t0);
    const seen: SteerRecord[] = [];
    steers.set(ctx.executionId, seen);

    ctx.publishProgress(`accepted: analyzing ${capabilityId} for ${workId}`);
    logger.info("worker-step", { executionId: ctx.executionId, step: 1 });
    if (!(await cancellableSleep(stepMs, ctx.signal))) return cancelled(ctx);
    const step1At = Date.now() - t0;

    ctx.publishProgress("drafting report");
    logger.info("worker-step", { executionId: ctx.executionId, step: 2 });
    if (!(await cancellableSleep(stepMs, ctx.signal))) return cancelled(ctx);
    const step2At = Date.now() - t0;

    const reportPath = join(workDir, "artifacts", `${ctx.executionId}-report.md`);
    mkdirSync(dirname(reportPath), { recursive: true, mode: 0o700 });
    const directiveLines =
      seen.length === 0 ? "none" : seen.map((s) => `- +${s.atMs}ms: ${s.directive}`).join("\n");
    const report = `# Research report ${workId}
Objective: ${objective}
Assignment: ${assignmentId} (execution ${ctx.executionId})
## Steps
- step 1 complete at +${step1At}ms
- step 2 complete at +${step2At}ms
## Steering directives
${directiveLines}
## Conclusion
Deterministic analysis complete.
`;
    const bytes = Buffer.from(report, "utf8");
    writeFileSync(reportPath, bytes);
    const digest = `sha256:${createHash("sha256").update(bytes).digest("hex")}`;

    ctx.publishEvent(
      buildEvent("org.facilitator.event.assignment.completed.v1", {
        source: `urn:workspace:${childWorkspaceId}`,
        subject: `work/${workId}/assignment/${assignmentId}`,
        data: {
          workId,
          assignmentId,
          executionId: ctx.executionId,
          executionGeneration: ctx.executionGeneration,
          resultManifest: {
            manifestId: `man-${ctx.executionId}`,
            outputs: [
              {
                artifactId: `art-${ctx.executionId}-report`,
                mediaType: "text/markdown",
                size: bytes.length,
                digest,
                storageUrl: pathToFileURL(reportPath).href,
                createdBy: `workspace:${childWorkspaceId}`,
                createdAt: new Date().toISOString(),
              },
            ],
          },
        },
        ...(ctx.command.traceparent !== undefined ? { traceparent: ctx.command.traceparent } : {}),
      }),
    );
    logger.info("worker-completed", { executionId: ctx.executionId, reportPath });
    return { kind: "completed" };
  }

  function cancelled(ctx: RunContext): RunOutcome {
    logger.info("worker-cancelled", { executionId: ctx.executionId });
    // The adapter DROPS cancelled runs without publishing (the stop reply
    // settled the row); the outcome kind only steers that law.
    return { kind: "completed" };
  }

  function onSteer(directive: string, ctx: RunContext): void {
    const t0 = startedAt.get(ctx.executionId) ?? Date.now();
    steers.get(ctx.executionId)?.push({ directive, atMs: Date.now() - t0 });
    logger.info("worker-steered", { executionId: ctx.executionId, directive });
  }

  return { onExecute, onSteer };
}
