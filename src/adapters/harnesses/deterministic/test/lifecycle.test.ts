// The lifecycle contract kit, run against the REAL @ofac/adapter with the
// deterministic harness's hooks (first consumer), and a second time with a
// no-steer hooks variant expecting the `unsupported` reply (proving the kit
// asserts, not just runs). The kit owns the Gateway side (an in-process
// startChildListener); this file only boots adapters.
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
  untilAborted,
  type AdapterRunGate,
} from "@ofac/adapter/testing";
import { createConsoleLogger } from "@ofac/workspace-link";
import { createDeterministicHooks } from "../src/harness.js";
import type { CapabilityManifestV1 } from "@ofac/protocol";

const MANIFEST: CapabilityManifestV1 = {
  apiVersion: "facilitator.framework/v1" as const,
  kind: "CapabilityManifest" as const,
  ownerTeam: "team-alpha",
  capabilities: [
    {
      id: "research.analyze",
      version: 1,
      execution: "asynchronous" as const,
      acceptedContexts: ["uri-reference"],
      outputKinds: ["analysis.report"],
      outputMediaTypes: ["text/markdown"],
      supportsSteering: true,
      supportsCancellation: true,
      supportsNudge: false,
    },
  ],
};

const CHILD_ID = "gw.kit/deterministic";

function baseHooks(workDir: string): HarnessHooks {
  return createDeterministicHooks({
    childWorkspaceId: CHILD_ID,
    workDir,
    stepMs: 0,
    logger: createConsoleLogger({ level: "error" }),
  });
}

/**
 * The kit's determinism seam: the run holds at the gate until the kit
 * releases it (or a stop aborts it), THEN the real two-step script runs
 * with stepMs=0 — in-flight assertions see a live execution, completion
 * assertions see the real completed event + report.
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

const socketPath = join(mkdtempSync(join(tmpdir(), "ofac-det-kit-")), "child.sock");
const workDir = mkdtempSync(join(tmpdir(), "ofac-det-kit-work-"));

runAdapterLifecycleContract({
  socketPath,
  gatewayId: "gw.kit",
  startAdapter: async (gate) => start(socketPath, gated(baseHooks(workDir), gate)),
});

// Second instantiation: a no-steer adapter (onSteer absent, setSteered
// never called) must answer the locked `unsupported` — the kit asserts.
const noSteerPath = join(mkdtempSync(join(tmpdir(), "ofac-det-kit2-")), "child.sock");
const noSteerWork = mkdtempSync(join(tmpdir(), "ofac-det-kit2-work-"));

runAdapterLifecycleContract({
  socketPath: noSteerPath,
  gatewayId: "gw.kit",
  steerReply: "unsupported",
  startAdapter: async (gate) => {
    const { onSteer, ...noSteer } = baseHooks(noSteerWork);
    void onSteer;
    return start(noSteerPath, gated(noSteer, gate));
  },
});
