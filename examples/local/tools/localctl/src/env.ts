// Roles-file loading + validation (fail-closed, every error names the key)
// and the secrets/versions .env readers. Mirrors examples/aws/lib/env.ts
// posture: a missing key is a hard stop, never a silent default.
import { readFileSync, existsSync } from "node:fs";
import path from "node:path";
import { fail } from "./util.js";

export type Topology = "sim" | "lan" | "tailscale";
export type ImageRef = "local" | string; // "name@sha256:…" or "name:tag"

export interface NodeRoles {
  host: string;
  gatewayId: string;
  teams: string[];
  launcher: boolean;
}

export interface KernelPorts {
  kernel: number;
  idp: number;
  aspireUi: number;
}

export interface RolesFile {
  name: string;
  topology: Topology;
  kernel: {
    host: string;
    ports: KernelPorts;
    teams: string[];
    enrollmentTtlMinutes: number;
  };
  nodes: NodeRoles[];
  images: { kernel: ImageRef; node: ImageRef; worker: ImageRef };
  tls: "internal" | "acme";
  tailscale?: { keyEnv: string; funnel: boolean };
  backupPath: string;
  secretsFile: string;
  externals?: Record<string, Record<string, string>>;
  sshUser: string;
  remoteDir: string;
}

export const DEFAULT_PORTS: KernelPorts = { kernel: 7488, idp: 7480, aspireUi: 7490 };
export const SHARED_NETWORK = "ofac-local";

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === "object" && value !== null && !Array.isArray(value);
}

function req(obj: Record<string, unknown>, key: string, where: string): unknown {
  const value = obj[key];
  if (value === undefined || value === null) fail(`${where}.${key}:? missing (required key)`);
  return value;
}

function reqString(obj: Record<string, unknown>, key: string, where: string): string {
  const value = req(obj, key, where);
  if (typeof value !== "string" || value === "") fail(`${where}.${key}:? must be a non-empty string`);
  return value;
}

function reqStringArray(obj: Record<string, unknown>, key: string, where: string): string[] {
  const value = req(obj, key, where);
  if (!Array.isArray(value) || value.length === 0 || !value.every((v) => typeof v === "string"))
    fail(`${where}.${key}:? must be a non-empty array of strings`);
  return value as string[];
}

function optNumber(obj: Record<string, unknown>, key: string, where: string, dflt: number): number {
  const value = obj[key];
  if (value === undefined) return dflt;
  if (typeof value !== "number" || !Number.isInteger(value) || value < 1)
    fail(`${where}.${key}:? must be a positive integer`);
  return value;
}

/** Validate a parsed roles file; every failure names the offending key path. */
export function parseRoles(raw: unknown, where: string): RolesFile {
  if (!isRecord(raw)) fail(`${where}:? must be a JSON object`);
  const topology = reqString(raw, "topology", where);
  if (topology !== "sim" && topology !== "lan" && topology !== "tailscale")
    fail(`${where}.topology:? must be "sim" | "lan" | "tailscale" (got "${topology}")`);

  const kernelRaw = req(raw, "kernel", where);
  if (!isRecord(kernelRaw)) fail(`${where}.kernel:? must be an object`);
  const portsRaw = kernelRaw["ports"];
  const ports: KernelPorts = { ...DEFAULT_PORTS };
  if (portsRaw !== undefined) {
    if (!isRecord(portsRaw)) fail(`${where}.kernel.ports:? must be an object`);
    ports.kernel = optNumber(portsRaw, "kernel", `${where}.kernel.ports`, DEFAULT_PORTS.kernel);
    ports.idp = optNumber(portsRaw, "idp", `${where}.kernel.ports`, DEFAULT_PORTS.idp);
    ports.aspireUi = optNumber(portsRaw, "aspireUi", `${where}.kernel.ports`, DEFAULT_PORTS.aspireUi);
  }

  const nodesRaw = req(raw, "nodes", where);
  if (!Array.isArray(nodesRaw) || nodesRaw.length === 0)
    fail(`${where}.nodes:? must be a non-empty array`);
  const nodes: NodeRoles[] = nodesRaw.map((node, i) => {
    const at = `${where}.nodes[${i}]`;
    if (!isRecord(node)) fail(`${at}:? must be an object`);
    const gatewayId = reqString(node, "gatewayId", at);
    if (!/^gw\.[a-z0-9-]+$/.test(gatewayId))
      fail(`${at}.gatewayId:? must match ^gw\\.[a-z0-9-]+$ (got "${gatewayId}")`);
    const launcher = node["launcher"];
    if (typeof launcher !== "boolean") fail(`${at}.launcher:? must be a boolean`);
    return {
      host: reqString(node, "host", at),
      gatewayId,
      teams: reqStringArray(node, "teams", at),
      launcher,
    };
  });
  if (new Set(nodes.map((n) => n.host)).size !== nodes.length)
    fail(`${where}.nodes:? duplicate node host`);

  const images: RolesFile["images"] = { kernel: "local", node: "local", worker: "local" };
  if (raw["images"] !== undefined) {
    if (!isRecord(raw["images"])) fail(`${where}.images:? must be an object`);
    for (const key of ["kernel", "node", "worker"] as const) {
      const value = raw["images"][key];
      if (value === undefined || value === "local") continue;
      if (typeof value !== "string" || !/^[a-z0-9.\/:-]+(@sha256:[0-9a-f]{64}|:[A-Za-z0-9._-]+)$/.test(value))
        fail(`${where}.images.${key}:? must be "local", "name@sha256:<64hex>", or "name:tag"`);
      images[key] = value;
    }
  }

  const tls = raw["tls"] ?? "internal";
  if (tls !== "internal" && tls !== "acme") fail(`${where}.tls:? must be "internal" | "acme"`);

  let tailscale: RolesFile["tailscale"];
  if (raw["tailscale"] !== undefined) {
    if (!isRecord(raw["tailscale"])) fail(`${where}.tailscale:? must be an object`);
    const funnel = raw["tailscale"]["funnel"];
    if (typeof funnel !== "boolean") fail(`${where}.tailscale.funnel:? must be a boolean`);
    tailscale = { keyEnv: reqString(raw["tailscale"], "keyEnv", `${where}.tailscale`), funnel };
  }
  if (topology === "tailscale" && tailscale === undefined)
    fail(`${where}.tailscale:? required when topology is "tailscale"`);

  const externals: RolesFile["externals"] = {};
  if (raw["externals"] !== undefined) {
    if (!isRecord(raw["externals"])) fail(`${where}.externals:? must be an object`);
    for (const [name, cfg] of Object.entries(raw["externals"])) {
      if (!isRecord(cfg) || !Object.values(cfg).every((v) => typeof v === "string"))
        fail(`${where}.externals.${name}:? must be an object of string fields`);
      externals[name] = cfg as Record<string, string>;
    }
  }

  const kernelTeams = reqStringArray(kernelRaw, "teams", `${where}.kernel`);
  for (const node of nodes)
    for (const team of node.teams)
      if (!kernelTeams.includes(team))
        fail(`${where}.nodes: node "${node.host}" claims team "${team}" absent from kernel.teams`);

  return {
    name: reqString(raw, "name", where),
    topology,
    kernel: {
      host: reqString(kernelRaw, "host", `${where}.kernel`),
      ports,
      teams: kernelTeams,
      enrollmentTtlMinutes: optNumber(kernelRaw, "enrollmentTtlMinutes", `${where}.kernel`, 60),
    },
    nodes,
    images,
    tls,
    ...(tailscale !== undefined ? { tailscale } : {}),
    backupPath: reqString(raw, "backupPath", where),
    secretsFile: reqString(raw, "secretsFile", where),
    ...(Object.keys(externals).length > 0 ? { externals } : {}),
    sshUser: raw["sshUser"] === undefined ? "facilitator" : reqString(raw, "sshUser", where),
    remoteDir: raw["remoteDir"] === undefined ? "/opt/facilitator" : reqString(raw, "remoteDir", where),
  };
}

export function loadRoles(exampleDir: string, envName: string): RolesFile {
  const file = path.join(exampleDir, "envs", `${envName}.json`);
  if (!existsSync(file)) fail(`env "${envName}":? no roles file at ${file}`);
  let raw: unknown;
  try {
    raw = JSON.parse(readFileSync(file, "utf8")) as unknown;
  } catch (e) {
    fail(`${file}: not valid JSON (${String(e)})`);
  }
  return parseRoles(raw, `${envName}.json`);
}

/** Minimal .env reader; `?`-style suffix noise tolerated, quotes stripped. */
export function readDotEnv(file: string): Record<string, string> {
  if (!existsSync(file)) fail(`secretsFile:? missing ${file} (run localctl init)`);
  const out: Record<string, string> = {};
  for (const line of readFileSync(file, "utf8").split("\n")) {
    const m = /^\s*([A-Za-z_][A-Za-z0-9_]*)\s*=\s*(.*)\s*$/.exec(line);
    if (m === null || line.trim().startsWith("#")) continue;
    out[m[1] as string] = (m[2] as string).replace(/^["']|["']$/g, "");
  }
  return out;
}

export function requireSecret(secrets: Record<string, string>, key: string, file: string): string {
  const value = secrets[key];
  if (value === undefined || value === "")
    fail(`${file}: ${key}:? not set (generate with localctl init)`);
  return value;
}
