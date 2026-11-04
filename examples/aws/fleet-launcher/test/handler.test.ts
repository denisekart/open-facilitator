// The launcher law, pinned handler-level against the fake EcsApi (the oci
// tier's precedent: handler.onCommand drives the whole command path; receipts
// are the CommandOutcome the client ships). Pins: a known workloadId runs
// EXACTLY one task with the rule's cluster/task-def/subnets, the expanded
// env as container overrides, and the workload/operation/managed tags; the
// `$VAR` name-indirection law (value resolves from the launcher env, a
// missing var rejects honestly); envAllowlist gating (non-listed spec env is
// DROPPED, never forwarded); unknown workloadId ⇒ rejected receipt; the
// idempotent re-offer no-op; stop stops the tracked task and no-ops (acked)
// on anything else.
import { describe, expect, it } from "vitest";
import type { EnvelopeCommand } from "@ofac/protocol";
import type { CommandContext, LinkLogger } from "@ofac/workspace-link";
import {
  CMD_PROVISION_START,
  CMD_PROVISION_STOP,
  createProvisionHandler,
  launcherManifest,
} from "../src/handler.js";
import { parseRules, type LauncherConfig } from "../src/config.js";
import { FakeEcs } from "./fake-ecs.js";

const silent: LinkLogger = {
  debug: () => undefined,
  info: () => undefined,
  warn: () => undefined,
  error: () => undefined,
};

const ctx = { publishEvent: async () => undefined } as unknown as CommandContext;

function config(rulesJson: string): LauncherConfig {
  return {
    kernelUrl: "https://kernel.example.org",
    enrollToken: "sekret",
    endpointId: "launcher.team-alpha",
    team: "team-alpha",
    statePath: ":memory:",
    rules: parseRules(rulesJson),
    logLevel: "error",
  };
}

const RULES = JSON.stringify({
  "gw.team-alpha": {
    taskDefinition: "arn:aws:ecs:eu-west-1:222:task-definition/gw:3",
    cluster: "arn:aws:ecs:eu-west-1:222:cluster/team",
    subnets: ["subnet-1a", "subnet-1b"],
  },
  "worker.gated": {
    taskDefinition: "td-gated",
    cluster: "cl",
    envAllowlist: ["TEAM_TOKEN"],
  },
});

function startEnvelope(data: Record<string, unknown>): EnvelopeCommand {
  return { type: CMD_PROVISION_START, data } as unknown as EnvelopeCommand;
}

function stopEnvelope(data: Record<string, unknown>): EnvelopeCommand {
  return { type: CMD_PROVISION_STOP, data } as unknown as EnvelopeCommand;
}

function startData(
  spec: Record<string, unknown>,
  over: Record<string, unknown> = {},
): Record<string, unknown> {
  return {
    commandId: "cmd-1",
    operationId: "op-1",
    workloadId: "gw.team-alpha",
    team: "team-alpha",
    actorEvidenceRef: "identity-evidence:unit",
    deadline: "2036-10-23T18:00:00Z",
    spec: { image: "ignored-by-design:latest" },
    ...over,
    ...(Object.keys(spec).length > 0
      ? { spec: { image: "ignored-by-design:latest", ...spec } }
      : {}),
  };
}

function stopData(over: Record<string, unknown> = {}): Record<string, unknown> {
  return {
    commandId: "cmd-2",
    operationId: "op-2",
    workloadId: "gw.team-alpha",
    team: "team-alpha",
    actorEvidenceRef: "identity-evidence:unit",
    deadline: "2036-10-23T18:00:00Z",
    ...over,
  };
}

describe("provision handler (fake EcsApi)", () => {
  it("known workloadId: RunTask with rule-pinned placement, expanded env, tags; receipt accepted", async () => {
    const ecs = new FakeEcs();
    const handler = createProvisionHandler({
      ecs,
      config: config(RULES),
      logger: silent,
      env: { TEAM_TOKEN: "s3kr3t" },
    });

    const outcome = await handler.onCommand(
      startEnvelope(
        startData(
          { env: { TEAM_TOKEN: "$TEAM_TOKEN", PLAIN: "lit" }, entrypoint: ["/bin/worker"] },
          { commandId: "cmd-start" },
        ),
      ),
      ctx,
    );

    expect(outcome).toEqual({ kind: "accepted" });
    expect(ecs.runs).toHaveLength(1);
    const run = ecs.runs[0]!;
    expect(run.cluster).toBe("arn:aws:ecs:eu-west-1:222:cluster/team");
    expect(run.taskDefinition).toBe("arn:aws:ecs:eu-west-1:222:task-definition/gw:3");
    expect(run.subnets).toEqual(["subnet-1a", "subnet-1b"]);
    // The spec env lands as containerOverrides.environment, $-names expanded
    // from the LAUNCHER env; entrypoint rides the command override.
    expect(run.environment).toEqual([
      { name: "TEAM_TOKEN", value: "s3kr3t" },
      { name: "PLAIN", value: "lit" },
    ]);
    expect(run.command).toEqual(["/bin/worker"]);
    expect(run.startedBy).toBe("ofac-gw-team-alpha");
    expect(run.tags).toEqual([
      { key: "ofac.workload", value: "gw.team-alpha" },
      { key: "ofac.operation", value: "op-1" },
      { key: "io.facilitator.managed", value: "kernel-provisioned" },
    ]);
  });

  it("spec labels ride the ECS tags (oci label law)", async () => {
    const ecs = new FakeEcs();
    const handler = createProvisionHandler({
      ecs,
      config: config(RULES),
      logger: silent,
      env: {},
    });
    await handler.onCommand(startEnvelope(startData({ labels: { team: "alpha" } })), ctx);
    expect(ecs.runs[0]!.tags).toContainEqual({ key: "team", value: "alpha" });
  });

  it("$VAR whose name is absent from the launcher env ⇒ honest rejection, never an empty secret", async () => {
    const ecs = new FakeEcs();
    const handler = createProvisionHandler({
      ecs,
      config: config(RULES),
      logger: silent,
      env: {},
    });
    const outcome = await handler.onCommand(
      startEnvelope(startData({ env: { TEAM_TOKEN: "$TEAM_TOKEN" } })),
      ctx,
    );
    expect(outcome).toEqual({ kind: "rejected", reason: "env-unresolved:TEAM_TOKEN" });
    expect(ecs.runs).toHaveLength(0);
  });

  it("unknown workloadId ⇒ rejected receipt, zero ECS calls", async () => {
    const ecs = new FakeEcs();
    const handler = createProvisionHandler({
      ecs,
      config: config(RULES),
      logger: silent,
      env: {},
    });
    const outcome = await handler.onCommand(
      startEnvelope(startData({}, { workloadId: "gw.not-here" })),
      ctx,
    );
    expect(outcome).toEqual({ kind: "rejected", reason: "workload-not-allowed" });
    expect(ecs.runs).toHaveLength(0);
    expect(ecs.stops).toHaveLength(0);
  });

  it("envAllowlist drops non-listed spec env (the rest still expands)", async () => {
    const ecs = new FakeEcs();
    const handler = createProvisionHandler({
      ecs,
      config: config(RULES),
      logger: silent,
      env: { TEAM_TOKEN: "tok", EXTRA: "leak-me" },
    });
    const outcome = await handler.onCommand(
      startEnvelope(
        startData(
          { env: { TEAM_TOKEN: "$TEAM_TOKEN", EXTRA: "leak-me" } },
          { workloadId: "worker.gated" },
        ),
      ),
      ctx,
    );
    expect(outcome).toEqual({ kind: "accepted" });
    expect(ecs.runs[0]!.environment).toEqual([{ name: "TEAM_TOKEN", value: "tok" }]);
  });

  it("re-offer of a tracked workload ⇒ accepted no-op, exactly one task", async () => {
    const ecs = new FakeEcs();
    const handler = createProvisionHandler({
      ecs,
      config: config(RULES),
      logger: silent,
      env: {},
    });
    await handler.onCommand(startEnvelope(startData({})), ctx);
    const second = await handler.onCommand(
      startEnvelope(startData({}, { commandId: "cmd-1b" })),
      ctx,
    );
    expect(second).toEqual({ kind: "accepted" });
    expect(ecs.runs).toHaveLength(1);
  });

  it("stop ⇒ StopTask with the tracked cluster + ARN, receipt accepted", async () => {
    const ecs = new FakeEcs();
    const handler = createProvisionHandler({
      ecs,
      config: config(RULES),
      logger: silent,
      env: {},
    });
    await handler.onCommand(startEnvelope(startData({})), ctx);
    const outcome = await handler.onCommand(stopEnvelope(stopData()), ctx);
    expect(outcome).toEqual({ kind: "accepted" });
    expect(ecs.stops).toEqual([
      {
        cluster: "arn:aws:ecs:eu-west-1:222:cluster/team",
        taskArn: "arn:aws:ecs:eu-west-1:222222222222:task/fleet-1",
      },
    ]);
  });

  it("stop of a workload this generation never ran ⇒ accepted noop, no StopTask (oci law)", async () => {
    const ecs = new FakeEcs();
    const handler = createProvisionHandler({
      ecs,
      config: config(RULES),
      logger: silent,
      env: {},
    });
    const outcome = await handler.onCommand(stopEnvelope(stopData()), ctx);
    expect(outcome).toEqual({ kind: "accepted" });
    expect(ecs.stops).toHaveLength(0);
  });

  it("start→stop→start runs a fresh task (the stop untracks)", async () => {
    const ecs = new FakeEcs();
    const handler = createProvisionHandler({
      ecs,
      config: config(RULES),
      logger: silent,
      env: {},
    });
    await handler.onCommand(startEnvelope(startData({})), ctx);
    await handler.onCommand(stopEnvelope(stopData()), ctx);
    await handler.onCommand(startEnvelope(startData({}, { commandId: "cmd-3" })), ctx);
    expect(ecs.runs).toHaveLength(2);
  });

  it("malformed data ⇒ rejected receipts; other command types ⇒ unsupported", async () => {
    const ecs = new FakeEcs();
    const handler = createProvisionHandler({
      ecs,
      config: config(RULES),
      logger: silent,
      env: {},
    });
    expect(await handler.onCommand(startEnvelope({ commandId: "x" }), ctx)).toEqual({
      kind: "rejected",
      reason: "malformed-provision-start-data",
    });
    expect(await handler.onCommand(stopEnvelope({ commandId: "x" }), ctx)).toEqual({
      kind: "rejected",
      reason: "malformed-provision-stop-data",
    });
    const other = await handler.onCommand(
      { type: "org.facilitator.command.work.steer.v1", data: {} } as unknown as EnvelopeCommand,
      ctx,
    );
    expect(other.kind).toBe("unsupported");
  });
});

describe("launcherManifest", () => {
  it("advertises exactly the two provision command types, synchronous", () => {
    const manifest = launcherManifest("team-alpha");
    expect(manifest.ownerTeam).toBe("team-alpha");
    expect(manifest.capabilities.map((c) => c.id)).toEqual([
      CMD_PROVISION_START,
      CMD_PROVISION_STOP,
    ]);
    for (const capability of manifest.capabilities) {
      expect(capability.execution).toBe("synchronous");
      expect(capability.inputSchema).toContain("command.provision");
    }
  });
});
