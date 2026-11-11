// Op execution (decision 5): the assign envelope's `data` carries
// `capabilityId` (the op id) and `inputs` (params). Every op runs the same
// law: canonicalize inputs → ledger intent → replay/reconcile → ONE provider
// mutation → settle. Policy and approval are NOT here — they rode the
// Gateway's confirm loop before this envelope ever arrived, so an op reaching
// this module is already authorized (privilege ceiling: the GitHub token
// lives only in this process).
//
// Ambiguous-timeout law: when a mutation's HTTP answer is lost
// (timeout/5xx on a write), the run fails with the ledger row left `pending`
// and NO provider object id; a later attempt reconciles by READ before
// touching GitHub again. (Ops here fail fast on transport errors, so the
// pending row is written first and reconciled by the retry.)
import type { GitHubApi } from "@ofac/github-adapter";
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
  api: GitHubApi;
  ledger: ActionLedger;
}

export function createOps(deps: OpDeps): OpTable {
  const { api, ledger } = deps;
  /** Shared replay law (provider-core): an executed row answers with the
   *  recorded id, full stop. */
  const replay = (commandId: string): OpResult | undefined => replayFrom(ledger, commandId);

  const prCreate: OpRunner = async (commandId, inputs) => {
    const op = "vcs.change_request.create";
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
    const op = "vcs.issue.read";
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
      const op = pr ? "vcs.change_request.comment" : "vcs.issue.comment";
      const repo = required(inputs, "repo", op);
      const number = requiredNumber(inputs, "issue", op);
      const body = required(inputs, "body", op);
      ledger.insert(commandId, op, payloadSha256({ repo, issue: number, body }));
      const cached = replay(commandId);
      if (cached !== undefined) return cached;

      // Ambiguous-timeout reconcile: GitHub answers whether the stamped
      // comment already exists; a lost response NEVER double-posts.
      const stamp = stampedBody(body, commandId);
      const found = await api.findCommentByMarker(repo, number, CMD_MARKER(commandId));
      const comment = found ?? (await api.postComment(repo, number, stamp));
      const id = `comment ${comment.id}`;
      ledger.settle(commandId, id);
      return { providerObjectId: id, structured: { repo, issue: number, commentId: comment.id } };
    };

  // C9: an automation-opened issue carries a HUMAN owner-account. The
  // creator identity is the bot (its credential signs the call) and cannot
  // be accountable; the input names the human, and a missing one is a
  // routing bug, refused loudly.
  const issueCreate: OpRunner = async (commandId, inputs) => {
    const op = "vcs.issue.create";
    const repo = required(inputs, "repo", op);
    const title = required(inputs, "title", op);
    const owner = required(inputs, "ownerProviderAccountId", op);
    const body = typeof inputs["body"] === "string" ? inputs["body"] : "";
    ledger.insert(commandId, op, payloadSha256({ repo, title, body, owner }));
    const cached = replay(commandId);
    if (cached !== undefined) return cached;

    // Read-before-fire: the stamped body is the marker (a lost response
    // reconciles by read, never a second ticket).
    const marker = CMD_MARKER(commandId);
    const number =
      (await api.findIssueByMarker(repo, marker)) ??
      (await api.createIssue(repo, title, stampedBody(body, commandId), owner));
    const id = `issue ${repo}#${number}`;
    ledger.settle(commandId, id);
    return { providerObjectId: id, structured: { repo, number, owner } };
  };

  const issueClose: OpRunner = async (commandId, inputs) => {
    const op = "vcs.issue.close";
    const repo = required(inputs, "repo", op);
    const issue = requiredNumber(inputs, "issue", op);
    ledger.insert(commandId, op, payloadSha256({ repo, issue }));
    const cached = replay(commandId);
    if (cached !== undefined) return cached;

    // Read-before-fire: an already-closed issue satisfies the intent (the
    // mutation is idempotent at the provider, but the READ keeps a replay
    // free of writes).
    const current = await api.getIssue(repo, issue);
    if (current.state !== "closed") {
      await api.closeIssue(repo, issue);
    }

    const id = `issue ${repo}#${issue}`;
    ledger.settle(commandId, id);
    return { providerObjectId: id, structured: { repo, issue, closed: true } };
  };

  // The merge evidence path (step 5): the kernel records the merge from the
  // provider's own answer, never an adapter's say-so.
  const prMerge: OpRunner = async (commandId, inputs) => {
    const op = "vcs.change_request.merge";
    const repo = required(inputs, "repo", op);
    const number = requiredNumber(inputs, "changeRequest", op);
    const method = typeof inputs["method"] === "string" ? inputs["method"] : "merge";
    ledger.insert(commandId, op, payloadSha256({ repo, changeRequest: number, method }));
    const cached = replay(commandId);
    if (cached !== undefined) return cached;

    // Read-before-fire: an already-merged PR is the same truth.
    const current = await api.getPullRequest(repo, number);
    const merged = current.merged === true || (await api.mergePr(repo, number, method));
    if (!merged) {
      throw new OpError(op, `PR ${repo}#${number} is not mergeable`);
    }

    const id = `PR ${repo}#${number}`;
    ledger.settle(commandId, id);
    return { providerObjectId: id, structured: { repo, number, merged: true, method } };
  };

  const branchPush: OpRunner = async (commandId, inputs) => {
    const op = "vcs.branch.push";
    const repo = required(inputs, "repo", op);
    const branch = required(inputs, "branch", op);
    const fromRef = required(inputs, "from", op);
    const payload = payloadSha256({ repo, branch, from: fromRef });
    // Probe BEFORE the insert: an ABSENT row is a fresh attempt (a 422 from
    // GitHub is a real conflict); a pre-existing PENDING row means a
    // previous attempt lost its answer, and 422 IS the reconciled truth
    // (GitHub answers "Reference already exists" as 422 Unprocessable).
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
        (error as { status: unknown }).status === 422;
      if (prior?.state !== "pending" || !conflict) throw error;
    }

    const id = `branch ${repo}/${branch}`;
    ledger.settle(commandId, id);
    return { providerObjectId: id, structured: { repo, branch, from: fromRef } };
  };

  const ciTrigger: OpRunner = async (commandId, inputs) => {
    const op = "vcs.pipeline.trigger";
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

    // Label-add is a set-union upsert on GitHub: a re-fire after an
    // ambiguous timeout converges, so a blind retry is safe.
    await api.addLabels(repo, number, labels);
    const id = `labels ${repo}#${number}:${labels.join(",")}`;
    ledger.settle(commandId, id);
    return { providerObjectId: id, structured: { repo, issue: number, labels } };
  };

  const prRead: OpRunner = async (commandId, inputs) => {
    const op = "vcs.change_request.read";
    const repo = required(inputs, "repo", op);
    const number = requiredNumber(inputs, "issue", op);
    ledger.insert(commandId, op, payloadSha256({ repo, issue: number }));
    const cached = replay(commandId);
    if (cached !== undefined) return cached;
    const dto = await api.getPullRequest(repo, number);
    const id = `PR ${repo}#${dto.number}`;
    ledger.settle(commandId, id);
    return {
      providerObjectId: id,
      structured: {
        repo,
        number: dto.number,
        state: dto.state,
        title: dto.title,
        merged: dto.merged,
        headSha: dto.head.sha,
        headRef: dto.head.ref,
      },
    };
  };

  const REVIEW_EVENTS: Record<string, true> = {
    APPROVED: true,
    REQUEST_CHANGES: true,
    COMMENT: true,
  };

  const prReview: OpRunner = async (commandId, inputs) => {
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
    const review = await api.reviewPullRequest(repo, number, event, body);
    const id = `review ${review.id}`;
    ledger.settle(commandId, id);
    return {
      providerObjectId: id,
      structured: { repo, issue: number, event, reviewId: review.id },
    };
  };

  const discussionReply: OpRunner = async (commandId, inputs) => {
    const op = "vcs.discussion.reply";
    const repo = required(inputs, "repo", op);
    // GitHub's review-thread dialogue keys on the PR number + the top-level
    // comment id (`in_reply_to`); the plan's generic `commentId` input.
    const number = requiredNumber(inputs, "issue", op);
    const commentIdRaw = required(inputs, "commentId", op);
    const commentId = Number(commentIdRaw);
    if (!Number.isInteger(commentId) || commentId <= 0) {
      throw new OpError(op, `commentId must be a positive integer, got '${commentIdRaw}'`);
    }
    const body = required(inputs, "body", op);
    ledger.insert(commandId, op, payloadSha256({ repo, issue: number, commentId, body }));
    const cached = replay(commandId);
    if (cached !== undefined) return cached;

    // Ambiguous-timeout reconcile: the review-thread read answers whether the
    // stamped reply already exists; a lost response NEVER double-posts.
    const stamp = stampedBody(body, commandId);
    const existing = await api.listReviewComments(repo, number);
    const found = existing.find((c) => c.body.includes(CMD_MARKER(commandId)));
    const reply =
      found === undefined
        ? await api.replyToReviewComment(repo, number, commentId, stamp)
        : { id: found.id, html_url: "" };
    const id = `reply ${reply.id}`;
    ledger.settle(commandId, id);
    return {
      providerObjectId: id,
      structured: { repo, issue: number, commentId, replyId: reply.id },
    };
  };

  const discussionResolve: OpRunner = async (commandId, inputs) => {
    const op = "vcs.discussion.resolve";
    const repo = required(inputs, "repo", op);
    const number = requiredNumber(inputs, "issue", op);
    // GitHub resolves a review thread by its GraphQL NODE id — the plan's
    // generic `commentId` input carries the thread id (provider-normalized
    // `PRRT_…` string) for the dialogue leg that minted it.
    const threadId = required(inputs, "commentId", op);
    ledger.insert(commandId, op, payloadSha256({ repo, issue: number, threadId }));
    const cached = replay(commandId);
    if (cached !== undefined) return cached;

    // Resolve is an idempotent state-set: a re-fire after an ambiguous
    // timeout converges (resolving an already-resolved thread is a no-op),
    // so a blind retry is safe.
    await api.resolveReviewThread(threadId);
    const id = `resolved ${threadId}`;
    ledger.settle(commandId, id);
    return { providerObjectId: id, structured: { repo, issue: number, threadId } };
  };

  return {
    "vcs.change_request.create": prCreate,
    "vcs.issue.read": issueRead,
    "vcs.issue.comment": makeComment(false),
    "vcs.issue.label": issueLabel,
    "vcs.issue.create": issueCreate,
    "vcs.issue.close": issueClose,
    "vcs.change_request.merge": prMerge,
    "vcs.change_request.read": prRead,
    "vcs.change_request.comment": makeComment(true),
    "vcs.change_request.review": prReview,
    "vcs.discussion.reply": discussionReply,
    "vcs.discussion.resolve": discussionResolve,
    "vcs.branch.push": branchPush,
    "vcs.pipeline.trigger": ciTrigger,
  };
}
