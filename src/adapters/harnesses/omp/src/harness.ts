// The omp harness: per execution, prepare the workspace (clone when
// OFAC_GIT_CLONE_URL is set), spawn `omp --mode rpc --no-ui --auto-approve`
// (agentic mode FORCED: fully autonomous — the law is in the README;
// override via worker.yaml extraArgs), speak the NDJSON rpc loop (probe
// normative: ready frame → prompt → success:true accept → event stream →
// prompt_result), and publish the completion over the kernel artifact path
// exactly where the deterministic worker does: transcript + final.md
// UPLOADED, canonical Result Manifest, completed event with ref + digest.
//
// Interactivity: when interactivity is "mcp", an ask_user MCP server (the
// package shim, bridge-backed) is injected via the omp-auto-loaded
// `.mcp.json` plus a forced `--append-system-prompt`; the tool call blocks
// in ctx.requestInput until the human's answer rides back on the live run.
//
// Cancel law: a stop aborts the prompt and kills the CLI on a grace; the
// SDK publishes NOTHING terminal (the accepted stop reply settled the row).
// Crash law: a nonzero exit before completion ⇒ {kind:"failed"} — the SDK
// publishes the retryable failed.v1 (the error leg to humans).
//
// Resume law: the native session id (get_state → data.sessionId) persists
// in the FileSessionStore keyed `<executionId>:<executionGeneration>` and
// mirrored under `assign:<assignmentId>`; a retry generation relaunches
// with `--resume <sessionId>` and a follow-up prompt instead of a fresh one.
import { existsSync, mkdirSync, writeFileSync } from "node:fs";
import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";
import type { LinkLogger } from "@ofac/workspace-link";
import {
  artifactUri,
  buildCompletedEvent,
  buildResultManifest,
  createArtifactClient,
} from "@ofac/adapter";
import type { RunContext, RunOutcome, SessionStore } from "@ofac/adapter";
import { checkoutBranch, gitClone, injectToken } from "./git.js";
import { startAskUserBridge, writeMcpConfig } from "./mcp/bridge.js";
import { OmpRpcSession } from "./rpc.js";
import type { AskUserBridge } from "./mcp/bridge.js";

/** The frozen injection literal (plan-normative): exact text, every run. */
export const SYSTEM_PROMPT_INJECTION =
  "You are running headless under the Open Facilitator. When blocked on a decision only a human can make, call the ask_user tool and wait for the answer. Do not stop to ask in plain text.";

/** argv override for the harness CLI: default `omp`; tests point it at a fake. */
export interface OmpInvocation {
  readonly command: string;
  readonly prefixArgs?: readonly string[] | undefined;
  /** Extra env merged over the worker's own (fake-CLI scenario switches). */
  readonly env?: Readonly<Record<string, string | undefined>> | undefined;
}

export interface OmpRunOptions {
  childWorkspaceId: string;
  workDir: string;
  /** Settle grace after prompt_result before the outcome is declared (ms). */
  stepMs: number;
  model?: string | undefined;
  extraArgs: readonly string[];
  interactivity: "mcp" | "off";
  promptTemplate?: string | undefined;
  /** Optional fixed question asked (via the live run) before the spawn. */
  askQuestion?: string | undefined;
  invocation?: OmpInvocation | undefined;
  sessionStore: SessionStore;
  logger: LinkLogger;
  /** MCP shim entry (dist/bin/mcp-shim.js); tests override with a stub. */
  mcpShimEntry?: string | undefined;
  /** Clone inputs (env-resolved by the bin); absent ⇒ bare work dirs. */
  clone?:
    | {
        readonly url: string;
        readonly branch?: string | undefined;
        readonly token?: string | undefined;
      }
    | undefined;
  /** Injectable fetch for the artifact upload path (tests); default global. */
  fetchImpl?: typeof fetch;
}

export interface OmpHooks {
  onExecute(ctx: RunContext): Promise<RunOutcome>;
  onSteer(directive: string, ctx: RunContext): void;
  onCancel(reason: string, ctx: RunContext): void;
}

/**
 * Default shim entry: the built `dist/bin/mcp-shim.js`. Resolves relative to
 * THIS module at runtime, which differs by entry (the worker bundle lives in
 * dist/bin, the library bundle in dist): the sibling first, then the bin
 * subdirectory.
 */
function defaultShimEntry(): string {
  const here = dirname(fileURLToPath(import.meta.url));
  const siblings = [join(here, "mcp-shim.js"), join(here, "bin", "mcp-shim.js")];
  return siblings.find((candidate) => existsSync(candidate)) ?? siblings[0]!;
}

interface Frame {
  readonly [key: string]: unknown;
}

interface EventTally {
  finalText: string;
  resultStatus: string | null;
  turns: number;
  tools: number;
}

function textBlocks(content: unknown): string {
  if (!Array.isArray(content)) return "";
  return content
    .filter(
      (block): block is Frame =>
        typeof block === "object" && block !== null && !Array.isArray(block),
    )
    .filter((block) => block["type"] === "text" && typeof block["text"] === "string")
    .map((block) => block["text"] as string)
    .join("");
}

function toolNames(content: unknown): string[] {
  if (!Array.isArray(content)) return [];
  const names: string[] = [];
  for (const block of content) {
    if (typeof block !== "object" || block === null) continue;
    const record = block as Record<string, unknown>;
    if (record["type"] === "toolCall" && typeof record["name"] === "string") {
      names.push(record["name"]);
    }
  }
  return names;
}

export function createOmpHooks(options: OmpRunOptions): OmpHooks {
  const { childWorkspaceId, workDir, logger } = options;
  const live = new Map<string, OmpRpcSession>();
  // Directives accepted before a live session exists (gate/clone/ask
  // window) buffer here and flush the moment the spawn answers.
  const pendingSteers = new Map<string, string[]>();

  async function onExecute(ctx: RunContext): Promise<RunOutcome> {
    const data = ctx.command.data as Record<string, unknown>;
    const workId = String(data["workId"] ?? ctx.executionId);
    const assignmentId = String(data["assignmentId"] ?? ctx.executionId);
    const inputs = data["inputs"] as Record<string, unknown> | undefined;
    const objective = typeof inputs?.["objective"] === "string" ? inputs["objective"] : workId;
    const capabilityId = String(data["capabilityId"] ?? "coding.omp");
    let prompt =
      options.promptTemplate !== undefined
        ? options.promptTemplate.replaceAll("{objective}", objective)
        : objective;
    // Inbound media law: assign `inputs.sourceRefs` are kernel artifacts (a
    // chat image the communication adapter uploaded). v1 keeps the accepted
    // behavior of handing the agent a fetchable human-plane URL per
    // attachment and staging the bytes into the omp workspace — the omp CLI
    // has no image-path argument today, so the file + the prompt line is the
    // contract (see README).
    const attachmentInputs = Array.isArray(inputs?.["sourceRefs"]) ? inputs["sourceRefs"] : [];
    if (attachmentInputs.length > 0) {
      const mediaClient = createArtifactClient({
        baseUrl: ctx.kernel.baseUrl,
        token: ctx.kernel.token,
      });
      const attachmentDir = join(workDir, ctx.executionId, "attachments");
      mkdirSync(attachmentDir, { recursive: true, mode: 0o700 });
      for (const [index, ref] of attachmentInputs.entries()) {
        const item = (ref ?? {}) as Record<string, unknown>;
        const mediaType = String(item["mediaType"] ?? "application/octet-stream");
        const size = Number(item["size"] ?? 0);
        const artifactId = String(item["artifactId"] ?? "");
        try {
          const blob = await mediaClient.download(artifactId);
          writeFileSync(join(attachmentDir, String(index)), blob, { mode: 0o600 });
          prompt += `\nImage attachment: ${ctx.kernel.baseUrl}/api/artifacts/${artifactId}/content (mediaType ${mediaType}, ${String(size)} bytes, staged at attachments/${String(index)})`;
        } catch (error) {
          logger.warn("attachment-unreadable", { artifactId, error: String(error) });
        }
      }
    }
    ctx.publishProgress(`accepted: omp ${capabilityId} for ${workId}`);
    if (ctx.signal.aborted) return { kind: "completed" };

    // The optional fixed question rides the LIVE run (the interactive leg
    // before the native one even starts).
    if (options.askQuestion !== undefined) {
      await ctx.requestInput({ question: options.askQuestion });
      if (ctx.signal.aborted) return { kind: "completed" };
    }

    const execDir = join(workDir, ctx.executionId);
    const repoDir = join(execDir, "repo");
    const sessionDir = join(execDir, "sessions");
    mkdirSync(sessionDir, { recursive: true, mode: 0o700 });
    const scrub: string[] = [];
    if (!existsSync(repoDir)) {
      mkdirSync(repoDir, { recursive: true, mode: 0o700 });
      if (options.clone !== undefined) {
        const pat = options.clone.token ?? "";
        if (pat !== "") scrub.push(pat);
        const user = "facilitator-bot";
        await gitClone(injectToken(options.clone.url, user, pat), repoDir, scrub);
        if (options.clone.branch !== undefined) await checkoutBranch(repoDir, options.clone.branch);
        ctx.publishProgress("cloned workspace repo");
      }
    }
    if (ctx.signal.aborted) return { kind: "completed" };

    // The resume law: a retry generation continues the native session the
    // first generation recorded (mirror key: assign:<assignmentId>).
    const resumeSession =
      ctx.executionGeneration > 1
        ? await Promise.resolve(options.sessionStore.get(`assign:${assignmentId}`))
        : undefined;

    // The ask_user leg: in-process bridge + the omp-auto-loaded .mcp.json.
    let bridge: AskUserBridge | undefined;
    const args = [
      "--mode",
      "rpc",
      "--no-ui",
      "--auto-approve",
      "--cwd",
      repoDir,
      "--session-dir",
      sessionDir,
    ];
    if (options.interactivity === "mcp") {
      bridge = await startAskUserBridge(async (params) => {
        const { text } = await ctx.requestInput({
          question: params.question,
          ...(params.choices !== undefined ? { choices: params.choices } : {}),
        });
        return text;
      });
      writeMcpConfig(repoDir, options.mcpShimEntry ?? defaultShimEntry(), bridge.address);
      args.push("--append-system-prompt", SYSTEM_PROMPT_INJECTION);
    }
    if (options.model !== undefined) args.push("--model", options.model);
    if (resumeSession !== undefined) args.push("--resume", resumeSession);
    args.push(...options.extraArgs);

    const tally: EventTally = { finalText: "", resultStatus: null, turns: 0, tools: 0 };
    const lastToolAt = new Map<string, number>();
    const onFrame = (frame: Frame): void => {
      switch (frame["type"]) {
        case "message_end": {
          if (
            frame["role"] === "assistant" ||
            (frame["message"] as Frame | undefined)?.["role"] === "assistant"
          ) {
            const message = (frame["message"] as Frame | undefined) ?? frame;
            const content = message["content"];
            const text = textBlocks(content);
            if (text !== "") tally.finalText = text;
            for (const name of toolNames(content)) {
              if (name === "ask_user") continue;
              tally.tools += 1;
              const now = Date.now();
              if (now - (lastToolAt.get(name) ?? 0) < 2_000) continue; // throttle
              lastToolAt.set(name, now);
              ctx.publishProgress(`tool: ${name}`);
            }
          }
          return;
        }
        case "turn_end":
        case "agent_end": {
          tally.turns += 1;
          ctx.publishProgress("turn complete");
          return;
        }
        case "prompt_result": {
          tally.resultStatus = typeof frame["status"] === "string" ? frame["status"] : "unknown";
          return;
        }
        default:
          return; // every other frame (updates, noise) is transcript-only
      }
    };

    const invocation = options.invocation ?? { command: "omp" };
    const session = OmpRpcSession.start({
      bin: invocation.command,
      args: [...(invocation.prefixArgs ?? []), ...args],
      cwd: execDir,
      ...(invocation.env !== undefined ? { env: invocation.env } : {}),
      onFrame,
    });
    live.set(ctx.executionId, session);
    for (const directive of pendingSteers.get(ctx.executionId) ?? []) {
      session.steer(directive);
    }
    pendingSteers.delete(ctx.executionId);
    const detach = (): void => {
      live.delete(ctx.executionId);
      pendingSteers.delete(ctx.executionId);
      void bridge?.close();
      // One prompt per spawn: whatever the outcome, the CLI is done —
      // cooperatively first (stdin close), SIGTERM on a short grace.
      session.closeStdin();
      const timer = setTimeout(() => session.kill("SIGTERM"), 2_000);
      timer.unref?.();
    };

    try {
      await session.waitReady(15_000);
      await session.prompt(prompt);
      // Persist the native session id as soon as the run is live.
      const sessionId = await session.getStateSessionId();
      if (sessionId !== undefined) {
        ctx.sessionRef = sessionId; // registry mirror
        await options.sessionStore.set(`${ctx.executionId}:${ctx.executionGeneration}`, sessionId);
        await options.sessionStore.set(`assign:${assignmentId}`, sessionId);
      }
      const outcome = await settle(session, ctx, tally);
      if (outcome === "cancelled") return { kind: "completed" };
      if (outcome === "crashed") {
        const exit = await session.waitExit().catch(() => null);
        return {
          kind: "failed",
          reason: `omp exit ${String(exit?.code ?? exit?.signal ?? "?")}: ${(exit?.stderrTail ?? "").trim() || "no stderr"}`,
        };
      }
      if (tally.resultStatus !== "completed") {
        return {
          kind: "failed",
          reason: `omp prompt_result status '${tally.resultStatus ?? "none"}'`,
        };
      }
      return await publishCompletion(ctx, workId, assignmentId, capabilityId, tally, session);
    } finally {
      detach();
    }
  }

  /** Resolves when the prompt settles, the child dies, or the run aborts. */
  async function settle(
    session: OmpRpcSession,
    ctx: RunContext,
    tally: EventTally,
  ): Promise<"completed" | "cancelled" | "crashed"> {
    const { promise, resolve } = Promise.withResolvers<"completed" | "cancelled" | "crashed">();
    const finish = (value: "completed" | "cancelled" | "crashed"): void => resolve(value);
    const onAbort = (): void => finish("cancelled");
    if (ctx.signal.aborted) return Promise.resolve("cancelled");
    ctx.signal.addEventListener("abort", onAbort, { once: true });
    void session
      .waitExit()
      .then(() => finish(tally.resultStatus !== null ? "completed" : "crashed"))
      .catch(() => finish("crashed"));
    const watcher = setInterval(() => {
      if (tally.resultStatus === "completed") {
        clearInterval(watcher);
        ctx.signal.removeEventListener("abort", onAbort);
        session.closeStdin();
        finish("completed");
      }
    }, 25);
    watcher.unref?.();
    return promise;
  }

  async function publishCompletion(
    ctx: RunContext,
    workId: string,
    assignmentId: string,
    capabilityId: string,
    tally: EventTally,
    session: OmpRpcSession,
  ): Promise<RunOutcome> {
    const finalMd = `# omp run ${workId}\n${tally.finalText}\n`;
    const transcript = `${session.transcript.join("\n")}\n`;
    const client = createArtifactClient({
      baseUrl: ctx.kernel.baseUrl,
      token: ctx.kernel.token,
      ...(options.fetchImpl !== undefined ? { fetchImpl: options.fetchImpl } : {}),
    });
    const createdAt = new Date().toISOString();
    const finalArtifact = await client.upload(Buffer.from(finalMd, "utf8"), {
      mediaType: "text/markdown",
      workId,
      assignmentId,
      retentionPolicy: "30d",
    });
    const transcriptArtifact = await client.upload(Buffer.from(transcript, "utf8"), {
      mediaType: "application/x-ndjson",
      workId,
      assignmentId,
      retentionPolicy: "30d",
    });
    const summary = tally.finalText.split("\n")[0]?.trim().slice(0, 200) ?? "omp run complete.";
    const manifest = buildResultManifest({
      manifestId: `man-${ctx.executionId}`,
      workId,
      assignmentId,
      capability: capabilityId,
      summary,
      structuredResult: {
        turns: tally.turns,
        toolCalls: tally.tools,
        finalChars: tally.finalText.length,
      },
      outputs: [
        {
          artifactId: finalArtifact.artifactId,
          mediaType: "text/markdown",
          size: Buffer.byteLength(finalMd),
          digest: finalArtifact.digest,
          artifactUri: artifactUri(workId, assignmentId, finalArtifact.artifactId),
          createdBy: `workspace:${childWorkspaceId}`,
          createdAt,
          kind: "analysis.report",
          classification: "internal",
        },
        {
          artifactId: transcriptArtifact.artifactId,
          mediaType: "application/x-ndjson",
          size: Buffer.byteLength(transcript),
          digest: transcriptArtifact.digest,
          artifactUri: artifactUri(workId, assignmentId, transcriptArtifact.artifactId),
          createdBy: `workspace:${childWorkspaceId}`,
          createdAt,
          kind: "harness.transcript",
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
    logger.info("omp-worker-completed", {
      executionId: ctx.executionId,
      manifestRef: artifactUri(workId, assignmentId, manifestDoc.artifactId),
    });
    return { kind: "completed" };
  }

  function onCancel(reason: string, ctx: RunContext): void {
    logger.info("omp-worker-cancelled", { executionId: ctx.executionId, reason });
    const session = live.get(ctx.executionId);
    if (session === undefined) return;
    session.abort(); // cooperative first…
    const timer = setTimeout(() => session.kill("SIGTERM"), 5_000); // …SIGTERM on a grace
    timer.unref?.();
  }

  // Steer law: omp supports live steering; the rpc client forwards the
  // directive to the live prompt while the run is alive.
  function onSteer(directive: string, ctx: RunContext): void {
    const session = live.get(ctx.executionId);
    if (session === undefined) {
      const held = pendingSteers.get(ctx.executionId) ?? [];
      held.push(directive);
      pendingSteers.set(ctx.executionId, held);
    } else {
      session.steer(directive);
    }
    logger.info("omp-worker-steered", { executionId: ctx.executionId });
  }

  return { onExecute, onSteer, onCancel };
}
