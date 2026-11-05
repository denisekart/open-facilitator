#!/usr/bin/env node
// Live smoke (non-gated, manual): the REAL codex CLI through the worker's
// own seams — stub kernel + in-process Gateway listener + one assignment
// end-to-end. Honest-skip law: no codex binary on PATH or no resolvable
// OpenAI credential ⇒ print the skip line, exit 0 (CI has neither).
// Run: pnpm build && node scripts/live-smoke.mjs
import { spawnSync } from "node:child_process";
import { mkdtempSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { pathToFileURL } from "node:url";

function skip(reason) {
  process.stdout.write(`live smoke not exercised — fake-CLI coverage only (${reason})\n`);
  process.exit(0);
}

const which = spawnSync("sh", ["-c", "command -v codex"], { encoding: "utf8" });
if (which.status !== 0) skip("codex binary not on PATH");
const key = process.env.OPENAI_API_KEY ?? process.env.CODEX_API_KEY;
if (key === undefined || key.length === 0) skip("no OpenAI credential resolved");

// Real leg — dist must be built (pnpm build).
const { startStubKernel } = await import("@ofac/adapter/testing");
const { createAdapter } = await import("@ofac/adapter");
const { ChildFleet, startChildListener } = await import("@ofac/gateway");
const { createConsoleLogger } = await import("@ofac/workspace-link");
const { createCodexHooks } = await import(
  pathToFileURL(join(import.meta.dirname, "..", "dist", "index.js")).href
);

const log = createConsoleLogger({ level: "info" });
const kernel = await startStubKernel();
const socketPath = join(mkdtempSync(join(tmpdir(), "cx-smoke-")), "child.sock");
const workDir = mkdtempSync(join(tmpdir(), "cx-smoke-work-"));

const linkReady = Promise.withResolvers();
const listener = await startChildListener(
  { socket: { path: socketPath }, handshakeTimeoutSeconds: 10, leaseMissedBeats: 1000 },
  {
    fleet: new ChildFleet(log),
    logger: log,
    gatewayId: "gw.smoke",
    heartbeatSeconds: 30,
    onRegistered: (link) => linkReady.resolve(link),
  },
);
const manifest = {
  apiVersion: "facilitator.framework/v1",
  kind: "CapabilityManifest",
  ownerTeam: "team-smoke",
  capabilities: [
    {
      id: "coding.codex",
      version: 1,
      execution: "asynchronous",
      acceptedContexts: ["uri-reference"],
      outputKinds: ["coding.final"],
      outputMediaTypes: ["text/markdown"],
      supportsSteering: false,
      supportsCancellation: true,
      supportsInteraction: false, // a live human is not seated for a smoke
      supportsNudge: false,
    },
  ],
};
const handle = await createAdapter({
  childWorkspaceId: "gw.smoke/codex",
  manifest,
  kernel: { baseUrl: kernel.url },
  transport: { socketPath },
  descriptor: { invocation: "sdk", supports: { steer: false, interrupt: true, resume: "restart-with-context" } },
  hooks: createCodexHooks({
    childWorkspaceId: "gw.smoke/codex",
    workDir,
    logger: log,
    promptTemplate: "{objective}\nKeep it to a few sentences.",
  }),
  heartbeatSeconds: 30,
  restartBackoffMs: [],
  logger: log,
});
await handle.ready();
const link = await linkReady.promise;
const terminal = Promise.withResolvers();
link.setPublisher(async (env) => {
  if (
    env.type === "org.facilitator.event.assignment.completed.v1" ||
    env.type === "org.facilitator.event.assignment.failed.v1"
  ) {
    terminal.resolve(env);
  }
});

const command = {
  specversion: "1.0",
  id: "smoke-assign",
  source: "urn:facilitator:instance:smoke",
  type: "org.facilitator.command.work.assign.v1",
  subject: "work/smoke-work/assignment/smoke-asg",
  time: new Date().toISOString(),
  datacontenttype: "application/json",
  dataschema: "https://schemas.openfacilitator.org/v1/command.work.assign.v1.json",
  data: {
    commandId: "smoke-cmd",
    actorEvidenceRef: "identity-evidence:smoke",
    workId: "smoke-work",
    targetId: "smoke",
    capabilityId: "coding.codex",
    expectedContextVersion: 1,
    executionGeneration: 1,
    inputs: { objective: "Say hello in one line and stop.", sourceRefs: [] },
    expectedDeliverables: [{ kind: "coding.final", mediaType: "text/markdown" }],
    deadline: new Date(Date.now() + 600_000).toISOString(),
    assignmentId: "smoke-asg",
  },
};
const reply = await link.send({ kind: "start", executionId: "smoke-exec", executionGeneration: 1, command });
if (reply.kind !== "accepted") {
  process.stdout.write(`live smoke FAILED: start reply ${JSON.stringify(reply)}\n`);
  process.exitCode = 1;
} else {
  const env = await Promise.race([
    terminal.promise,
    new Promise((_, reject) => setTimeout(() => reject(new Error("timeout after 10 min")), 600_000)),
  ]).catch((e) => {
    process.stdout.write(`live smoke FAILED: ${String(e)}\n`);
    process.exitCode = 1;
    return undefined;
  });
  if (env !== undefined) {
    const data = env.data;
    process.stdout.write(
      `live smoke ${env.type.endsWith("completed.v1") ? "OK" : "SETTLED-FAILED"}: ${env.type} ` +
        `manifestRef=${String(data.resultManifestRef ?? "-")} digest=${String(data.resultDigest ?? data.reason ?? "-")}\n`,
    );
    if (!env.type.endsWith("completed.v1")) process.exitCode = 1;
  }
}
await handle.close();
await listener.close();
await kernel.close();
