// The lifecycle contract kit, run against the REAL @ofac/adapter with the
// coding harness's hooks (gitless mode: the kit's stub assign carries no
// repo, so the git phases are skipped and the completed publish still
// rides), and a second time with a no-steer hooks variant expecting the
// `unsupported` reply. The kit owns the Gateway side (an in-process
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
  startStubKernel,
  untilAborted,
  type AdapterRunGate,
  type StubKernel,
} from "@ofac/adapter/testing";
import { createConsoleLogger } from "@ofac/workspace-link";
import { afterAll } from "vitest";
import { createCodingHooks } from "../src/harness.js";
import type { CapabilityManifestV1 } from "@ofac/protocol";

const MANIFEST: CapabilityManifestV1 = {
  apiVersion: "facilitator.framework/v1" as const,
  kind: "CapabilityManifest" as const,
  ownerTeam: "team-coding" as const,
  capabilities: [
    {
      id: "coding.implement",
      version: 1,
      execution: "asynchronous" as const,
      acceptedContexts: ["uri-reference"],
      outputKinds: ["coding.change"],
      outputMediaTypes: ["text/markdown"],
      supportsSteering: true,
      supportsCancellation: true,
      supportsNudge: false,
    },
  ],
};

const CHILD_ID = "gw.coding/coding-worker";

// The harness completes over the kernel artifact path, so every adapter
// booted here needs a kernel surface — the stub stands in.
const stubKernel: StubKernel = await startStubKernel();

// gitless: the kit's assign envelope (verbatim from testing.ts) carries
// `inputs: {objective, sourceRefs}` — no repo — and the kit pins
// capabilityId `research.analyze`; the coding run's git phases are
// therefore skipped while the artifact upload + completed publish — the
// parts the kit asserts — ride for real.
function baseHooks(workDir: string): HarnessHooks {
  return createCodingHooks({
    childWorkspaceId: CHILD_ID,
    workDir,
    forgejoUrl: "http://forgejo.test:3000",
    tokenFile: "/nonexistent/pat",
    authorName: "facilitator-bot",
    authorEmail: "bot@openfacilitator.org",
    gitless: true,
    logger: createConsoleLogger({ level: "error" }),
  });
}

/**
 * The kit's determinism seam: the run holds at the gate until the kit
 * releases it (or a stop aborts it), THEN the real run proceeds — in-flight
 * assertions see a live execution, completion assertions see the real
 * completed event + manifest.
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

const socketPath = join(mkdtempSync(join(tmpdir(), "ofac-cw-kit-")), "child.sock");
const workDir = mkdtempSync(join(tmpdir(), "ofac-cw-kit-work-"));

runAdapterLifecycleContract({
  socketPath,
  gatewayId: "gw.coding",
  startAdapter: async (gate) => start(socketPath, gated(baseHooks(workDir), gate)),
});

// Second instantiation: a no-steer adapter (onSteer absent, setSteered
// never called) must answer the locked `unsupported` — the kit asserts.
const noSteerPath = join(mkdtempSync(join(tmpdir(), "ofac-cw-kit2-")), "child.sock");
const noSteerWork = mkdtempSync(join(tmpdir(), "ofac-cw-kit2-work-"));

runAdapterLifecycleContract({
  socketPath: noSteerPath,
  gatewayId: "gw.coding",
  steerReply: "unsupported",
  startAdapter: async (gate) => {
    const { onSteer, ...noSteer } = baseHooks(noSteerWork);
    void onSteer;
    return start(noSteerPath, gated(noSteer, gate));
  },
});

afterAll(async () => {
  await stubKernel.close();
});
