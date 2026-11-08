// The Codex harness hooks: one execution = a per-execution workspace
// (optionally a clone) + one fully autonomous
// `codex exec --json -s workspace-write -c approval_policy="never"` spawn
// whose stdout JSONL is the only truth (probe law: the `--ask-for-approval`
// flag does NOT exist in 0.166.0 exec; `-c approval_policy="never"` is the
// supported belt-and-braces spelling over exec's non-interactive default).
// Completion truth mirrors the deterministic worker: report bytes + raw
// transcript are UPLOADED to the kernel artifact store, an immutable
// canonical Result Manifest rides the completed event — no file:// URLs, no
// self-declared digests. Cancel law: SIGTERM the process group (SIGKILL
// after a grace), and an aborted run publishes NOTHING (the accepted stop
// reply already settled the Gateway row). Steering is NOT implemented: the
// SDK answers `unsupported` honestly, and a steer landing after the run
// settled never resurrects it (the SDK's settled-execution law — no
// `setSteered` is ever called here).
//
// Interactive law: with interactivity 'mcp' (default), the worker seeds
// $CODEX_HOME/config.toml with an [mcp_servers.ofac] entry launching the
// package MCP shim, which back-connects to an in-process bridge; the
// agent's ask_user tool call blocks in the shim, the bridge calls
// `ctx.requestInput` on the LIVE run (publishes input.required), and the
// tool resolves when the `respond` lands on the child wire.
import { spawn, type ChildProcess } from "node:child_process";
import { existsSync, mkdirSync, readFileSync, writeFileSync } from "node:fs";
import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";
import type { LinkLogger } from "@ofac/workspace-link";
import {
  artifactUri,
  buildCompletedEvent,
  buildResultManifest,
  createArtifactClient,
} from "@ofac/adapter";
import type { RunContext, RunOutcome } from "@ofac/adapter";
import { checkoutBranch, gitClone, injectToken } from "./git.js";
import { startAskUserBridge, writeCodexConfig, type AskUserBridge } from "./mcp/bridge.js";
import { FileSessionStore } from "./sessions.js";
import type { InteractivityMode } from "./config.js";

export interface CodexRunOptions {
  childWorkspaceId: string;
  workDir: string;
  /** Codex CLI binary; default env OFAC_CODEX_BIN else `codex`. */
  codexBin?: string | undefined;
  model?: string | undefined;
  /** Verbatim argv tail before the prompt — the agentic-flag override seam. */
  extraArgs?: readonly string[] | undefined;
  /** 'mcp' (default): inject the ask_user MCP server. 'off': no injection. */
  interactivity?: InteractivityMode | undefined;
  /** '{objective}' placeholder; absent ⇒ the verbatim assign objective. */
  promptTemplate?: string | undefined;
  /** Optional fixed question asked (via the live run) before the spawn. */
  askQuestion?: string | undefined;
  /** MCP shim entry (dist/bin/mcp-shim.js); tests point at the .ts source. */
  mcpShimEntry?: string | undefined;
  logger: LinkLogger;
  /** Injectable fetch for the artifact upload path (tests); default global. */
  fetchImpl?: typeof fetch;
  /** Env base for the codex spawn + clone inputs; default process.env. */
  env?: Readonly<Record<string, string | undefined>> | undefined;
  /**
   * Progress-note throttle window in ms; default PROGRESS_THROTTLE_MS.
   * 0 disables coalescing (every note publishes at once) — the deterministic
   * seam tests use so note law never rides wall-clock speed.
   */
  progressThrottleMs?: number | undefined;
}

export interface CodexHooks {
  onExecute(ctx: RunContext): Promise<RunOutcome>;
  onCancel(reason: string, ctx: RunContext): void;
  /** The resume ledger (also the SDK's optional persistence seam). */
  sessionStore: FileSessionStore;
}

/** Default shim entry: sibling of this module in the built bundle. */
function defaultShimEntry(): string {
  return join(dirname(fileURLToPath(import.meta.url)), "bin", "mcp-shim.js");
}

/** The kill grace between SIGTERM (whole group) and SIGKILL. */
const KILL_GRACE_MS = 5_000;
/** Progress-note throttle window for item/turn event noise. */
const PROGRESS_THROTTLE_MS = 2_000;
/** The frozen autonomous combo (probe law — see the header). */
const AUTONOMY_ARGS: readonly string[] = [
  "--json",
  "-s",
  "workspace-write",
  "-c",
  'approval_policy="never"',
];

/** One parsed JSONL frame from codex's stdout stream (probe vocabulary). */
interface CodexFrame {
  readonly type?: unknown;
  readonly thread_id?: unknown;
  readonly message?: unknown;
  readonly item?: unknown;
}

interface CodexItemFields {
  readonly itemType: string;
  readonly text: string | undefined;
  readonly command: string | undefined;
}

function itemFields(item: unknown): CodexItemFields | null {
  if (typeof item !== "object" || item === null) return null;
  const record: Record<string, unknown> = item as Record<string, unknown>; // parsed JSON object, fields narrowed below
  const itemType = record["item_type"];
  if (typeof itemType !== "string") return null;
  const text = record["text"];
  const command = record["command"];
  return {
    itemType,
    text: typeof text === "string" ? text : undefined,
    command: typeof command === "string" ? command : undefined,
  };
}

function killGroup(child: ChildProcess): void {
  const pid = child.pid;
  if (pid === undefined) return; // never booted
  const signalGroup = (signal: NodeJS.Signals): boolean => {
    try {
      process.kill(-pid, signal);
      return true;
    } catch {
      return false; // group already gone
    }
  };
  if (!signalGroup("SIGTERM")) return;
  const timer = setTimeout(() => {
    signalGroup("SIGKILL");
  }, KILL_GRACE_MS);
  timer.unref();
  child.once("close", () => clearTimeout(timer));
}

function byteTail(text: string, maxBytes: number): string {
  const bytes = Buffer.from(text, "utf8");
  if (bytes.length <= maxBytes) return text;
  return bytes.subarray(bytes.length - maxBytes).toString("utf8");
}

export function createCodexHooks(options: CodexRunOptions): CodexHooks {
  const { childWorkspaceId, workDir, logger } = options;
  const interactivity: InteractivityMode = options.interactivity ?? "mcp";
  const store = new FileSessionStore(join(workDir, "sessions.json"));
  /**
   * The CODEX_HOME config.toml is worker-global: the next run may re-seed
   * it only after this run's codex has booted (first stdout byte) or died.
   * Serialized runs (the realistic single-worker cadence) never race.
   */
  let spawnGate: Promise<void> = Promise.resolve();

  async function onExecute(ctx: RunContext): Promise<RunOutcome> {
    const data: Record<string, unknown> = ctx.command.data ?? {};
    const workId = typeof data["workId"] === "string" ? data["workId"] : ctx.executionId;
    const assignmentId =
      typeof data["assignmentId"] === "string" ? data["assignmentId"] : ctx.executionId;
    const capabilityId =
      typeof data["capabilityId"] === "string" ? data["capabilityId"] : "coding.codex";
    const rawInputs = data["inputs"];
    const inputs: Record<string, unknown> =
      typeof rawInputs === "object" && rawInputs !== null
        ? (rawInputs as Record<string, unknown>)
        : {};
    const objective = typeof inputs["objective"] === "string" ? inputs["objective"] : workId;
    const env = options.env ?? process.env;
    const bin = env["OFAC_CODEX_BIN"] ?? options.codexBin ?? "codex";

    const execDir = join(workDir, ctx.executionId);
    const repoDir = join(execDir, "repo");
    const codexHome = join(workDir, "codex-home");
    const finalPath = join(execDir, "final.md");
    const transcriptPath = join(execDir, "transcript.jsonl");
    mkdirSync(repoDir, { recursive: true, mode: 0o700 });
    mkdirSync(codexHome, { recursive: true, mode: 0o700 });

    ctx.publishProgress(`accepted: ${capabilityId} via codex exec for ${workId}`);

    // Workspace law: clone per execution when a clone URL is configured
    // (PAT read at every run, argv-only, error text scrubbed). A plain
    // directory is honest too: codex then needs --skip-git-repo-check.
    let skipGitCheck = false;
    const cloneUrl = env["OFAC_GIT_CLONE_URL"];
    if (cloneUrl !== undefined && cloneUrl.length > 0) {
      const pat = env["OFAC_GIT_TOKEN"] ?? "";
      const scrub = pat.length > 0 ? [pat] : [];
      const url = pat.length > 0 ? injectToken(cloneUrl, "facilitator-bot", pat) : cloneUrl;
      await gitClone(url, repoDir, scrub);
      const branch = env["OFAC_GIT_BRANCH"];
      if (branch !== undefined && branch.length > 0) await checkoutBranch(repoDir, branch);
      ctx.publishProgress("cloned workspace");
    } else {
      skipGitCheck = true;
    }

    // The interactive seam the conformance kit drives: a fixed question
    // asked through the LIVE run (an MCP tool call reaches the same path).
    let humanAnswer: string | undefined;
    if (options.askQuestion !== undefined) {
      humanAnswer = (await ctx.requestInput({ question: options.askQuestion })).text;
      if (ctx.signal.aborted) return { kind: "completed" };
    }

    const prompt =
      options.promptTemplate !== undefined
        ? options.promptTemplate.replaceAll("{objective}", objective)
        : objective;

    // Resume law: a re-dispatched assignment (later generation, or a
    // re-delivery of the same one after a crash) continues its native
    // thread — never a cold restart.
    const prior = store.findLatestForAssignment(assignmentId);
    const resuming = prior !== undefined && prior.generation <= ctx.executionGeneration;

    let bridge: AskUserBridge | null = null;
    if (interactivity === "mcp") {
      bridge = await startAskUserBridge(async (params) => {
        if (ctx.signal.aborted) throw new Error("run no longer live");
        const response = await ctx.requestInput({
          question: params.question,
          ...(params.choices !== undefined ? { choices: params.choices } : {}),
        });
        return response.text;
      }, logger);
    }

    const argv = ["exec"];
    if (resuming && prior !== undefined) argv.push("resume", prior.threadId);
    argv.push(...AUTONOMY_ARGS);
    if (skipGitCheck) argv.push("--skip-git-repo-check");
    argv.push("-C", repoDir, "-o", finalPath);
    if (options.model !== undefined) argv.push("--model", options.model);
    if (bridge !== null) {
      writeCodexConfig(codexHome, options.mcpShimEntry ?? defaultShimEntry(), bridge.address);
    }
    argv.push(...(options.extraArgs ?? []), prompt);

    // Serialized spawn: the config file this run seeded must survive
    // unread-touching until this codex has booted.
    if (ctx.signal.aborted) return { kind: "completed" }; // cancelled while queued
    await spawnGate;
    const { promise: gateRelease, resolve: releaseGate } = Promise.withResolvers<void>();
    spawnGate = gateRelease;
    const child = spawn(bin, argv, {
      cwd: workDir,
      env: {
        ...Object.fromEntries(
          Object.entries(env).filter((entry): entry is [string, string] => entry[1] !== undefined),
        ),
        CODEX_HOME: codexHome,
      },
      stdio: ["ignore", "pipe", "pipe"],
      // Own process group: cancel SIGTERMs the GROUP — codex may fork
      // helpers, and they must die with it.
      detached: true,
    });
    child.once("error", () => undefined); // surfaced via the exit promise below
    child.stdout.once("data", releaseGate);
    child.once("close", releaseGate);

    const transcript: Buffer[] = [];
    let stderrText = "";
    let finalAnswer: string | undefined;
    let lastProgressAt = 0;
    let pendingNote: string | null = null;
    const throttleMs = options.progressThrottleMs ?? PROGRESS_THROTTLE_MS;
    const note = (text: string): void => {
      if (Date.now() - lastProgressAt >= throttleMs) {
        if (pendingNote !== null) {
          ctx.publishProgress(pendingNote);
          pendingNote = null;
        }
        ctx.publishProgress(text);
        lastProgressAt = Date.now();
      } else {
        pendingNote = text; // newest wins; flushed at settle
      }
    };

    const abortKill = (): void => {
      killGroup(child);
    };
    ctx.signal.addEventListener("abort", abortKill, { once: true });

    let buffer = "";
    child.stdout.on("data", (chunk: Buffer) => {
      transcript.push(chunk);
      buffer += chunk.toString("utf8");
      for (;;) {
        const nl = buffer.indexOf("\n");
        if (nl < 0) break;
        const line = buffer.slice(0, nl).trim();
        buffer = buffer.slice(nl + 1);
        if (line.length === 0) continue;
        let frame: CodexFrame;
        try {
          frame = JSON.parse(line) as CodexFrame;
        } catch {
          logger.debug("codex-nonjson-line", { executionId: ctx.executionId });
          continue; // non-JSON noise on stdout: ignored, never a crash
        }
        const type = typeof frame.type === "string" ? frame.type : "";
        if (type === "thread.started") {
          const threadId = typeof frame.thread_id === "string" ? frame.thread_id : undefined;
          if (threadId !== undefined && threadId.length > 0) {
            // The persistence seam: composite executionId:generation row +
            // the assignment pointer the resume lookup reads.
            ctx.sessionRef = threadId;
            store.save(`${ctx.executionId}:${ctx.executionGeneration}`, threadId, {
              assignmentId,
              workId,
              generation: ctx.executionGeneration,
            });
            note(`codex session ${threadId}`);
          }
        } else if (type === "turn.completed") {
          note("codex: turn complete");
        } else if (type === "error") {
          // Transient/provider noise (incl. reconnect notices): never
          // terminal by itself — only a nonzero exit kills the run.
          logger.warn("codex-stream-error-frame", {
            executionId: ctx.executionId,
            message: typeof frame.message === "string" ? frame.message : "",
          });
        } else if (type === "item.started" || type === "item.updated") {
          const item = itemFields(frame.item);
          if (item?.itemType === "command_execution" && item.command !== undefined) {
            note(`codex: running ${item.command.slice(0, 80)}`);
          }
        } else if (type === "item.completed") {
          const item = itemFields(frame.item);
          if (item?.itemType === "agent_message" && item.text !== undefined) {
            finalAnswer = item.text; // last one wins (probe law)
            note("codex: agent message");
          } else if (item !== null && item.itemType !== "agent_message") {
            note(`codex: ${item.itemType}`);
          }
        }
        // unknown event types: ignored, never a crash
      }
    });
    child.stderr.on("data", (chunk: Buffer) => {
      stderrText += chunk.toString("utf8");
    });

    const exit = await new Promise<{ code: number | null; error: Error | null }>((resolve) => {
      child.once("error", (error) => resolve({ code: null, error }));
      child.once("close", (code) => resolve({ code, error: null }));
    });
    ctx.signal.removeEventListener("abort", abortKill);
    if (pendingNote !== null) {
      ctx.publishProgress(pendingNote);
      pendingNote = null;
    }
    writeFileSync(transcriptPath, Buffer.concat(transcript));
    await bridge?.close().catch(() => undefined);

    if (ctx.signal.aborted) {
      // Cancelled: the accepted stop reply already settled the Gateway
      // row — NO terminal event, honest drop.
      logger.info("codex-cancelled", { executionId: ctx.executionId });
      return { kind: "completed" };
    }
    if (exit.error !== null || exit.code !== 0) {
      const detail =
        exit.error !== null ? `${exit.error.message}; stderr: ${stderrText}` : stderrText;
      return {
        kind: "failed",
        reason: `codex exit ${String(exit.code)}: ${byteTail(detail.trim(), 2048)}`,
      };
    }

    // The answer carrier: the last agent_message item; the -o file is the
    // documented fallback carrier (probe law).
    const answer =
      finalAnswer ??
      (existsSync(finalPath) ? readFileSync(finalPath, "utf8") : "(codex completed silently)");
    return await publish(ctx, {
      workId,
      assignmentId,
      capabilityId,
      objective,
      answer,
      humanAnswer,
      finalPath,
      transcript,
      resumed: resuming,
      bin,
    });
  }

  async function publish(
    ctx: RunContext,
    facts: {
      workId: string;
      assignmentId: string;
      capabilityId: string;
      objective: string;
      answer: string;
      humanAnswer: string | undefined;
      finalPath: string;
      transcript: readonly Buffer[];
      resumed: boolean;
      bin: string;
    },
  ): Promise<RunOutcome> {
    // Two outputs, per the publication law: the final-answer document
    // (the -o carrier, normalized to disk, with the human's answer
    // appended when the run asked one) and the raw stdout transcript.
    const answerLine =
      facts.humanAnswer === undefined ? "" : `\n- input answered: ${facts.humanAnswer}`;
    const finalDoc = `${facts.answer}${answerLine}\n`;
    mkdirSync(dirname(facts.finalPath), { recursive: true, mode: 0o700 });
    writeFileSync(facts.finalPath, finalDoc);
    const finalBytes = Buffer.from(finalDoc, "utf8");
    const transcriptBytes = Buffer.concat(
      facts.transcript.length > 0 ? facts.transcript : [Buffer.alloc(0)],
    );
    const firstLine = facts.answer.trim().split("\n")[0] ?? "complete.";
    const client = createArtifactClient({
      baseUrl: ctx.kernel.baseUrl,
      token: ctx.kernel.token,
      ...(options.fetchImpl !== undefined ? { fetchImpl: options.fetchImpl } : {}),
    });
    const createdAt = new Date().toISOString();
    // The answer's own bytes land first; the manifest declares them with the
    // kernel-verified digest from the upload receipt.
    const finalArtifact = await client.upload(finalBytes, {
      mediaType: "text/markdown",
      workId: facts.workId,
      assignmentId: facts.assignmentId,
      retentionPolicy: "30d",
    });
    const transcriptArtifact = await client.upload(transcriptBytes, {
      mediaType: "application/x-ndjson",
      workId: facts.workId,
      assignmentId: facts.assignmentId,
      retentionPolicy: "30d",
    });
    const manifest = buildResultManifest({
      manifestId: `man-${ctx.executionId}`,
      workId: facts.workId,
      assignmentId: facts.assignmentId,
      capability: facts.capabilityId,
      summary: firstLine,
      structuredResult: {
        objectiveChars: facts.objective.length,
        resumed: facts.resumed,
        harness: facts.bin,
      },
      outputs: [
        {
          artifactId: finalArtifact.artifactId,
          mediaType: "text/markdown",
          size: finalBytes.length,
          digest: finalArtifact.digest,
          artifactUri: artifactUri(facts.workId, facts.assignmentId, finalArtifact.artifactId),
          createdBy: `workspace:${childWorkspaceId}`,
          createdAt,
          kind: "coding.final",
          classification: "internal",
        },
        {
          artifactId: transcriptArtifact.artifactId,
          mediaType: "application/x-ndjson",
          size: transcriptBytes.length,
          digest: transcriptArtifact.digest,
          artifactUri: artifactUri(facts.workId, facts.assignmentId, transcriptArtifact.artifactId),
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
      workId: facts.workId,
      assignmentId: facts.assignmentId,
      retentionPolicy: "30d",
    });
    ctx.publishEvent(
      buildCompletedEvent(
        ctx,
        {
          manifestId: `man-${ctx.executionId}`,
          workId: facts.workId,
          assignmentId: facts.assignmentId,
        },
        {
          manifestRef: artifactUri(facts.workId, facts.assignmentId, manifestDoc.artifactId),
          digest: manifest.digest,
        },
      ),
    );
    logger.info("codex-completed", {
      executionId: ctx.executionId,
      finalPath: facts.finalPath,
      manifestRef: artifactUri(facts.workId, facts.assignmentId, manifestDoc.artifactId),
    });
    return { kind: "completed" };
  }

  function onCancel(reason: string, ctx: RunContext): void {
    logger.info("codex-cancel", { executionId: ctx.executionId, reason });
    // Cooperative: onExecute's abort listener SIGTERMs the process group;
    // the SDK law (no terminal event on cancel) does the rest.
  }

  return { onExecute, onCancel, sessionStore: store };
}
