// Normalize classification law (decision 6, fan-out form): every mapped event
// kind produces the exact schema data as a NormalizedEvent[]; the echo sender
// drops (empty array); unmapped kinds drop; the workTrigger flag follows ONLY
// the label config; the issue_comment PR discriminator routes to the PR
// domain; PR closed maps merged/closed apart; status drops without a resolved
// number; a bot-mentioning comment fans out a companion mention.v1 under
// `<delivery>:mention`; the normalized fields (headRef/labels/commentId) ride
// so the kernel never digs payload.
import { describe, expect, it } from "vitest";
import {
  CI_EVENT,
  ISSUE_EVENT,
  MENTION_EVENT,
  PULL_REQUEST_EVENT,
  normalizeIssueComment,
  normalizeIssues,
  normalizePullRequest,
  normalizeReview,
  normalizeStatus,
  type NormalizeContext,
  type NormalizedEvent,
} from "../src/normalize.js";
import {
  botLabeled,
  commentOnIssue,
  commentOnPr,
  issueLabelUpdated,
  issueLabeled,
  issueLabeledOther,
  issueOpened,
  prClosedMerged,
  prOpened,
  reviewSubmitted,
  statusFailure,
} from "./fixtures/payloads.js";

import type {
  ProviderChangeRequestEventV1Data,
  ProviderIssueEventV1Data,
  ProviderPipelineEventV1Data,
} from "@ofac/protocol";

const ctx: NormalizeContext = {
  providerInstanceId: "local-git",
  tenantOrOrganizationId: "git.localhost",
  workLabels: new Set(["ofac:work"]),
  botUser: "ofac-bot",
};

/** The single event a one-event delivery must produce (fails on 0 or 2+). */
function only(events: NormalizedEvent[]): NormalizedEvent {
  expect(events).toHaveLength(1);
  return events[0] as NormalizedEvent;
}

const issue = (event: NormalizedEvent): ProviderIssueEventV1Data =>
  event.data as ProviderIssueEventV1Data;
const pr = (event: NormalizedEvent): ProviderChangeRequestEventV1Data =>
  event.data as ProviderChangeRequestEventV1Data;
const ci = (event: NormalizedEvent): ProviderPipelineEventV1Data =>
  event.data as ProviderPipelineEventV1Data;

describe("normalizeIssues", () => {
  it("opened passes through with workTrigger false and the label snapshot", () => {
    const event = only(normalizeIssues(issueOpened, "dlv-1", ctx));
    expect(event.type).toBe(ISSUE_EVENT);
    expect(issue(event).action).toBe("opened");
    expect(event.data).toMatchObject({ workTrigger: false, providerDeliveryId: "dlv-1" });
    expect(event.data.reference).toMatchObject({
      provider: "forgejo",
      channelOrResourceId: "demo/widgets",
      rootThreadId: "1",
      conversationKind: "comment",
      authorProviderAccountId: "alice",
    });
    expect(issue(event).labels).toEqual([]);
    expect(event.data.payload).toBe(issueOpened);
  });

  it("labeled with a configured trigger label sets workTrigger and carries labels", () => {
    const event = only(normalizeIssues(issueLabeled, "dlv-2", ctx));
    expect(issue(event).action).toBe("labeled");
    expect(issue(event).workTrigger).toBe(true);
    expect(issue(event).labels).toContain("ofac:work");
  });

  it("labeled with an unconfigured label keeps workTrigger false", () => {
    const event = only(normalizeIssues(issueLabeledOther, "dlv-3", ctx));
    expect(issue(event).workTrigger).toBe(false);
  });

  it("16.x label_updated normalizes onto labeled with labels from issue.labels[]", () => {
    const event = only(normalizeIssues(issueLabelUpdated, "dlv-17", ctx));
    expect(issue(event).action).toBe("labeled");
    expect(issue(event).workTrigger).toBe(true);
    expect(issue(event).labels).toEqual(["ofac:work"]);
  });

  it("drops the bot's own event (echo law, exact-match)", () => {
    expect(normalizeIssues(botLabeled, "dlv-4", ctx)).toEqual([]);
  });

  it("drops an unmapped action", () => {
    expect(normalizeIssues({ ...issueOpened, action: "assigned" }, "dlv-5", ctx)).toEqual([]);
  });
});

describe("normalizeIssueComment", () => {
  it("comment on an ISSUE normalizes to issue.v1 commented", () => {
    const event = only(normalizeIssueComment(commentOnIssue, "dlv-6", ctx));
    expect(event.type).toBe(ISSUE_EVENT);
    expect(event.data).toMatchObject({
      action: "commented",
      workTrigger: false,
      text: "Please also cover the timeout path.",
    });
    expect(event.data.reference.rootThreadId).toBe("1");
    expect(event.data.reference.messageId).toBe("10");
  });

  it("comment on a PR (pull_request discriminator) routes to change_request.v1 with normalized fields", () => {
    const events = normalizeIssueComment(commentOnPr, "dlv-7", ctx);
    expect(events).toHaveLength(1);
    const event = events[0] as NormalizedEvent;
    expect(event.type).toBe(PULL_REQUEST_EVENT);
    expect(pr(event).action).toBe("commented");
    expect(event.data.reference.rootThreadId).toBe("2");
    // The kernel-side dialog reply target rides normalized.
    expect(pr(event).commentId).toBe("11");
    expect(pr(event).headRef).toBe("feature/summarizer");
  });

  it("a bot-mentioning comment fans out the issue event plus a mention companion", () => {
    const events = normalizeIssueComment(
      { ...commentOnIssue, comment: { id: 12, body: "@ofac-bot please retry" } },
      "dlv-18",
      ctx,
    );
    expect(events).toHaveLength(2);
    expect(events[0]?.type).toBe(ISSUE_EVENT);
    expect(events[1]?.type).toBe(MENTION_EVENT);
    expect(events[1]?.data.providerDeliveryId).toBe("dlv-18:mention");
    expect(events[1]?.data.reference.messageId).toBe("12");
  });

  it("drops edited/deleted comment actions and bot echoes", () => {
    expect(normalizeIssueComment({ ...commentOnIssue, action: "edited" }, "dlv-8", ctx)).toEqual(
      [],
    );
    expect(
      normalizeIssueComment({ ...commentOnIssue, sender: { login: "ofac-bot" } }, "dlv-9", ctx),
    ).toEqual([]);
  });
});

describe("normalizePullRequest", () => {
  it("opened maps 1:1 with the head ref normalized", () => {
    const event = only(normalizePullRequest(prOpened, "dlv-10", ctx));
    expect(event.type).toBe(PULL_REQUEST_EVENT);
    expect(pr(event).action).toBe("opened");
    expect(pr(event).text).toBe("Adds the summarizer");
    expect(pr(event).headRef).toBe("feature/summarizer");
  });

  it("closed + merged:true maps to merged", () => {
    const event = only(normalizePullRequest(prClosedMerged, "dlv-11", ctx));
    expect(pr(event).action).toBe("merged");
  });

  it("closed + merged:false maps to closed", () => {
    const event = only(
      normalizePullRequest(
        { ...prClosedMerged, pull_request: { number: 2, merged: false } },
        "dlv-12",
        ctx,
      ),
    );
    expect(pr(event).action).toBe("closed");
  });

  it("drops an unmapped action", () => {
    expect(normalizePullRequest({ ...prOpened, action: "assigned" }, "dlv-13", ctx)).toEqual([]);
  });

  it("a bot-opened PR survives the echo filter (resolver input, not an echo)", () => {
    const event = only(
      normalizePullRequest({ ...prOpened, sender: { login: "ofac-bot" } }, "dlv-15", ctx),
    );
    expect(pr(event).action).toBe("opened");
    expect(event.data.actorProviderAccountId).toBe("ofac-bot");
  });

  it("a bot synchronized event still drops (echo law, no resolver role)", () => {
    expect(
      normalizePullRequest(
        { ...prOpened, action: "synchronized", sender: { login: "ofac-bot" } },
        "dlv-16",
        ctx,
      ),
    ).toEqual([]);
  });
});

describe("normalizeReview", () => {
  it("review submission maps into change_request.v1 action reviewed with commentId", () => {
    const event = only(normalizeReview(reviewSubmitted, "dlv-14", ctx));
    expect(event.type).toBe(PULL_REQUEST_EVENT);
    expect(event.data).toMatchObject({
      action: "reviewed",
      text: "Approve with the retry clause reworded.",
    });
    expect(event.data.reference.rootThreadId).toBe("2");
    expect(pr(event).commentId).toBe("4");
  });
});

describe("normalizeStatus", () => {
  it("maps the commit status state verbatim with the caller-resolved number", () => {
    const event = only(normalizeStatus(statusFailure, "dlv-15", "2", ctx));
    expect(event.type).toBe(CI_EVENT);
    expect(ci(event)).toMatchObject({ action: "failure", description: "test suite red" });
    expect(event.data.reference.rootThreadId).toBe("2");
  });

  it("drops without a resolved number (unmappable to a conversation)", () => {
    expect(normalizeStatus(statusFailure, "dlv-16", undefined, ctx)).toEqual([]);
  });

  it("drops an unknown state", () => {
    expect(normalizeStatus({ ...statusFailure, status: "warning" }, "dlv-17", "2", ctx)).toEqual(
      [],
    );
  });
});
