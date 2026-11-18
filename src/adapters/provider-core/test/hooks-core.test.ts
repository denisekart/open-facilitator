// The hook dispatch law: an unknown op id fails with its name; an op that
// throws an HttpError fails with the provider's status named (a 403 and a 429
// read apart); any OTHER throw propagates (the SDK's own retry law answers);
// success publishes the child's OWN assignment.completed.v1 with the inline
// result manifest — and a stall that did not stop the op rides along as
// `degraded`, never a silent green row.
import { describe, expect, it } from "vitest";
import type { EnvelopeCommand, EnvelopeEvent } from "@ofac/protocol";
import type { RunContext } from "@ofac/adapter";
import {
  HttpError,
  createConnectorHooks,
  type ConnectorHooks,
  type OpResult,
  type OpTable,
} from "../src/index.js";

const COMPLETED_TYPE = "org.facilitator.event.assignment.completed.v1";

const logger = { debug() {}, info() {}, warn() {}, error() {} };

interface Captured {
  progress: string[];
  events: EnvelopeEvent[];
}

/** One work.assign envelope shaped enough for the hook: `data` is what the
 *  hook actually reads; the rest is envelope furniture. */
function context(
  data: Record<string, unknown>,
  captured: Captured,
  traceparent?: string,
): RunContext {
  const command = {
    specversion: "1.0",
    id: "cmd-env-1",
    source: "urn:facilitator:kernel",
    type: "org.facilitator.command.work.assign.v1",
    subject: "work/work-1/assignment/cmd-1",
    time: "2026-11-01T12:00:00Z",
    datacontenttype: "application/json",
    dataschema: "https://schemas.openfacilitator.org/v1/command.work.assign.v1.json",
    ...(traceparent === undefined ? {} : { traceparent }),
    data,
  };
  return {
    executionId: "exec-1",
    executionGeneration: 3,
    command: command as unknown as EnvelopeCommand,
    signal: new AbortController().signal,
    publishEvent: (env: EnvelopeEvent) => {
      captured.events.push(env);
    },
    publishProgress: (note: string) => {
      captured.progress.push(note);
    },
    setSteered: () => undefined,
    requestInput: async () => ({ text: "" }),
    kernel: { baseUrl: "http://kernel.test", token: "t" },
    fetchContext: async () => ({ fetchId: "f", items: [] }),
  };
}

/** One published envelope, read as data-of-record after the hook ran. */
function dataOf(env: EnvelopeEvent): Record<string, unknown> {
  // The hook builds the data itself; the shape is what each test asserts.
  return env.data as Record<string, unknown>;
}

const ok: OpResult = { providerObjectId: "comment 42", structured: { url: "https://x/42" } };

function hooks(ops: OpTable): ConnectorHooks {
  return createConnectorHooks({ ops, logger, childWorkspaceId: "gw-1/forge" });
}

describe("hook dispatch", () => {
  it("an unknown op id fails, naming it, and publishes nothing", async () => {
    const captured: Captured = { progress: [], events: [] };
    const outcome = await hooks({}).onExecute(
      context({ capabilityId: "vcs.issue.read" }, captured),
    );
    expect(outcome).toEqual({ kind: "failed", reason: "unknown-op:vcs.issue.read" });
    expect(captured.events).toHaveLength(0);
  });

  it("a non-string capabilityId fails as the empty op", async () => {
    const outcome = await hooks({}).onExecute(
      context({ capabilityId: 7 }, { progress: [], events: [] }),
    );
    expect(outcome).toEqual({ kind: "failed", reason: "unknown-op:" });
  });

  it("a provider HttpError is named on the failure, never a bare throw", async () => {
    for (const [status, reason] of [
      [403, "provider-http-403"],
      [429, "provider-http-429"],
    ] as const) {
      const captured: Captured = { progress: [], events: [] };
      const outcome = await hooks({
        "vcs.issue.comment": async () => {
          throw new HttpError(status, "nope");
        },
      }).onExecute(context({ capabilityId: "vcs.issue.comment" }, captured));
      expect(outcome).toEqual({ kind: "failed", reason });
      expect(captured.events).toHaveLength(0);
    }
  });

  it("a non-HTTP throw propagates: the SDK's retry law answers", async () => {
    await expect(
      hooks({
        "vcs.issue.comment": async () => {
          throw new Error("socket died");
        },
      }).onExecute(context({ capabilityId: "vcs.issue.comment" }, { progress: [], events: [] })),
    ).rejects.toThrow("socket died");
  });

  it("success publishes the completed event with the inline manifest and resolves completed", async () => {
    const captured: Captured = { progress: [], events: [] };
    const outcome = await hooks({ "vcs.issue.comment": async () => ok }).onExecute(
      context(
        {
          capabilityId: "vcs.issue.comment",
          commandId: "cmd-1",
          workId: "work-9",
          assignmentId: "asg-9",
          inputs: { body: "hi" },
        },
        captured,
      ),
    );
    expect(outcome).toEqual({ kind: "completed" });
    expect(captured.progress).toEqual(["vcs.issue.comment -> comment 42"]);
    expect(captured.events).toHaveLength(1);
    const env = captured.events[0]!;
    expect(env.type).toBe(COMPLETED_TYPE);
    expect(env.source).toBe("urn:workspace:gw-1/forge");
    expect(env.dataschema).toBe(
      "https://schemas.openfacilitator.org/v1/event.assignment.completed.v1.json",
    );
    const data = dataOf(env);
    expect(data["workId"]).toBe("work-9");
    expect(data["assignmentId"]).toBe("asg-9");
    expect(data["executionId"]).toBe("exec-1");
    expect(data["executionGeneration"]).toBe(3);
    expect(data["resultManifest"]).toMatchObject({
      manifestId: "man-exec-1",
      outputs: [],
      capability: "vcs.issue.comment",
      summary: "vcs.issue.comment -> comment 42",
      structuredResult: { url: "https://x/42" },
    });
    expect(data).not.toHaveProperty("resultManifestRef");
  });

  it("the op runs with the command's own commandId and the verbatim inputs", async () => {
    const seen: [string, Record<string, unknown>][] = [];
    await hooks({
      "vcs.pr.create": async (commandId, inputs) => {
        seen.push([commandId, inputs]);
        return ok;
      },
    }).onExecute(
      context(
        { capabilityId: "vcs.pr.create", commandId: "cmd-77", inputs: { title: "t" } },
        { progress: [], events: [] },
      ),
    );
    expect(seen).toEqual([["cmd-77", { title: "t" }]]);
  });

  it("a missing commandId falls back to the execution id; work/assignment ids too", async () => {
    const seen: string[] = [];
    const captured: Captured = { progress: [], events: [] };
    await hooks({
      "vcs.pr.create": async (commandId) => {
        seen.push(commandId);
        return ok;
      },
    }).onExecute(context({ capabilityId: "vcs.pr.create" }, captured));
    expect(seen).toEqual(["exec-1"]);
    const data = dataOf(captured.events[0]!);
    expect(data["workId"]).toBe("exec-1");
    expect(data["assignmentId"]).toBe("exec-1");
  });

  it("a degraded op says so in the structured result (N7/F6)", async () => {
    const captured: Captured = { progress: [], events: [] };
    const outcome = await hooks({
      "vcs.issue.comment": async () => ({
        ...ok,
        degraded: { status: 429, reason: "throttled", retryAfterSeconds: 4 },
      }),
    }).onExecute(context({ capabilityId: "vcs.issue.comment" }, captured));
    expect(outcome).toEqual({ kind: "completed" });
    const manifest = dataOf(captured.events[0]!)["resultManifest"];
    expect(manifest).toMatchObject({
      structuredResult: {
        url: "https://x/42",
        degraded: { status: 429, reason: "throttled", retryAfterSeconds: 4 },
      },
    });
  });

  it("the command's traceparent rides the published envelope", async () => {
    const captured: Captured = { progress: [], events: [] };
    await hooks({ "vcs.issue.comment": async () => ok }).onExecute(
      context(
        { capabilityId: "vcs.issue.comment" },
        captured,
        "00-0af7651916cd43dd8448eb211c80319c-b7ad6b7169203331-01",
      ),
    );
    expect(captured.events[0]?.["traceparent"]).toBe(
      "00-0af7651916cd43dd8448eb211c80319c-b7ad6b7169203331-01",
    );
  });
});
