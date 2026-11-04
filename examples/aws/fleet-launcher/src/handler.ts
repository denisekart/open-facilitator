// NON-PRODUCTION REFERENCE: this launcher is an unsupported showcase of the
// link-plane client contract on AWS ECS. It is NOT part of the supported
// adapter set — the supported provisioning launcher is @ofac/provisioning-oci.
//
// The provision command dispatcher (the oci launcher's handler.ts analog):
// executes `command.provision.start.v1` as an ECS RunTask and
// `.stop.v1` as a StopTask of the tracked task. The static rules file replaces
// the docker image/network allowlists: the workloadId itself is the approved
// surface (unknown ⇒ rejected), the rule pins task definition + cluster +
// subnets, and an optional envAllowlist gates the spec env NAMES. The
// `$VAR` name-indirection law is copied verbatim: values ride the launcher,
// never the wire.
import type { CapabilityDescriptor, CapabilityManifestV1, EnvelopeCommand } from "@ofac/protocol";
import { dataschemaFor } from "@ofac/workspace-link";
import type { CommandOutcome, LinkHandler, LinkLogger } from "@ofac/workspace-link";
import type { LauncherConfig } from "./config.js";
import type { EcsApi, EcsTag, EnvVar } from "./ecs.js";

export const CMD_PROVISION_START = "org.facilitator.command.provision.start.v1";
export const CMD_PROVISION_STOP = "org.facilitator.command.provision.stop.v1";

// The ECS tag analog of the oci labels: every launched task carries the
// workload + operation identity and the managed-by marker.
export const WORKLOAD_TAG = "ofac.workload";
export const OPERATION_TAG = "ofac.operation";
export const MANAGED_TAG = "io.facilitator.managed";
export const MANAGED_VALUE = "kernel-provisioned";

/** The capability manifest this launcher advertises (POST /api/link/v1/capabilities).
 *  The provision envelopes carry no capabilityId — the kernel routes by
 *  recipient and the client hands every delivered command to onCommand — but
 *  the manifest stays honest: these ARE the two types this endpoint executes. */
export function launcherManifest(team: string): CapabilityManifestV1 {
  const descriptor = (id: string): CapabilityDescriptor => ({
    id,
    version: 1,
    execution: "synchronous",
    inputSchema: dataschemaFor(id),
  });
  return {
    apiVersion: "facilitator.framework/v1",
    kind: "CapabilityManifest",
    ownerTeam: team,
    capabilities: [descriptor(CMD_PROVISION_START), descriptor(CMD_PROVISION_STOP)],
  };
}

export interface ProvisionStartData {
  commandId: string;
  operationId: string;
  workloadId: string;
  team: string;
  deadline: string;
  spec: {
    image: string;
    entrypoint?: string[] | undefined;
    network?: string | undefined;
    env?: Record<string, string> | undefined;
    labels?: Record<string, string> | undefined;
    stopGraceSeconds?: number | undefined;
  };
}

export interface ProvisionStopData {
  commandId: string;
  operationId: string;
  workloadId: string;
  team: string;
  deadline: string;
}

export interface HandlerDeps {
  ecs: EcsApi;
  config: LauncherConfig;
  logger: LinkLogger;
  /** The launcher's own process env — the source for `$VAR` expansion. */
  env: Record<string, string | undefined>;
}

/** The workload id folded to a DNS-safe `startedBy` token (oci's slug law). */
export function slug(workloadId: string): string {
  const folded = workloadId
    .toLowerCase()
    .replace(/[^a-z0-9]+/g, "-")
    .replace(/^-+|-+$/g, "")
    .slice(0, 63)
    .replace(/-+$/g, "");
  return folded === "" ? "x" : folded;
}

/** The env-expansion law (oci verbatim): a value that consists SOLELY of
 *  `$VARNAME` is replaced by the launcher's own environment (secrets ride the
 *  launcher, never the wire); a missing var fails the command, never an empty
 *  secret. */
export function expandEnvValue(
  value: string,
  env: Record<string, string | undefined>,
): string | undefined {
  const match = /^\$([A-Za-z_][A-Za-z0-9_]*)$/.exec(value);
  if (match === null) return value;
  const resolved = env[match[1] as string];
  return resolved === undefined || resolved === "" ? undefined : resolved;
}

export interface RunPlan {
  cluster: string;
  taskDefinition: string;
  subnets: readonly string[];
  environment: EnvVar[];
  command?: readonly string[] | undefined;
  tags: EcsTag[];
  startedBy: string;
}

/** spec → RunTask parameters (pure; the exact-payload test pins it).
 *  `{ reason }` ⇒ an honest rejected receipt. The spec's image/network are
 *  IGNORED on purpose — the rule's task definition + cluster + subnets are
 *  the approved surface (the image-allowlist + network-pinning analog). */
export function planRun(
  data: ProvisionStartData,
  deps: Pick<HandlerDeps, "config" | "env">,
): RunPlan | { reason: string } {
  // hasOwn (not `in`): a rules key like `constructor` must never resolve via the prototype.
  const rule = Object.hasOwn(deps.config.rules, data.workloadId)
    ? deps.config.rules[data.workloadId]
    : undefined;
  if (rule === undefined) {
    return { reason: "workload-not-allowed" };
  }

  const allowlist = rule.envAllowlist;
  const environment: EnvVar[] = [];
  for (const [key, raw] of Object.entries(data.spec.env ?? {})) {
    if (allowlist !== undefined && !allowlist.includes(key)) continue;
    const value = expandEnvValue(raw, deps.env);
    if (value === undefined) return { reason: `env-unresolved:${raw.slice(1)}` };
    environment.push({ name: key, value });
  }

  const tags: EcsTag[] = [
    { key: WORKLOAD_TAG, value: data.workloadId },
    { key: OPERATION_TAG, value: data.operationId },
    { key: MANAGED_TAG, value: MANAGED_VALUE },
    ...Object.entries(data.spec.labels ?? {}).map(([key, value]) => ({ key, value })),
  ];

  return {
    cluster: rule.cluster,
    taskDefinition: rule.taskDefinition,
    subnets: rule.subnets,
    environment,
    ...(data.spec.entrypoint === undefined ? {} : { command: data.spec.entrypoint }),
    tags,
    startedBy: `ofac-${slug(data.workloadId)}`,
  };
}

/**
 * The stop-target law (the oci handler derives container names/labels; ECS
 * StopTask demands the exact task ARN): the handler keeps an in-memory map
 * `workloadId -> {cluster, taskArn}` of what THIS launcher generation ran.
 * - a start for a tracked workload is the idempotent re-offer ⇒ accepted
 *   no-op (the fleet task is a long-running service; it stays tracked until
 *   stopped);
 * - a stop of a tracked workload ⇒ StopTask + untrack ⇒ accepted;
 * - a stop of anything else (never started here, or a task from a previous
 *   launcher generation) ⇒ accepted NO-OP, mirroring the oci law that "a stop
 *   with nothing to stop is accepted". A cross-generation stop is a
 *   documented reference-only limitation: the upgrade path is
 *   ListTasks(startedBy=`ofac-<slug>`), intentionally not implemented here.
 */
export interface TrackedTask {
  cluster: string;
  taskArn: string;
}

export function createProvisionHandler(deps: HandlerDeps): LinkHandler {
  const { ecs, config, logger, env } = deps;
  const tracked = new Map<string, TrackedTask>();

  const onStart = async (data: ProvisionStartData): Promise<CommandOutcome> => {
    if (tracked.has(data.workloadId)) {
      logger.info("provision start no-op: workload already tracked", {
        workloadId: data.workloadId,
        commandId: data.commandId,
      });
      return { kind: "accepted" };
    }

    const plan = planRun(data, { config, env });
    if ("reason" in plan) {
      logger.warn("provision start rejected", { reason: plan.reason, commandId: data.commandId });
      return { kind: "rejected", reason: plan.reason };
    }

    const { taskArn } = await ecs.runTask(plan);
    tracked.set(data.workloadId, { cluster: plan.cluster, taskArn });
    logger.info("task provisioned", {
      taskArn,
      workloadId: data.workloadId,
      operationId: data.operationId,
      commandId: data.commandId,
    });
    return { kind: "accepted" };
  };

  const onStop = async (data: ProvisionStopData): Promise<CommandOutcome> => {
    const task = tracked.get(data.workloadId);
    if (task !== undefined) {
      await ecs.stopTask({ cluster: task.cluster, taskArn: task.taskArn });
      tracked.delete(data.workloadId);
    }
    logger.info("provision stop complete", {
      workloadId: data.workloadId,
      stopped: task !== undefined,
      commandId: data.commandId,
    });
    return { kind: "accepted" };
  };

  const asData = <T>(cmd: EnvelopeCommand, keys: readonly string[]): T | undefined => {
    const data = cmd.data;
    if (typeof data !== "object" || data === null) return undefined;
    const record = data as Record<string, unknown>;
    return keys.every((key) => record[key] !== undefined) ? (data as unknown as T) : undefined;
  };

  return {
    async onCommand(cmd): Promise<CommandOutcome> {
      if (cmd.type === CMD_PROVISION_START) {
        const data = asData<ProvisionStartData>(cmd, [
          "commandId",
          "operationId",
          "workloadId",
          "deadline",
          "spec",
        ]);
        if (data === undefined || typeof data.spec.image !== "string") {
          return { kind: "rejected", reason: "malformed-provision-start-data" };
        }

        return onStart(data);
      }

      if (cmd.type === CMD_PROVISION_STOP) {
        const data = asData<ProvisionStopData>(cmd, [
          "commandId",
          "operationId",
          "workloadId",
          "deadline",
        ]);
        if (data === undefined) {
          return { kind: "rejected", reason: "malformed-provision-stop-data" };
        }

        return onStop(data);
      }

      return {
        kind: "unsupported",
        reason: `fleet-launcher handles provision start/stop only, got ${cmd.type}`,
      };
    },
  };
}
