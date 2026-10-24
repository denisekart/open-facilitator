// Gateway E2E harness: imports workspace-link's harness (server boot,
// seeding, psql) and adds the Gateway-process primitives. Cross-package
// imports of the harness TS source resolve fine under vitest + tsc here,
// so no fork (Plan 05 step 0 sanctioned fallback stays unused).
import { type ChildProcess, spawn } from "node:child_process";
import { createServer } from "node:net";
import { mkdtempSync, readFileSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { ENROLL_SECRET, psql, waitExit } from "../../../workspace-link/test/e2e/harness.js";

export {
  E2E,
  ENROLL_SECRET,
  FACILITATOR_URL,
  PG_URL,
  assignEnvelope,
  kernelDeliveries,
  linkRow,
  psql,
  seedDelivery,
  useFacilitator,
  waitFor,
  type ServerConfig,
} from "../../../workspace-link/test/e2e/harness.js";

// The harness's local interface isn't exported (Plan 04 file, don't fork):
// derive it from the factory's return type.
import { useFacilitator as _useFacilitator } from "../../../workspace-link/test/e2e/harness.js";
export type Harness = ReturnType<typeof _useFacilitator>;

const GATEWAY_BIN = join(
  new URL("../../../../", import.meta.url).pathname,
  "src/gateway/dist/bin/gateway.js",
);

/** OS-assigned free port for the diagnostics endpoint (config 0 = disabled). */
export function freePort(): Promise<number> {
  return new Promise((resolve, reject) => {
    const probe = createServer();
    probe.once("error", reject);
    probe.listen(0, "127.0.0.1", () => {
      const address = probe.address();
      const port = typeof address === "object" && address !== null ? address.port : 0;
      probe.close(() => resolve(port));
    });
  });
}

export interface GatewayHandleConfig {
  configPath: string;
  stateDir: string;
  gatewayId: string;
  diagnosticsPort: number;
  /** Absolute unix socket path (fleet modes); undefined for embedded. */
  socketPath?: string;
}

let idCounter = 0;

/** Fresh gatewayId + stateDir + explicit diagnostics port; writes the YAML. */
export async function gatewayConfig(options: {
  facilitatorUrl: string;
  mode?: "echo" | "stub";
  childName?: string;
  heartbeatSeconds?: number;
  /** Fleet children (shared/standalone): name + token; socket auto-configured. */
  fleet?: { name: string; token?: string; maxInFlight?: number }[];
  /** Gateway role for fleet YAML (default: shared). */
  gatewayMode?: "shared" | "standalone";
}): Promise<GatewayHandleConfig> {
  idCounter += 1;
  const gatewayId = `gw.e2e-${Math.random().toString(36).slice(2, 8)}${idCounter}`;
  const stateDir = mkdtempSync(join(tmpdir(), "gw-e2e-"));
  const diagnosticsPort = await freePort();
  const yaml = `
apiVersion: facilitator.framework/v1
kind: Gateway
gatewayId: ${gatewayId}
team: default
facilitatorUrl: ${options.facilitatorUrl}
enrollmentSecret: ${ENROLL_SECRET}
trustMode: shared-runtime
stateDir: ${stateDir}
${
  options.fleet === undefined
    ? `child:
  name: ${options.childName ?? "echo"}
  mode: ${options.mode ?? "echo"}`
    : `children${
        options.fleet.length === 0
          ? ": []"
          : `:
${options.fleet
  .map(
    (c) =>
      `  - name: ${c.name}
    token: ${c.token ?? "tok"}
    maxInFlight: ${String(c.maxInFlight ?? 1)}
    timeoutSeconds: 120`,
  )
  .join("\n")}`
      }
childTransport:
  socket:
    path: child.sock
mode: ${options.gatewayMode ?? "shared"}`
}
diagnostics:
  port: ${diagnosticsPort}
heartbeatSeconds: ${options.heartbeatSeconds ?? 5}
`;
  const configPath = join(stateDir, "gateway.yaml");
  writeFileSync(configPath, yaml);
  return {
    configPath,
    stateDir,
    gatewayId,
    diagnosticsPort,
    ...(options.fleet === undefined ? {} : { socketPath: join(stateDir, "child.sock") }),
  };
}

export interface GatewayHandle {
  child: ChildProcess;
  /** stdout+stderr, for log-law assertions. */
  log(): string;
  /** Resolves on the next `gateway ready` line. */
  ready(): Promise<{ epoch: number; children: number; line: string }>;
  kill(signal?: NodeJS.Signals): Promise<number | null>;
}

export function startGateway(configPath: string): GatewayHandle {
  const child = spawn("node", [GATEWAY_BIN, "run", "--config", configPath], {
    env: { ...process.env, OFAC_GATEWAY_LOG: "debug" },
  });
  let log = "";
  child.stdout?.on("data", (c: Buffer) => {
    log += c.toString();
  });
  child.stderr?.on("data", (c: Buffer) => {
    log += c.toString();
  });
  let buffer = "";
  const readyWaits: ((v: { epoch: number; children: number; line: string }) => void)[] = [];
  child.stdout?.on("data", (chunk: Buffer) => {
    buffer += chunk.toString();
    for (;;) {
      const newline = buffer.indexOf("\n");
      if (newline < 0) break;
      const line = buffer.slice(0, newline);
      buffer = buffer.slice(newline + 1);
      const match = /^gateway ready endpointId=\S+ epoch=(\d+) children=(\d+)/.exec(line);
      if (match) {
        const ready = { epoch: Number(match[1]), children: Number(match[2]), line };
        readyWaits.shift()?.(ready);
      }
    }
  });
  return {
    child,
    log: () => log,
    ready() {
      return new Promise((resolve) => readyWaits.push(resolve));
    },
    kill(signal = "SIGKILL") {
      child.kill(signal);
      return waitExit(child, 15_000);
    },
  };
}

/** The link token the Gateway persisted at <stateDir>/token. */
export function gatewayToken(stateDir: string): string {
  return readFileSync(join(stateDir, "token"), "utf8").trim();
}

/** The /status document, as observed over HTTP (loose mirror of GatewayStatus). */
export interface GatewayStatusDoc {
  gatewayId: string;
  state: string;
  mode: string;
  reconciled: boolean;
  session?: { leaseEpoch: number; sessionId: string; state: string } | undefined;
  executions: { running: number; completed: number; failed: number; lost: number };
  bindings: {
    assignmentId: string;
    executionId: string;
    state: string;
    connectionGeneration: number;
    childId: string;
    childGeneration: number;
  }[];
  children?: {
    childWorkspaceId: string;
    state: string;
    childGeneration: number;
    manifestDigest: string;
    inFlight: number;
  }[];
}

/** GET the Gateway's own diagnostics status document. */
export async function gatewayStatus(port: number): Promise<GatewayStatusDoc> {
  const response = await fetch(`http://127.0.0.1:${port}/status`);
  if (!response.ok) throw new Error(`/status answered ${response.status}`);
  return (await response.json()) as GatewayStatusDoc;
}

/** psql helper: inbox rows whose CloudEvents envelope carries `type`. */
export async function inboxRowsByType(
  dbUrl: string,
  type: string,
): Promise<{ eventId: string; state: string; payload: string }[]> {
  const rows = await psql(
    dbUrl,
    `SELECT event_id, state, payload::text FROM facilitator.inbox WHERE payload->>'type' = '${type}'`,
  );
  return rows.map((line) => {
    const first = line.indexOf("|");
    const second = line.indexOf("|", first + 1);
    return {
      eventId: line.slice(0, first),
      state: line.slice(first + 1, second),
      payload: line.slice(second + 1),
    };
  });
}

/** Spawn a real child process (test/e2e/child-runner.mjs) against a socket. */
export function startChild(options: {
  childWorkspaceId: string;
  socket: string;
  token?: string;
  mode?: "complete" | "silent";
  capability?: string;
}): ChildProcess {
  const runner = join(new URL(".", import.meta.url).pathname, "child-runner.mjs");
  const child = spawn(
    "node",
    [
      runner,
      "--child",
      options.childWorkspaceId,
      "--socket",
      options.socket,
      "--token",
      options.token ?? "tok",
      "--mode",
      options.mode ?? "silent",
      "--capability",
      options.capability ?? "research.analyze",
    ],
    { stdio: ["ignore", "pipe", "pipe"] },
  );
  return child;
}

/** Resolves when the child process prints its `child ready` line. */
export function childReady(child: ChildProcess): Promise<string> {
  return new Promise((resolve, reject) => {
    let buffer = "";
    child.stdout?.on("data", (c: Buffer) => {
      buffer += c.toString();
      const line = buffer.split("\n")[0];
      if (line !== undefined && line.startsWith("child ready")) resolve(line);
    });
    child.once("exit", (code) => reject(new Error(`child exited early: ${String(code)}`)));
  });
}
