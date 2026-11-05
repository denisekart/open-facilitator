// The omp RPC loop driven in-process against the fake-omp fixture: a fake
// RunContext + the SDK's stub kernel (the completion path uploads
// transcript + final.md + manifest, completes with ref+digest). The
// assertion surface is the fixture's argv summary: the forced agentic flags,
// cwd/session-dir layout, the resume law, the system-prompt injection
// literal, extraArgs passthrough, and the REAL ask_user MCP round-trip
// through the package shim (a .ts spawn on node ≥22.6 type-stripping).
// The steer/cancel legs wait on a REAL spawned child, so they pace on short
// wall-clock probes of the awaited condition (fake timers cannot drive a
// subprocess).
import { randomUUID } from "node:crypto";
import { mkdtempSync, readFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";
import { computeManifestDigest, validateAgainstSchemaId } from "@ofac/protocol";
import type { EnvelopeCommand, EnvelopeEvent } from "@ofac/protocol";
import { createMemoryLogger } from "@ofac/workspace-link";
import { startStubKernel, type StubKernel } from "@ofac/adapter/testing";
import { afterAll, beforeAll, describe, expect, it } from "vitest";
import type { RunContext, SessionStore } from "@ofac/adapter";
import { createOmpHooks, SYSTEM_PROMPT_INJECTION, type OmpHooks } from "../src/harness.js";
import { FileSessionStore } from "../src/session-store.js";

const FAKE_OMP = join(dirname(fileURLToPath(import.meta.url)), "fake-omp.mjs");
const SHIM_TS = join(dirname(fileURLToPath(import.meta.url)), "..", "src", "bin", "mcp-shim.ts");

function assignCommand(workId: string, assignmentId: string): EnvelopeCommand {
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
      capabilityId: "coding.omp",
      expectedContextVersion: 1,
      inputs: { objective: `objective ${workId}`, sourceRefs: [] },
      expectedDeliverables: [{ kind: "analysis.report", mediaType: "text/markdown" }],
      deadline: "2036-10-23T18:00:00Z",
      assignmentId,
      childWorkspaceId: "gw.t/omp",
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
  questions: string[];
}

function fakeContext(
  executionId: string,
  workId: string,
  assignmentId: string,
  opts: { answer?: string; generation?: number; hold?: boolean } = {},
): FakeCtx {
  const controller = new AbortController();
  const events: EnvelopeEvent[] = [];
  const progress: string[] = [];
  const questions: string[] = [];
  const command = assignCommand(workId, assignmentId);
  const ctx: RunContext = {
    executionId,
    executionGeneration: opts.generation ?? 1,
    command: opts.hold === true ? withHold(command) : command,
    signal: controller.signal,
    kernel: { baseUrl: kernel.url, token: "child-token" },
    publishEvent: (env) => {
      events.push(env);
    },
    publishProgress: (note) => {
      progress.push(note);
    },
    setSteered: () => undefined,
    requestInput: (req) => {
      questions.push(req.question);
      if (opts.answer === undefined) return Promise.reject(new Error("unexpected requestInput"));
      return Promise.resolve({ text: opts.answer });
    },
  };
  return { ctx, events, progress, controller, questions };
}

/** Rewrites the objective so the fixture dwells (mid-run steer/kill window). */
function withHold(command: EnvelopeCommand): EnvelopeCommand {
  const data = command.data as Record<string, unknown>;
  const inputs = data["inputs"] as Record<string, unknown>;
  return {
    ...command,
    data: {
      ...data,
      inputs: { ...inputs, objective: `${String(inputs["objective"])} HOLD` },
    },
  } as EnvelopeCommand;
}

function workDir(): string {
  return mkdtempSync(join(tmpdir(), "ofac-omp-"));
}

interface FixtureSummary {
  argv: string[];
  resume: string | null;
  prompt: string | null;
  systemPrompt: string | null;
  steers: string[];
  mcp: {
    configSeen: boolean;
    bridge: string | null;
    tools: string[];
    answer: string | null;
    error: string | null;
  };
  flags: Record<string, string | boolean>;
}

interface HarnessFixture {
  hooks: OmpHooks;
  summary(): FixtureSummary;
}

function harnessFixture(
  dir: string,
  extra: {
    askQuestion?: string;
    promptTemplate?: string;
    model?: string;
    extraArgs?: readonly string[];
    interactivity?: "mcp" | "off";
    fakeMode?: string;
    sessionStore?: SessionStore;
  } = {},
): HarnessFixture {
  const argvFile = join(dir, `argv-${randomUUID()}.json`);
  const env: Record<string, string> = { OFAC_FAKE_OMP_ARGV_FILE: argvFile };
  if (extra.fakeMode !== undefined) env["OFAC_FAKE_OMP_MODE"] = extra.fakeMode;
  const hooks = createOmpHooks({
    childWorkspaceId: "gw.t/omp",
    workDir: dir,
    stepMs: 0,
    extraArgs: extra.extraArgs ?? [],
    interactivity: extra.interactivity ?? "mcp",
    ...(extra.askQuestion !== undefined ? { askQuestion: extra.askQuestion } : {}),
    ...(extra.promptTemplate !== undefined ? { promptTemplate: extra.promptTemplate } : {}),
    ...(extra.model !== undefined ? { model: extra.model } : {}),
    invocation: {
      command: process.execPath,
      prefixArgs: [FAKE_OMP],
      env,
    },
    mcpShimEntry: SHIM_TS,
    sessionStore: extra.sessionStore ?? new FileSessionStore(dir),
    logger: createMemoryLogger(),
    fetchImpl: ((...args: Parameters<typeof fetch>) => fetch(...args)) as typeof fetch,
  });
  return {
    hooks,
    summary: () => JSON.parse(readFileSync(argvFile, "utf8")) as FixtureSummary,
  };
}

/** The served final.md bytes the completed event points at (via the stub kernel). */
function servedFinal(event: EnvelopeEvent): string {
  const data = event.data as Record<string, unknown>;
  const doc = kernel.artifacts.get(String(data["resultManifestRef"]).split("/").pop()!)!;
  const outputs = (JSON.parse(doc.bytes.toString("utf8")) as Record<string, unknown>)[
    "outputs"
  ] as Record<string, unknown>[];
  const final = outputs.find((o) => o["kind"] === "analysis.report")!;
  return kernel.artifacts
    .get(String(final["storageUrl"]).split("/").pop()!)!
    .bytes.toString("utf8");
}

/** Polls the condition (a real subprocess wrote the file); rejects loudly. */
async function settled<T>(probe: () => T | false, what: string, tries = 100): Promise<T> {
  for (let i = 0; i < tries; i += 1) {
    const value = probe();
    if (value !== false) return value;
    await new Promise((resolve) => setTimeout(resolve, 25));
  }
  throw new Error(`not settled: ${what}`);
}

describe("omp harness rpc loop (fake-omp fixture)", () => {
  it("plain run: forced agentic argv, prompt verbatim, completion over the kernel path", async () => {
    const dir = workDir();
    const { hooks, summary } = harnessFixture(dir);
    const { ctx, events, progress } = fakeContext("exec-a", "work-a", "asg-a");
    const outcome = await hooks.onExecute(ctx);
    expect(outcome).toEqual({ kind: "completed" });
    expect(progress[0]).toBe("accepted: omp coding.omp for work-a");
    expect(progress).toContain("turn complete");
    expect(progress).toContain("tool: bash"); // ask_user excluded, bash relayed
    const s = await settled(summary, "fixture wrote its argv summary");
    expect(s.flags["modeRpc"]).toBe(true);
    expect(s.flags["noUi"]).toBe(true);
    expect(s.flags["autoApprove"]).toBe(true);
    expect(s.flags["cwd"]).toBe(join(dir, "exec-a", "repo"));
    expect(s.flags["sessionDir"]).toBe(join(dir, "exec-a", "sessions"));
    expect(s.systemPrompt).toBe(SYSTEM_PROMPT_INJECTION);
    expect(s.prompt).toBe("objective work-a");
    expect(s.resume).toBeNull();
    // Completion law: one event, schema-valid, ref+digest over stored bytes.
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
    expect(outputs.map((o) => o["kind"]).sort()).toEqual(["analysis.report", "harness.transcript"]);
    expect(servedFinal(completed)).toBe("# omp run work-a\nDone.\n");
    // Native session id persisted for the resume law.
    const store = new FileSessionStore(dir);
    expect(store.get("exec-a:1")).toMatch(/^fake-sess-/);
    expect(store.get("assign:asg-a")).toBe(store.get("exec-a:1"));
  });

  it("ask_user round-trip: real shim + bridge + .mcp.json; the answer rides the final text", async () => {
    const dir = workDir();
    const { hooks, summary } = harnessFixture(dir, { promptTemplate: "{objective} ASKME" });
    const { ctx, events, questions } = fakeContext("exec-m", "work-m", "asg-m", {
      answer: "sqlite",
    });
    const outcome = await hooks.onExecute(ctx);
    expect(outcome).toEqual({ kind: "completed" });
    const s = await settled(summary, "fixture wrote its argv summary");
    expect(s.mcp.configSeen).toBe(true); // the .mcp.json was written + read
    expect(s.mcp.tools).toContain("ask_user"); // the shim served the tool
    expect(s.mcp.error).toBeNull();
    expect(s.mcp.bridge).toMatch(/^127\.0\.0\.1:\d+$/);
    expect(s.mcp.answer).toBe("sqlite"); // round-trip through bridge → requestInput
    expect(questions).toContain("which framework?");
    expect(servedFinal(events[0]!)).toContain("answer: sqlite");
  }, 30_000);

  it("askQuestion before the spawn: the fixed question rides requestInput", async () => {
    const dir = workDir();
    const { hooks } = harnessFixture(dir, { askQuestion: "which framework?" });
    const { ctx, questions } = fakeContext("exec-q", "work-q", "asg-q", { answer: "postgres" });
    expect(await hooks.onExecute(ctx)).toEqual({ kind: "completed" });
    expect(questions).toEqual(["which framework?"]);
  });

  it("crash law: nonzero exit before completion ⇒ failed with the stderr tail", async () => {
    const dir = workDir();
    const { hooks } = harnessFixture(dir, { fakeMode: "fail" });
    const { ctx, events } = fakeContext("exec-f", "work-f", "asg-f");
    const outcome = await hooks.onExecute(ctx);
    expect(outcome).toEqual({ kind: "failed", reason: "omp exit 3: fake omp exploded" });
    expect(events).toEqual([]); // the SDK owns the failed.v1 publish
  });

  it("cancel law: abort mid-run ⇒ completed, NOTHING published", async () => {
    const dir = workDir();
    const { hooks } = harnessFixture(dir);
    const { ctx, events, controller } = fakeContext("exec-c", "work-c", "asg-c", {
      hold: true,
    });
    const running = hooks.onExecute(ctx);
    await new Promise((resolve) => setTimeout(resolve, 200)); // fixture is past the ack, mid-dwell
    controller.abort();
    expect(await running).toEqual({ kind: "completed" });
    expect(events).toEqual([]); // no terminal event after a cancel
  });

  it("steer law: a live directive reaches the fixture and rides the final text", async () => {
    const dir = workDir();
    const { hooks, summary } = harnessFixture(dir);
    const { ctx, events } = fakeContext("exec-s", "work-s", "asg-s", { hold: true });
    const running = hooks.onExecute(ctx);
    await new Promise((resolve) => setTimeout(resolve, 200)); // fixture is past the ack
    hooks.onSteer("focus on costs", ctx);
    expect(await running).toEqual({ kind: "completed" });
    const s = await settled(summary, "fixture wrote its argv summary");
    expect(s.steers).toEqual(["focus on costs"]);
    expect(servedFinal(events[0]!)).toContain("steered: focus on costs");
  });

  it("resume law: generation 2 relaunches with --resume <persisted sessionId>", async () => {
    const dir = workDir();
    const store = new FileSessionStore(dir);
    store.set("assign:asg-r", "sess-9");
    const { hooks, summary } = harnessFixture(dir, { sessionStore: store });
    const { ctx } = fakeContext("exec-r2", "work-r", "asg-r", { generation: 2 });
    expect(await hooks.onExecute(ctx)).toEqual({ kind: "completed" });
    const s = await settled(summary, "fixture wrote its argv summary");
    expect(s.resume).toBe("sess-9");
    expect(s.argv).toContain("--resume");
    // The fixture adopts resumed-<id>; the store re-keys under the new generation.
    expect(store.get("exec-r2:2")).toBe("resumed-sess-9");
  });

  it("argv seams: model, extraArgs verbatim, interactivity off omits the injection", async () => {
    const dir = workDir();
    const { hooks, summary } = harnessFixture(dir, {
      model: "qwen-max",
      extraArgs: ["--thinking", "high"],
      interactivity: "off",
    });
    const { ctx } = fakeContext("exec-v", "work-v", "asg-v");
    expect(await hooks.onExecute(ctx)).toEqual({ kind: "completed" });
    const s = await settled(summary, "fixture wrote its argv summary");
    expect(s.flags["model"]).toBe("qwen-max");
    expect(s.argv).toContain("--thinking");
    expect(s.argv).toContain("high");
    expect(s.systemPrompt).toBeNull(); // no injection without interactivity
    expect(s.mcp.configSeen).toBe(false);
  });
});
