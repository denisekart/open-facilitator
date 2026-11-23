// The lifecycle contract kit, run against the REAL @ofac/adapter with the
// CLI harness's playbook-driven hooks. The kit owns the Gateway side (an
// in-process startChildListener); this file only boots adapters. Two
// instantiations: the main one proves the playbook's `ask` step rides the
// interaction leg (input.required observed, respond unlocks the live run)
// and the steer leg answers the locked `unsupported` (fire-per-step, no
// mid-run channel); a context-leg variant proves the dispatch-context
// pass-through reaches a playbook run.
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
import { createCliHooks } from "../src/harness.js";
import { parsePlaybook } from "../src/playbook/load.js";
import type { Playbook } from "../src/playbook/schema.js";

const FAKE = join(import.meta.dirname, "..", "sample", "fake-tool.mjs");

// The kit assigns research.analyze with an analysis.report deliverable, so
// the kit playbook advertises exactly that — and its `ask` step gives the
// interaction leg a real requestInput to observe and answer.
const KIT_PLAYBOOK: Playbook = parsePlaybook(`
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
    steps:
      - { name: analyze, run: { command: node, args: ["${FAKE}", report] }, expect: { exit: 0 }, capture: { as: report, from: stdout, mediaType: text/markdown } }
      - name: ask
        ask: { question: which framework? }
      - name: draft
        run: { command: node, args: ["${FAKE}", report, "# kit report: {{steps.ask.answer}}"] }
        expect: { exit: 0 }
        capture: { as: draft, from: stdout, mediaType: text/markdown }
`);

const CHILD_ID = "gw.kit/cli";

// The harness completes over the kernel artifact path, so every
// adapter booted here needs a kernel surface — the stub stands in.
const stubKernel: StubKernel = await startStubKernel();

function baseHooks(workDir: string): HarnessHooks {
  return createCliHooks(
    {
      childWorkspaceId: CHILD_ID,
      workDir,
      killGraceMs: 200,
      askUser: "off",
      logger: createConsoleLogger({ level: "error" }),
    },
    KIT_PLAYBOOK,
  );
}

/**
 * The kit's determinism seam: the run holds at the gate until the kit
 * releases it (or a stop aborts it), THEN the real playbook runs —
 * in-flight assertions see a live execution, completion assertions see
 * the real completed event + artifacts.
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
    manifest: KIT_PLAYBOOK.manifest,
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

const socketPath = join(mkdtempSync(join(tmpdir(), "ofac-cli-kit-")), "child.sock");
const workDir = mkdtempSync(join(tmpdir(), "ofac-cli-kit-work-"));

// Main instantiation: fire-per-step steering means the locked `unsupported`
// reply, and the playbook's ask step rides the interaction leg (the kit's
// answer unlocks the live run and lands in the draft capture).
runAdapterLifecycleContract({
  socketPath,
  gatewayId: "gw.kit",
  steerReply: "unsupported",
  interactionAnswer: "kit says: postgres",
  startAdapter: async (gate) => start(socketPath, gated(baseHooks(workDir), gate)),
});

// Context-leg variant: the folded dispatch document must reach the run.
const contextPath = join(mkdtempSync(join(tmpdir(), "ofac-cli-kit3-")), "child.sock");
const contextWork = mkdtempSync(join(tmpdir(), "ofac-cli-kit3-work-"));
runAdapterLifecycleContract({
  socketPath: contextPath,
  gatewayId: "gw.kit",
  steerReply: "unsupported",
  contextLeg: true,
  interactionAnswer: "kit says: postgres",
  startAdapter: async (gate) => start(contextPath, gated(baseHooks(contextWork), gate)),
});

afterAll(async () => {
  await stubKernel.close();
});
