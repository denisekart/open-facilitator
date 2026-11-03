// Pure inbound normalization (no I/O): a GitLab system-hook payload in, the
// normalized provider events out — empty when the delivery must be DROPPED
// (audit line only):
//
//   user.username === botUser       → dropped (echo law, exact-match), EXCEPT
//                                     a bot-opened/reopened MR: a state change
//                                     the resolver must see, no loop possible
//   Issue Hook open|update+labels|close → issue.v1 (workTrigger = labels ∩ config)
//   Note Hook on an Issue         → issue.v1 action "commented"
//   Note Hook on a MergeRequest   → change_request.v1 action "commented"
//   Merge Request Hook (mapped actions) → change_request.v1 (merge ⇒ merged)
//   Pipeline/Build Hook           → pipeline.v1 (the sha→MR lookup on a
//                                     merge_request-less payload is I/O, so it
//                                     rides in as a parameter; undefined ⇒ dropped)
//   ANY mapped comment whose body @-mentions the bot → additionally
//     mention.v1 (the @-bot inbound trigger; the kernel's mention arm decides
//     Work-trigger vs dialogue from the binding, the adapter cannot know).
//     Its providerDeliveryId is `<delivery>:mention` — the (provider,
//     delivery-id) dedupe spine is one row per event, so the pair needs
//     distinct ids.
//   anything else                 → dropped
//
// The label policy lives HERE (the kernel only reads workTrigger). The
// normalized fields (headRef/labels/commentId) ride so the kernel NEVER
// digs vendor payload shapes. GitLab labels are `[{title}]` objects or plain
// strings depending on hook family — the mapping to names is THIS module's
// job, before the shared normalizeLabels pass.
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
  isBotAuthored,
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
  CHANGE_REQUEST_EVENT_TYPE as CHANGE_REQUEST_EVENT,
  ISSUE_EVENT_TYPE as ISSUE_EVENT,
  MENTION_EVENT_TYPE as MENTION_EVENT,
  PIPELINE_EVENT_TYPE as PIPELINE_EVENT,
} from "@ofac/provider-core";

export interface NormalizeContext {
  providerInstanceId: string;
  tenantOrOrganizationId: string;
  /** Lowercased label set that triggers a Work on a label change. */
  workLabels: ReadonlySet<string>;
  /** Exact-match echo identity (GITLAB_BOT_USER). */
  botUser: string;
}

/** GitLab hooks scope everything to `project` — `path_with_namespace` is the
 *  repo identity (`group/proj`), the same shape the connector's op inputs
 *  carry. */
function repoOf(payload: Payload): string | undefined {
  return str(obj(payload["project"])?.["path_with_namespace"]);
}

function authorOf(payload: Payload): string {
  return str(obj(payload["user"])?.["username"]) ?? "";
}

function authoredByBot(payload: Payload, ctx: NormalizeContext): boolean {
  return isBotAuthored(authorOf(payload), ctx.botUser);
}

/** GitLab label containers are either `["str"]` or `[{title}]` (the hook
 *  family decides); both flatten through the shared normalizer after the
 *  title-extraction pass. */
function labelsOf(container: Payload | undefined): string[] {
  const raw = container?.["labels"];
  if (!Array.isArray(raw)) return [];
  const names = raw.map((entry) =>
    typeof entry === "string"
      ? entry
      : (str(obj(entry)?.["title"]) ?? str(obj(entry)?.["name"]) ?? ""),
  );
  return normalizeLabels(names.filter((name) => name !== ""));
}

function reference(
  ctx: NormalizeContext,
  repo: string,
  number: string,
  author: string,
  messageId?: string,
): ConversationReference {
  return {
    provider: "gitlab",
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
    provider: "gitlab",
    providerDeliveryId: `${deliveryId}:mention`,
    actorProviderAccountId: author,
    text: (text ?? "").slice(0, 8192),
    payload,
  };
  return [{ type: MENTION_EVENT_TYPE, data }];
}

/** `Issue Hook`: `open`/`reopen` map to `opened`; an `update` whose
 *  `changes.labels` record names a change maps to `labeled` (the label
 *  policy reads the post-change set); `close` maps to `closed`; every other
 *  update drops. */
export function normalizeIssueHook(
  payload: Payload,
  deliveryId: string,
  ctx: NormalizeContext,
): NormalizedEvent[] {
  if (authoredByBot(payload, ctx)) return [];
  const object = obj(payload["object_attributes"]);
  const rawAction = str(object?.["action"]);
  if (rawAction === undefined) return [];
  let action: ProviderIssueEventV1Data["action"] | undefined;
  if (rawAction === "open" || rawAction === "reopen") action = "opened";
  else if (rawAction === "close") action = "closed";
  else if (rawAction === "update" && obj(object?.["changes"])?.["labels"] !== undefined) {
    action = "labeled";
  }
  if (action === undefined) return [];

  const repo = repoOf(payload);
  const number = num(object?.["iid"]) ?? "";
  if (repo === undefined || number === "") return [];
  const author = authorOf(payload);
  const labels = labelsOf(object);
  const body = str(object?.["description"]);
  const data: ProviderIssueEventV1Data = {
    reference: reference(ctx, repo, number, author),
    provider: "gitlab",
    providerDeliveryId: deliveryId,
    actorProviderAccountId: author,
    action,
    workTrigger: action === "labeled" ? workTriggerFor(labels, ctx.workLabels) : false,
    labels,
    ...(body === undefined ? {} : { text: body }),
    payload,
  };
  return [
    { type: ISSUE_EVENT_TYPE, data },
    ...mentionCompanion(payload, deliveryId, ctx, repo, number, author, id(object?.["id"]), body),
  ];
}

/** `Note Hook`: the `object_attributes.noteable_type` discriminator routes
 *  the note to the issue domain or the merge-request domain (GitLab has no
 *  unified comment endpoint); system notes (journal entries) drop; a
 *  bot-mentioning note body rides a companion mention.v1. */
export function normalizeNoteHook(
  payload: Payload,
  deliveryId: string,
  ctx: NormalizeContext,
): NormalizedEvent[] {
  if (authoredByBot(payload, ctx)) return [];
  const object = obj(payload["object_attributes"]);
  if (object === undefined || object["system"] === true) return [];
  const repo = repoOf(payload);
  if (repo === undefined) return [];
  const author = authorOf(payload);
  const text = str(object["note"]);
  const commentId = id(object["id"]);
  const noteable = str(object["noteable_type"]);
  if (noteable === "MergeRequest") {
    const mr = obj(payload["merge_request"]);
    const number = num(mr?.["iid"]) ?? "";
    if (number === "") return [];
    const headRef = str(mr?.["source_branch"]);
    const data: ProviderChangeRequestEventV1Data = {
      reference: reference(ctx, repo, number, author, commentId),
      provider: "gitlab",
      providerDeliveryId: deliveryId,
      actorProviderAccountId: author,
      action: "commented",
      ...(text === undefined ? {} : { text }),
      ...(headRef === undefined ? {} : { headRef }),
      ...(commentId === undefined ? {} : { commentId }),
      payload,
    };
    return [
      { type: CHANGE_REQUEST_EVENT_TYPE, data },
      ...mentionCompanion(payload, deliveryId, ctx, repo, number, author, commentId, text),
    ];
  }

  if (noteable === "Issue") {
    const issue = obj(payload["issue"]);
    const number = num(issue?.["iid"]) ?? "";
    if (number === "") return [];
    const data: ProviderIssueEventV1Data = {
      reference: reference(ctx, repo, number, author, commentId),
      provider: "gitlab",
      providerDeliveryId: deliveryId,
      actorProviderAccountId: author,
      action: "commented",
      workTrigger: false,
      labels: labelsOf(issue),
      ...(text === undefined ? {} : { text }),
      payload,
    };
    return [
      { type: ISSUE_EVENT_TYPE, data },
      ...mentionCompanion(payload, deliveryId, ctx, repo, number, author, commentId, text),
    ];
  }

  return [];
}

const MR_ACTIONS: Record<string, ProviderChangeRequestEventV1Data["action"]> = {
  open: "opened",
  reopen: "opened",
  update: "synchronized",
  approve: "reviewed",
  merge: "merged",
  close: "closed",
};

/** `Merge Request Hook`: the plan's action mapping — open/reopen→opened,
 *  update→synchronized, approve→reviewed, merge→merged, close→closed; the
 *  rest drop. `headRef` rides from `object_attributes.source_branch` so the
 *  kernel's head-branch resolver binds the marker without digging payload. */
export function normalizeMergeRequestHook(
  payload: Payload,
  deliveryId: string,
  ctx: NormalizeContext,
): NormalizedEvent[] {
  const object = obj(payload["object_attributes"]);
  if (object === undefined) return [];
  const rawAction = str(object?.["action"]);
  if (rawAction === undefined) return [];
  const action = MR_ACTIONS[rawAction];
  if (action === undefined) return [];

  // Echo law with one exemption: a bot-opened/reopened MR is a load-bearing
  // state change (the head-branch resolver binds the pending marker to the
  // minted MR number), not a conversation echo — there is nothing for it to
  // answer, so no loop can form. Every other bot-side MR activity drops.
  if (authoredByBot(payload, ctx) && action !== "opened" && action !== "reopened") return [];

  const repo = repoOf(payload);
  const number = num(object?.["iid"]) ?? "";
  if (repo === undefined || number === "") return [];
  const author = authorOf(payload);
  const text = str(object["description"]);
  const headRef = str(object["source_branch"]);
  const labels = labelsOf(object);
  const data: ProviderChangeRequestEventV1Data = {
    reference: reference(ctx, repo, number, author),
    provider: "gitlab",
    providerDeliveryId: deliveryId,
    actorProviderAccountId: author,
    action,
    ...(text === undefined ? {} : { text }),
    ...(headRef === undefined ? {} : { headRef }),
    ...(labels.length === 0 ? {} : { labels }),
    payload,
  };
  return [{ type: CHANGE_REQUEST_EVENT_TYPE, data }];
}

const PIPELINE_STATES: Record<string, ProviderPipelineEventV1Data["action"]> = {
  created: "pending",
  waiting_for_resource: "pending",
  waiting: "pending",
  running: "pending",
  scheduled: "pending",
  manual: "pending",
  success: "success",
  failed: "failure",
  canceled: "error",
  skipped: "pending",
};

/** `Pipeline Hook` / `Job Hook` (build): the status→action map above —
 *  pipelines carry `object_attributes.status`, job hooks carry `build_status`
 *  at top level. The sha→number resolution is I/O and belongs to the caller
 *  (an unresolved number drops the delivery, same law as forgejo's
 *  commit-status arm). */
export function normalizePipeline(
  payload: Payload,
  deliveryId: string,
  number: string | undefined,
  ctx: NormalizeContext,
): NormalizedEvent[] {
  if (authoredByBot(payload, ctx)) return [];
  const object = obj(payload["object_attributes"]);
  const rawStatus = str(object?.["status"]) ?? str(payload["build_status"]);
  const repo = repoOf(payload);
  const action = rawStatus === undefined ? undefined : PIPELINE_STATES[rawStatus];
  if (action === undefined || repo === undefined || number === undefined) return [];
  // Build Hooks carry no `user` object — the CI actor identity falls back.
  const author = authorOf(payload) || "gitlab";
  const description =
    str(object?.["detailed_status"]) ??
    str(object?.["name"]) ??
    str(payload["build_name"]) ??
    rawStatus;
  const data: ProviderPipelineEventV1Data = {
    reference: reference(ctx, repo, number, author),
    provider: "gitlab",
    providerDeliveryId: deliveryId,
    actorProviderAccountId: author,
    action,
    ...(description === undefined ? {} : { description }),
    payload,
  };
  return [{ type: PIPELINE_EVENT_TYPE, data }];
}
