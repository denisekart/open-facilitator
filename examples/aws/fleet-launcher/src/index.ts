// NON-PRODUCTION REFERENCE: example-fleet-launcher is the AWS reference fleet
// launcher — an unsupported showcase, NOT part of the supported adapter set
// (@ofac/provisioning-oci is). It is a link-plane endpoint (enroll →
// capabilities → SignalR) that executes
// `command.provision.start.v1` / `.stop.v1` as ECS RunTask/StopTask against a
// cluster, from a static local rules file — so a team account needs NO inbound
// port: like every other workspace workload, the launcher dials the kernel
// out. Stateless apart from the SQLite delivery ledger and the in-process
// task map.
import { randomBytes } from "node:crypto";
import {
  createLinkClient,
  enroll,
  type LinkClient,
  type LinkLogger,
  type LinkTransport,
  type LinkTokenProvider,
} from "@ofac/workspace-link";
import { SignalrTransport, SqliteLedger, postCapabilities } from "@ofac/workspace-link/node";
import { EcsClient, type EcsApi } from "./ecs.js";
import { createProvisionHandler, launcherManifest } from "./handler.js";
import type { LauncherConfig } from "./config.js";

export {
  LauncherConfigError,
  parseLauncherConfig,
  parseRules,
  type FleetRule,
  type FleetRules,
} from "./config.js";
export {
  EcsClient,
  type EcsApi,
  type EcsRunTaskParams,
  type EcsStopTaskParams,
  type EcsTag,
  type EnvVar,
} from "./ecs.js";
export {
  CMD_PROVISION_START,
  CMD_PROVISION_STOP,
  WORKLOAD_TAG,
  OPERATION_TAG,
  MANAGED_TAG,
  MANAGED_VALUE,
  createProvisionHandler,
  expandEnvValue,
  launcherManifest,
  planRun,
  slug,
  type HandlerDeps,
  type ProvisionStartData,
  type ProvisionStopData,
  type RunPlan,
  type TrackedTask,
} from "./handler.js";

export interface RunHandle {
  client: LinkClient;
  ready: Promise<void>;
  shutdown(): Promise<void>;
}

export interface RunDeps {
  /** Injected ECS seam (tests run the whole loop against a fake client). */
  ecs?: EcsApi;
  /** Fetch seam for the capabilities POST (the SDK's enroll uses global
   *  fetch by law — tests stub it, as the oci tier's tests do the socket). */
  fetchImpl?: typeof fetch;
  env?: Record<string, string | undefined>;
  /** Injected link transport seam (tests run the loop against a mock; the
   *  SDK's own law: transports are "two impls in ./node, mocks in tests"). */
  transport?: LinkTransport;
}

export async function run(
  config: LauncherConfig,
  logger: LinkLogger,
  deps: RunDeps = {},
): Promise<RunHandle> {
  const ecs = deps.ecs ?? new EcsClient();
  const env = deps.env ?? process.env;

  // ── link plane: enroll (secret mode) → capabilities → client ───────────
  // The enrollment shape is the oci launcher's verbatim: secret-mode POST
  // /enroll with an instanceId, trustMode isolated-executor, role child. The
  // instanceId law is copied exactly: a fresh random id per launcher start
  // (a fixed OFAC_INSTANCE_ID overrides it for operators who want one).
  const activeToken = { value: "" };
  const enrollSelf = async (): Promise<void> => {
    const response = await enroll({
      baseUrl: config.kernelUrl,
      token: config.enrollToken,
      endpointId: config.endpointId,
      instanceId: config.instanceId ?? `lnk-${randomBytes(8).toString("hex")}`,
      trustMode: "isolated-executor",
      role: "child",
      team: config.team,
    });
    if (response.linkToken !== undefined) activeToken.value = response.linkToken;
  };
  const token: LinkTokenProvider = {
    getToken: async () => activeToken.value,
    // 401 ⇒ rotate: re-enrollment mints a fresh token (the SDK law).
    refresh: enrollSelf,
  };
  await enrollSelf();
  if (activeToken.value === "") {
    throw new Error("enrollment issued no linkToken");
  }

  const { digest } = await postCapabilities(launcherManifest(config.team), {
    baseUrl: config.kernelUrl,
    getToken: token.getToken,
    ...(deps.fetchImpl === undefined ? {} : { fetchImpl: deps.fetchImpl }),
  });

  const client = createLinkClient({
    transport: deps.transport ?? new SignalrTransport({ baseUrl: config.kernelUrl, token }),
    ledger: new SqliteLedger(config.statePath),
    endpointId: config.endpointId,
    capabilityManifestDigest: digest,
    token,
    handler: createProvisionHandler({
      ecs,
      config,
      logger,
      env,
    }),
    logger,
  });

  const ready = (async () => {
    await client.start();
    logger.info("fleet launcher ready", {
      endpointId: config.endpointId,
      kernel: config.kernelUrl,
      workloads: Object.keys(config.rules).length,
    });
  })();

  return {
    client,
    ready,
    async shutdown() {
      await client.stop("launcher-shutdown");
    },
  };
}
