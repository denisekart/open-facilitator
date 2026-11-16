// NON-PRODUCTION REFERENCE: example-fleet-launcher is the AWS reference fleet
// launcher — an unsupported showcase, NOT part of the supported adapter set
// (@ofac/provisioning-oci is). It is a link-plane endpoint (enroll →
// capabilities → SignalR) that executes
// `command.provision.start.v1` / `.stop.v1` as ECS RunTask/StopTask against a
// cluster, from a static local rules file — so a team account needs NO inbound
// port: like every other workspace workload, the launcher dials the kernel
// out. Stateless apart from the SQLite delivery ledger and the in-process
// task map.
import {
  createEndpointCredential,
  identityDirFor,
  createLinkClient,
  type LinkClient,
  type LinkLogger,
  type LinkTransport,
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

  // ── link plane: identity → first-boot enroll → sessions → client ────────
  // The fleet's one credential law (see createEndpointCredential): an Ed25519
  // identity beside the ledger, the enrollment code spent on the boot that has
  // nothing on file, every later bearer a short session JWT.
  const { provider: token } = await createEndpointCredential({
    stateDir: identityDirFor(config.statePath),
    baseUrl: config.kernelUrl,
    endpointId: config.endpointId,
    team: config.team,
    trustMode: "isolated-executor",
    role: "child",
    enrollSecret: config.enrollToken,
    logger,
    ...(deps.fetchImpl === undefined ? {} : { fetchImpl: deps.fetchImpl }),
    instancePrefix: "lnk",
    ...(config.instanceId === undefined ? {} : { instanceId: config.instanceId }),
  });
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
