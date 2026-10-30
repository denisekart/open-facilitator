// Normalize classification law (decision 6): every mapped event kind produces
// the exact schema data; the echo sender drops; unmapped kinds drop; the
// workTrigger flag follows ONLY the label config; the issue_comment PR
// discriminator routes to the PR domain; PR closed maps merged/closed apart;
// status drops without a resolved number.
import { describe, expect, it } from "vitest";
import {
  CI_EVENT,
  ISSUE_EVENT,
  PULL_REQUEST_EVENT,
  normalizeIssueComment,
  normalizeIssues,
  normalizePullRequest,
  normalizeReview,
  normalizeStatus,
  type NormalizeContext,
} from "../src/normalize.js";
import {
  botLabeled,
  commentOnIssue,
  commentOnPr,
  issueLabeled,
  issueLabeledOther,
  issueOpened,
  prClosedMerged,
  prOpened,
  reviewSubmitted,
  statusFailure,
} from "./fixtures/payloads.js";

import type {
  ProviderPipelineEventV1Data,
  ProviderIssueEventV1Data,
  ProviderChangeRequestEventV1Data,
} from "@ofac/protocol";

const issue = (data: unknown): ProviderIssueEventV1Data => data as ProviderIssueEventV1Data;
const pr = (data: unknown): ProviderChangeRequestEventV1Data =>
  data as ProviderChangeRequestEventV1Data;
const ci = (data: unknown): ProviderPipelineEventV1Data => data as ProviderPipelineEventV1Data;

const ctx: NormalizeContext = {
  providerInstanceId: "local-git",
  tenantOrOrganizationId: "git.localhost",
  workLabels: new Set(["ofac:work"]),
  botUser: "ofac-bot",
};

describe("normalizeIssues", () => {
  it("opened passes through with workTrigger false", () => {
    const event = normalizeIssues(issueOpened, "dlv-1", ctx);
    expect(event?.type).toBe(ISSUE_EVENT);
    expect(event?.data.action).toBe("opened");
    expect(event?.data).toMatchObject({ workTrigger: false, providerDeliveryId: "dlv-1" });
    expect(event?.data.reference).toMatchObject({
      provider: "forgejo",
      channelOrResourceId: "demo/widgets",
      rootThreadId: "1",
      conversationKind: "comment",
      authorProviderAccountId: "alice",
    });
    expect(event?.data.payload).toBe(issueOpened);
  });

  it("labeled with a configured trigger label sets workTrigger", () => {
    const event = normalizeIssues(issueLabeled, "dlv-2", ctx);
    expect(event?.data.action).toBe("labeled");
    expect(issue(event?.data).workTrigger).toBe(true);
  });

  it("labeled with an unconfigured label keeps workTrigger false", () => {
    const event = normalizeIssues(issueLabeledOther, "dlv-3", ctx);
    expect(issue(event?.data).workTrigger).toBe(false);
  });

  it("drops the bot's own event (echo law, exact-match)", () => {
    expect(normalizeIssues(botLabeled, "dlv-4", ctx)).toBeNull();
  });

  it("drops an unmapped action", () => {
    expect(normalizeIssues({ ...issueOpened, action: "assigned" }, "dlv-5", ctx)).toBeNull();
  });
});

describe("normalizeIssueComment", () => {
  it("comment on an ISSUE normalizes to issue.v1 commented", () => {
    const event = normalizeIssueComment(commentOnIssue, "dlv-6", ctx);
    expect(event?.type).toBe(ISSUE_EVENT);
    expect(event?.data).toMatchObject({
      action: "commented",
      workTrigger: false,
      text: "Please also cover the timeout path.",
    });
    expect(event?.data.reference.rootThreadId).toBe("1");
    expect(event?.data.reference.messageId).toBe("10");
  });

  it("comment on a PR (pull_request discriminator) routes to pull_request.v1", () => {
    const event = normalizeIssueComment(commentOnPr, "dlv-7", ctx);
    expect(event?.type).toBe(PULL_REQUEST_EVENT);
    expect(event?.data.action).toBe("commented");
    expect(event?.data.reference.rootThreadId).toBe("2");
  });

  it("drops edited/deleted comment actions and bot echoes", () => {
    expect(normalizeIssueComment({ ...commentOnIssue, action: "edited" }, "dlv-8", ctx)).toBeNull();
    expect(
      normalizeIssueComment({ ...commentOnIssue, sender: { login: "ofac-bot" } }, "dlv-9", ctx),
    ).toBeNull();
  });
});

describe("normalizePullRequest", () => {
  it("opened maps 1:1", () => {
    const event = normalizePullRequest(prOpened, "dlv-10", ctx);
    expect(event?.type).toBe(PULL_REQUEST_EVENT);
    expect(event?.data.action).toBe("opened");
    expect(pr(event?.data).text).toBe("Adds the summarizer");
  });

  it("closed + merged:true maps to merged", () => {
    const event = normalizePullRequest(prClosedMerged, "dlv-11", ctx);
    expect(event?.data.action).toBe("merged");
  });

  it("closed + merged:false maps to closed", () => {
    const event = normalizePullRequest(
      { ...prClosedMerged, pull_request: { number: 2, merged: false } },
      "dlv-12",
      ctx,
    );
    expect(event?.data.action).toBe("closed");
  });

  it("drops an unmapped action", () => {
    expect(normalizePullRequest({ ...prOpened, action: "assigned" }, "dlv-13", ctx)).toBeNull();
  });
});

describe("normalizeReview", () => {
  it("review submission maps into pull_request.v1 action reviewed", () => {
    const event = normalizeReview(reviewSubmitted, "dlv-14", ctx);
    expect(event?.type).toBe(PULL_REQUEST_EVENT);
    expect(event?.data).toMatchObject({
      action: "reviewed",
      text: "Approve with the retry clause reworded.",
    });
    expect(event?.data.reference.rootThreadId).toBe("2");
  });
});

describe("normalizeStatus", () => {
  it("maps the commit status state verbatim with the caller-resolved number", () => {
    const event = normalizeStatus(statusFailure, "dlv-15", "2", ctx);
    expect(event?.type).toBe(CI_EVENT);
    expect(ci(event?.data)).toMatchObject({ action: "failure", description: "test suite red" });
    expect(event?.data.reference.rootThreadId).toBe("2");
  });

  it("drops without a resolved number (unmappable to a conversation)", () => {
    expect(normalizeStatus(statusFailure, "dlv-16", undefined, ctx)).toBeNull();
  });

  it("drops an unknown state", () => {
    expect(normalizeStatus({ ...statusFailure, status: "warning" }, "dlv-17", "2", ctx)).toBeNull();
  });
});
