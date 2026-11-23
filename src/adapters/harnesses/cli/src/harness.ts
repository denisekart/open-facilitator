// The CLI harness hooks: a playbook run driven by the declarative runbook.
// Script per assignment: dereference inbound media (`inputs.sourceRefs`
// through the link-plane content GET; unreadable refs degrade to a marker
// line, never fail the run) → seed the ask_user shim when the capability
// declares it → execute the capability's steps (executor law in
// playbook/run.ts) → upload every `capture` over the artifact path →
// publish assignment.completed.v1 with a canonical Result Manifest (no
// file:// URLs, no self-declared digests). Cancel law: the abort group-
// kills the live step and the run publishes NOTHING (the accepted stop
// reply settled the Gateway row). A failed upload is an honest failure:
// the hook throws and the SDK publishes assignment.failed.
import { mkdirSync, writeFileSync } from "node:fs";
import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";
import type { LinkLogger } from "@ofac/workspace-link";
import {
  artifactUri,
  buildCompletedEvent,
  buildResultManifest,
  createArtifactClient,
  type ResultManifestOutput,
} from "@ofac/adapter";
import type { RunContext, RunOutcome } from "@ofac/adapter";
import type { Playbook } from "./playbook/schema.js";
import { runCapability } from "./playbook/run.js";
import type { InterpolationScope } from "./playbook/interpolate.js";
import { startAskUserBridge, writeMcpConfig } from "./mcp/bridge.js";

export interface CliRunOptions {
  readonly childWorkspaceId: string;
  /** Absolute: per-run sandboxes land under <workDir>/runs/<executionId>. */
  readonly workDir: string;
  readonly killGraceMs: number;
  /** "mcp" enables the ask_user shim leg for capabilities that opt in. */
  readonly askUser: "mcp" | "off";
  /** Optional: injected into every run's vars, gating `when: askQuestion`
   * steps (the parity seam for the deterministic worker's askQuestion). */
  readonly askQuestion?: string | undefined;
  readonly logger: LinkLogger;
  /** Test seam: fetch for the artifact leg; default global. */
  readonly fetchImpl?: typeof fetch;
  /** Test seam: shim entry (default: the sibling bin/mcp-shim.js of this bundle). */
  readonly shimEntry?: string | undefined;
}

export interface CliHooks {
  onExecute(ctx: RunContext): Promise<RunOutcome>;
  onCancel?(reason: string, ctx: RunContext): void;
}

/** The per-run sandbox every step executes inside. */
function runDir(workDir: string, executionId: string): string {
  const dir = join(workDir, "runs", executionId);
  mkdirSync(join(dir, "attachments"), { recursive: true, mode: 0o700 });
  return dir;
}

/** Default shim entry: sibling of this module's bin dir in the built bundle. */
function defaultShimEntry(): string {
  return join(dirname(fileURLToPath(import.meta.url)), "bin", "mcp-shim.js");
}

export function createCliHooks(options: CliRunOptions, playbook: Playbook): CliHooks {
  const { childWorkspaceId, workDir, killGraceMs, askUser, askQuestion, logger } = options;
  const shimEntry = options.shimEntry ?? defaultShimEntry();

  async function attachmentsSection(
    ctx: RunContext,
    dir: string,
    inputs: Record<string, unknown>,
  ): Promise<string> {
    const client = createArtifactClient({
      baseUrl: ctx.kernel.baseUrl,
      token: ctx.kernel.token,
      ...(options.fetchImpl !== undefined ? { fetchImpl: options.fetchImpl } : {}),
    });
    // Inbound media law: sourceRefs are kernel artifacts; the run proves it
    // holds the bytes via the link-plane content GET, landing them under
    // attachments/<index> for the CLI to read. Media never fails a run —
    // an unreadable ref degrades to a marker line.
    const lines: string[] = [];
    const sourceRefs = Array.isArray(inputs["sourceRefs"]) ? inputs["sourceRefs"] : [];
    for (const [index, ref] of sourceRefs.entries()) {
      const item = (ref ?? {}) as Record<string, unknown>;
      const mediaType = String(item["mediaType"] ?? "application/octet-stream");
      const artifactId = String(item["artifactId"] ?? "");
      try {
        const blob = await client.download(artifactId);
        const path = join(dir, "attachments", String(index + 1));
        writeFileSync(path, blob);
        lines.push(`attachment ${String(index + 1)}: ${mediaType} ${String(blob.byteLength)} ${path}`);
      } catch (error) {
        logger.warn("attachment-unreadable", { artifactId, error: String(error) });
        lines.push(`attachment ${String(index + 1)}: ${mediaType} unreadable (${artifactId})`);
      }
    }
    return lines.length === 0 ? "" : `## Attachments\n${lines.join("\n")}`;
  }

  async function onExecute(ctx: RunContext): Promise<RunOutcome> {
    const data = ctx.command.data as Record<string, unknown>;
    const workId = String(data["workId"] ?? ctx.executionId);
    const assignmentId = String(data["assignmentId"] ?? ctx.executionId);
    const capabilityId = String(data["capabilityId"] ?? "");
    const inputs = (data["inputs"] ?? {}) as Record<string, unknown>;
    const objective = typeof inputs["objective"] === "string" ? inputs["objective"] : workId;
    const capability = playbook.capabilities.get(capabilityId);
    if (capability === undefined) {
      return { kind: "failed", reason: `no playbook capability '${capabilityId}'` };
    }
    const dir = runDir(workDir, ctx.executionId);
    const attachments = await attachmentsSection(ctx, dir, inputs);

    // The ask leg: with config askUser 'mcp' and a capability declaring
    // where its MCP config lives, seed the shim so the hosted CLI can
    // ask_user unprompted; a shim tool call bridges into ctx.requestInput
    // on the LIVE run (publishes input.required).
    let bridge: Awaited<ReturnType<typeof startAskUserBridge>> | undefined;
    let shimCommand: string | undefined;
    if (askUser === "mcp" && capability.askUserInject !== undefined) {
      bridge = await startAskUserBridge(async ({ question, choices }) =>
        (await ctx.requestInput({ question, ...(choices !== undefined ? { choices } : {}) })).text,
      );
      writeMcpConfig(
        dir,
        shimEntry,
        bridge.address,
        capability.askUserInject.format,
        capability.askUserInject.file,
      );
      shimCommand = `${process.execPath} ${shimEntry}`;
    }

    const inputVars = (inputs["vars"] ?? {}) as Record<string, unknown>;
    const vars: Record<string, string> = { ...(capability.vars ?? {}) };
    for (const [key, value] of Object.entries(inputVars)) {
      if (typeof value === "string") vars[key] = value;
    }
    // The config-injected optional human leg: a playbook step guarded with
    // `when: askQuestion` runs exactly when the deployment sets the key.
    if (askQuestion !== undefined) vars["askQuestion"] = askQuestion;
    const base: Omit<InterpolationScope, "steps"> = {
      workId,
      assignmentId,
      objective,
      attachments,
      playbookDir: playbook.playbookDir,
      vars,
      ...(shimCommand !== undefined ? { shimCommand } : {}),
    };

    const outcome = await runCapability(
      capability,
      base,
      {
        publishProgress: (note) => ctx.publishProgress(note),
        requestInput: async (ask) =>
          (
            await ctx.requestInput({
              question: ask.question,
              ...(ask.choices !== undefined ? { choices: [...ask.choices] } : {}),
              ...(ask.contextRef !== undefined ? { contextRef: ask.contextRef } : {}),
            })
          ).text,
        signal: ctx.signal,
        logger,
        ...(bridge !== undefined ? { spawnEnv: { OFAC_MCP_BRIDGE: bridge.address } } : {}),
      },
      { workDir: dir, killGraceMs },
    );
    await bridge?.close();

    if (outcome.kind === "cancelled") {
      logger.info("worker-cancelled", { executionId: ctx.executionId });
      // The adapter DROPS cancelled runs without publishing (the stop reply
      // settled the row); the outcome kind only steers that law.
      return { kind: "completed" };
    }
    if (outcome.kind === "failed") {
      return { kind: "failed", reason: outcome.reason };
    }

    // The artifact leg: each capture ships as an artifact over the link
    // plane; the canonical Result Manifest declares them with the
    // kernel-verified digests from the upload receipts.
    const client = createArtifactClient({
      baseUrl: ctx.kernel.baseUrl,
      token: ctx.kernel.token,
      ...(options.fetchImpl !== undefined ? { fetchImpl: options.fetchImpl } : {}),
    });
    const createdAt = new Date().toISOString();
    const outputs: ResultManifestOutput[] = [];
    const structuredResult: Record<string, unknown> = { objectiveChars: objective.length };
    for (const step of outcome.steps) {
      if (step.capture === undefined) continue;
      const receipt = await client.upload(step.capture.bytes, {
        mediaType: step.capture.mediaType,
        workId,
        assignmentId,
        retentionPolicy: "30d",
      });
      outputs.push({
        artifactId: receipt.artifactId,
        mediaType: step.capture.mediaType,
        size: step.capture.bytes.length,
        digest: receipt.digest,
        artifactUri: artifactUri(workId, assignmentId, receipt.artifactId),
        createdBy: `workspace:${childWorkspaceId}`,
        createdAt,
        kind: capability.outputKinds[0] ?? "harness.capture",
        classification: "internal",
      });
      structuredResult[step.capture.as] = {
        artifactId: receipt.artifactId,
        size: step.capture.bytes.length,
      };
    }
    if (outputs.length === 0) {
      return {
        kind: "failed",
        reason: `step '${capability.steps[0]?.name ?? capability.id}': the run captured no output for the result manifest`,
      };
    }
    const firstTextCapture = outcome.steps
      .filter((step) => step.capture !== undefined)
      .map((step) => step.capture as { mediaType: string; bytes: Buffer })
      .find((capture) => capture.mediaType.startsWith("text/"));
    const summary =
      firstTextCapture?.bytes
        .toString("utf8")
        .split("\n")
        .find((line) => line.trim() !== "")
        ?.slice(0, 200) ?? "complete.";
    const manifest = buildResultManifest({
      manifestId: `man-${ctx.executionId}`,
      workId,
      assignmentId,
      capability: capabilityId,
      summary,
      structuredResult,
      outputs,
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
      outputs: outputs.length,
      manifestRef: artifactUri(workId, assignmentId, manifestDoc.artifactId),
    });
    return { kind: "completed" };
  }

  function onCancel(reason: string, ctx: RunContext): void {
    // The executor watches ctx.signal and group-kills the live step; the
    // run then resolves cancelled and publishes nothing.
    logger.info("worker-cancel-requested", { executionId: ctx.executionId, reason });
  }

  return { onExecute, onCancel };
}
