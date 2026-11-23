// The playbook run driven in-process: a fake RunContext + the stub kernel
// (the artifact byte path). Proves the end-to-end law of the hooks —
// sourceRefs land as attachments (unreadable refs never fail the run),
// captures upload, the completed event carries the manifest ref + kernel
// digest, a failed expect publishes nothing and returns the honest reason,
// cancel publishes nothing, and the shim seeding writes the MCP config.
import { mkdtempSync, existsSync, readFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { createConsoleLogger } from "@ofac/workspace-link";
import { startStubKernel, type StubKernel } from "@ofac/adapter/testing";
import { createArtifactClient } from "@ofac/adapter";
import type { EnvelopeCommand, EnvelopeEvent } from "@ofac/protocol";
import type { RunContext } from "@ofac/adapter";
import { afterAll, describe, expect, it } from "vitest";
import { createCliHooks, type CliRunOptions } from "../src/harness.js";
import { parsePlaybook } from "../src/playbook/load.js";
import type { Playbook } from "../src/playbook/schema.js";

const FAKE = join(import.meta.dirname, "..", "sample", "fake-tool.mjs");
const stubKernel: StubKernel = await startStubKernel();
afterAll(async () => {
  await stubKernel.close();
});

const PLAYBOOK = `
apiVersion: facilitator.framework/v1
kind: Playbook
ownerTeam: team-alpha
capabilities:
  - id: research.analyze
    version: 1
    execution: asynchronous
    acceptedContexts: [uri-reference]
    outputKinds: [analysis.report]
    outputMediaTypes: [text/markdown]
    askUserInject: { file: .mcp.json, format: json }
    steps:
      - name: analyze
        run: { command: node, args: ["${FAKE}", report, report.md] }
        expect: { exit: 0, fileExists: report.md }
      - name: draft
        run: { command: node, args: ["${FAKE}", report] }
        expect: { exit: 0 }
        capture: { as: report, from: stdout, mediaType: text/markdown }
  - id: demo.broken
    version: 1
    execution: asynchronous
    acceptedContexts: [uri-reference]
    outputKinds: [demo.out]
    outputMediaTypes: [text/plain]
    steps:
      - { name: wants-three, run: { command: node, args: ["${FAKE}", echo, --fail, "7"] }, expect: { exit: 3 } }
`;

const playbook: Playbook = parsePlaybook(PLAYBOOK);

const BASE_CONFIG: Omit<CliRunOptions, "logger"> = {
  childWorkspaceId: "gw.kit/cli",
  workDir: mkdtempSync(join(tmpdir(), "cli-hooks-")),
  killGraceMs: 200,
  askUser: "off",
};

interface FakeRun {
  readonly ctx: RunContext;
  readonly executionId: string;
  readonly events: EnvelopeEvent[];
  readonly progress: string[];
}

function fakeRun(commandData: Record<string, unknown>, answer = "red"): FakeRun {
  const events: EnvelopeEvent[] = [];
  const progress: string[] = [];
  const executionId = `exec-${String(Math.random()).slice(2, 10)}`;
  const workId = String(commandData["workId"] ?? "w");
  const assignmentId = String(commandData["assignmentId"] ?? "a");
  const command = {
    specversion: "1.0",
    id: `env-${executionId}`,
    source: "urn:facilitator:instance:t",
    type: "org.facilitator.command.work.assign.v1",
    subject: `work/${workId}/assignment/${assignmentId}`,
    time: "2036-10-23T18:00:00Z",
    datacontenttype: "application/json",
    dataschema: "https://schemas.openfacilitator.org/v1/command.work.assign.v1.json",
    data: commandData,
  } as unknown as EnvelopeCommand;
  const ctx = {
    executionId,
    executionGeneration: 1,
    command,
    signal: new AbortController().signal,
    publishEvent: (env: EnvelopeEvent) => {
      events.push(env);
    },
    publishProgress: (note: string) => {
      progress.push(note);
    },
    setSteered: () => undefined,
    requestInput: async () => ({ text: answer }),
    kernel: { baseUrl: stubKernel.url, token: "stub-token" },
  } as unknown as RunContext;
  return { ctx, executionId, events, progress };
}

describe("cli hooks", () => {
  it("runs a capture playbook, uploads artifacts and publishes the completed event", async () => {
    const hooks = createCliHooks(
      { ...BASE_CONFIG, logger: createConsoleLogger({ level: "error" }) },
      playbook,
    );
    const { ctx, events } = fakeRun({
      workId: "w-9",
      assignmentId: "a-9",
      capabilityId: "research.analyze",
      inputs: { objective: "why" },
    });
    const outcome = await hooks.onExecute(ctx);
    expect(outcome).toEqual({ kind: "completed" });
    expect(events).toHaveLength(1);
    const event = events[0] as Record<string, Record<string, unknown>>;
    expect(event["type"]).toBe("org.facilitator.event.assignment.completed.v1");
    const payload = (event["data"] ?? event["payload"]) as Record<string, unknown>;
    expect(String(payload["resultManifestRef"])).toContain("artifact://w-9/a-9/");
    expect(String(payload["resultDigest"])).toMatch(/^sha256:/);
    // The report and the manifest both landed on the kernel.
    const reports = [...stubKernel.artifacts.values()].filter(
      (a) => a.mediaType === "text/markdown",
    );
    expect(reports.some((a) => a.bytes.toString().includes("Fake report"))).toBe(true);
    const manifests = [...stubKernel.artifacts.values()].filter(
      (a) => a.mediaType === "application/json",
    );
    expect(manifests.length).toBeGreaterThan(0);
  });

  it("unreadable sourceRefs degrade to marker lines, never failing the run", async () => {
    const hooks = createCliHooks(
      { ...BASE_CONFIG, logger: createConsoleLogger({ level: "error" }) },
      playbook,
    );
    const { ctx, events } = fakeRun({
      workId: "w-8",
      assignmentId: "a-8",
      capabilityId: "research.analyze",
      inputs: {
        objective: "why",
        sourceRefs: [{ artifactId: "does-not-exist", mediaType: "image/png" }],
      },
    });
    const outcome = await hooks.onExecute(ctx);
    expect(outcome.kind).toBe("completed");
    expect(events).toHaveLength(1);
  });

  it("a readable sourceRef lands under attachments/<index>", async () => {
    const seeded = await uploadToStub(stubKernel, Buffer.from("PNGDATA"), "image/png");
    const hooks = createCliHooks(
      { ...BASE_CONFIG, logger: createConsoleLogger({ level: "error" }) },
      playbook,
    );
    const { ctx, executionId } = fakeRun({
      workId: "w-7",
      assignmentId: "a-7",
      capabilityId: "research.analyze",
      inputs: { objective: "why", sourceRefs: [{ artifactId: seeded, mediaType: "image/png" }] },
    });
    const outcome = await hooks.onExecute(ctx);
    expect(outcome.kind).toBe("completed");
    expect(
      readFileSync(join(BASE_CONFIG.workDir, "runs", executionId, "attachments", "1"), "utf8"),
    ).toBe("PNGDATA");
  });

  it("an expect miss fails with the step-named reason and publishes no event", async () => {
    const hooks = createCliHooks(
      { ...BASE_CONFIG, logger: createConsoleLogger({ level: "error" }) },
      playbook,
    );
    const { ctx, events } = fakeRun({
      workId: "w-6",
      assignmentId: "a-6",
      capabilityId: "demo.broken",
      inputs: { objective: "why" },
    });
    const outcome = await hooks.onExecute(ctx);
    expect(outcome).toEqual({
      kind: "failed",
      reason: "step 'wants-three': exit code 7, expected 3",
    });
    expect(events).toHaveLength(0);
  });

  it("an unknown capabilityId fails honestly", async () => {
    const hooks = createCliHooks(
      { ...BASE_CONFIG, logger: createConsoleLogger({ level: "error" }) },
      playbook,
    );
    const { ctx, events } = fakeRun({
      workId: "w-5",
      assignmentId: "a-5",
      capabilityId: "no.such",
      inputs: {},
    });
    const outcome = await hooks.onExecute(ctx);
    expect(outcome).toEqual({ kind: "failed", reason: "no playbook capability 'no.such'" });
    expect(events).toHaveLength(0);
  });

  it("askUser: mcp seeds the capability's MCP config file in the run dir", async () => {
    const workDir = mkdtempSync(join(tmpdir(), "cli-hooks-mcp-"));
    const hooks = createCliHooks(
      {
        ...BASE_CONFIG,
        workDir,
        askUser: "mcp",
        logger: createConsoleLogger({ level: "error" }),
      },
      playbook,
    );
    const { ctx, executionId } = fakeRun({
      workId: "w-4",
      assignmentId: "a-4",
      capabilityId: "research.analyze",
      inputs: { objective: "why" },
    });
    const outcome = await hooks.onExecute(ctx);
    expect(outcome.kind).toBe("completed");
    const seeded = join(workDir, "runs", executionId, ".mcp.json");
    expect(existsSync(seeded)).toBe(true);
    const config = JSON.parse(readFileSync(seeded, "utf8")) as {
      mcpServers: { ofac: { env: { OFAC_MCP_BRIDGE: string } } };
    };
    expect(config.mcpServers["ofac"]?.env["OFAC_MCP_BRIDGE"]).toMatch(/^127\.0\.0\.1:\d+$/);
  });
});

async function uploadToStub(kernel: StubKernel, bytes: Buffer, mediaType: string): Promise<string> {
  const client = createArtifactClient({ baseUrl: kernel.url, token: "stub-token" });
  const receipt = await client.upload(bytes, {
    mediaType,
    workId: "seed",
    assignmentId: "seed",
    retentionPolicy: "30d",
  });
  return receipt.artifactId;
}
