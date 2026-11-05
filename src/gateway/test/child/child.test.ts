import type { CapabilityManifestV1, EnvelopeCommand } from "@ofac/protocol";
import { describe, expect, it } from "vitest";

import {
  EmbeddedChild,
  builtinChildManifest,
  newExecutionId,
  type ChildMessage,
} from "../../src/child.js";

function assignCommand(overrides: Partial<EnvelopeCommand> = {}): EnvelopeCommand {
  return {
    specversion: "1.0",
    id: "env-1",
    source: "urn:facilitator:instance:t",
    type: "org.facilitator.command.work.assign.v1",
    subject: "work/work-1/assignment/asg-1",
    time: new Date().toISOString(),
    datacontenttype: "application/json",
    dataschema: "https://schemas.openfacilitator.org/v1/command.work.assign.v1.json",
    data: {
      commandId: "cmd-1",
      actorEvidenceRef: "identity-evidence:t",
      workId: "work-1",
      targetId: "t",
      capabilityId: "research.analyze",
      expectedContextVersion: 1,
      executionGeneration: 1,
      inputs: { objective: "x", sourceRefs: [] },
      expectedDeliverables: [{ kind: "analysis.report", mediaType: "text/markdown" }],
      deadline: "2036-10-23T18:00:00Z",
      assignmentId: "asg-1",
    },
    ...overrides,
  };
}

function child(mode: "echo" | "stub" = "echo"): EmbeddedChild {
  return new EmbeddedChild({
    childWorkspaceId: "gw.t/echo",
    mode,
    manifest: builtinChildManifest(mode),
  });
}

const CHILD_ID = "gw.t/echo";

describe("ChildMessage vocabulary", () => {
  it("every message round-trips JSON (socket-compatible)", () => {
    const messages: ChildMessage[] = [
      { kind: "register", childWorkspaceId: CHILD_ID, manifest: builtinChildManifest("echo") },
      {
        kind: "start",
        executionId: "exec-1",
        executionGeneration: 3,
        command: assignCommand(),
      },
      { kind: "steer", executionId: "exec-1", command: assignCommand() },
      { kind: "status", executionId: "exec-1" },
      { kind: "stop", executionId: "exec-1", reason: "work.cancel" },
      { kind: "respond", executionId: "exec-1", requestId: "inp-1", text: "postgres" },
    ];
    for (const message of messages) {
      expect(JSON.parse(JSON.stringify(message))).toEqual(message);
    }
  });
});

describe("EmbeddedChild register", () => {
  it("carries the child's OWN ownerTeam (aggregation rewrites it, not here)", async () => {
    const msg = await child().register();
    expect(msg.kind).toBe("register");
    expect(msg.childWorkspaceId).toBe(CHILD_ID);
    expect((msg.manifest as CapabilityManifestV1).ownerTeam).toBe("child-echo");
    expect(msg.manifest.capabilities[0]?.id).toBe("research.analyze");
  });
});

describe("EmbeddedChild echo mode", () => {
  it("start publishes work.progress then assignment.completed (generation 1) into drainEvents", async () => {
    const c = child("echo");
    const reply = await c.send({
      kind: "start",
      executionId: "exec-1",
      executionGeneration: 1,
      command: assignCommand(),
    });
    expect(reply).toEqual({ kind: "accepted", executionId: "exec-1" });

    const events = c.drainEvents();
    expect(events.map((e) => e.type)).toEqual([
      "org.facilitator.event.work.progress.v1",
      "org.facilitator.event.assignment.completed.v1",
    ]);
    const completed = events[1];
    expect(completed?.source).toBe(`urn:workspace:${CHILD_ID}`);
    expect(completed?.data).toMatchObject({
      workId: "work-1",
      assignmentId: "asg-1",
      executionGeneration: 1,
    });
    // drainEvents empties the queue
    expect(c.drainEvents()).toHaveLength(0);
    // the child settled the execution from its own event
    expect(await c.send({ kind: "status", executionId: "exec-1" })).toEqual({
      kind: "status",
      executionId: "exec-1",
      state: "completed",
    });
  });

  it("echo stamps the command's executionGeneration onto both events (generation fence)", async () => {
    const c = child("echo");
    const command = assignCommand();
    (command.data as Record<string, unknown>)["executionGeneration"] = 4;
    const reply = await c.send({
      kind: "start",
      executionId: "exec-4",
      executionGeneration: 1,
      command,
    });
    expect(reply).toEqual({ kind: "accepted", executionId: "exec-4" });
    const events = c.drainEvents();
    expect(events.map((e) => e.data?.["executionGeneration"])).toEqual([4, 4]);
  });

  it("a second start for a running execution answers accepted (echo re-publishes)", async () => {
    const c = child("echo");
    await c.send({
      kind: "start",
      executionId: "exec-1",
      executionGeneration: 1,
      command: assignCommand(),
    });
    expect(c.drainEvents()).toHaveLength(2);
    const again = await c.send({
      kind: "start",
      executionId: "exec-1",
      executionGeneration: 1,
      command: assignCommand(),
    });
    expect(again.kind).toBe("accepted");
    // echo re-published its pair
    expect(c.drainEvents()).toHaveLength(2);
  });

  it("setPublisher forwards live instead of queueing", async () => {
    const c = child("echo");
    const sent: string[] = [];
    c.setPublisher(async (env) => {
      sent.push(env.type);
    });
    await c.send({
      kind: "start",
      executionId: "exec-1",
      executionGeneration: 1,
      command: assignCommand(),
    });
    expect(sent).toEqual([
      "org.facilitator.event.work.progress.v1",
      "org.facilitator.event.assignment.completed.v1",
    ]);
    expect(c.drainEvents()).toHaveLength(0);
  });
});

describe("EmbeddedChild stub mode + reconcile answers", () => {
  it("stub accepts with NO events: execution stays running (no phantom completion)", async () => {
    const c = child("stub");
    const reply = await c.send({
      kind: "start",
      executionId: "exec-1",
      executionGeneration: 1,
      command: assignCommand(),
    });
    expect(reply.kind).toBe("accepted");
    expect(c.drainEvents()).toHaveLength(0);
    expect(await c.send({ kind: "status", executionId: "exec-1" })).toMatchObject({
      state: "running",
    });
  });

  it("status for an execution the child never saw answers unknown", async () => {
    const c = child("echo");
    expect(await c.send({ kind: "status", executionId: "exec-nope" })).toEqual({
      kind: "unknown",
      executionId: "exec-nope",
    });
  });

  it("stop settles a running execution completed", async () => {
    const c = child("stub");
    await c.send({
      kind: "start",
      executionId: "exec-1",
      executionGeneration: 1,
      command: assignCommand(),
    });
    expect(await c.send({ kind: "stop", executionId: "exec-1", reason: "work.cancel" })).toEqual({
      kind: "accepted",
      executionId: "exec-1",
    });
    expect(await c.send({ kind: "status", executionId: "exec-1" })).toMatchObject({
      state: "completed",
    });
    // stop on a never-seen execution answers unknown (reconcile honesty)
    expect(await c.send({ kind: "stop", executionId: "exec-x", reason: "r" })).toEqual({
      kind: "unknown",
      executionId: "exec-x",
    });
  });

  it("respond on a known execution answers unsupported (builtin never asks); unknown for foreign", async () => {
    const c = child("stub");
    await c.send({
      kind: "start",
      executionId: "exec-1",
      executionGeneration: 1,
      command: assignCommand(),
    });
    expect(
      await c.send({ kind: "respond", executionId: "exec-1", requestId: "inp-1", text: "x" }),
    ).toEqual({ kind: "unsupported", reason: "builtin-child-never-asks" });
    expect(
      await c.send({ kind: "respond", executionId: "exec-nope", requestId: "inp-1", text: "x" }),
    ).toEqual({ kind: "unknown", executionId: "exec-nope" });
  });

  it("a closed child rejects: no dispatch after close", async () => {
    const c = child("echo");
    await c.close();
    expect(
      await c.send({
        kind: "start",
        executionId: "e",
        executionGeneration: 1,
        command: assignCommand(),
      }),
    ).toMatchObject({ kind: "rejected", reason: "child closed" });
  });
});

describe("newExecutionId", () => {
  it("mints distinct exec-<16hex> ids", () => {
    const a = newExecutionId();
    const b = newExecutionId();
    expect(a).toMatch(/^exec-[0-9a-f]{16}$/);
    expect(a).not.toBe(b);
  });
});
