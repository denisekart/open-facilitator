// The endpoint credential, once for the whole TypeScript fleet. Every link
// endpoint — the gateway, an adapter, a harness, a launcher — runs the same
// three lines of law, and every one of them used to get them subtly wrong:
//
//   1. the identity is a file: `<stateDir>/identity.key`, plaintext, the state
//      dir IS the logical instance (no backup, migration is `mv`, loss is rejoin);
//   2. the enrollment code is spent ONCE, on the boot that has no live
//      enrollment on file — after that it is dead weight, and a restart
//      re-proves with the stored key instead of needing a secret;
//   3. every credential after that is a 5-minute session JWT, re-minted by
//      challenge+proof, which is what `SessionProvider` hands out.
//
// The kernel answers a challenge for an endpoint with no live enrollment (or a
// revoked one) with 401 + "No live enrollment for this endpoint.", and the
// no-existence-oracle law makes it the same answer for an unknown endpoint. That
// refusal — and only that refusal — is the signal to enroll.
import { randomBytes } from "node:crypto";
import { mkdtempSync } from "node:fs";
import { tmpdir } from "node:os";
import { dirname, join } from "node:path";

import { EnrollmentError } from "./errors.js";
import type { LinkLogger, LinkTokenProvider } from "./types.js";
import { enroll } from "./enroll.js";
import { loadOrCreateIdentity, type EndpointIdentity } from "./keystore.js";
import { SessionProvider } from "./session.js";

/**
 * The keystore home implied by a component's state FILE path — the one-liner
 * every bin otherwise gets subtly wrong. `":memory:"` (and any bare filename,
 * whose dirname is a meaningless ".") means "no durable state dir", so the
 * identity lives in a per-process temp dir instead of dropping `identity.key`
 * into the cwd: such a process is explicitly disposable, and so is its key.
 */
export function identityDirFor(statePath: string): string {
  if (statePath === ":memory:" || statePath === "") {
    return mkdtempSync(join(tmpdir(), "ofac-identity-"));
  }
  return dirname(statePath);
}

const sleep = (ms: number): Promise<void> => new Promise((resolve) => setTimeout(resolve, ms));

/**
 * The session route's refusal to mint: 401 means the kernel will not
 * authenticate THIS presentation — no enrollment at all, a revoked one, or a
 * proof a pinned key does not answer. None of those is decidable from here
 * without an existence oracle, and every one of them is cured the same way by
 * a caller that still holds a credential source (an enroll code or a workload
 * token): re-enroll, which re-presents the key and un-revokes the row. The
 * pre-lifecycle client always re-authed against its token; this is that
 * durability under keys — a disposable worker that wakes with a new identity
 * and a spent-but-redeemable code gets back in by itself.
 */
export function isNotEnrolled(error: unknown): boolean {
  return error instanceof EnrollmentError && error.status === 401;
}

export interface EndpointCredentialOptions {
  /** Where `identity.key` lives (created 0700 when missing). */
  stateDir: string;
  baseUrl: string;
  endpointId: string;
  team: string;
  trustMode: "shared-runtime" | "separate-process-same-host" | "isolated-executor";
  role?: "gateway" | "child";
  /** First-boot-only enrollment code (mode (a)). */
  enrollSecret?: string;
  /**
   * Class `oidc` (mode (b)): a workload token source. Presenting it binds the
   * enrollment to the workload identity instead of the key.
   */
  workloadToken?: () => Promise<string>;
  logger?: LinkLogger;
  fetchImpl?: typeof fetch;
  /** Audit string for the enroll call's instanceId (defaults to a random one). */
  instancePrefix?: string;
  /** A fixed instanceId (OFAC_INSTANCE_ID law) overrides the random one. */
  instanceId?: string;
}

export interface EndpointCredential {
  /** Hand this to the transports, the artifact client, `postCapabilities`. */
  provider: SessionProvider & LinkTokenProvider;
  identity: EndpointIdentity;
}

/**
 * Load (or mint) the identity, prove for a session, and enroll on the one boot
 * that has nothing on file. Returns the provider every later call rides.
 */
export async function createEndpointCredential(
  options: EndpointCredentialOptions,
): Promise<EndpointCredential> {
  const identity = loadOrCreateIdentity(options.stateDir);
  // A class-key endpoint buys sessions with challenge+proof; the workload
  // arm exists only when the enrollment itself is class `oidc` (no secret —
  // issuer trust binds it). When BOTH are configured, mode (a) pins the key
  // and the workload token stays a re-pin path for the next enrollment, so
  // the provider must not try to buy sessions through a binding that was
  // never written.
  const secretMode = options.enrollSecret !== undefined;
  const provider = new SessionProvider({
    baseUrl: options.baseUrl,
    endpointId: options.endpointId,
    identity,
    ...(options.logger === undefined ? {} : { logger: options.logger }),
    ...(options.fetchImpl === undefined ? {} : { fetchImpl: options.fetchImpl }),
    ...(options.workloadToken === undefined || secretMode
      ? {}
      : { workloadToken: options.workloadToken }),
  });
  const hasSource = options.enrollSecret !== undefined || options.workloadToken !== undefined;
  const enrollOnce = async (): Promise<void> => {
    await enroll(
      {
        baseUrl: options.baseUrl,
        ...(options.enrollSecret === undefined
          ? { token: await options.workloadToken!() }
          : { token: options.enrollSecret }),
        endpointId: options.endpointId,
        instanceId:
          options.instanceId ??
          `${options.instancePrefix ?? "lnk"}-${randomBytes(8).toString("hex")}`,
        trustMode: options.trustMode,
        ...(options.role === undefined ? {} : { role: options.role }),
        team: options.team,
        publicKey: identity.publicKeyBase64Url,
        keyAlgorithm: "ed25519",
      },
      ...(options.fetchImpl === undefined ? [] : [options.fetchImpl]),
    );
    options.logger?.info("endpoint key enrolled", {
      endpointId: options.endpointId,
      keyId: identity.keyId,
    });
  };
  // The boot loop: prove, and enroll when the kernel has nothing for this
  // endpoint. A 4xx answer is decided truth — a refusal (throw) or a missing
  // enrollment (enroll, then prove). A 5xx answer or a transport failure is a
  // KERNEL THAT IS NOT READY, and with a credential source on hand the honest
  // answer is to wait it out: every supervisor's restart budget is smaller
  // than a cold-start dependency race, and every crash re-spends first-boot
  // enrollment against a kernel that merely needs a second to answer. Bounded
  // (one minute), so a kernel that is truly down surfaces as a startup error.
  const isTransientBootError = (error: unknown): boolean =>
    (error instanceof EnrollmentError && error.status >= 500) ||
    (error instanceof Error &&
      /fetch failed|ECONNREFUSED|EAI_AGAIN|ECONNRESET|socket hang up/.test(error.message));
  const deadline = Date.now() + 60_000;
  let backoff = 500;
  // "fetch failed" alone names nothing: walk the cause chain so the log
  // carries the transport truth (ECONNREFUSED, UND_ERR_SOCKET, …).
  const describeError = (error: unknown): string => {
    const parts: string[] = [String(error)];
    let cause = error instanceof Error ? error.cause : undefined;
    for (let depth = 0; cause !== undefined && depth < 4; depth += 1) {
      const code =
        typeof cause === "object" && cause !== null && "code" in cause
          ? String((cause as { code: unknown }).code)
          : undefined;
      parts.push(code !== undefined ? `${code} (${String(cause)})` : String(cause));
      cause = cause instanceof Error ? cause.cause : undefined;
    }
    return parts.join(" <- ");
  };
  const wait = async (error: unknown, phase: string): Promise<void> => {
    options.logger?.warn(`kernel not ready at boot (${phase}); waiting`, {
      endpointId: options.endpointId,
      error: describeError(error),
    });
    await sleep(backoff);
    backoff = Math.min(backoff * 2, 5_000);
  };
  for (;;) {
    try {
      await provider.getToken(); // enrolled: prove, do not enroll
      return { provider, identity };
    } catch (error) {
      if (isNotEnrolled(error)) {
        if (!hasSource) {
          throw new EnrollmentError(401, {
            detail:
              `no enrollment code for the first-boot enrollment of ${options.endpointId} ` +
              `(the key is only a credential once the kernel has it)`,
          });
        }
        try {
          await enrollOnce();
        } catch (error2) {
          if (!isTransientBootError(error2) || Date.now() >= deadline) throw error2;
          await wait(error2, "enroll");
          continue;
        }
      } else {
        if (!isTransientBootError(error) || !hasSource || Date.now() >= deadline) throw error;
        await wait(error, "prove");
        continue;
      }
    }
    try {
      await provider.refresh();
      return { provider, identity };
    } catch (error) {
      // The enroll landed; the kernel may still be settling underneath it.
      if (!isTransientBootError(error) || Date.now() >= deadline) throw error;
      await wait(error, "session");
    }
  }
}
