// The kernel leg of a worker/connector bin, once for all of them. The
// Gateway-facing leg pairs with a fleet secret; the KERNEL leg (artifact byte
// path, context fetch) is its own endpoint relationship, and the credential
// law there is the fleet's: a key in the process's own state dir, the
// enrollment code spent on the boot that has nothing on file, short sessions
// after. A shell unit used to self-enroll a static bearer for this leg — a
// 5-minute session JWT cannot be exported once at boot, so the bin owns it now.
//
// The trigger is env-only (no config-file surface): OFAC_KERNEL_ENDPOINT names
// the endpoint to enroll as; without it the bin keeps whatever static bearer
// (or nothing) its config carries — the unauthenticated local fleet is law.
import { createEndpointCredential, identityDirFor, type LinkLogger } from "@ofac/workspace-link";

export interface KernelLegOptions {
  /**
   * The bin's state home: a directory (a worker's workDir) gets the identity
   * file directly; a state FILE path (a ledger) is reduced to its directory
   * (and `":memory:"` to a temp dir) by the same law as everywhere else.
   */
  stateDir: string;
  kernelUrl: string;
  env?: Record<string, string | undefined>;
  logger?: LinkLogger;
}

/**
 * When `OFAC_KERNEL_ENDPOINT` is set: run the credential law against the
 * kernel and return the live token factory (plus the endpoint id, for logs).
 * Returns undefined when the env names no kernel endpoint — the caller then
 * keeps its configured static bearer.
 */
export async function kernelLegFromEnv(
  options: KernelLegOptions,
): Promise<{ endpointId: string; getToken: () => Promise<string> } | undefined> {
  const env = options.env ?? process.env;
  const endpointId = env["OFAC_KERNEL_ENDPOINT"];
  if (endpointId === undefined || endpointId === "") return undefined;
  const team = env["OFAC_WORKER_TEAM"] ?? env["OFAC_TEAM"] ?? "team-alpha";
  const { provider } = await createEndpointCredential({
    stateDir: looksLikeDirectory(options.stateDir)
      ? options.stateDir
      : identityDirFor(options.stateDir),
    baseUrl: options.kernelUrl,
    endpointId,
    team,
    trustMode: "isolated-executor",
    role: "child",
    ...(env["OFAC_ENROLL_SECRET"] === undefined ? {} : { enrollSecret: env["OFAC_ENROLL_SECRET"] }),
    ...(options.logger === undefined ? {} : { logger: options.logger }),
    instancePrefix: "worker",
  });
  return { endpointId, getToken: () => provider.getToken() };
}

/** A path with an extension is a state FILE; anything else is a directory. */
function looksLikeDirectory(statePath: string): boolean {
  return statePath !== ":memory:" && !/\.[a-z0-9]+$/i.test(statePath);
}
