// ofac-linkd — the headless Link supervisor (spec §8.2 supervisor entrypoint).
// Enroll (or reuse a token) → publish capabilities → connect → execute
// injected handlers → survive disconnect/restart with replay.
//
// Exit codes (locked): 0 clean · 2 config error · 3 protocol mismatch ·
// 4 enrollment rejected.
import { readFileSync } from "node:fs";
import { randomBytes } from "node:crypto";
import type { CapabilityManifestV1 } from "@ofac/protocol";
import {
  ConfigError,
  createConsoleLogger,
  createEchoHandler,
  createLinkClient,
  createStubHandler,
  enroll,
  EnrollmentError,
  parseArgv,
  ProtocolMismatchError,
  type LinkClient,
  type LinkdConfig,
  type LinkHandler,
  type LinkLogger,
  type LinkSession,
  type LinkTokenProvider,
  type LinkTransport,
} from "../index.js";
import {
  HttpPollTransport,
  postCapabilities,
  SignalrTransport,
  SqliteLedger,
} from "../node/index.js";

const USAGE = `ofac-linkd run --url <facilitatorBaseUrl> (--token <linkToken> | --secret <enrollmentSecret>)
                --endpoint <endpointId> --team <teamSlug> --manifest <path.json>
                [--role child|gateway] [--trust-mode isolated-executor] [--transport signalr|http-poll]
                [--state <sqlitePath>] [--mode echo|stub]

--team is the kernel team roster slug (Link:Teams) the enrollment binds to
(403 team-unknown otherwise); --role must satisfy the gw. prefix law (a
gw.-prefixed endpoint must enroll as gateway).

Enroll with --secret or OFAC_ENROLL_SECRET (the env var is consulted first —
argv is visible in ps; prints the minted link token to stderr; mount it as
OFAC_LINK_TOKEN on the next start and pass it via --token), publish the
capability manifest, connect outbound, and serve command deliveries.
Exit codes: 0 clean, 2 config error, 3 protocol mismatch, 4 enrollment rejected.`;

export interface RunHandle {
  client: LinkClient;
  ledger: SqliteLedger;
  ready: Promise<LinkSession>;
  shutdown(): Promise<void>;
}

export async function run(
  config: LinkdConfig,
  logger: LinkLogger = createConsoleLogger(),
): Promise<RunHandle> {
  let manifestText: string;
  try {
    manifestText = readFileSync(config.manifestPath, "utf8");
  } catch {
    throw new ConfigError(`cannot read manifest '${config.manifestPath}'`);
  }
  const manifest = JSON.parse(manifestText) as CapabilityManifestV1;
  if (manifest.ownerTeam !== config.team) {
    // Server answers 403 team-mismatch; failing here maps to a config exit.
    throw new ConfigError(`manifest ownerTeam '${manifest.ownerTeam}' ≠ --team '${config.team}'`);
  }

  // Rotatable credential: enroll rotates the old token, so refresh re-enrolls.
  const activeToken = { value: config.token ?? "" };
  const tokenProvider: LinkTokenProvider = {
    getToken: async () => activeToken.value,
    refresh: async () => {
      if (config.secret === undefined) return; // static token: nothing to rotate
      const response = await enroll({
        baseUrl: config.url,
        token: config.secret,
        endpointId: config.endpointId,
        instanceId: `lnk-${randomBytes(8).toString("hex")}`,
        trustMode: config.trustMode,
        role: config.role,
        team: config.team,
      });
      if (response.linkToken !== undefined) activeToken.value = response.linkToken;
    },
  };

  if (config.secret !== undefined) {
    const response = await enroll({
      baseUrl: config.url,
      token: config.secret,
      endpointId: config.endpointId,
      instanceId: `lnk-${randomBytes(8).toString("hex")}`,
      trustMode: config.trustMode,
      role: config.role,
      team: config.team,
    });
    if (response.linkToken === undefined) {
      throw new EnrollmentError(201, {
        detail: "server issued no linkToken; restart with --token",
      });
    }
    activeToken.value = response.linkToken;
    process.stderr.write(
      `linkd link token minted (mount as OFAC_LINK_TOKEN): ${response.linkToken}\n`,
    );
  }
  if (activeToken.value === "")
    throw new ConfigError("no usable token (--token or --secret required)");

  // Capabilities: postCapabilities serializes once and digests the POSTed
  // body, so hello's capabilityManifestDigest covers the exact bytes the
  // server stores.
  const { digest } = await postCapabilities(manifest, {
    baseUrl: config.url,
    getToken: tokenProvider.getToken,
  });

  const ledger = new SqliteLedger(config.statePath);
  const transport: LinkTransport =
    config.transport === "http-poll"
      ? new HttpPollTransport({ baseUrl: config.url }, tokenProvider)
      : new SignalrTransport({ baseUrl: config.url, token: tokenProvider });
  const handler: LinkHandler =
    config.mode === "echo"
      ? createEchoHandler({ source: `urn:workspace:linkd/${config.endpointId}` })
      : createStubHandler();

  const client = createLinkClient({
    transport,
    ledger,
    endpointId: config.endpointId,
    capabilityManifestDigest: digest,
    token: tokenProvider,
    handler,
    logger,
  });

  const ready = client.start().then((session) => {
    process.stdout.write(
      `linkd ready endpointId=${config.endpointId} epoch=${session.leaseEpoch} transport=${config.transport}\n`,
    );
    return session;
  });

  return {
    client,
    ledger,
    ready,
    async shutdown() {
      await client.stop("linkd-shutdown");
      ledger.close();
    },
  };
}

export async function main(argv: readonly string[]): Promise<number> {
  let config: LinkdConfig;
  try {
    config = parseArgv(argv);
  } catch (error) {
    if (error instanceof ConfigError && error.message === "help") {
      process.stdout.write(`${USAGE}\n`);
      return 0;
    }
    process.stderr.write(`linkd config error: ${(error as Error).message}\n\n${USAGE}\n`);
    return 2;
  }
  const logger = createConsoleLogger({
    level: (process.env["OFAC_LINKD_LOG"] as "debug" | undefined) ?? "info",
  });
  let handle: RunHandle;
  try {
    handle = await run(config, logger);
  } catch (error) {
    if (error instanceof ConfigError) {
      process.stderr.write(`linkd config error: ${error.message}\n`);
      return 2;
    }
    if (error instanceof EnrollmentError) {
      process.stderr.write(`linkd enrollment refused: ${error.message}\n`);
      return 4;
    }
    if (error instanceof ProtocolMismatchError) {
      process.stderr.write(`linkd protocol mismatch: ${error.message}\n`);
      return 3;
    }
    throw error;
  }
  try {
    await handle.ready;
  } catch (error) {
    await handle.shutdown().catch(() => undefined);
    if (error instanceof ProtocolMismatchError) {
      process.stderr.write(`linkd protocol mismatch: ${error.message}\n`);
      return 3;
    }
    process.stderr.write(`linkd failed to reach ready: ${String(error)}\n`);
    return 1;
  }
  // Park until signal (clean 0) or terminal client failure (closed state ⇒ 3).
  const signal = Promise.withResolvers<number>();
  const stop = (): void => {
    void handle.shutdown().then(
      () => signal.resolve(0),
      () => signal.resolve(1),
    );
  };
  process.on("SIGINT", stop);
  process.on("SIGTERM", stop);
  const terminal = Promise.withResolvers<number>();
  handle.client.on("state", (state) => {
    if (state === "closed") {
      void handle.shutdown().then(
        () => terminal.resolve(3),
        () => terminal.resolve(1),
      );
    }
  });
  return Promise.race([signal.promise, terminal.promise]);
}

// Only execute when invoked as a program (importable for the E2E harness).
if (
  process.argv[1] !== undefined &&
  import.meta.url === new URL(`file://${process.argv[1]}`).href
) {
  main(process.argv.slice(2)).then((code) => {
    process.exitCode = code;
  });
}
