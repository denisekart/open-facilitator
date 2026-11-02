// The provision command dispatcher: the launcher executes
// `command.provision.start.v1` as docker create+start (pull first, bounded by
// the envelope deadline) and `command.provision.stop.v1` as docker stop+rm of
// EVERY container carrying the workload label. The reconcile-by-label law:
// every created container carries `ofac.workload` + `ofac.operation`; a start
// whose workload ALREADY has a running labeled container is the idempotent
// re-offer ⇒ no-op accepted; a stop with nothing to stop is accepted too. The
// approved-image/network gate and the `$VAR` env-expansion law fail closed
// with honest rejected receipts — the launcher never invents authority.
import type { CapabilityDescriptor, CapabilityManifestV1, EnvelopeCommand } from "@ofac/protocol";
import { dataschemaFor } from "@ofac/workspace-link";
import type { CommandOutcome, LinkHandler, LinkLogger } from "@ofac/workspace-link";
import { allowed, type LauncherConfig } from "./config.js";
import type { DockerApi } from "./docker.js";

export const CMD_PROVISION_START = "org.facilitator.command.provision.start.v1";
export const CMD_PROVISION_STOP = "org.facilitator.command.provision.stop.v1";

export const WORKLOAD_LABEL = "ofac.workload";
export const OPERATION_LABEL = "ofac.operation";

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

export interface VolumeSpec {
  source: string;
  target: string;
  readOnly?: boolean | undefined;
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
    volumes?: VolumeSpec[] | undefined;
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
  docker: DockerApi;
  config: LauncherConfig;
  logger: LinkLogger;
  /** The launcher's own process env — the source for `$VAR` expansion. */
  env: Record<string, string | undefined>;
  /** Injectable clock for the GC age law (unix seconds). */
  now?: () => number;
}

/** `ofac-` + the workload id folded to a DNS-safe container/hostname token. */
export function slug(workloadId: string): string {
  const folded = workloadId
    .toLowerCase()
    .replace(/[^a-z0-9]+/g, "-")
    .replace(/^-+|-+$/g, "")
    .slice(0, 63)
    .replace(/-+$/g, "");
  return folded === "" ? "x" : folded;
}

/** The env-expansion law: a value that consists SOLELY of `$VARNAME` is
 *  replaced by the launcher's own environment (secrets ride the launcher,
 *  never the wire); a missing var fails the command, never an empty secret. */
export function expandEnvValue(
  value: string,
  env: Record<string, string | undefined>,
): string | undefined {
  const match = /^\$([A-Za-z_][A-Za-z0-9_]*)$/.exec(value);
  if (match === null) return value;
  const resolved = env[match[1] as string];
  return resolved === undefined || resolved === "" ? undefined : resolved;
}

export interface CreatePlan {
  name: string;
  body: Record<string, unknown>;
}

/** spec → Engine create payload (pure; the exact-payload test pins it).
 *  `{ reason }` ⇒ an honest rejected receipt. */
export function planCreate(
  data: ProvisionStartData,
  deps: Pick<HandlerDeps, "config" | "env">,
): CreatePlan | { reason: string } {
  const { config, env } = deps;
  if (!allowed(config.allowedImages, data.spec.image)) {
    return { reason: "image-not-allowed" };
  }

  if (data.spec.network !== undefined && !allowed(config.allowedNetworks, data.spec.network)) {
    return { reason: "network-not-allowed" };
  }

  const envPairs: string[] = [];
  for (const [key, raw] of Object.entries(data.spec.env ?? {})) {
    const value = expandEnvValue(raw, env);
    if (value === undefined) return { reason: `env-unresolved:${raw.slice(1)}` };
    envPairs.push(`${key}=${value}`);
  }

  const binds = (data.spec.volumes ?? []).map(
    (v) => `${v.source}:${v.target}${v.readOnly === true ? ":ro" : ""}`,
  );
  const hostConfig: Record<string, unknown> = { AutoRemove: false, RestartPolicy: { Name: "no" } };
  if (data.spec.network !== undefined) hostConfig["NetworkMode"] = data.spec.network;
  if (binds.length > 0) hostConfig["Binds"] = binds;

  const body: Record<string, unknown> = {
    Image: data.spec.image,
    Hostname: `ofac-${slug(data.workloadId)}`.slice(0, 64),
    Env: envPairs,
    Labels: {
      [WORKLOAD_LABEL]: data.workloadId,
      [OPERATION_LABEL]: data.operationId,
      ...(data.spec.labels ?? {}),
    },
    HostConfig: hostConfig,
  };
  if (data.spec.entrypoint !== undefined && data.spec.entrypoint.length > 0) {
    body["Entrypoint"] = data.spec.entrypoint;
  }

  return { name: `ofac-${slug(data.workloadId)}`, body };
}

/** The envelope deadline enforced over the pull: bounded wait, then an honest
 *  `start-deadline` rejection. `dispose()` clears the timer on every exit. */
function deadlineSignal(deadlineIso: string): { signal: AbortSignal; dispose(): void } {
  const controller = new AbortController();
  const remaining = new Date(deadlineIso).getTime() - Date.now();
  const timer = setTimeout(
    () => controller.abort(new Error("start-deadline")),
    Math.max(remaining, 0),
  );
  return { signal: controller.signal, dispose: () => clearTimeout(timer) };
}

const RUNNING_STATES: Record<string, true> = { running: true, restarting: true, paused: true };

/** GC law: ofac-labeled containers exited/dead longer than gcMinutes get
 *  removed. Never touches unlabeled or running containers; runs at startup
 *  and on every provision.stop. */
export async function garbageCollect(
  docker: DockerApi,
  config: Pick<LauncherConfig, "gcMinutes">,
  logger: LinkLogger,
  nowUnix: () => number,
): Promise<number> {
  const cutoff = nowUnix() - config.gcMinutes * 60;
  const stale = (await docker.listContainers([WORKLOAD_LABEL])).filter(
    (c) => (c.state === "exited" || c.state === "dead") && c.finishedAt <= cutoff,
  );
  for (const container of stale) await docker.removeContainer(container.id);
  if (stale.length > 0)
    logger.info("provision gc removed stale containers", { count: stale.length });
  return stale.length;
}

export function createProvisionHandler(deps: HandlerDeps): LinkHandler {
  const { docker, config, logger, env } = deps;
  const nowUnix = deps.now ?? ((): number => Math.floor(Date.now() / 1000));

  const onStart = async (data: ProvisionStartData): Promise<CommandOutcome> => {
    const existing = await docker.listContainers([`${WORKLOAD_LABEL}=${data.workloadId}`]);
    if (existing.some((c) => c.state in RUNNING_STATES)) {
      logger.info("provision start no-op: workload already running", {
        workloadId: data.workloadId,
        commandId: data.commandId,
      });
      return { kind: "accepted" };
    }

    // The kill-9 continuity path: the dead container still HOLDS the fixed
    // name, and the real engine answers create with 409. Sweep the
    // workload's non-running labeled containers (they are exited/dead by
    // this point — no stop needed, just remove) before re-creating.
    for (const stale of existing) await docker.removeContainer(stale.id);

    const plan = planCreate(data, { config, env });
    if ("reason" in plan) {
      logger.warn("provision start rejected", { reason: plan.reason, commandId: data.commandId });
      return { kind: "rejected", reason: plan.reason };
    }

    const { signal, dispose } = deadlineSignal(data.deadline);
    try {
      // Inspect-first: the reference env's worker images are locally built and
      // never pushed, so a pull is the fallback, not the ritual.
      try {
        if (!(await docker.imageExists(data.spec.image, signal))) {
          await docker.pullImage(data.spec.image, signal);
        }
      } catch (error) {
        if (signal.aborted) return { kind: "rejected", reason: "start-deadline" };
        // The pull failed and the image is still absent: honest answer, the
        // kernel sees WHY the worker can't boot.
        if (!(await docker.imageExists(data.spec.image, signal))) {
          logger.warn("provision start rejected: image absent, pull failed", {
            image: data.spec.image,
            error: String(error),
          });
          return { kind: "rejected", reason: "image-missing" };
        }

        throw error;
      }

      const id = await docker.createContainer(plan.name, plan.body);
      await docker.startContainer(id);
      logger.info("container provisioned", {
        containerId: id,
        workloadId: data.workloadId,
        operationId: data.operationId,
        commandId: data.commandId,
      });
      return { kind: "accepted" };
    } finally {
      dispose();
    }
  };

  const onStop = async (data: ProvisionStopData): Promise<CommandOutcome> => {
    const targets = await docker.listContainers([`${WORKLOAD_LABEL}=${data.workloadId}`]);
    for (const container of targets) {
      await docker.stopContainer(container.id, config.stopGraceSeconds);
      await docker.removeContainer(container.id);
    }

    logger.info("provision stop complete", {
      workloadId: data.workloadId,
      removed: targets.length,
      commandId: data.commandId,
    });
    // stop envelopes carry no spec ⇒ the config grace applies; nothing to
    // stop is the idempotent answer, not an error. The GC law rides the stop.
    await garbageCollect(docker, config, logger, nowUnix);
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
        reason: `oci-launcher handles provision start/stop only, got ${cmd.type}`,
      };
    },
  };
}
