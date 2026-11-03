// Op execution: the assign envelope's `data` carries `capabilityId` (the op
// id) and `inputs` (params). Every op runs the same law: canonicalize inputs
// → ledger intent → replay/reconcile → ONE provider mutation → settle.
// Policy and approval are NOT here — they rode the Gateway's confirm loop
// before this envelope ever arrived, so an op reaching this module is already
// authorized (privilege ceiling: the gitlab token lives only in this process).
//
// Ambiguous-timeout law: when a mutation's HTTP answer is lost
// (timeout/5xx on a write), the run fails with the ledger row left `pending`
// and NO provider object id; a later attempt reconciles by READ before
// touching GitLab again. (Ops here fail fast on transport errors, so the
// pending row is written first and reconciled by the retry.)
import type { GitLabApi } from "@ofac/gitlab-adapter";
import {
  CMD_MARKER,
  OpError,
  payloadSha256,
  replayFrom,
  required,
  requiredNumber,
  stampedBody,
  type ActionLedger,
  type OpResult,
  type OpRunner,
  type OpTable,
} from "@ofac/provider-core";
export { CMD_MARKER, OpError, payloadSha256, type OpResult, type OpRunner, type OpTable };
export type { ActionLedger };

export interface OpDeps {
  api: GitLabApi;
  ledger: ActionLedger;
}

export function createOps(deps: OpDeps): OpTable {
  const { api, ledger } = deps;
  /** Shared replay law (provider-core): an executed row answers with the
   *  recorded id, full stop. */
  const replay = (commandId: string): OpResult | undefined => replayFrom(ledger, commandId);

  const mrCreate: OpRunner = async (commandId, inputs) => {
    const op = "vcs.change_request.create";
    const repo = required(inputs, "repo", op);
    const head = required(inputs, "head", op);
    const base = required(inputs, "base", op);
    const title = required(inputs, "title", op);
    const body = typeof inputs["body"] === "string" ? inputs["body"] : "";
    ledger.insert(commandId, op, payloadSha256({ repo, head, base, title, body }));
    const cached = replay(commandId);
    if (cached !== undefined) return cached;

    // Read-before-fire: an identical open MR (same source branch) satisfies
    // the intent without a second POST — covers BOTH a previous ambiguous
    // timeout and a genuinely concurrent duplicate request.
    const existing = await api.openMrsForSource(repo, head);
    const iid = existing[0] ?? (await api.openMr(repo, head, base, title, body)).iid;
    const id = `MR ${repo}!${iid}`;
    ledger.settle(commandId, id);
    return { providerObjectId: id, structured: { repo, number: iid, head, base } };
  };

  const issueRead: OpRunner = async (commandId, inputs) => {
    const op = "vcs.issue.read";
    const repo = required(inputs, "repo", op);
    const issue = requiredNumber(inputs, "issue", op);
    // Read-only: still ledgered, so a delivery replay costs zero provider calls.
    ledger.insert(commandId, op, payloadSha256({ repo, issue }));
    const cached = replay(commandId);
    if (cached !== undefined) return cached;
    const dto = await api.getIssue(repo, issue);
    const id = `issue ${repo}#${dto.iid}`;
    ledger.settle(commandId, id);
    return {
      providerObjectId: id,
      structured: { repo, number: dto.iid, state: dto.state, title: dto.title },
    };
  };

  // GitLab has no unified comment endpoint: the domain is part of the op
  // (issue note vs MR note), so the two comment ops carry it.
  const makeComment =
    (mr: boolean): OpRunner =>
    async (commandId, inputs) => {
      const op = mr ? "vcs.change_request.comment" : "vcs.issue.comment";
      const repo = required(inputs, "repo", op);
      const number = requiredNumber(inputs, "issue", op);
      const body = required(inputs, "body", op);
      ledger.insert(commandId, op, payloadSha256({ repo, issue: number, body }));
      const cached = replay(commandId);
      if (cached !== undefined) return cached;

      // Ambiguous-timeout reconcile: GitLab answers whether the stamped note
      // already exists; a lost response NEVER double-posts.
      const stamp = stampedBody(body, commandId);
      const found = await api.findNoteByMarker(repo, number, CMD_MARKER(commandId), mr);
      const note =
        found ?? (await (mr ? api.postMrNote : api.postIssueNote).call(api, repo, number, stamp));
      const id = `note ${note.id}`;
      ledger.settle(commandId, id);
      return { providerObjectId: id, structured: { repo, issue: number, commentId: note.id } };
    };

  const branchPush: OpRunner = async (commandId, inputs) => {
    const op = "vcs.branch.push";
    const repo = required(inputs, "repo", op);
    const branch = required(inputs, "branch", op);
    const fromRef = required(inputs, "from", op);
    const payload = payloadSha256({ repo, branch, from: fromRef });
    // Probe BEFORE the insert: an ABSENT row is a fresh attempt (a 409 from
    // GitLab is a real conflict); a pre-existing PENDING row means a previous
    // attempt lost its answer, and 409 IS the reconciled truth.
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

  const pipelineTrigger: OpRunner = async (commandId, inputs) => {
    const op = "vcs.pipeline.trigger";
    const repo = required(inputs, "repo", op);
    const sha = required(inputs, "sha", op);
    const state = required(inputs, "state", op);
    const description = typeof inputs["description"] === "string" ? inputs["description"] : state;
    const context = typeof inputs["context"] === "string" ? inputs["context"] : "ofac";
    ledger.insert(commandId, op, payloadSha256({ repo, sha, state, description, context }));
    const cached = replay(commandId);
    if (cached !== undefined) return cached;

    // Commit statuses are idempotent upserts keyed by (name, sha): a re-fire
    // after an ambiguous timeout converges, so a blind retry is safe.
    await api.commitStatus(repo, sha, state, description, context);
    const id = `status ${repo}@${sha.slice(0, 10)}:${context}`;
    ledger.settle(commandId, id);
    return { providerObjectId: id, structured: { repo, sha, state, context } };
  };

  const issueLabel: OpRunner = async (commandId, inputs) => {
    const op = "vcs.issue.label";
    const repo = required(inputs, "repo", op);
    const number = requiredNumber(inputs, "issue", op);
    const raw = inputs["labels"];
    if (!Array.isArray(raw) || raw.length === 0 || raw.some((l) => typeof l !== "string")) {
      throw new OpError(op, "inputs.labels must be a non-empty string array");
    }
    const labels = raw as string[];
    ledger.insert(commandId, op, payloadSha256({ repo, issue: number, labels }));
    const cached = replay(commandId);
    if (cached !== undefined) return cached;

    // `add_labels` is a set-union upsert on GitLab: a re-fire after an
    // ambiguous timeout converges, so a blind retry is safe.
    await api.addLabels(repo, number, labels);
    const id = `labels ${repo}#${number}:${labels.join(",")}`;
    ledger.settle(commandId, id);
    return { providerObjectId: id, structured: { repo, issue: number, labels } };
  };

  const mrRead: OpRunner = async (commandId, inputs) => {
    const op = "vcs.change_request.read";
    const repo = required(inputs, "repo", op);
    const number = requiredNumber(inputs, "issue", op);
    ledger.insert(commandId, op, payloadSha256({ repo, issue: number }));
    const cached = replay(commandId);
    if (cached !== undefined) return cached;
    const dto = await api.getMergeRequest(repo, number);
    const id = `MR ${repo}!${dto.iid}`;
    ledger.settle(commandId, id);
    return {
      providerObjectId: id,
      structured: {
        repo,
        number: dto.iid,
        state: dto.state,
        title: dto.title,
        merged: dto.merged,
        headSha: dto.sha,
        headRef: dto.source_branch,
      },
    };
  };

  const REVIEW_EVENTS: Record<string, true> = {
    APPROVED: true,
    REQUEST_CHANGES: true,
    COMMENT: true,
  };

  const mrReview: OpRunner = async (commandId, inputs) => {
    const op = "vcs.change_request.review";
    const repo = required(inputs, "repo", op);
    const number = requiredNumber(inputs, "issue", op);
    const event = required(inputs, "event", op);
    if (!(event in REVIEW_EVENTS)) {
      throw new OpError(op, `event must be APPROVED|REQUEST_CHANGES|COMMENT, got '${event}'`);
    }
    const body = typeof inputs["body"] === "string" ? inputs["body"] : "";
    ledger.insert(commandId, op, payloadSha256({ repo, issue: number, event, body }));
    const cached = replay(commandId);
    if (cached !== undefined) return cached;
    const review = await api.reviewMergeRequest(repo, number, event, body);
    const id = `review ${review.id}`;
    ledger.settle(commandId, id);
    return {
      providerObjectId: id,
      structured: { repo, issue: number, event, reviewId: review.id },
    };
  };

  const makeDiscussion =
    (resolve: boolean): OpRunner =>
    async (commandId, inputs) => {
      const op = resolve ? "vcs.discussion.resolve" : "vcs.discussion.reply";
      const repo = required(inputs, "repo", op);
      const number = requiredNumber(inputs, "issue", op);
      const discussionId = required(inputs, "discussionId", op);
      // A resolve carries no text; a reply must.
      const body = resolve
        ? typeof inputs["body"] === "string"
          ? inputs["body"]
          : ""
        : required(inputs, "body", op);
      ledger.insert(commandId, op, payloadSha256({ repo, issue: number, discussionId, body }));
      const cached = replay(commandId);
      if (cached !== undefined) return cached;

      // Resolve is an idempotent upsert (`resolved=true` converges on
      // re-fire); a reply re-fire after an ambiguous timeout may duplicate
      // only when the answer was lost — the ledger row pins the first
      // settled note id, so a settled replay NEVER re-posts.
      const noteId = resolve
        ? (await api.resolveDiscussion(repo, number, discussionId), "resolved")
        : await api.replyDiscussion(repo, number, discussionId, body);
      const id = `discussion ${repo}!${String(number)}:${discussionId}:${noteId}`;
      ledger.settle(commandId, id);
      return {
        providerObjectId: id,
        structured: {
          repo,
          issue: number,
          discussionId,
          ...(resolve ? {} : { commentId: noteId }),
        },
      };
    };

  return {
    "vcs.change_request.create": mrCreate,
    "vcs.issue.read": issueRead,
    "vcs.issue.comment": makeComment(false),
    "vcs.issue.label": issueLabel,
    "vcs.change_request.read": mrRead,
    "vcs.change_request.comment": makeComment(true),
    "vcs.change_request.review": mrReview,
    "vcs.discussion.reply": makeDiscussion(false),
    "vcs.discussion.resolve": makeDiscussion(true),
    "vcs.branch.push": branchPush,
    "vcs.pipeline.trigger": pipelineTrigger,
  };
}
