// The lifecycle contract kit, run against a MINIMAL in-repo adapter: the
// kit's first self-consumer. Sibling harnesses (deterministic, codex, omp)
// point the kit at their native hooks; this file points it at the SDK with
// hand-rolled hooks, so every leg of the kit itself — hold/release, the
// steer law in both replies, stop-with-no-terminal-event, dedupe, the
// status phases, the interaction loop, the context pass-through, and the
// restart/reconnect law — is proven to assert, not just run. The kit owns
// the Gateway side (an in-process startChildListener); this file only
// boots adapters, exactly like a third-party consumer would.
import {
  artifactUri,
  buildCompletedEvent,
  buildResultManifest,
  createAdapter,
  createArtifactClient,
  type AdapterHandle,
  type HarnessHooks,
  type RunContext,
} from "../src/index.js";
import {
  kitSocketPath,
  runAdapterLifecycleContract,
  startStubKernel,
  untilAborted,
  type AdapterRunGate,
  type StubKernel,
} from "../src/testing.js";
import { createConsoleLogger } from "@ofac/workspace-link";
import { afterAll, expect } from "vitest";
import type { CapabilityManifestV1 } from "@ofac/protocol";

const MANIFEST: CapabilityManifestV1 = {
  apiVersion: "facilitator.framework/v1",
  kind: "CapabilityManifest",
  ownerTeam: "team-kit",
  capabilities: [
    {
      id: "research.analyze",
      version: 1,
      execution: "asynchronous",
      acceptedContexts: ["uri-reference"],
      outputKinds: ["analysis.report"],
      outputMediaTypes: ["text/markdown"],
      supportsSteering: true,
      supportsCancellation: true,
      supportsNudge: false,
    },
  ],
};

const CHILD_ID = "gw.kit/minimal";

// The minimal harness completes over the kernel artifact path, so every
// adapter booted here needs a kernel surface — the stub stands in (and the
// stub kernel's own legs are pinned in kit.test.ts).
const stubKernel: StubKernel = await startStubKernel();

/** Directives the steer-able runs received (the afterSteer proof). */
const steersReceived: string[] = [];

function idsOf(ctx: RunContext): { workId: string; assignmentId: string } {
  const data = ctx.command.data as Record<string, unknown>;
  return {
    workId: String(data["workId"] ?? ctx.executionId),
    assignmentId: String(data["assignmentId"] ?? ctx.executionId),
  };
}

/** The run's report: uploaded so the completed event's ref names a real row. */
async function publishCompleted(ctx: RunContext, note: string): Promise<void> {
  const { workId, assignmentId } = idsOf(ctx);
  const report = new TextEncoder().encode(`# kit report\n${note}\n`);
  const client = createArtifactClient({ baseUrl: ctx.kernel.baseUrl, token: ctx.kernel.token });
  const upload = await client.upload(report, {
    mediaType: "text/markdown",
    retentionPolicy: "90d",
    actorEvidenceRef: `workload:${CHILD_ID}`,
  });
  const manifest = buildResultManifest({
    manifestId: `man-${ctx.executionId}`,
    workId,
    assignmentId,
    outputs: [
      {
        artifactId: upload.artifactId,
        mediaType: "text/markdown",
        size: report.byteLength,
        digest: upload.digest,
        artifactUri: artifactUri(workId, assignmentId, upload.artifactId),
        createdBy: `workspace:${CHILD_ID}`,
        createdAt: new Date().toISOString(),
      },
    ],
    createdAt: new Date().toISOString(),
  });
  ctx.publishEvent(
    buildCompletedEvent(
      ctx,
      { manifestId: `man-${ctx.executionId}`, workId, assignmentId },
      {
        manifestRef: artifactUri(workId, assignmentId, upload.artifactId),
        digest: manifest.digest,
      },
    ),
  );
}

interface MinimalHookOptions {
  readonly gate: AdapterRunGate;
  /** When set, the run asks the human this question before completing. */
  readonly askQuestion?: string;
  /** False ⇒ no onSteer hook: the kit must see the locked `unsupported`. */
  readonly steerable?: boolean;
}

/**
 * The kit's determinism seam (law): hold at the gate, race the cooperative
 * abort, and publish the harness's OWN completed event once the run survives.
 */
function minimalHooks(opts: MinimalHookOptions): HarnessHooks {
  return {
    onExecute: async (ctx: RunContext) => {
      ctx.publishProgress("kit-minimal: started");
      await Promise.race([opts.gate.hold(ctx), untilAborted(ctx.signal)]);
      if (ctx.signal.aborted) return { kind: "failed", reason: "kit: aborted" };
      let note = "held, released, done";
      if (opts.askQuestion !== undefined) {
        const answer = await ctx.requestInput({
          question: opts.askQuestion,
          choices: ["kit-choice"],
        });
        note = `answered: ${answer.text}`;
      }
      await publishCompleted(ctx, note);
      return { kind: "completed", usage: { promptTokens: 7, completionTokens: 3 } };
    },
    ...(opts.steerable === false
      ? {}
      : {
          onSteer: (directive: string): void => {
            steersReceived.push(directive);
          },
        }),
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

// ── instantiation 1: steer-able, interaction leg ON (the hook asks) ────────
// The askQuestion hook drives the kit's interaction loop (input.required
// observed, respond unlocks the live run) and the auto-answer keeps every
// other leg's run completing after release.
const mainPath = kitSocketPath();
runAdapterLifecycleContract({
  socketPath: mainPath,
  gatewayId: "gw.kit",
  interactionAnswer: "kit says: minimal",
  startAdapter: async (gate) =>
    start(mainPath, minimalHooks({ gate, askQuestion: "which minimal kit?" })),
  afterSteer: (directive) => {
    // Consumer-side proof the steer reached the harness.
    expect(steersReceived).toContain(directive);
  },
});

// ── instantiation 2: no-steer adapter + interaction-leg opt-out ────────────
// No onSteer hook ⇒ the locked answer is `unsupported` (reported, never
// reinterpreted); a hook that can never answer proves the opt-out passes.
const noSteerPath = kitSocketPath();
runAdapterLifecycleContract({
  socketPath: noSteerPath,
  gatewayId: "gw.kit",
  steerReply: "unsupported",
  interactionLeg: false,
  startAdapter: async (gate) => start(noSteerPath, minimalHooks({ gate, steerable: false })),
});

// ── instantiation 3: steer-able, context leg ON, hook never asks ────────────
// The kit's context-bearing assign proves the pass-through law: the SDK
// parses inputs.context.document, this consumer's hook sees ctx.context.
// The interaction leg stays ON with a hook that never asks: the kit's
// honest-`unknown` branch (a live run with no pending question) must pass.
const contextPath = kitSocketPath();
runAdapterLifecycleContract({
  socketPath: contextPath,
  gatewayId: "gw.kit",
  contextLeg: true,
  startAdapter: async (gate) => start(contextPath, minimalHooks({ gate })),
});

afterAll(async () => {
  await stubKernel.close();
});
