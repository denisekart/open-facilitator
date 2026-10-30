// Op execution (decision 5): the assign envelope's `data` carries
// `capabilityId` (the op id) and `inputs` (params). Every op runs the same
// law: canonicalize inputs → ledger intent → replay/reconcile → ONE provider
// mutation → settle. Policy and approval are NOT here — they rode the
// Gateway's confirm loop before this envelope ever arrived, so an op reaching
// this module is already authorized (privilege ceiling: the forge token
// lives only in this process).
//
// Ambiguous-timeout law (§5.4): when a mutation's HTTP answer is lost
// (timeout/5xx on a write), the run fails with the ledger row left `pending`
// and NO provider object id; a later attempt reconciles by READ before
// touching the forge again. (Ops here fail fast on transport errors, so the
// pending row is written first and reconciled by the retry.)
import { createHash } from "node:crypto";
import type { ForgejoApi } from "@ofac/forgejo-adapter";
import type { ActionLedger } from "./ledger.js";

export class OpError extends Error {
  constructor(
    readonly op: string,
    message: string,
  ) {
    super(`${op}: ${message}`);
    this.name = "OpError";
  }
}

export interface OpResult {
  /** The provider object the op touched, wire-presented (e.g. `PR demo/widgets#7`). */
  providerObjectId: string;
  /** Machine-readable result payload for the completed event. */
  structured: Record<string, unknown>;
}

/** Canonical digest of the op inputs: stable key order, JSON encoding. */
export function payloadSha256(inputs: Record<string, unknown>): string {
  const sorted: Record<string, unknown> = {};
  for (const key of Object.keys(inputs).sort()) {
    sorted[key] = inputs[key];
  }

  return "sha256:" + createHash("sha256").update(JSON.stringify(sorted), "utf8").digest("hex");
}

function required(inputs: Record<string, unknown>, key: string, op: string): string {
  const v = inputs[key];
  if (typeof v !== "string" || v === "") throw new OpError(op, `inputs.${key}: required string`);
  return v;
}

function requiredNumber(inputs: Record<string, unknown>, key: string, op: string): number {
  const v = inputs[key];
  const n = typeof v === "number" ? v : typeof v === "string" && v !== "" ? Number(v) : Number.NaN;
  if (!Number.isInteger(n) || n <= 0)
    throw new OpError(op, `inputs.${key}: required positive integer`);
  return n;
}

/** The comment body stamp: the ledger idempotency marker rides in the body,
 *  so a lost response is reconcilable by READ of the forge's own truth. */
function stampedBody(body: string, commandId: string): string {
  return `${body}\n\n<!-- ofac-cmd:${commandId} -->`;
}

const MARKER = (commandId: string): string => `ofac-cmd:${commandId}`;

export interface OpDeps {
  api: ForgejoApi;
  ledger: ActionLedger;
}

export type OpRunner = (commandId: string, inputs: Record<string, unknown>) => Promise<OpResult>;

/** One runner per op id; the manifest filter decides which are reachable. */
export type OpTable = Record<string, OpRunner>;

export function createOps(deps: OpDeps): OpTable {
  const { api, ledger } = deps;

  /** Shared replay law: an executed row answers with the recorded id, full stop. */
  function replay(commandId: string): OpResult | undefined {
    const row = ledger.find(commandId);
    if (row !== undefined && row.state === "executed" && row.providerObjectId !== null) {
      return {
        providerObjectId: row.providerObjectId,
        structured: { replayed: true, providerObjectId: row.providerObjectId },
      };
    }

    return undefined; // absent or pending: the domain proceeds (and reconciles)
  }

  const prCreate: OpRunner = async (commandId, inputs) => {
    const op = "forgejo.pr.create";
    const repo = required(inputs, "repo", op);
    const head = required(inputs, "head", op);
    const base = required(inputs, "base", op);
    const title = required(inputs, "title", op);
    const body = typeof inputs["body"] === "string" ? inputs["body"] : "";
    ledger.insert(commandId, op, payloadSha256({ repo, head, base, title, body }));
    const cached = replay(commandId);
    if (cached !== undefined) return cached;

    // Read-before-fire: an identical open PR (same head ref) satisfies the
    // intent without a second POST — covers BOTH a previous ambiguous
    // timeout and a genuinely concurrent duplicate request.
    const existing = await api.openPrsForHead(repo, head);
    const number = existing[0] ?? (await api.openPr(repo, head, base, title, body)).number;
    const id = `PR ${repo}#${number}`;
    ledger.settle(commandId, id);
    return { providerObjectId: id, structured: { repo, number, head, base } };
  };

  const issueRead: OpRunner = async (commandId, inputs) => {
    const op = "forgejo.issue.read";
    const repo = required(inputs, "repo", op);
    const issue = requiredNumber(inputs, "issue", op);
    // Read-only: still ledgered, so a delivery replay costs zero provider calls.
    ledger.insert(commandId, op, payloadSha256({ repo, issue }));
    const cached = replay(commandId);
    if (cached !== undefined) return cached;
    const dto = await api.getIssue(repo, issue);
    const id = `issue ${repo}#${dto.number}`;
    ledger.settle(commandId, id);
    return {
      providerObjectId: id,
      structured: { repo, number: dto.number, state: dto.state, title: dto.title },
    };
  };

  const makeComment =
    (pr: boolean): OpRunner =>
    async (commandId, inputs) => {
      const op = pr ? "forgejo.pr.comment" : "forgejo.issue.comment";
      const repo = required(inputs, "repo", op);
      const number = requiredNumber(inputs, "issue", op);
      const body = required(inputs, "body", op);
      ledger.insert(commandId, op, payloadSha256({ repo, issue: number, body }));
      const cached = replay(commandId);
      if (cached !== undefined) return cached;

      // Ambiguous-timeout reconcile: the forge answers whether the stamped
      // comment already exists; a lost response NEVER double-posts.
      const stamp = stampedBody(body, commandId);
      const found = await api.findCommentByMarker(repo, number, MARKER(commandId));
      const comment = found ?? (await api.postComment(repo, number, stamp));
      const id = `comment ${comment.id}`;
      ledger.settle(commandId, id);
      return { providerObjectId: id, structured: { repo, issue: number, commentId: comment.id } };
    };

  const branchPush: OpRunner = async (commandId, inputs) => {
    const op = "forgejo.vcs.branch.push";
    const repo = required(inputs, "repo", op);
    const branch = required(inputs, "branch", op);
    const fromRef = required(inputs, "from", op);
    const payload = payloadSha256({ repo, branch, from: fromRef });
    // Probe BEFORE the insert: an ABSENT row is a fresh attempt (a 409 from
    // the forge is a real conflict); a pre-existing PENDING row means a
    // previous attempt lost its answer, and 409 IS the reconciled truth.
    const prior = ledger.find(commandId);
    ledger.insert(commandId, op, payload);
    const cached = replay(commandId);
    if (cached !== undefined) return cached;
    try {
      await api.createBranch(repo, branch, fromRef);
    } catch (error) {
      const conflict =
        typeof error === "object" &&
        error !== null &&
        "status" in error &&
        (error as { status: unknown }).status === 409;
      if (prior?.state !== "pending" || !conflict) throw error;
    }

    const id = `branch ${repo}/${branch}`;
    ledger.settle(commandId, id);
    return { providerObjectId: id, structured: { repo, branch, from: fromRef } };
  };

  const ciTrigger: OpRunner = async (commandId, inputs) => {
    const op = "forgejo.ci.trigger";
    const repo = required(inputs, "repo", op);
    const sha = required(inputs, "sha", op);
    const state = required(inputs, "state", op);
    const description = typeof inputs["description"] === "string" ? inputs["description"] : state;
    const context = typeof inputs["context"] === "string" ? inputs["context"] : "ofac";
    ledger.insert(commandId, op, payloadSha256({ repo, sha, state, description, context }));
    const cached = replay(commandId);
    if (cached !== undefined) return cached;

    // Commit statuses are idempotent upserts keyed by (context, sha): a
    // re-fire after an ambiguous timeout converges, so a blind retry is safe.
    await api.commitStatus(repo, sha, state, description, context);
    const id = `status ${repo}@${sha.slice(0, 10)}:${context}`;
    ledger.settle(commandId, id);
    return { providerObjectId: id, structured: { repo, sha, state, context } };
  };

  return {
    "forgejo.pr.create": prCreate,
    "forgejo.issue.read": issueRead,
    "forgejo.issue.comment": makeComment(false),
    "forgejo.pr.comment": makeComment(true),
    "forgejo.vcs.branch.push": branchPush,
    "forgejo.ci.trigger": ciTrigger,
  };
}
