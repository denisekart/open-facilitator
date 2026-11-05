// Live smoke (non-gated): the REAL omp CLI end-to-end through the SDK.
// Boots the stub kernel + the in-process Gateway child-listener (the
// conformance kit's own scaffolding), attaches the built worker's hooks,
// registers coding.omp, and drives ONE assignment: prompt → progress →
// completed with manifest ref+digest (or an honest failed reason).
// Env knobs: OFAC_SMOKE_PROMPT (default tiny), OFAC_SMOKE_MODEL,
// OFAC_SMOKE_MCP=1 (inject ask_user; questions auto-answer "ok").
// Exits nonzero with a readable reason when no omp CLI / no provider.
import { spawnSync } from "node:child_process";
import { mkdtempSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { createAdapter } from "@ofac/adapter";
import { startStubKernel } from "@ofac/adapter/testing";
import { ChildFleet, startChildListener } from "@ofac/gateway";
import { createConsoleLogger } from "@ofac/workspace-link";
import { createOmpHooks, FileSessionStore } from "../dist/index.js";

const log = createConsoleLogger({ level: "info" });
const PROMPT = process.env["OFAC_SMOKE_PROMPT"] ?? "Reply with the single word OK. No tools.";
const TIMEOUT_MS = Number(process.env["OFAC_SMOKE_TIMEOUT_MS"] ?? 180_000);

const version = spawnSync("omp", ["--version"], { encoding: "utf8" });
if (version.status !== 0) fail("no `omp` CLI on PATH — install oh-my-pi (or set PATH) first");
const models = spawnSync("omp", ["models"], { encoding: "utf8" });
// "provider (N)" header lines; zero across the board ⇒ no provider.
const modelCount = [...(models.stdout ?? "").matchAll(/\((\d+)\)/g)].reduce(
  (sum, m) => sum + Number(m[1]),
  0,
);
if (models.status !== 0 || modelCount === 0) {
  fail("no provider configured — `omp models` lists zero models (configure e.g. litellm/credentials)");
}

function fail(reason) {
  process.stderr.write(`live-smoke FAILED: ${reason}\n`);
  process.exit(2);
}

const kernel = await startStubKernel();
const workDir = mkdtempSync(join(tmpdir(), "ofac-omp-smoke-"));
const socketPath = join(workDir, "child.sock");
const linkReady = Promise.withResolvers();
const listener = await startChildListener(
  { socket: { path: socketPath }, handshakeTimeoutSeconds: 10, leaseMissedBeats: 1000 },
  { fleet: new ChildFleet(log), logger: log, gatewayId: "gw.smoke", heartbeatSeconds: 30,
    onRegistered: (link) => linkReady.resolve(link) },
);
const handle = await createAdapter({
  childWorkspaceId: "gw.smoke/omp",
  manifest: {
    apiVersion: "facilitator.framework/v1",
    kind: "CapabilityManifest",
    ownerTeam: "team-alpha",
    capabilities: [{
      id: "coding.omp", version: 1, execution: "asynchronous",
      acceptedContexts: ["uri-reference"], outputKinds: ["analysis.report"],
      outputMediaTypes: ["text/markdown"], supportsSteering: true,
      supportsCancellation: true, supportsNudge: false, supportsInteraction: true,
    }],
  },
  kernel: { baseUrl: kernel.url },
  transport: { socketPath },
  descriptor: { invocation: "sdk", supports: { steer: true, interrupt: true, resume: "restart-with-context" } },
  hooks: createOmpHooks({
    childWorkspaceId: "gw.smoke/omp", workDir, stepMs: 0, extraArgs: [],
    interactivity: process.env["OFAC_SMOKE_MCP"] === "1" ? "mcp" : "off",
    ...(process.env["OFAC_SMOKE_MODEL"] !== undefined ? { model: process.env["OFAC_SMOKE_MODEL"] } : {}),
    invocation: { command: "omp" }, // the REAL CLI
    sessionStore: new FileSessionStore(workDir),
    logger: log,
  }),
  heartbeatSeconds: 30,
  restartBackoffMs: [],
  logger: log,
});
await handle.ready();
const link = await linkReady.promise;
const done = Promise.withResolvers();
link.setPublisher(async (env) => {
  if (env.type === "org.facilitator.event.work.progress.v1") {
    console.log(`progress: ${env.data.progressNote}`);
  } else if (env.type === "org.facilitator.event.assignment.input.required.v1") {
    console.log(`input.required: ${env.data.question} (auto-answering 'ok')`);
    void link.send({ kind: "respond", executionId: env.data.executionId, requestId: env.data.requestId, text: "ok" });
  } else if (env.type === "org.facilitator.event.assignment.completed.v1") {
    done.resolve(env);
  } else if (env.type === "org.facilitator.event.assignment.failed.v1") {
    done.reject(new Error(`assignment.failed: ${env.data.reason}`));
  }
});

const command = {
  specversion: "1.0", id: "smoke-env", source: "urn:facilitator:instance:smoke",
  type: "org.facilitator.command.work.assign.v1",
  subject: "work/smoke-work/assignment/smoke-asg", time: new Date().toISOString(),
  datacontenttype: "application/json",
  dataschema: "https://schemas.openfacilitator.org/v1/command.work.assign.v1.json",
  traceparent: "00-4bf92f3577b34da6a3ce929d0e0e4736-00f067aa0ba902b7-01",
  data: {
    commandId: "smoke-cmd", actorEvidenceRef: "identity-evidence:smoke",
    workId: "smoke-work", targetId: "smoke", capabilityId: "coding.omp",
    expectedContextVersion: 1, executionGeneration: 1,
    inputs: { objective: PROMPT, sourceRefs: [] },
    expectedDeliverables: [{ kind: "analysis.report", mediaType: "text/markdown" }],
    deadline: "2036-10-23T18:00:00Z", assignmentId: "smoke-asg",
  },
};
const start = await link.send({ kind: "start", executionId: "smoke-exec", executionGeneration: 1, command });
if (start.kind !== "accepted") fail(`start refused: ${JSON.stringify(start)}`);
console.log("assign accepted — running the REAL omp prompt (may take ~30s)...");

const timer = setTimeout(() => done.reject(new Error(`timeout after ${TIMEOUT_MS}ms — provider stalling?`)), TIMEOUT_MS);
try {
  const completed = await done.promise;
  console.log(`completed: resultManifestRef=${completed.data.resultManifestRef} resultDigest=${completed.data.resultDigest}`);
} catch (error) {
  clearTimeout(timer);
  await handle.close();
  await listener.close();
  await kernel.close();
  fail(error instanceof Error ? error.message : String(error));
}
clearTimeout(timer);
await handle.close();
await listener.close();
await kernel.close();
console.log("live-smoke OK — real omp turn completed end-to-end (progress → completed + manifest).");
