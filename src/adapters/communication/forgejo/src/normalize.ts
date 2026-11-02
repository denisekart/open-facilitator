// Pure inbound normalization (no I/O, decision 6): a Forgejo webhook
// payload in, one normalized provider event out — or null when the delivery
// must be DROPPED (audit line only, 200 back):
//
//   sender.login === botUser        → dropped (echo law, exact-match), EXCEPT
//                                     a bot-opened/reopened PR: a state change
//                                     the resolver must see, no loop possible
//   issues opened|labeled|closed    → issue.v1 (workTrigger = labels ∩ config)
//   issue_comment on an issue       → issue.v1 action "commented"
//   issue_comment on a PR           → change_request.v1 action "commented"
//   pull_request (mapped actions)   → change_request.v1 (closed+merged ⇒ merged)
//   review / pull_request_review    → change_request.v1 action "reviewed"
//   status                          → pipeline.v1 (number resolved by the
//                                     caller: the sha→PR lookup is I/O, so it
//                                     rides in as a parameter; undefined ⇒ dropped)
//   anything else                   → dropped
//
// The label policy lives HERE (decision 3): the kernel only reads workTrigger.
import type {
  ConversationReference,
  ProviderPipelineEventV1Data,
  ProviderIssueEventV1Data,
  ProviderChangeRequestEventV1Data,
} from "@ofac/protocol";

// Forge-domain vocabulary: the type names the PURPOSE; this adapter IS the
// Forgejo→purpose mapping layer, so the provider token is in-package data.
export const ISSUE_EVENT = "org.facilitator.event.provider.issue.v1";
export const PULL_REQUEST_EVENT = "org.facilitator.event.provider.change_request.v1";
export const CI_EVENT = "org.facilitator.event.provider.pipeline.v1";

export type Payload = Record<string, unknown>;

export interface NormalizeContext {
  providerInstanceId: string;
  tenantOrOrganizationId: string;
  /** Lowercased label set that triggers a Work on `labeled`. */
  workLabels: ReadonlySet<string>;
  /** Exact-match echo identity (FORGEJO_BOT_USER). */
  botUser: string;
}

export interface NormalizedEvent {
  type: string;
  data: ProviderIssueEventV1Data | ProviderChangeRequestEventV1Data | ProviderPipelineEventV1Data;
}

function str(value: unknown): string | undefined {
  return typeof value === "string" && value !== "" ? value : undefined;
}

/** Wire ids arrive as strings or JSON numbers depending on the event kind. */
function id(value: unknown): string | undefined {
  if (typeof value === "string" && value !== "") return value;
  return typeof value === "number" && Number.isFinite(value) ? String(value) : undefined;
}

/** Wire numbers arrive as JSON numbers. */
function num(value: unknown): string | undefined {
  return typeof value === "number" && Number.isFinite(value) ? String(value) : undefined;
}

function obj(value: unknown): Payload | undefined {
  return typeof value === "object" && value !== null && !Array.isArray(value)
    ? (value as Payload)
    : undefined;
}

function repoOf(payload: Payload): string | undefined {
  return str(obj(payload["repository"])?.["full_name"]);
}

function senderDropped(payload: Payload, ctx: NormalizeContext): boolean {
  return str(obj(payload["sender"])?.["login"]) === ctx.botUser;
}

function labelsOf(container: Payload | undefined): string[] {
  const labels = container?.["labels"];
  if (!Array.isArray(labels)) return [];
  return labels
    .map((label) => str(obj(label)?.["name"]))
    .filter((name): name is string => name !== undefined);
}

function triggers(labels: string[], ctx: NormalizeContext): boolean {
  return labels.some((label) => ctx.workLabels.has(label.toLowerCase()));
}

function reference(
  ctx: NormalizeContext,
  repo: string,
  number: string,
  author: string,
  messageId?: string,
): ConversationReference {
  return {
    provider: "forgejo",
    providerInstanceId: ctx.providerInstanceId,
    tenantOrOrganizationId: ctx.tenantOrOrganizationId,
    channelOrResourceId: repo,
    rootThreadId: number,
    subjectRef: number,
    conversationKind: "comment",
    authorProviderAccountId: author,
    ...(messageId === undefined ? {} : { messageId }),
  };
}

/** `issues` event: opened|labeled|closed map 1:1; other actions drop.
 *  Forgejo 16 renamed the labeling action to `label_updated` (verified live:
 *  top-level `label` is null and the new label rides `issue.labels[]`) —
 *  normalized onto the wire's `labeled`; `label_deleted` (removal) drops. */
export function normalizeIssues(
  payload: Payload,
  deliveryId: string,
  ctx: NormalizeContext,
): NormalizedEvent | null {
  if (senderDropped(payload, ctx)) return null;
  const rawAction = str(payload["action"]);
  const action = rawAction === "label_updated" ? "labeled" : rawAction;
  if (action !== "opened" && action !== "labeled" && action !== "closed") return null;
  const repo = repoOf(payload);
  const issue = obj(payload["issue"]);
  const number = str(payload["number"]) ?? num(payload["number"]) ?? num(issue?.["number"]) ?? "";
  if (repo === undefined || number === "") return null;
  const author = str(obj(payload["sender"])?.["login"]) ?? "";
  const label = str(obj(payload["label"])?.["name"]);
  const labels = label === undefined ? labelsOf(issue) : [label, ...labelsOf(issue)];
  const body = str(issue?.["body"]);
  const data: ProviderIssueEventV1Data = {
    reference: reference(ctx, repo, number, author),
    provider: "forgejo",
    providerDeliveryId: deliveryId,
    actorProviderAccountId: author,
    action,
    workTrigger: action === "labeled" ? triggers(labels, ctx) : false,
    ...(body === undefined ? {} : { text: body }),
    payload,
  };
  return { type: ISSUE_EVENT, data };
}

/** `issue_comment`: the `issue.pull_request` discriminator routes the comment
 *  to the PR domain (step 0: comment-on-PR vs comment-on-issue). */
export function normalizeIssueComment(
  payload: Payload,
  deliveryId: string,
  ctx: NormalizeContext,
): NormalizedEvent | null {
  if (senderDropped(payload, ctx)) return null;
  if (str(payload["action"]) !== "created") return null;
  const repo = repoOf(payload);
  const issue = obj(payload["issue"]);
  const number = num(issue?.["number"]) ?? str(issue?.["number"]) ?? "";
  const comment = obj(payload["comment"]);
  if (repo === undefined || number === "" || comment === undefined) return null;
  const author = str(obj(payload["sender"])?.["login"]) ?? "";
  const text = str(comment["body"]);
  const messageId = id(comment["id"]);
  const onPr = obj(issue?.["pull_request"]) !== undefined;
  if (onPr) {
    const data: ProviderChangeRequestEventV1Data = {
      reference: reference(ctx, repo, number, author, messageId),
      provider: "forgejo",
      providerDeliveryId: deliveryId,
      actorProviderAccountId: author,
      action: "commented",
      ...(text === undefined ? {} : { text }),
      payload,
    };
    return { type: PULL_REQUEST_EVENT, data };
  }

  const data: ProviderIssueEventV1Data = {
    reference: reference(ctx, repo, number, author, messageId),
    provider: "forgejo",
    providerDeliveryId: deliveryId,
    actorProviderAccountId: author,
    action: "commented",
    workTrigger: false,
    ...(text === undefined ? {} : { text }),
    payload,
  };
  return { type: ISSUE_EVENT, data };
}

/** Review-directive text: 16.x sends `review: {type, content}` (verified live
 *  against 16.0.7); the GitHub-style `body` shape is kept as a fallback. */
function reviewText(payload: Payload): string | undefined {
  const review = obj(payload["review"]);
  return str(review?.["content"]) ?? str(review?.["body"]);
}

const PR_ACTIONS: Record<string, ProviderChangeRequestEventV1Data["action"]> = {
  opened: "opened",
  synchronized: "synchronized",
  reopened: "reopened",
  reviewed: "reviewed",
};

/** `pull_request`: opened/synchronized/reopened/reviewed map; closed maps to
 *  `merged` when the PR merged, else `closed`; the rest drop. */
export function normalizePullRequest(
  payload: Payload,
  deliveryId: string,
  ctx: NormalizeContext,
): NormalizedEvent | null {
  const raw = str(payload["action"]);
  if (raw === undefined) return null;
  const pr = obj(payload["pull_request"]);
  let action = PR_ACTIONS[raw];
  if (action === undefined) {
    if (raw === "closed") action = pr?.["merged"] === true ? "merged" : "closed";
    else return null;
  }

  // Echo law (decision 5) with one exemption: a bot-opened PR is a
  // load-bearing state change (the head-branch resolver binds the pending
  // marker to the minted PR number), not a conversation echo — there is
  // nothing for it to answer, so no loop can form. Every other bot-side
  // PR activity (synchronized from a worker push, a bot comment's
  // side-events) still drops.
  if (senderDropped(payload, ctx) && action !== "opened" && action !== "reopened") return null;

  const repo = repoOf(payload);
  const number = num(payload["number"]) ?? num(pr?.["number"]) ?? "";
  if (repo === undefined || number === "") return null;
  const author = str(obj(payload["sender"])?.["login"]) ?? "";
  // 16.x fires PR-side events with `review: {type, content}` (verified live);
  // the GitHub-style `body` field never appears on this provider.
  const text = reviewText(payload) ?? str(pr?.["body"]);
  const data: ProviderChangeRequestEventV1Data = {
    reference: reference(ctx, repo, number, author),
    provider: "forgejo",
    providerDeliveryId: deliveryId,
    actorProviderAccountId: author,
    action,
    ...(text === undefined ? {} : { text }),
    payload,
  };
  return { type: PULL_REQUEST_EVENT, data };
}

/** `review` / `pull_request_review` (review submitted as its own event kind):
 *  maps into pull_request.v1 action "reviewed" (decision 6). Forgejo 16.x
 *  fires review submissions under per-action kinds (`pull_request_approved`,
 *  `pull_request_review_comment`, `pull_request_rejected`) carrying
 *  `review: {type, content}` — the review directive lives in `content`
 *  (verified live against 16.0.7). */
export function normalizeReview(
  payload: Payload,
  deliveryId: string,
  ctx: NormalizeContext,
): NormalizedEvent | null {
  if (senderDropped(payload, ctx)) return null;
  const repo = repoOf(payload);
  const number = num(obj(payload["pull_request"])?.["number"]) ?? "";
  if (repo === undefined || number === "") return null;
  const author = str(obj(payload["sender"])?.["login"]) ?? "";
  const text = reviewText(payload);
  const data: ProviderChangeRequestEventV1Data = {
    reference: reference(ctx, repo, number, author),
    provider: "forgejo",
    providerDeliveryId: deliveryId,
    actorProviderAccountId: author,
    action: "reviewed",
    ...(text === undefined ? {} : { text }),
    payload,
  };
  return { type: PULL_REQUEST_EVENT, data };
}

const CI_STATES: Record<string, true> = {
  pending: true,
  success: true,
  failure: true,
  error: true,
};

/** `status` (commit status): the sha→issue/PR number resolution is I/O and
 *  belongs to the caller; an unresolved number drops the delivery. */
export function normalizeStatus(
  payload: Payload,
  deliveryId: string,
  number: string | undefined,
  ctx: NormalizeContext,
): NormalizedEvent | null {
  if (senderDropped(payload, ctx)) return null;
  const action = str(payload["status"]);
  const repo = repoOf(payload);
  if (
    action === undefined ||
    !(action in CI_STATES) ||
    repo === undefined ||
    number === undefined
  ) {
    return null;
  }

  const author = str(obj(payload["sender"])?.["login"]) ?? str(payload["commit_branch"]) ?? "ci";
  const description = str(payload["description"]);
  const data: ProviderPipelineEventV1Data = {
    reference: reference(ctx, repo, number, author),
    provider: "forgejo",
    providerDeliveryId: deliveryId,
    actorProviderAccountId: author,
    action: action as ProviderPipelineEventV1Data["action"],
    ...(description === undefined ? {} : { description }),
    payload,
  };
  return { type: CI_EVENT, data };
}
