// The built-in handlers: the deterministic-worker stand-ins every embedder
// composes. Echo answers work.assign with progress + completion (exercising
// the outbound-event wiring); stub accepts any kernel command and refuses
// anything else. Driven through a fake CommandContext — no client, no socket.
import { describe, expect, it } from "vitest";
import type { EnvelopeCommand, EnvelopeEvent } from "@ofac/protocol";
import { createEchoHandler, createStubHandler, type CommandContext } from "../../src/index.js";
import { DECIDED_TYPE, assignEnvelope } from "./fixtures.js";

const published: EnvelopeEvent[] = [];

function context(deliveryId = "del-1"): CommandContext {
  return {
    deliveryId,
    envelope: assignEnvelope(),
    signal: new AbortController().signal,
    publishEvent: async (env) => {
      published.push(env);
      return { status: "accepted" };
    },
  };
}

describe("createEchoHandler", () => {
  it("answers work.assign with progress + completion on the injected source", async () => {
    published.length = 0;
    const handler = createEchoHandler({ source: "urn:workspace:linkd/gw-1" });
    const cmd = assignEnvelope("cmd-echo");
    const outcome = await handler.onCommand(cmd, context("del-echo"));
    expect(outcome).toEqual({ kind: "accepted" });
    expect(published.map((e) => e.type)).toEqual([
      "org.facilitator.event.work.progress.v1",
      "org.facilitator.event.assignment.completed.v1",
    ]);
    const progress = published[0]!;
    expect(progress.source).toBe("urn:workspace:linkd/gw-1");
    expect(progress.subject).toBe(cmd.subject);
    expect(progress.data).toEqual({
      workId: "work-123",
      assignmentId: "cmd-echo",
      executionGeneration: 2,
      progressNote: "linkd echo: assignment received.",
    });
    const completed = published[1]!;
    expect(completed.data).toEqual({
      workId: "work-123",
      assignmentId: "cmd-echo",
      executionId: "exec-del-echo",
      executionGeneration: 2,
      resultManifest: { manifestId: "man-del-echo", outputs: [] },
    });
    // CloudEvents discipline: the builders fill id/time/schema.
    for (const env of published) {
      expect(env.specversion).toBe("1.0");
      expect(env.id).toBeTruthy();
      expect(env.time).toBeTruthy();
      expect(env.dataschema).toContain("schemas.openfacilitator.org");
    }
  });

  it("defaults executionGeneration to 1 when the command omits it", async () => {
    published.length = 0;
    const handler = createEchoHandler({ source: "urn:workspace:linkd/gw-1" });
    const cmd = assignEnvelope("cmd-gen") as EnvelopeCommand & {
      data: Record<string, unknown>;
    };
    delete cmd.data["executionGeneration"];
    await handler.onCommand(cmd, context("del-gen"));
    expect((published[0]!.data as Record<string, unknown>)["executionGeneration"]).toBe(1);
  });

  it("refuses anything that is not work.assign", async () => {
    const handler = createEchoHandler({ source: "urn:workspace:linkd/gw-1" });
    const decided: EnvelopeCommand = { ...assignEnvelope(), type: DECIDED_TYPE };
    const outcome = await handler.onCommand(decided, context());
    expect(outcome.kind).toBe("unsupported");
    if (outcome.kind === "unsupported") {
      expect(outcome.reason).toBe(`echo handles work.assign only, got ${DECIDED_TYPE}`);
    }
  });
});

describe("createStubHandler", () => {
  it("accepts every kernel command", async () => {
    const handler = createStubHandler();
    expect(await handler.onCommand(assignEnvelope(), context())).toEqual({ kind: "accepted" });
    const other: EnvelopeCommand = {
      ...assignEnvelope(),
      type: "org.facilitator.command.work.cancel.v1",
    };
    expect(await handler.onCommand(other, context())).toEqual({ kind: "accepted" });
  });

  it("refuses a non-command type", async () => {
    const handler = createStubHandler();
    const event: EnvelopeCommand = { ...assignEnvelope(), type: "com.example.thing" };
    const outcome = await handler.onCommand(event, context());
    expect(outcome.kind).toBe("unsupported");
    if (outcome.kind === "unsupported") {
      expect(outcome.reason).toBe("not a command: com.example.thing");
    }
  });
});
