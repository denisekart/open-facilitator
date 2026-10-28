// The deterministic harness: a two-step, zero-randomness research run that
// behaves identically every time (Plan 07's worked example — third-party
// implementers copy this file as the shape of "harness hooks").
// Script (locked): progress → sleep → progress → sleep → write report →
// publish assignment.completed.v1. Completion truth (Plan 15): the report
// bytes are UPLOADED to the kernel artifact store over the link plane
// (child link token), an immutable canonical Result Manifest is uploaded as
// an artifact, and the completed event carries resultManifestRef +
// resultDigest — no file:// URLs, no self-declared digests. Cancel law: an
// abort during a sleep unwinds as cancelled — NO terminal event (the
// accepted stop reply already settled the Gateway row). A failed upload is
// an honest failure: the hook throws and the SDK publishes
// assignment.failed.
import { mkdirSync, writeFileSync } from "node:fs";
import { dirname, join } from "node:path";
import type { LinkLogger } from "@ofac/workspace-link";
import {
  artifactUri,
  buildCompletedEvent,
  buildResultManifest,
  createArtifactClient,
} from "@ofac/adapter";
import type { RunContext, RunOutcome } from "@ofac/adapter";

export interface DeterministicRunOptions {
  childWorkspaceId: string;
  workDir: string;
  stepMs: number;
  logger: LinkLogger;
  /** Injectable fetch for the artifact upload path (tests); default global. */
  fetchImpl?: typeof fetch;
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
    // Plan 12: the confirm-loop e2e rides research.publish — identical
    // deterministic script, published-flavored artifact + conclusion.
    const publishing = capabilityId === "research.publish";
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

    const suffix = publishing ? "published" : "report";
    const reportPath = join(workDir, "artifacts", `${ctx.executionId}-${suffix}.md`);
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
Deterministic ${publishing ? "publish" : "analysis"} complete.
`;
    const bytes = Buffer.from(report, "utf8");
    writeFileSync(reportPath, bytes);
    const client = createArtifactClient({
      baseUrl: ctx.kernel.baseUrl,
      token: ctx.kernel.token,
      ...(options.fetchImpl !== undefined ? { fetchImpl: options.fetchImpl } : {}),
    });
    const createdAt = new Date().toISOString();
    // The report's own bytes land first; the manifest declares them with the
    // kernel-verified digest from the upload receipt.
    const reportArtifact = await client.upload(bytes, {
      mediaType: "text/markdown",
      workId,
      assignmentId,
      retentionPolicy: "30d",
    });
    const conclusion = report.split("## Conclusion\n")[1]?.trim().split("\n")[0] ?? "complete.";
    const manifest = buildResultManifest({
      manifestId: `man-${ctx.executionId}`,
      workId,
      assignmentId,
      capability: capabilityId,
      summary: conclusion,
      structuredResult: {
        objectiveChars: objective.length,
        steeringDirectives: seen.length,
      },
      outputs: [
        {
          artifactId: reportArtifact.artifactId,
          mediaType: "text/markdown",
          size: bytes.length,
          digest: reportArtifact.digest,
          artifactUri: artifactUri(workId, assignmentId, reportArtifact.artifactId),
          createdBy: `workspace:${childWorkspaceId}`,
          createdAt,
          kind: "analysis.report",
          classification: "internal",
        },
      ],
      createdAt,
    });
    const manifestDoc = await client.upload(manifest.document, {
      mediaType: "application/json",
      workId,
      assignmentId,
      retentionPolicy: "30d",
    });
    ctx.publishEvent(
      buildCompletedEvent(
        ctx,
        { manifestId: `man-${ctx.executionId}`, workId, assignmentId },
        {
          manifestRef: artifactUri(workId, assignmentId, manifestDoc.artifactId),
          digest: manifest.digest,
        },
      ),
    );
    logger.info("worker-completed", {
      executionId: ctx.executionId,
      reportPath,
      manifestRef: artifactUri(workId, assignmentId, manifestDoc.artifactId),
    });
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
