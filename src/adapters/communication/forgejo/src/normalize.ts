// Pure inbound normalization (no I/O): a Forgejo webhook payload in, the
// normalized provider events out — empty when the delivery must be DROPPED
// (audit line only):
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
//   ANY mapped comment whose body @-mentions the bot → additionally
//     mention.v1 (the @-bot inbound trigger; the kernel's mention arm decides
//     Work-trigger vs dialogue from the binding, the adapter cannot know).
//     Its providerDeliveryId is `<delivery>:mention` — the (provider,
//     delivery-id) dedupe spine is one row per event, so the pair needs
//     distinct ids.
//   anything else                   → dropped
//
// The label policy lives HERE (the kernel only reads workTrigger). The
// normalized fields (headRef/labels/commentId) ride so the kernel NEVER
// digs vendor payload shapes.
import type {
  ConversationReference,
  ProviderChangeRequestEventV1Data,
  ProviderIssueEventV1Data,
  ProviderMentionEventV1Data,
  ProviderPipelineEventV1Data,
} from "@ofac/protocol";
import {
  CHANGE_REQUEST_EVENT_TYPE,
  ISSUE_EVENT_TYPE,
  MENTION_EVENT_TYPE,
  PIPELINE_EVENT_TYPE,
  id,
  mentionsBot,
  normalizeLabels,
  num,
  obj,
  str,
  workTriggerFor,
  type NormalizedEvent,
  type Payload,
} from "@ofac/provider-core";

export type { NormalizedEvent, Payload } from "@ofac/provider-core";
export {
  CHANGE_REQUEST_EVENT_TYPE as PULL_REQUEST_EVENT,
  ISSUE_EVENT_TYPE as ISSUE_EVENT,
  MENTION_EVENT_TYPE as MENTION_EVENT,
  PIPELINE_EVENT_TYPE as CI_EVENT,
} from "@ofac/provider-core";

export interface NormalizeContext {
  providerInstanceId: string;
  tenantOrOrganizationId: string;
  /** Lowercased label set that triggers a Work on `labeled`. */
  workLabels: ReadonlySet<string>;
  /** Exact-match echo identity (FORGEJO_BOT_USER). */
  botUser: string;
}

function repoOf(payload: Payload): string | undefined {
  return str(obj(payload["repository"])?.["full_name"]);
}

function senderDropped(payload: Payload, ctx: NormalizeContext): boolean {
  return str(obj(payload["sender"])?.["login"]) === ctx.botUser;
}

function labelsOf(container: Payload | undefined): string[] {
  return normalizeLabels(container?.["labels"]);
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

/** The @-bot mention companion for a mapped comment delivery (empty when the
 *  body does not name the bot or carries no text). */
function mentionCompanion(
  payload: Payload,
  deliveryId: string,
  ctx: NormalizeContext,
  repo: string,
  number: string,
  author: string,
  messageId: string | undefined,
  text: string | undefined,
): NormalizedEvent[] {
  if (!mentionsBot(text, ctx.botUser)) return [];
  const data: ProviderMentionEventV1Data = {
    reference: reference(ctx, repo, number, author, messageId),
    provider: "forgejo",
    providerDeliveryId: `${deliveryId}:mention`,
    actorProviderAccountId: author,
    text: (text ?? "").slice(0, 8192),
    payload,
  };
  return [{ type: MENTION_EVENT_TYPE, data }];
}

/** `issues` event: opened|labeled|closed map 1:1; other actions drop.
 *  Forgejo 16 renamed the labeling action to `label_updated` (verified live:
 *  top-level `label` is null and the new label rides `issue.labels[]`) —
 *  normalized onto the wire's `labeled`; `label_deleted` (removal) drops. */
export function normalizeIssues(
  payload: Payload,
  deliveryId: string,
  ctx: NormalizeContext,
): NormalizedEvent[] {
  if (senderDropped(payload, ctx)) return [];
  const rawAction = str(payload["action"]);
  const action = rawAction === "label_updated" ? "labeled" : rawAction;
  if (action !== "opened" && action !== "labeled" && action !== "closed") return [];
  const repo = repoOf(payload);
  const issue = obj(payload["issue"]);
  const number = str(payload["number"]) ?? num(payload["number"]) ?? num(issue?.["number"]) ?? "";
  if (repo === undefined || number === "") return [];
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
    workTrigger: action === "labeled" ? workTriggerFor(labels, ctx.workLabels) : false,
    labels,
    ...(body === undefined ? {} : { text: body }),
    payload,
  };
  return [{ type: ISSUE_EVENT_TYPE, data }];
}

/** `issue_comment`: the `issue.pull_request` discriminator routes the comment
 *  to the PR domain (comment-on-PR vs comment-on-issue); a bot-mentioning
 *  body rides a companion mention.v1. */
export function normalizeIssueComment(
  payload: Payload,
  deliveryId: string,
  ctx: NormalizeContext,
): NormalizedEvent[] {
  if (senderDropped(payload, ctx)) return [];
  if (str(payload["action"]) !== "created") return [];
  const repo = repoOf(payload);
  const issue = obj(payload["issue"]);
  const number = num(issue?.["number"]) ?? str(issue?.["number"]) ?? "";
  const comment = obj(payload["comment"]);
  if (repo === undefined || number === "" || comment === undefined) return [];
  const author = str(obj(payload["sender"])?.["login"]) ?? "";
  const text = str(comment["body"]);
  const messageId = id(comment["id"]);
  const onPr = obj(issue?.["pull_request"]) !== undefined;
  const headRef = str(obj(obj(issue?.["pull_request"])?.["head"])?.["ref"]);
  const companion = mentionCompanion(
    payload,
    deliveryId,
    ctx,
    repo,
    number,
    author,
    messageId,
    text,
  );
  if (onPr) {
    const data: ProviderChangeRequestEventV1Data = {
      reference: reference(ctx, repo, number, author, messageId),
      provider: "forgejo",
      providerDeliveryId: deliveryId,
      actorProviderAccountId: author,
      action: "commented",
      ...(text === undefined ? {} : { text }),
      ...(headRef === undefined ? {} : { headRef }),
      ...(messageId === undefined ? {} : { commentId: messageId }),
      payload,
    };
    return [{ type: CHANGE_REQUEST_EVENT_TYPE, data }, ...companion];
  }

  const data: ProviderIssueEventV1Data = {
    reference: reference(ctx, repo, number, author, messageId),
    provider: "forgejo",
    providerDeliveryId: deliveryId,
    actorProviderAccountId: author,
    action: "commented",
    workTrigger: false,
    labels: labelsOf(issue),
    ...(text === undefined ? {} : { text }),
    payload,
  };
  return [{ type: ISSUE_EVENT_TYPE, data }, ...companion];
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
): NormalizedEvent[] {
  const raw = str(payload["action"]);
  if (raw === undefined) return [];
  const pr = obj(payload["pull_request"]);
  let action = PR_ACTIONS[raw];
  if (action === undefined) {
    if (raw === "closed") action = pr?.["merged"] === true ? "merged" : "closed";
    else return [];
  }

  // Echo law with one exemption: a bot-opened PR is a load-bearing state
  // change (the head-branch resolver binds the pending marker to the minted
  // PR number), not a conversation echo — there is nothing for it to answer,
  // so no loop can form. Every other bot-side PR activity still drops.
  if (senderDropped(payload, ctx) && action !== "opened" && action !== "reopened") return [];

  const repo = repoOf(payload);
  const number = num(payload["number"]) ?? num(pr?.["number"]) ?? "";
  if (repo === undefined || number === "") return [];
  const author = str(obj(payload["sender"])?.["login"]) ?? "";
  // 16.x fires PR-side events with `review: {type, content}` (verified live);
  // the GitHub-style `body` field never appears on this provider.
  const text = reviewText(payload) ?? str(pr?.["body"]);
  const headRef = str(obj(pr?.["head"])?.["ref"]);
  const data: ProviderChangeRequestEventV1Data = {
    reference: reference(ctx, repo, number, author),
    provider: "forgejo",
    providerDeliveryId: deliveryId,
    actorProviderAccountId: author,
    action,
    ...(text === undefined ? {} : { text }),
    ...(headRef === undefined ? {} : { headRef }),
    payload,
  };
  return [{ type: CHANGE_REQUEST_EVENT_TYPE, data }];
}

/** `review` / `pull_request_review` (review submitted as its own event kind):
 *  maps into change_request.v1 action "reviewed". Forgejo 16.x fires review
 *  submissions under per-action kinds (`pull_request_approved`,
 *  `pull_request_review_comment`, `pull_request_rejected`) carrying
 *  `review: {type, content}` — the review directive lives in `content`
 *  (verified live against 16.0.7). */
export function normalizeReview(
  payload: Payload,
  deliveryId: string,
  ctx: NormalizeContext,
): NormalizedEvent[] {
  if (senderDropped(payload, ctx)) return [];
  const repo = repoOf(payload);
  const pr = obj(payload["pull_request"]);
  const number = num(pr?.["number"]) ?? "";
  if (repo === undefined || number === "") return [];
  const author = str(obj(payload["sender"])?.["login"]) ?? "";
  const text = reviewText(payload);
  const headRef = str(obj(pr?.["head"])?.["ref"]);
  const commentId = id(obj(payload["review"])?.["id"]);
  const data: ProviderChangeRequestEventV1Data = {
    reference: reference(ctx, repo, number, author),
    provider: "forgejo",
    providerDeliveryId: deliveryId,
    actorProviderAccountId: author,
    action: "reviewed",
    ...(text === undefined ? {} : { text }),
    ...(headRef === undefined ? {} : { headRef }),
    ...(commentId === undefined ? {} : { commentId }),
    payload,
  };
  const companion = mentionCompanion(
    payload,
    deliveryId,
    ctx,
    repo,
    number,
    author,
    commentId,
    text,
  );
  return [{ type: CHANGE_REQUEST_EVENT_TYPE, data }, ...companion];
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
): NormalizedEvent[] {
  if (senderDropped(payload, ctx)) return [];
  const action = str(payload["status"]);
  const repo = repoOf(payload);
  if (
    action === undefined ||
    !(action in CI_STATES) ||
    repo === undefined ||
    number === undefined
  ) {
    return [];
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
  return [{ type: PIPELINE_EVENT_TYPE, data }];
}
