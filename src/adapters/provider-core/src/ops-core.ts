// The connector's advertised manifest + op-table scaffolding (provider-
// agnostic). The opt-in flags filter the list: a disabled domain is invisible
// on the wire, so no row can ever route to it. Op inputs mirror the
// facilitator-core conventions (`repo`, `number`/`issue`, `body`, `event`,
// `commentId`, `sha`, `state`, `labels[]`); a provider lacking an op simply
// omits the descriptor from its map.
import { createHash } from "node:crypto";
import type { CapabilityDescriptor, CapabilityManifestV1 } from "@ofac/protocol";
import type { ActionLedger } from "./ledger-core.js";

export function connectorManifest(
  team: string,
  flagDomains: Readonly<Record<string, readonly string[]>>,
): CapabilityManifestV1 {
  const descriptor = (id: string): CapabilityDescriptor => ({
    id,
    version: 1,
    execution: "asynchronous",
    supportsSteering: false,
    supportsCancellation: false,
    supportsNudge: false,
  });
  const capabilities: CapabilityDescriptor[] = [];
  const seen = new Set<string>();
  for (const ids of Object.values(flagDomains)) {
    for (const id of ids) {
      if (!seen.has(id)) {
        seen.add(id);
        capabilities.push(descriptor(id));
      }
    }
  }

  if (capabilities.length === 0) {
    throw new Error("no op domain enabled (no flag mapped any capability id)");
  }

  return {
    apiVersion: "facilitator.framework/v1",
    kind: "CapabilityManifest",
    ownerTeam: team,
    capabilities: capabilities as CapabilityManifestV1["capabilities"],
  };
}

/** Canonical digest of the op inputs: stable key order, JSON encoding. */
export function payloadSha256(inputs: Record<string, unknown>): string {
  const sorted: Record<string, unknown> = {};
  for (const key of Object.keys(inputs).sort()) {
    sorted[key] = inputs[key];
  }

  return `sha256:${createHash("sha256").update(JSON.stringify(sorted), "utf8").digest("hex")}`;
}

export class OpError extends Error {
  constructor(op: string, message: string) {
    super(`${op}: ${message}`);
    this.name = "OpError";
  }
}

export interface OpDegradation {
  /** The provider HTTP status that stalled the op (429/403 are the named classes). */
  status: number;
  /** Machine-readable stall reason, e.g. `throttled` / `forbidden`. */
  reason: string;
  retryAfterSeconds?: number;
}

export interface OpResult {
  providerObjectId: string;
  structured: Record<string, unknown>;
  /** N7/F6: a stall that did NOT stop the op (reconciled, retried into
   *  success) rides here, so the operator sees the provider's reluctance
   *  instead of a silent green row. */
  degraded?: OpDegradation;
}

export function required(inputs: Record<string, unknown>, key: string, op: string): string {
  const v = inputs[key];
  if (typeof v !== "string" || v === "") throw new OpError(op, `input '${key}' required`);
  return v;
}

export function requiredNumber(inputs: Record<string, unknown>, key: string, op: string): number {
  const v = inputs[key];
  const n = typeof v === "number" ? v : typeof v === "string" && v !== "" ? Number(v) : Number.NaN;
  if (!Number.isInteger(n) || n <= 0) {
    throw new OpError(op, `input '${key}' must be a positive integer`);
  }

  return n;
}

/** The comment body stamp: the ledger idempotency marker rides in the body,
 *  so a lost response is reconcilable by READ of the provider's own truth. */
export function stampedBody(body: string, commandId: string): string {
  return `${body}\n\n<!-- ofac-cmd:${commandId} -->`;
}

export const CMD_MARKER = (commandId: string): string => `ofac-cmd:${commandId}`;

export interface OpDeps {
  ledger: ActionLedger;
}

export type OpRunner = (commandId: string, inputs: Record<string, unknown>) => Promise<OpResult>;

/** One runner per op id; the manifest filter decides which are reachable. */
export type OpTable = Record<string, OpRunner>;

/** The shared replay law: an executed row answers with the recorded id, full stop. */
/** The one mapping from an HTTP stall to the observable degradation record
 *  (N7, F6): ops surfaces copy it verbatim, so 429/403 read the same on the
 *  wire for every vendor. */
export function degradationFrom(error: {
  status: number;
  retryAfterSeconds?: number | undefined;
}): OpDegradation {
  return {
    status: error.status,
    reason: error.status === 429 ? "throttled" : error.status === 403 ? "forbidden" : "http-error",
    ...(error.retryAfterSeconds !== undefined ? { retryAfterSeconds: error.retryAfterSeconds } : {}),
  };
}

export function replayFrom(ledger: ActionLedger, commandId: string): OpResult | undefined {
  const row = ledger.find(commandId);
  if (row !== undefined && row.state === "executed" && row.providerObjectId !== null) {
    return {
      providerObjectId: row.providerObjectId,
      structured: { replayed: true, providerObjectId: row.providerObjectId },
    };
  }

  return undefined; // absent or pending: the domain proceeds (and reconciles)
}
