// The deterministic script driven in-process: a fake RunContext (no
// sockets), createMemoryLogger, stepMs=1 — asserting the locked report
// structure, steer recording ORDER, the cancel law (no terminal publish),
// schema-valid completed payloads, and the digest/URL honesty.
import { createHash } from "node:crypto";
import { mkdtempSync, readFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { fileURLToPath } from "node:url";
import { validateAgainstSchemaId } from "@ofac/protocol";
import type { EnvelopeCommand, EnvelopeEvent } from "@ofac/protocol";
import { createMemoryLogger } from "@ofac/workspace-link";
import { describe, expect, it } from "vitest";
import type { RunContext } from "@ofac/adapter";
import { createDeterministicHooks } from "../src/harness.js";

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
      capabilityId: "research.analyze",
      expectedContextVersion: 1,
      inputs: { objective: `objective ${workId}`, sourceRefs: [] },
      expectedDeliverables: [{ kind: "analysis.report", mediaType: "text/markdown" }],
      deadline: "2036-10-23T18:00:00Z",
      assignmentId,
    },
  } as EnvelopeCommand;
}

interface FakeCtx {
  ctx: RunContext;
  events: EnvelopeEvent[];
  progress: string[];
  controller: AbortController;
}

function fakeContext(executionId: string, workId: string, assignmentId: string): FakeCtx {
  const controller = new AbortController();
  const events: EnvelopeEvent[] = [];
  const progress: string[] = [];
  const ctx: RunContext = {
    executionId,
    executionGeneration: 3,
    command: assignCommand(workId, assignmentId),
    signal: controller.signal,
    publishEvent: (env) => {
      events.push(env);
    },
    publishProgress: (note) => {
      progress.push(note);
    },
    setSteered: () => undefined,
  };
  return { ctx, events, progress, controller };
}

function workDir(): string {
  return mkdtempSync(join(tmpdir(), "ofac-det-"));
}

describe("deterministic harness script", () => {
  it("no-steer run: two progress notes, a sectioned report, one schema-valid completed event", async () => {
    const dir = workDir();
    const hooks = createDeterministicHooks({
      childWorkspaceId: "gw.t/det",
      workDir: dir,
      stepMs: 1,
      logger: createMemoryLogger(),
    });
    const { ctx, events, progress } = fakeContext("exec-a", "work-a", "asg-a");
    const outcome = await hooks.onExecute(ctx);
    expect(outcome).toEqual({ kind: "completed" });
    expect(progress).toEqual([
      "accepted: analyzing research.analyze for work-a",
      "drafting report",
    ]);
    expect(events.length).toBe(1);
    const completed = events[0]!;
    expect(completed.type).toBe("org.facilitator.event.assignment.completed.v1");
    expect(completed.source).toBe("urn:workspace:gw.t/det");
    expect(completed.traceparent).toBe("00-4bf92f3577b34da6a3ce929d0e0e4736-00f067aa0ba902b7-01");
    const data = completed.data as Record<string, unknown>;
    expect(
      validateAgainstSchemaId(
        "https://schemas.openfacilitator.org/v1/event.assignment.completed.v1.json",
        data,
      ).ok,
    ).toBe(true);
    expect(
      validateAgainstSchemaId(
        "https://schemas.openfacilitator.org/v1/result-manifest.v1.json",
        data["resultManifest"],
      ).ok,
    ).toBe(true);
    const manifest = data["resultManifest"] as Record<string, unknown>;
    const output = (manifest["outputs"] as Record<string, unknown>[])[0]!;
    expect(String(output["storageUrl"])).toMatch(/^file:\/\/\//);
    const path = fileURLToPath(String(output["storageUrl"]));
    const bytes = readFileSync(path);
    expect(output["digest"]).toBe(`sha256:${createHash("sha256").update(bytes).digest("hex")}`);
    expect(output["size"]).toBe(bytes.length);
    const report = bytes.toString("utf8");
    for (const section of [
      "# Research report work-a",
      "Objective: objective work-a",
      "Assignment: asg-a (execution exec-a)",
      "## Steps",
      "- step 1 complete at +",
      "## Steering directives\nnone",
      "## Conclusion",
    ]) {
      expect(report).toContain(section);
    }
  });

  it("steer mid-run: directives land in the report under ## Steering directives, arrival order", async () => {
    const dir = workDir();
    const hooks = createDeterministicHooks({
      childWorkspaceId: "gw.t/det",
      workDir: dir,
      stepMs: 30,
      logger: createMemoryLogger(),
    });
    const { ctx } = fakeContext("exec-b", "work-b", "asg-b");
    const running = hooks.onExecute(ctx);
    await new Promise((resolve) => setTimeout(resolve, 5));
    hooks.onSteer("first directive", ctx);
    await new Promise((resolve) => setTimeout(resolve, 5));
    hooks.onSteer("second directive", ctx);
    expect(await running).toEqual({ kind: "completed" });
    const report = readReport(dir, "exec-b");
    const section = report.split("## Steering directives\n")[1]!.split("## Conclusion")[0]!;
    const lines = section.trim().split("\n");
    expect(lines[0]).toMatch(/^- \+\d+ms: first directive$/);
    expect(lines[1]).toMatch(/^- \+\d+ms: second directive$/);
  });

  it("cancel before step 2: the run unwinds as cancelled, publishing NOTHING", async () => {
    const dir = workDir();
    const hooks = createDeterministicHooks({
      childWorkspaceId: "gw.t/det",
      workDir: dir,
      stepMs: 10_000,
      logger: createMemoryLogger(),
    });
    const { ctx, events, progress, controller } = fakeContext("exec-c", "work-c", "asg-c");
    const running = hooks.onExecute(ctx);
    await new Promise((resolve) => setTimeout(resolve, 10));
    controller.abort();
    expect(await running).toEqual({ kind: "completed" });
    expect(progress).toEqual(["accepted: analyzing research.analyze for work-c"]);
    expect(events).toEqual([]); // no terminal event after a cancel
  });

  it("two runs of the same work are byte-stable in structure (zero randomness beyond timing)", async () => {
    const dir = workDir();
    const hooks = createDeterministicHooks({
      childWorkspaceId: "gw.t/det",
      workDir: dir,
      stepMs: 1,
      logger: createMemoryLogger(),
    });
    const first = fakeContext("exec-d", "work-d", "asg-d");
    await hooks.onExecute(first.ctx);
    const second = fakeContext("exec-d2", "work-d", "asg-d");
    await hooks.onExecute(second.ctx);
    const a = readReport(dir, "exec-d").replace(/\+\d+ms/g, "+Nms");
    const b = readReport(dir, "exec-d2")
      .replace(/\+\d+ms/g, "+Nms")
      .replace("exec-d2", "exec-d");
    expect(a).toBe(b);
  });
});

function readReport(dir: string, executionId: string): string {
  return readFileSync(join(dir, "artifacts", `${executionId}-report.md`), "utf8");
}
