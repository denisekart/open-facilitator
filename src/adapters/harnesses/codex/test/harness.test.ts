import { createHash } from "node:crypto";
import { existsSync, mkdtempSync, readFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { computeManifestDigest, validateAgainstSchemaId } from "@ofac/protocol";
import type { EnvelopeCommand, EnvelopeEvent } from "@ofac/protocol";
import { createMemoryLogger } from "@ofac/workspace-link";
import { startStubKernel, type StubKernel } from "@ofac/adapter/testing";
import { afterAll, beforeAll, describe, expect, it } from "vitest";
import type { RunContext } from "@ofac/adapter";
import { createCodexHooks, type CodexHooks } from "../src/harness.js";

const FAKE = new URL("./fake-codex.mjs", import.meta.url).pathname;
const SHIM = new URL("../src/bin/mcp-shim.ts", import.meta.url).pathname;

/** Awaits a condition the run itself produces (no guessed durations). */
async function waitFor(what: string, probe: () => boolean): Promise<void> {
  for (let attempt = 0; attempt < 200; attempt += 1) {
    if (probe()) return;
    await new Promise((resolve) => setTimeout(resolve, 25));
  }
  throw new Error(`timed out waiting for ${what}`);
}

function assignCommand(
  workId: string,
  assignmentId: string,
  objective: string,
  generation: number,
): EnvelopeCommand {
  return {
    specversion: "1.0",
    id: `env-${workId}`,
    source: "urn:facilitator:instance:t",
    type: "org.facilitator.command.work.assign.v1",
    subject: `work/${workId}/assignment/${assignmentId}`,
    time: "2036-10-23T18:00:00Z",
    datacontenttype: "application/json",
    dataschema: "https://schemas.openfacilitator.org/v1/command.work.assign.v1.json",
    traceparent: "00-4bf92f3577b34da6a3ce929d0e0e4736-00f067aa0ba902b7-01",
    data: {
      commandId: `cmd-${workId}`,
      actorEvidenceRef: "identity-evidence:t",
      workId,
      targetId: "t",
      capabilityId: "coding.codex",
      expectedContextVersion: 1,
      inputs: { objective, sourceRefs: [] },
      expectedDeliverables: [{ kind: "coding.final", mediaType: "text/markdown" }],
      deadline: "2036-10-23T18:00:00Z",
      assignmentId,
      executionGeneration: generation,
    },
  } as EnvelopeCommand;
}

let kernel: StubKernel;

beforeAll(async () => {
  kernel = await startStubKernel();
});

afterAll(async () => {
  await kernel.close();
});

interface FakeCtx {
  ctx: RunContext;
  events: EnvelopeEvent[];
  progress: string[];
  controller: AbortController;
}

function fakeContext(
  executionId: string,
  workId: string,
  assignmentId: string,
  options: { answer?: string; generation?: number } = {},
): FakeCtx {
  const controller = new AbortController();
  const askAnswer = options.answer;
  const events: EnvelopeEvent[] = [];
  const progress: string[] = [];
  const ctx: RunContext = {
    executionId,
    executionGeneration: options.generation ?? 1,
    command: assignCommand(workId, assignmentId, `objective ${workId}`, options.generation ?? 1),
    signal: controller.signal,
    kernel: { baseUrl: kernel.url, token: "child-token" },
    publishEvent: (env) => {
      events.push(env);
    },
    publishProgress: (note) => {
      progress.push(note);
    },
    setSteered: () => undefined,
    // Unset ⇒ these fakes never ask and a question is a bug; set ⇒ the
    // fake human answers immediately with `answer`.
    requestInput: askAnswer
      ? () => Promise.resolve({ text: askAnswer })
      : () => Promise.reject(new Error("unexpected requestInput")),
  };
  return { ctx, events, progress, controller };
}

function workDir(): string {
  return mkdtempSync(join(tmpdir(), "ofac-cx-"));
}

function hooksFor(dir: string, extra: Partial<Parameters<typeof createCodexHooks>[0]> = {}) {
  return createCodexHooks({
    childWorkspaceId: "gw.t/cx",
    workDir: dir,
    logger: createMemoryLogger(),
    fetchImpl: ((...args: Parameters<typeof fetch>) => fetch(...args)) as typeof fetch,
    // The fake is a shebang executable: argv arrives exactly like a real
    // installed codex — `exec <flags> <prompt>`, nothing wrapped.
    codexBin: FAKE,
    ...extra,
  });
}

describe("codex harness execution", () => {
  it("happy path: probe-shaped stream → final.md + transcript uploaded, manifest digests honest", async () => {
    const dir = workDir();
    const argvPath = join(dir, "argv.json");
    const hooks = hooksFor(dir, {
      env: { ...process.env, OFAC_FAKE_ARGV_OUT: argvPath },
    });
    const { ctx, events, progress } = fakeContext("exec-a", "work-a", "asg-a");
    const outcome = await hooks.onExecute(ctx);
    expect(outcome).toEqual({ kind: "completed" });

    // argv law (probe): exec + the frozen autonomy combo + -C + -o, prompt LAST.
    const argv = JSON.parse(readFileSync(argvPath, "utf8")) as string[];
    expect(argv[0]).toBe("exec");
    expect(argv).toContain("--json");
    expect(argv).toContain("-s");
    expect(argv).toContain("workspace-write");
    expect(argv).toContain("-c");
    expect(argv[argv.indexOf("-c") + 1]).toBe('approval_policy="never"');
    expect(argv[argv.indexOf("-C") + 1]).toBe(join(dir, "exec-a", "repo"));
    expect(argv[argv.indexOf("-o") + 1]).toBe(join(dir, "exec-a", "final.md"));
    // No clone URL in env ⇒ the plain dir is honest: --skip-git-repo-check.
    expect(argv).toContain("--skip-git-repo-check");
    expect(argv.at(-1)).toBe("objective work-a");

    // Progress law: accepted first, the session note second; item notes are
    // throttled (newest-wins inside the window) and flushed at settle.
    expect(progress[0]).toBe("accepted: coding.codex via codex exec for work-a");
    expect(progress[1]).toBe("codex session fake-thread-1");
    expect(progress.at(-1)).toBe("codex: turn complete");

    // The resume ledger recorded the fake thread under executionId:generation
    // AND the assignment pointer.
    const ledger = JSON.parse(readFileSync(join(dir, "sessions.json"), "utf8")) as Record<
      string,
      { threadId: string; generation: number }
    >;
    expect(ledger["exec-a:1"]?.threadId).toBe("fake-thread-1");
    expect(ledger["assign:asg-a"]?.threadId).toBe("fake-thread-1");

    // One terminal event: completed, schema-valid, ref+digest law.
    expect(events.length).toBe(1);
    const completed = events[0]!;
    expect(completed.type).toBe("org.facilitator.event.assignment.completed.v1");
    const data = completed.data as Record<string, unknown>;
    expect(
      validateAgainstSchemaId(
        "https://schemas.openfacilitator.org/v1/event.assignment.completed.v1.json",
        data,
      ).ok,
    ).toBe(true);
    const ref = String(data["resultManifestRef"]);
    expect(ref).toMatch(/^artifact:\/\/work-a\/asg-a\/[0-9a-f]{32}$/);
    const doc = kernel.artifacts.get(ref.split("/").pop()!)!;
    expect(String(data["resultDigest"])).toBe(
      computeManifestDigest(JSON.parse(doc.bytes.toString("utf8")) as Record<string, unknown>),
    );
    const manifest = JSON.parse(doc.bytes.toString("utf8")) as Record<string, unknown>;
    const outputs = manifest["outputs"] as Record<string, unknown>[];
    expect(outputs.map((o) => o["kind"])).toEqual(["coding.final", "harness.transcript"]);
    // The final.md carrier: the fake wrote the -o file; the harness
    // normalized it (agent_message wins as the answer, so the -o copy is
    // what got re-written by the harness with the JSONL-derived answer).
    const finalOutput = outputs[0]!;
    const finalBytes = kernel.artifacts.get(String(finalOutput["storageUrl"]).split("/").pop()!)!;
    expect(finalBytes.bytes.toString("utf8")).toContain("fake codex final answer");
    expect(finalOutput["digest"]).toBe(
      `sha256:${createHash("sha256").update(finalBytes.bytes).digest("hex")}`,
    );
    // The transcript is the raw stdout: unknown/ error/ non-JSON frames all
    // survived, and the run ignored them without crashing.
    const transcriptOutput = outputs[1]!;
    const transcriptBytes = kernel.artifacts.get(
      String(transcriptOutput["storageUrl"]).split("/").pop()!,
    )!;
    const transcript = transcriptBytes.bytes.toString("utf8");
    expect(transcript).toContain('"type":"thread.started"');
    expect(transcript).toContain("this line is not JSON");
    expect(transcript).toContain("Reconnecting");
    // The -o fallback file exists on disk for the operator.
    expect(existsSync(join(dir, "exec-a", "transcript.jsonl"))).toBe(true);
  });

  it("-o fallback: no agent_message in the stream ⇒ the -o file is the answer carrier", async () => {
    const dir = workDir();
    const hooks = hooksFor(dir, { env: { ...process.env, OFAC_FAKE_NO_FINAL: "1" } });
    const { ctx } = fakeContext("exec-f", "work-f", "asg-f");
    expect(await hooks.onExecute(ctx)).toEqual({ kind: "completed" });
    const final = readFileSync(join(dir, "exec-f", "final.md"), "utf8");
    expect(final).toContain("fake codex final answer (via -o)"); // the fake's -o write was the carrier
  });

  it("error frames are noise; a nonzero exit is an honest failed outcome with the stderr tail", async () => {
    const dir = workDir();
    const hooks = hooksFor(dir, {
      env: { ...process.env, OFAC_FAKE_EXIT: "3", OFAC_FAKE_STDERR: "x".repeat(3000) + " boom" },
    });
    const { ctx, events } = fakeContext("exec-e", "work-e", "asg-e");
    const outcome = await hooks.onExecute(ctx);
    expect(outcome).toEqual({ kind: "failed", reason: expect.stringMatching(/^codex exit 3: /) });
    expect(String((outcome as { reason: string }).reason)).not.toContain("\n");
    const failed = events.find((e) => e.type === "org.facilitator.event.assignment.failed.v1");
    // The SDK publishes failed over a real adapter; the hook only returns
    // the outcome — the harness itself emits nothing terminal.
    expect(failed).toBeUndefined();
  });

  it("cancel mid-run: SIGTERM the group, no terminal event, ledger keeps the thread", async () => {
    const dir = workDir();
    const hooks = hooksFor(dir, { env: { ...process.env, OFAC_FAKE_SLEEP_MS: "10000" } });
    const { ctx, events, progress, controller } = fakeContext("exec-c", "work-c", "asg-c");
    const running = hooks.onExecute(ctx);
    // Await the real condition: the thread-id note lands once the ledger
    // write happened (the fake then sleeps 10s, so the run is mid-flight).
    await waitFor("thread-id progress note", () => progress.length >= 2);
    controller.abort();
    expect(await running).toEqual({ kind: "completed" }); // honest drop; SDK publishes nothing
    expect(events).toEqual([]);
    // The thread id was recorded before the kill — a retry may resume it.
    const ledger = JSON.parse(readFileSync(join(dir, "sessions.json"), "utf8")) as Record<
      string,
      { threadId: string }
    >;
    expect(ledger["assign:asg-c"]?.threadId).toBe("fake-thread-1");
  });

  it("resume law: a retry generation re-spawns with `exec resume <thread>`", async () => {
    const dir = workDir();
    const argvPath = join(dir, "argv2.json");
    const hooks = hooksFor(dir, {
      env: { ...process.env, OFAC_FAKE_ARGV_OUT: argvPath },
    });
    const first = fakeContext("exec-r1", "work-r", "asg-r", { generation: 1 });
    expect(await hooks.onExecute(first.ctx)).toEqual({ kind: "completed" });
    const second = fakeContext("exec-r2", "work-r", "asg-r", { generation: 2 });
    expect(await hooks.onExecute(second.ctx)).toEqual({ kind: "completed" });
    const argv = JSON.parse(readFileSync(argvPath, "utf8")) as string[];
    expect(argv.slice(0, 3)).toEqual(["exec", "resume", "fake-thread-1"]);
    // The prompt still rides a re-dispatched run (follow-up turn on the thread).
    expect(argv.at(-1)).toBe("objective work-r");
  });

  it("promptTemplate: {objective} composes the prompt; ASK marker drives a real MCP round-trip", async () => {
    const dir = workDir();
    const argvPath = join(dir, "argv3.json");
    const hooks = createCodexHooks({
      childWorkspaceId: "gw.t/cx",
      workDir: dir,
      logger: createMemoryLogger(),
      fetchImpl: ((...args: Parameters<typeof fetch>) => fetch(...args)) as typeof fetch,
      codexBin: FAKE,
      promptTemplate: "work on: {objective} ASK(which framework?)",
      mcpShimEntry: SHIM,
      env: { ...process.env, OFAC_FAKE_ARGV_OUT: argvPath },
    });
    const { ctx, progress } = fakeContext("exec-m", "work-m", "asg-m", { answer: "postgres" });
    const outcome = await hooks.onExecute(ctx);
    expect(outcome).toEqual({ kind: "completed" });
    const argv = JSON.parse(readFileSync(argvPath, "utf8")) as string[];
    expect(argv.at(-1)).toBe("work on: objective work-m ASK(which framework?)");
    // The shim's answer rode the fake's final agent_message.
    expect(progress.some((note) => note.includes("agent message"))).toBe(true);
    const final = readFileSync(join(dir, "exec-m", "final.md"), "utf8");
    expect(final).toContain("postgres");
  }, 30_000);

  it("interactivity off: no config.toml seeding, no bridge, no ask", async () => {
    const dir = workDir();
    const hooks = hooksFor(dir, { interactivity: "off" });
    const { ctx } = fakeContext("exec-o", "work-o", "asg-o");
    expect(await hooks.onExecute(ctx)).toEqual({ kind: "completed" });
    // The fake only reads config.toml on an ASK prompt; the seed itself:
    // interactivity off must not write one at all.
    expect(existsSync(join(dir, "codex-home", "config.toml"))).toBe(false);
  });

  it("spawn failure (missing binary): failed outcome, never a hang", async () => {
    const dir = workDir();
    const hooks = createCodexHooks({
      childWorkspaceId: "gw.t/cx",
      workDir: dir,
      logger: createMemoryLogger(),
      codexBin: "/nonexistent/ofac-codex-binary",
    });
    const { ctx } = fakeContext("exec-nb", "work-nb", "asg-nb");
    const outcome = await hooks.onExecute(ctx);
    expect(outcome).toEqual({
      kind: "failed",
      reason: expect.stringMatching(/^codex exit null: .*ENOENT/),
    });
  });
});

describe("steer honesty", () => {
  it("the hook surface has no onSteer: the SDK answers unsupported (no resurrection path)", () => {
    const hooks: CodexHooks = hooksFor(workDir());
    expect("onSteer" in hooks).toBe(false);
    expect(hooks.onCancel).toBeTypeOf("function");
  });
});
