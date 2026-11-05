// The lifecycle contract kit, run against the REAL @ofac/adapter with the
// omp hooks driving the fake-omp fixture (interactivity off: the kit's own
// question leg is the askQuestion hook; the MCP round-trip is proven in
// harness.test). Main instantiation: interaction leg ON + steer accepted.
// Second instantiation: a no-steer hooks variant expecting the locked
// `unsupported` reply, with the interaction-leg opt-out (hooks that can
// never answer).
import { mkdtempSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";
import {
  createAdapter,
  type AdapterHandle,
  type HarnessHooks,
  type RunContext,
} from "@ofac/adapter";
import {
  runAdapterLifecycleContract,
  startStubKernel,
  untilAborted,
  type AdapterRunGate,
  type StubKernel,
} from "@ofac/adapter/testing";
import { createConsoleLogger } from "@ofac/workspace-link";
import { afterAll } from "vitest";
import { createOmpHooks } from "../src/harness.js";
import { FileSessionStore } from "../src/session-store.js";
import type { CapabilityManifestV1 } from "@ofac/protocol";

const MANIFEST: CapabilityManifestV1 = {
  apiVersion: "facilitator.framework/v1" as const,
  kind: "CapabilityManifest" as const,
  ownerTeam: "team-alpha",
  capabilities: [
    {
      id: "coding.omp",
      version: 1,
      execution: "asynchronous" as const,
      acceptedContexts: ["uri-reference"],
      outputKinds: ["analysis.report"],
      outputMediaTypes: ["text/markdown"],
      supportsSteering: true,
      supportsCancellation: true,
      supportsNudge: false,
      supportsInteraction: true,
    },
  ],
};

const CHILD_ID = "gw.kit/omp";
const FAKE = new URL("./fake-omp.mjs", import.meta.url).pathname;

// The harness uploads final.md + transcript + manifest over the kernel
// artifact path, so every adapter booted here needs a kernel surface.
const stubKernel: StubKernel = await startStubKernel();

function baseHooks(workDir: string, askQuestion?: string): HarnessHooks {
  return createOmpHooks({
    childWorkspaceId: CHILD_ID,
    workDir,
    stepMs: 0,
    extraArgs: [],
    interactivity: "off",
    ...(askQuestion !== undefined ? { askQuestion } : {}),
    invocation: { command: process.execPath, prefixArgs: [FAKE] },
    sessionStore: new FileSessionStore(workDir),
    logger: createConsoleLogger({ level: "error" }),
  });
}

/**
 * The kit's determinism seam: the run holds at the gate until the kit
 * releases it (or a stop aborts it), THEN the real fake-omp run executes —
 * in-flight assertions see a live execution, completion assertions see the
 * real completed event + artifacts.
 */
function gated(hooks: HarnessHooks, gate: AdapterRunGate): HarnessHooks {
  return {
    ...hooks,
    onExecute: async (ctx: RunContext) => {
      await Promise.race([gate.hold(ctx), untilAborted(ctx.signal)]);
      return hooks.onExecute(ctx);
    },
  };
}

function start(
  socketPath: string,
  hooks: HarnessHooks,
): Promise<{ handle: AdapterHandle; stop(): Promise<void> }> {
  return createAdapter({
    childWorkspaceId: CHILD_ID,
    manifest: MANIFEST,
    kernel: { baseUrl: stubKernel.url },
    transport: { socketPath },
    descriptor: {
      invocation: "sdk",
      supports: { steer: true, interrupt: true, resume: "restart-with-context" },
    },
    hooks,
    heartbeatSeconds: 30,
    restartBackoffMs: [10, 20],
    logger: createConsoleLogger({ level: "error" }),
  }).then((handle) => ({ handle, stop: () => handle.close() }));
}

const socketPath = join(mkdtempSync(join(tmpdir(), "ofac-omp-kit-")), "child.sock");
const workDir = mkdtempSync(join(tmpdir(), "ofac-omp-kit-work-"));

// Main instantiation: the askQuestion hook exercises the kit's interaction
// leg (input.required observed, respond unlocks the live run) and the
// auto-answer keeps every other leg's run completing.
runAdapterLifecycleContract({
  socketPath,
  gatewayId: "gw.kit",
  interactionAnswer: "kit says: postgres",
  startAdapter: async (gate) =>
    start(socketPath, gated(baseHooks(workDir, "which framework?"), gate)),
});

// Second instantiation: a no-steer adapter (onSteer absent) must answer the
// locked `unsupported`, AND a consumer whose hooks can never answer proves
// the interaction-leg opt-out passes.
const noSteerPath = join(mkdtempSync(join(tmpdir(), "ofac-omp-kit2-")), "child.sock");
const noSteerWork = mkdtempSync(join(tmpdir(), "ofac-omp-kit2-work-"));

runAdapterLifecycleContract({
  socketPath: noSteerPath,
  gatewayId: "gw.kit",
  steerReply: "unsupported",
  interactionLeg: false,
  startAdapter: async (gate) => {
    const { onSteer, ...noSteer } = baseHooks(noSteerWork);
    void onSteer;
    return start(noSteerPath, gated(noSteer, gate));
  },
});

afterAll(async () => {
  await stubKernel.close();
});
