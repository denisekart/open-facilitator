// Shared E2E harness (Plan 04 step 6). Each e2e file gets its OWN Facilitator
// process + its OWN Postgres database: tests are fully isolated, and the
// in-flight lease can be shortened (Link__InFlightLeaseSeconds=2) without
// racing other files. Skips cleanly when FACILITATOR_PG is unset.
//
// Law pinned from the as-built server (LinkService.cs): Hello's OpenSessionAsync
// is INSERT … ON CONFLICT DO UPDATE SET session_epoch = existing + 1, and a
// client goodbye DELETES the row (CloseSessionAsync, epoch-matched). So a
// reconnect over a STILL-LIVE row (socket died, no goodbye) bumps the epoch,
// while the first hello after a graceful goodbye re-INSERTs and the epoch
// restarts at 1. GET /deliveries requires a live row and refreshes the lease.
import { type ChildProcess, spawn } from "node:child_process";
import { mkdtempSync, rmSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { afterAll, beforeAll } from "vitest";

export const FACILITATOR_URL = process.env["FACILITATOR_URL"] ?? "";
export const PG_URL = process.env["FACILITATOR_PG"] ?? "";
export const KEYCLOAK_URL = process.env["KEYCLOAK_URL"] ?? "";
export const ENROLL_SECRET = "enroll-dev";
export const E2E = FACILITATOR_URL !== "" && PG_URL !== "" && KEYCLOAK_URL !== "";

let cachedToken: { value: string; expiresAtMs: number } | null = null;

/**
 * Kernel bearer: a client-credentials token for the `facilitator-cli`
 * service account (realm roles carry the operator mapping), cached until 30 s
 * before `exp` — the realm's 300 s lifespan keeps rotation exercised on long
 * files.
 */
export async function kernelToken(): Promise<string> {
  const now = Date.now();
  if (cachedToken && now < cachedToken.expiresAtMs) return cachedToken.value;
  const response = await fetch(`${KEYCLOAK_URL}/realms/facilitator/protocol/openid-connect/token`, {
    method: "post",
    headers: { "content-type": "application/x-www-form-urlencoded" },
    body: new URLSearchParams({
      grant_type: "client_credentials",
      client_id: "facilitator-cli",
      client_secret: "e2e-cli-secret",
    }),
  });
  if (!response.ok) throw new Error(`keycloak token mint failed: ${response.status}`);
  const body = (await response.json()) as { access_token: string; expires_in: number };
  cachedToken = {
    value: body.access_token,
    expiresAtMs: now + (body.expires_in - 30) * 1000,
  };
  return body.access_token;
}

const REPO_ROOT = new URL("../../../../", import.meta.url).pathname;

/** Per-file config overrides merged into the spawned server env. */
export interface ServerConfig {
  [key: string]: string;
}

interface Harness {
  baseUrl: string;
  dbUrl: string;
}

/**
 * psql invocation prefix: bare `psql` by default; locally with no psql
 * binary, `PG_ADMIN_CMD="docker exec facilitator-dev psql"`. Split on
 * spaces (no-args-in-prefix constraint).
 */
const PSQL_CMD = (process.env["PG_ADMIN_CMD"] ?? "psql").split(" ").filter((s) => s.length > 0);
/**
 * URL handed to psql. Defaults to PG_URL; when psql runs INSIDE a container
 * (PG_ADMIN_CMD="docker exec facilitator-dev psql"), the in-container
 * host:port differs from the host-mapped one, so point this at, e.g.,
 * postgresql://postgres:postgres@localhost:5432/postgres.
 */
const PSQL_URL = process.env["PG_ADMIN_URL"] ?? PG_URL;

/** Rewrite a per-file dbUrl's host:port to the psql-side admin endpoint. */
function adminUrl(dbUrl: string): string {
  const target = new URL(PSQL_URL);
  const per = new URL(dbUrl);
  return dbUrl.replace(per.host, target.host);
}
/**
 * Boot a dedicated Facilitator for the current test file. Returns its base
 * URL; `afterAll` tears the process + database down.
 */
export function useFacilitator(config: ServerConfig = {}): Harness {
  const dbName = `e2e_${Math.random().toString(36).slice(2, 10)}`;
  const perFile = new URL(PG_URL);
  perFile.pathname = `/${dbName}`;
  perFile.search = "";
  const harness: Harness = { baseUrl: "", dbUrl: perFile.toString() };
  let server: ChildProcess | null = null;
  const dir = mkdtempSync(join(tmpdir(), "link-e2e-"));

  beforeAll(async () => {
    // Dedicated database → zero cross-file interference (link_sessions,
    // outbox, dead-letters all per-file).
    const [adminCmd, ...adminPre] = PSQL_CMD;
    const admin = spawn(
      adminCmd,
      [...adminPre, PSQL_URL, "-v", "ON_ERROR_STOP=1", "-c", `CREATE DATABASE ${dbName}`],
      { stdio: "inherit" },
    );
    if ((await waitExit(admin)) !== 0) throw new Error(`cannot create ${dbName}`);

    // Port 0 = OS-assigned; scraped from the server's own startup log.
    const portFile = join(dir, "port");
    server = spawn(
      "dotnet",
      [join(REPO_ROOT, "src/facilitator/bin/Release/net10.0/Facilitator.dll")],
      {
        cwd: join(REPO_ROOT, "src/facilitator"),
        env: {
          ...process.env,
          ASPNETCORE_ENVIRONMENT: "Development",
          ASPNETCORE_URLS: "http://127.0.0.1:0",
          ConnectionStrings__Facilitator: `Host=${pgHost()};Port=${pgPort()};Database=${dbName};Username=${pgUser()};Password=${pgPassword()}`,
          DOTNET_ENVIRONMENT: "Development",
          Identity__Oidc__Authority: `${KEYCLOAK_URL}/realms/facilitator`,
          Identity__Oidc__Audience: "facilitator-api",
          Identity__Oidc__RequireHttps: "false",
          ...Object.fromEntries(Object.entries(config).map(([k, v]) => [`Link__${k}`, v])),
          OFAC_E2E_PORTFILE: portFile,
        },
      },
    );
    // Kestrel prints "Now listening on: http://127.0.0.1:PORT" — poll /health
    // across a range of plausible URLs by parsing stdout instead.
    const url = await scrapeUrl(server);
    harness.baseUrl = url;
    // Migrations run at startup (IDatabaseInitializer); /health answers only
    // once the app is serving — but not only after migrations (they precede
    // Listen), so a 200 is enough.
  }, 120_000);

  afterAll(async () => {
    server?.kill("SIGTERM");
    if (server) await waitExit(server, 10_000).catch(() => undefined);
    const [dropCmd, ...dropPre] = PSQL_CMD;
    const drop = spawn(
      dropCmd,
      [...dropPre, PSQL_URL, "-v", "ON_ERROR_STOP=1", "-c", `DROP DATABASE ${dbName} WITH (FORCE)`],
      {
        stdio: "inherit",
      },
    );
    await waitExit(drop, 30_000).catch(() => undefined);
    rmSync(dir, { recursive: true, force: true });
  });

  return harness;
}
/** Run one SQL statement over psql; returns non-empty output lines. */
export function psql(dbUrl: string, sql: string): Promise<string[]> {
  const [cmd, ...pre] = PSQL_CMD;
  return new Promise((resolve, reject) => {
    const child = spawn(cmd, [...pre, adminUrl(dbUrl), "-v", "ON_ERROR_STOP=1", "-At", "-c", sql], {
      stdio: ["ignore", "pipe", "pipe"],
    });
    let out = "";
    let err = "";
    child.stdout?.on("data", (c: Buffer) => {
      out += c.toString();
    });
    child.stderr?.on("data", (c: Buffer) => {
      err += c.toString();
    });
    child.once("exit", (code) =>
      code === 0
        ? resolve(
            out
              .split("\n")
              .map((l) => l.trimEnd())
              .filter((l) => l.length > 0),
          )
        : reject(new Error(`psql failed (${code}): ${err}`)),
    );
  });
}

/** Server-side link_deliveries row for a delivery id, or null when absent. */
export async function linkRow(
  dbUrl: string,
  deliveryId: string,
): Promise<{ state: string; attempts: number; receiptStatus: string } | null> {
  const rows = await psql(
    dbUrl,
    `SELECT state, attempts, COALESCE(receipt->>'status','') FROM facilitator.link_deliveries WHERE delivery_id = '${deliveryId}'`,
  );
  if (rows.length === 0) return null;
  const [state, attempts, receiptStatus] = rows[0]!.split("|");
  return { state: state!, attempts: Number(attempts), receiptStatus: receiptStatus ?? "" };
}

/** Inbox row state for an event envelope id, or null when absent. */
export async function inboxState(dbUrl: string, eventId: string): Promise<string | null> {
  const rows = await psql(
    dbUrl,
    `SELECT state FROM facilitator.inbox WHERE event_id = '${eventId}'`,
  );
  return rows.length === 0 ? null : rows[0]!;
}

export async function waitExit(child: ChildProcess, timeout = 60_000): Promise<number | null> {
  const code = await Promise.race([
    new Promise<number | null>((resolve) => child.once("exit", (c) => resolve(c))),
    new Promise<null>((resolve) => setTimeout(() => resolve(null), timeout)),
  ]);
  if (code === null) child.kill("SIGKILL");
  return code;
}

function scrapeUrl(child: ChildProcess): Promise<string> {
  return new Promise((resolve, reject) => {
    let buffer = "";
    const onData = (chunk: Buffer): void => {
      buffer += chunk.toString();
      const match = /Now listening on: (http:\/\/[^\s]+)/.exec(buffer);
      if (match) {
        child.stdout?.off("data", onData);
        child.stderr?.off("data", onData);
        void fetch(`${match[1]}/health`)
          .then((r) => (r.ok ? resolve(match[1]!) : reject(new Error("health not ok"))))
          .catch(reject);
      }
    };
    child.stdout?.on("data", onData);
    child.stderr?.on("data", onData);
    child.once("exit", (code, signal) =>
      reject(new Error(`facilitator exited early: code=${code} signal=${signal}\n${buffer}`)),
    );
    setTimeout(() => reject(new Error(`no listening URL in 60s\n${buffer}`)), 60_000);
  });
}

function pgUrlParts(): URL {
  return new URL(PG_URL.replace(/^postgres(ql)?:\/\//, "http://"));
}
function pgHost(): string {
  return pgUrlParts().hostname;
}
function pgPort(): number {
  return Number(pgUrlParts().port || 5432);
}
function pgUser(): string {
  return pgUrlParts().username || "postgres";
}
function pgPassword(): string {
  return decodeURIComponent(pgUrlParts().password || "");
}
export { pgPort };

// ── dev affordances ─────────────────────────────────────────────────────────

/** Seed a link-addressed outbox row; returns the outboxId (= future deliveryId). */
export async function seedDelivery(
  baseUrl: string,
  endpointId: string,
  envelope: unknown,
): Promise<string> {
  const response = await post(baseUrl, "/api/dev/link-outbox", await kernelToken(), {
    endpointId,
    envelope,
  });
  if (response.status !== 202) {
    throw new Error(`seed failed ${response.status}: ${JSON.stringify(response.body)}`);
  }
  return (response.body as { outboxId: string }).outboxId;
}

/** Kernel-side ack ledger rows (link acks land there too, LinkHub.AckAsync). */
export async function kernelDeliveries(baseUrl: string): Promise<{ deliveryId: string }[]> {
  const response = await get(baseUrl, "/api/dev/deliveries", await kernelToken());
  return response.body as { deliveryId: string }[];
}

export async function deadLetters(
  baseUrl: string,
): Promise<{ outboxId: string; reason: string }[]> {
  const response = await get(baseUrl, "/api/dead-letters", await kernelToken());
  return response.body as { outboxId: string; reason: string }[];
}

export async function linkGet(baseUrl: string, path: string, token: string): Promise<Reply> {
  return get(baseUrl, path, token);
}
export async function linkPost(
  baseUrl: string,
  path: string,
  token: string,
  body: unknown,
): Promise<Reply> {
  return post(baseUrl, path, token, body);
}

interface Reply {
  status: number;
  body: unknown;
}

async function get(baseUrl: string, path: string, token: string): Promise<Reply> {
  return reply(await fetch(`${baseUrl}${path}`, { headers: { authorization: `Bearer ${token}` } }));
}

async function post(baseUrl: string, path: string, token: string, body: unknown): Promise<Reply> {
  return reply(
    await fetch(`${baseUrl}${path}`, {
      method: "POST",
      headers: { authorization: `Bearer ${token}`, "content-type": "application/json" },
      body: JSON.stringify(body),
    }),
  );
}

async function reply(response: Response): Promise<Reply> {
  const text = await response.text();
  return {
    status: response.status,
    body: text.length === 0 ? null : (JSON.parse(text) as unknown),
  };
}

// ── linkd child-process helper (replay test) ────────────────────────────────

export interface LinkdHandle {
  child: ChildProcess;
  stderr: string;
  /** Resolves on the next `linkd ready` line (any epoch). */
  ready(): Promise<{ epoch: number; line: string }>;
  kill(signal?: NodeJS.Signals): Promise<number | null>;
}

const LINKD = join(REPO_ROOT, "src/workspace-link/dist/bin/linkd.js");

export function startLinkd(args: string[]): LinkdHandle {
  const child = spawn("node", [LINKD, ...args], {
    env: { ...process.env, OFAC_LINKD_LOG: "debug" },
  });
  let stderr = "";
  child.stderr?.on("data", (c: Buffer) => {
    stderr += c.toString();
  });
  let buffer = "";
  const readyWaits: ((v: { epoch: number; line: string }) => void)[] = [];
  child.stdout?.on("data", (chunk: Buffer) => {
    buffer += chunk.toString();
    for (;;) {
      const newline = buffer.indexOf("\n");
      if (newline < 0) break;
      const line = buffer.slice(0, newline);
      buffer = buffer.slice(newline + 1);
      const match = /^linkd ready endpointId=\S+ epoch=(\d+)/.exec(line);
      if (match) {
        const ready = { epoch: Number(match[1]), line };
        readyWaits.shift()?.(ready);
      }
    }
  });
  return {
    child,
    get stderr() {
      return stderr;
    },
    ready() {
      return new Promise((resolve) => readyWaits.push(resolve));
    },
    kill(signal = "SIGKILL") {
      child.kill(signal);
      return waitExit(child, 15_000);
    },
  };
}

// ── misc ────────────────────────────────────────────────────────────────────

export function tempFile(name: string): string {
  return join(mkdtempSync(join(tmpdir(), "link-e2e-")), name);
}

export function makeManifest(ownerTeam: string): Record<string, unknown> {
  return {
    apiVersion: "facilitator.framework/v1",
    kind: "CapabilityManifest",
    ownerTeam,
    capabilities: [
      {
        id: "research.analyze",
        version: 1,
        execution: "asynchronous",
        acceptedContexts: ["uri-reference"],
        outputKinds: ["analysis.report"],
        outputMediaTypes: ["text/markdown"],
        supportsSteering: true,
        supportsCancellation: true,
        supportsNudge: false,
      },
    ],
  };
}

export function manifestFile(contents: Record<string, unknown>): string {
  const path = tempFile("manifest.json");
  writeFileSync(path, JSON.stringify(contents));
  return path;
}

/** A work.assign command envelope with a unique command/work/assignment id. */
export function assignEnvelope(overrides: {
  commandId: string;
  workId: string;
  assignmentId: string;
}): Record<string, unknown> {
  return {
    specversion: "1.0",
    id: `e2e-${overrides.commandId}`,
    source: "urn:facilitator:instance:e2e",
    type: "org.facilitator.command.work.assign.v1",
    subject: `work/${overrides.workId}/assignment/${overrides.assignmentId}`,
    time: new Date().toISOString(),
    datacontenttype: "application/json",
    dataschema: "https://schemas.openfacilitator.org/v1/command.work.assign.v1.json",
    data: {
      commandId: overrides.commandId,
      actorEvidenceRef: "identity-evidence:e2e",
      workId: overrides.workId,
      targetId: "e2e",
      capabilityId: "research.analyze",
      expectedContextVersion: 1,
      inputs: { objective: "e2e", sourceRefs: [] },
      expectedDeliverables: [{ kind: "analysis.report", mediaType: "text/markdown" }],
      deadline: "2036-10-23T18:00:00Z",
      assignmentId: overrides.assignmentId,
    },
  };
}

export async function waitFor<T>(
  what: string,
  probe: () => T | null | false | Promise<T | null | false>,
  timeoutMs = 30_000,
  intervalMs = 250,
): Promise<T> {
  const deadline = Date.now() + timeoutMs;
  for (;;) {
    const value = await probe();
    if (value) return value;
    if (Date.now() > deadline) throw new Error(`timeout waiting for ${what}`);
    await new Promise((r) => setTimeout(r, intervalMs));
  }
}

// ── SDK client builder ────────────────────────────────────────────────────
import {
  createLinkClient,
  createMemoryLogger,
  enroll,
  type LinkClient,
  type LinkHandler,
  type LinkLogger,
  type LinkTokenProvider,
  type LinkTransport,
} from "../../src/index.js";
import { postCapabilities, SignalrTransport, SqliteLedger } from "../../src/node/index.js";
import type { CapabilityManifestV1 } from "@ofac/protocol";

export interface SdkClient {
  client: LinkClient;
  ledger: SqliteLedger;
  token: string;
  endpointId: string;
  digest: string;
  manifestText: string;
}

/**
 * Enroll (or reuse a token) → POST capabilities → build a started SDK client
 * over SignalR by default. The manifest's ownerTeam is `default`: Plan 03's
 * enrollment store binds every enrollment to the seeded `default` team, and
 * POST /capabilities rejects a manifest whose ownerTeam differs.
 */
export async function sdkClient(
  harness: Harness,
  options: {
    endpointId: string;
    token?: string;
    handler?: LinkHandler;
    transport?: LinkTransport;
    /** Builds the transport AFTER enrollment (needs the minted token). */
    transportFactory?: (token: string) => LinkTransport;
    stateFile?: string;
    logger?: LinkLogger;
    maxReconnectAttempts?: number;
  },
): Promise<SdkClient> {
  const token =
    options.token ??
    (
      await enroll({
        baseUrl: harness.baseUrl,
        token: ENROLL_SECRET,
        endpointId: options.endpointId,
        instanceId: `e2e-${Math.random().toString(36).slice(2, 10)}`,
        trustMode: "isolated-executor",
      })
    ).linkToken;
  if (token === undefined) throw new Error("enroll issued no linkToken");
  const provider: LinkTokenProvider = { getToken: async () => token };
  const manifest = makeManifest("default") as unknown as CapabilityManifestV1;
  const posted = await postCapabilities(manifest, {
    baseUrl: harness.baseUrl,
    getToken: async () => token,
  });
  const ledger = new SqliteLedger(options.stateFile ?? tempFile("link-e2e.sqlite"));
  const client = createLinkClient({
    transport:
      options.transport ??
      options.transportFactory?.(token) ??
      new SignalrTransport({ baseUrl: harness.baseUrl, token: provider }),
    ledger,
    endpointId: options.endpointId,
    capabilityManifestDigest: posted.digest,
    token: provider,
    logger: options.logger ?? createMemoryLogger(),
    ...(options.handler !== undefined ? { handler: options.handler } : {}),
    ...(options.maxReconnectAttempts !== undefined
      ? { maxReconnectAttempts: options.maxReconnectAttempts }
      : {}),
  });
  await client.start();
  return {
    client,
    ledger,
    token,
    endpointId: options.endpointId,
    digest: posted.digest,
    manifestText: posted.body,
  };
}
