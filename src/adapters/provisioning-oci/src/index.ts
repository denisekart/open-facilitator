// @ofac/provisioning-oci — the local OCI provisioning launcher:
// a link-plane endpoint (enroll → capabilities → SignalR) that
// executes `command.provision.start.v1` / `.stop.v1` against the local Docker
// Engine socket, so a killed/idle worker container auto-relaunches and rebinds
// the same assignment at a new generation. Stateless apart from the SQLite
// delivery ledger — deployable wherever a docker socket is reachable and the
// kernel link plane is dialable.
import { randomBytes } from "node:crypto";
import {
  createLinkClient,
  enroll,
  type LinkClient,
  type LinkLogger,
  type LinkTokenProvider,
} from "@ofac/workspace-link";
import { SignalrTransport, SqliteLedger, postCapabilities } from "@ofac/workspace-link/node";
import { dockerSocketPath, DockerClient, type DockerApi } from "./docker.js";
import { createProvisionHandler, garbageCollect, launcherManifest } from "./handler.js";
import type { LauncherConfig } from "./config.js";

export {
  LauncherConfigError,
  allowed,
  commaList,
  parseLauncherConfig,
  readConfigFile,
  type LauncherConfig,
} from "./config.js";
export {
  DockerError,
  DockerClient,
  dockerSocketPath,
  type ContainerSummary,
  type DockerApi,
} from "./docker.js";
export {
  CMD_PROVISION_START,
  CMD_PROVISION_STOP,
  WORKLOAD_LABEL,
  OPERATION_LABEL,
  createProvisionHandler,
  expandEnvValue,
  garbageCollect,
  launcherManifest,
  planCreate,
  slug,
  type HandlerDeps,
  type ProvisionStartData,
  type ProvisionStopData,
} from "./handler.js";

export interface RunHandle {
  client: LinkClient;
  ready: Promise<void>;
  /** One stale-container GC pass (the periodic anchor the bin drives). */
  gcOnce(): Promise<number>;
  shutdown(): Promise<void>;
}

export interface RunDeps {
  /** Injected Docker seam (tests run the whole loop against a fake engine). */
  docker?: DockerApi;
  fetchImpl?: typeof fetch;
  /** The env feeding `$VAR` expansion (default: this process's env). */
  env?: Record<string, string | undefined>;
  now?: () => number;
}

export async function run(
  config: LauncherConfig,
  logger: LinkLogger,
  deps: RunDeps = {},
): Promise<RunHandle> {
  const docker =
    deps.docker ?? new DockerClient({ socketPath: dockerSocketPath(config.dockerHost) });
  const env = deps.env ?? process.env;

  // ── link plane: enroll (secret mode) → capabilities → client ───────────
  // The enrollment shape is forgejo's verbatim: secret-mode POST /enroll with
  // a random instanceId, trustMode isolated-executor, role child.
  const activeToken = { value: "" };
  const enrollSelf = async (): Promise<void> => {
    const response = await enroll({
      baseUrl: config.kernelUrl,
      token: config.enrollSecret,
      endpointId: config.endpointId,
      instanceId: `lnk-${randomBytes(8).toString("hex")}`,
      trustMode: "isolated-executor",
      role: "child",
      team: config.team,
    });
    if (response.linkToken !== undefined) activeToken.value = response.linkToken;
  };
  const token: LinkTokenProvider = {
    getToken: async () => activeToken.value,
    // 401 ⇒ rotate: re-enrollment mints a fresh token (linkd's precedent).
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
    transport: new SignalrTransport({ baseUrl: config.kernelUrl, token }),
    ledger: new SqliteLedger(config.statePath),
    endpointId: config.endpointId,
    capabilityManifestDigest: digest,
    token,
    handler: createProvisionHandler({
      docker,
      config,
      logger,
      env,
      ...(deps.now === undefined ? {} : { now: deps.now }),
    }),
    logger,
  });

  // Startup GC leg: crash-orphaned exited/dead ofac-labeled containers from a
  // previous launcher generation get removed before the first delivery lands.
  const nowUnix = () => Math.floor(Date.now() / 1000);
  const ready = (async () => {
    await garbageCollect(docker, config, logger, deps.now ?? nowUnix);
    await client.start();
    logger.info("oci launcher ready", {
      endpointId: config.endpointId,
      kernel: config.kernelUrl,
      docker: config.dockerHost,
    });
  })();

  return {
    client,
    ready,
    gcOnce: () => garbageCollect(docker, config, logger, deps.now ?? nowUnix),
    async shutdown() {
      await client.stop("launcher-shutdown");
    },
  };
}
