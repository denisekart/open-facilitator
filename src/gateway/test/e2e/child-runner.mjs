// E2E child process: a real second process attaching over the unix socket
// with `connectChild` from the BUILT dist (the exact downstream consumer
// contract). Modes:
//   --mode complete  reply accepted + publish assignment.completed (echo law)
//   --mode silent    reply accepted, publish nothing (stub law: stays running)
// SIGTERM/exit → the process dies; the Gateway's lease + reconcile law does
// the rest. No reconnect: Plan 07's adapter owns restart policy.
import { connectChild } from "@ofac/gateway";
import { buildEvent } from "@ofac/workspace-link";

const args = process.argv.slice(2);
const flag = (name) => {
  const i = args.indexOf(name);
  return i >= 0 ? args[i + 1] : undefined;
};
const childWorkspaceId = flag("--child");
const socket = flag("--socket");
const token = flag("--token") ?? "tok";
const mode = flag("--mode") ?? "silent";
// Plan 12: repeatable --capability so one child can advertise several ids.
const capabilities = args.filter((_a, i) => args[i - 1] === "--capability");

const manifest = {
  apiVersion: "facilitator.framework/v1",
  kind: "CapabilityManifest",
  ownerTeam: childWorkspaceId.split("/").pop(),
  capabilities: capabilities.map((capability) => ({
    id: capability,
    version: 1,
    execution: "asynchronous",
    acceptedContexts: ["uri-reference"],
    outputKinds: [`${capability.replaceAll(".", "-")}.result`],
    outputMediaTypes: ["text/markdown"],
    supportsSteering: false,
    supportsCancellation: false,
    supportsNudge: false,
  })),
};

// A fresh process forgets everything: the reconcile probe must honestly
// answer `unknown` for executions only a dead predecessor ran.
const executions = new Set();
let lastCommand = null;
const conn = await connectChild({
  childWorkspaceId,
  token,
  manifest,
  path: socket,
  heartbeatSeconds: 2,
  async onCommand(frame) {
    if (frame.message.kind === "status") {
      return executions.has(frame.message.executionId)
        ? { kind: "status", executionId: frame.message.executionId, state: "running" }
        : { kind: "unknown", executionId: frame.message.executionId };
    }
    if (frame.message.kind !== "start")
      return { kind: "accepted", executionId: frame.message.executionId };
    executions.add(frame.message.executionId);
    lastCommand = frame.message.command;
    if (mode === "complete") {
      const data = lastCommand.data;
      await conn.publishEvent(
        buildEvent("org.facilitator.event.assignment.completed.v1", {
          source: `urn:workspace:${childWorkspaceId}`,
          subject: lastCommand.subject,
          data: {
            workId: data.workId,
            assignmentId: data.assignmentId,
            executionId: frame.message.executionId,
            executionGeneration: 1,
            resultManifest: { manifestId: `man-${frame.requestId}`, outputs: [] },
          },
        }),
      );
    }
    return { kind: "accepted", executionId: frame.message.executionId };
  },
});
process.stdout.write(`child ready ${childWorkspaceId}\n`);
process.on("SIGTERM", () => process.exit(0));
await conn.waitClosed();
process.exit(0);
