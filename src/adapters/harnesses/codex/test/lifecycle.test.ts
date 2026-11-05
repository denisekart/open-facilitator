// The lifecycle contract kit, run against the codex hooks (fake CLI):
// BOTH legs honestly. Run 1: the interaction leg ON — the askQuestion hook
// drives requestInput on a live run, the kit observes input.required and
// answers with command.assignment.input.v1, the run completes; steerReply
// 'unsupported' because this adapter has no onSteer (frozen descriptor:
// supportsSteering:false). Run 2: the interaction-leg opt-out proof with a
// hook that never asks.
import { mkdtempSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";
import type { CapabilityManifestV1 } from "@ofac/protocol";
import { createConsoleLogger } from "@ofac/workspace-link";
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
import { afterAll } from "vitest";
import { createCodexHooks } from "../src/harness.js";

const MANIFEST: CapabilityManifestV1 = {
  apiVersion: "facilitator.framework/v1" as const,
  kind: "CapabilityManifest" as const,
  ownerTeam: "team-alpha",
  capabilities: [
    {
      id: "coding.codex",
      version: 1,
      execution: "asynchronous" as const,
      acceptedContexts: ["uri-reference"],
      outputKinds: ["coding.final"],
      outputMediaTypes: ["text/markdown"],
      supportsSteering: false,
      supportsCancellation: true,
      supportsInteraction: true,
      supportsNudge: false,
    },
  ],
};

const CHILD_ID = "gw.kit/codex";
// The fake speaks the probe-shaped stream; interactivity 'off' keeps the
// kit runs free of a shim process — the MCP round-trip itself is covered
// by harness.test.
const FAKE = new URL("./fake-codex.mjs", import.meta.url).pathname;

// The harness uploads final.md + transcript + manifest over the kernel
// artifact path, so every adapter booted here needs a kernel surface.
const stubKernel: StubKernel = await startStubKernel();

function baseHooks(workDir: string, askQuestion?: string): HarnessHooks {
  return createCodexHooks({
    childWorkspaceId: CHILD_ID,
    workDir,
    codexBin: FAKE,
    interactivity: "off",
    ...(askQuestion !== undefined ? { askQuestion } : {}),
    logger: createConsoleLogger({ level: "error" }),
  });
}

/**
 * The kit's determinism seam: the run holds at the gate until the kit
 * releases it (or a stop aborts it), THEN the real spawn runs — the fake
 * completes instantly, so in-flight assertions see the held execution and
 * completion assertions see the real completed event + artifacts.
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
      supports: { steer: false, interrupt: true, resume: "restart-with-context" },
    },
    hooks,
    heartbeatSeconds: 30,
    restartBackoffMs: [10, 20],
    logger: createConsoleLogger({ level: "error" }),
  }).then((handle) => ({ handle, stop: () => handle.close() }));
}

const socketPath = join(mkdtempSync(join(tmpdir(), "ofac-cx-kit-")), "child.sock");
const workDir = mkdtempSync(join(tmpdir(), "ofac-cx-kit-work-"));

// Honest surface #1: interaction leg ON (askQuestion drives the kit's
// question leg; the auto-answer keeps every other leg's run completing),
// steer honestly 'unsupported' — the hooks have no onSteer to accept it.
runAdapterLifecycleContract({
  socketPath,
  gatewayId: "gw.kit",
  steerReply: "unsupported",
  interactionAnswer: "kit says: postgres",
  startAdapter: async (gate) =>
    start(socketPath, gated(baseHooks(workDir, "which framework?"), gate)),
});

// Honest surface #2: a consumer whose hooks can never answer proves the
// interaction-leg opt-out passes; steer stays 'unsupported'.
const noAskPath = join(mkdtempSync(join(tmpdir(), "ofac-cx-kit2-")), "child.sock");
const noAskWork = mkdtempSync(join(tmpdir(), "ofac-cx-kit2-work-"));

runAdapterLifecycleContract({
  socketPath: noAskPath,
  gatewayId: "gw.kit",
  steerReply: "unsupported",
  interactionLeg: false,
  startAdapter: async (gate) => start(noAskPath, gated(baseHooks(noAskWork), gate)),
});

afterAll(async () => {
  await stubKernel.close();
});
