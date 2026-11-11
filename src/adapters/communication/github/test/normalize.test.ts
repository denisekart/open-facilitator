// Classification law over the docs-shaped fixtures: the action mapping
// (incl. synchronize→synchronized, closed+merged→merged), the echo law with
// the bot-opened-PR exemption, the mention companion, the label→workTrigger
// policy, the normalized fields (headRef/labels/commentId), and the drop
// arms (unmapped actions, unresolved pipeline numbers).
import { describe, expect, it } from "vitest";
import type {
  ProviderChangeRequestEventV1Data,
  ProviderIssueEventV1Data,
  ProviderPipelineEventV1Data,
  ProviderReviewEventV1Data,
} from "@ofac/protocol";
import { CHANGE_REQUEST, ISSUE, MENTION, REVIEW, ctx, eventsOfType } from "./helpers.js";
import {
  normalizeCheckSuite,
  normalizeIssueComment,
  normalizeIssues,
  normalizePullRequest,
  normalizeReview,
  normalizeReviewComment,
  normalizeStatus,
} from "../src/normalize.js";
import {
  checkSuiteCompleted,
  issueCommentCreated,
  issuesLabeled,
  issuesOpened,
  pullRequestOpened,
  pullRequestReviewSubmitted,
  pullRequestSynchronize,
  reviewCommentCreated,
  statusSuccess,
} from "./fixtures/payloads.js";

describe("normalizeIssues", () => {
  it("opened maps with workTrigger false and empty labels", () => {
    const events = normalizeIssues(issuesOpened as never, "d1", ctx);
    expect(events).toHaveLength(1);
    const data = events[0]?.data as ProviderIssueEventV1Data;
    expect(events[0]?.type).toBe(ISSUE);
    expect(data.action).toBe("opened");
    expect(data.workTrigger).toBe(false);
    expect(data.labels).toEqual([]);
    expect(data.reference.channelOrResourceId).toBe("octocat/Hello-World");
    expect(data.reference.rootThreadId).toBe("7");
  });

  it("labeled maps workTrigger from the label policy and normalizes labels[]", () => {
    const data = normalizeIssues(issuesLabeled as never, "d2", ctx)[0]
      ?.data as ProviderIssueEventV1Data;
    expect(data.action).toBe("labeled");
    expect(data.workTrigger).toBe(true);
    expect(data.labels).toContain("ofac:work");
  });

  it("a labeled issue WITHOUT a work trigger answers workTrigger false", () => {
    const payload = structuredClone(issuesLabeled) as Record<string, unknown>;
    (payload["label"] as { name: string }).name = "nice-issue";
    (payload["issue"] as { labels: unknown[] }).labels = [{ id: 1, name: "nice-issue" }];
    const data = normalizeIssues(payload as never, "d3", ctx)[0]?.data as ProviderIssueEventV1Data;
    expect(data.workTrigger).toBe(false);
    expect(data.labels).toEqual(["nice-issue"]);
  });

  it("unmapped actions and unlabeled drop", () => {
    expect(normalizeIssues({ ...issuesOpened, action: "deleted" } as never, "d", ctx)).toEqual([]);
    expect(normalizeIssues({ ...issuesLabeled, action: "unlabeled" } as never, "d", ctx)).toEqual(
      [],
    );
  });

  it("echo law: the bot's own issue activity drops", () => {
    const payload = structuredClone(issuesLabeled) as Record<string, unknown>;
    (payload["sender"] as { login: string }).login = "ofac-bot";
    expect(normalizeIssues(payload as never, "d", ctx)).toEqual([]);
  });
});

describe("normalizeIssueComment", () => {
  it("comment on an ISSUE maps to issue.v1 commented, no trigger", () => {
    const events = normalizeIssueComment(issueCommentCreated as never, "d4", ctx);
    expect(events).toHaveLength(1);
    const data = events[0]?.data as ProviderIssueEventV1Data;
    expect(events[0]?.type).toBe(ISSUE);
    expect(data.action).toBe("commented");
    expect(data.workTrigger).toBe(false);
    expect(data.text).toBe("The table should sort by risk.");
    expect(data.reference.messageId).toBe("5");
  });

  it("comment on a PR (issue.pull_request link) maps to change_request commented + commentId", () => {
    const payload = structuredClone(issueCommentCreated) as Record<string, unknown>;
    (payload["issue"] as Record<string, unknown>)["pull_request"] = {
      url: "https://api.github.com/repos/octocat/Hello-World/pulls/8",
    };
    const data = normalizeIssueComment(payload as never, "d5", ctx)[0]
      ?.data as ProviderChangeRequestEventV1Data;
    expect(
      eventsOfType(normalizeIssueComment(payload as never, "d5", ctx), CHANGE_REQUEST),
    ).toHaveLength(1);
    expect(data.action).toBe("commented");
    expect(data.commentId).toBe("5");
  });

  it("a bot-mentioning comment body rides a companion mention.v1 with a derived delivery id", () => {
    const payload = structuredClone(issueCommentCreated) as Record<string, unknown>;
    (payload["comment"] as { body: string }).body = "ping @ofac-bot please";
    const events = normalizeIssueComment(payload as never, "d6", ctx);
    expect(events).toHaveLength(2);
    const mention = events.find((e) => e.type === MENTION)?.data as
      { providerDeliveryId: string; text: string } | undefined;
    expect(mention?.providerDeliveryId).toBe("d6:mention");
    expect(mention?.text).toContain("@ofac-bot");
  });

  it("only `created` maps; the bot's own comment drops (echo law)", () => {
    expect(
      normalizeIssueComment({ ...issueCommentCreated, action: "deleted" } as never, "d", ctx),
    ).toEqual([]);
    const payload = structuredClone(issueCommentCreated) as Record<string, unknown>;
    (payload["sender"] as { login: string }).login = "ofac-bot";
    expect(normalizeIssueComment(payload as never, "d", ctx)).toEqual([]);
  });
});

describe("normalizePullRequest", () => {
  it("opened maps with headRef", () => {
    const events = normalizePullRequest(pullRequestOpened as never, "d7", ctx);
    expect(events).toHaveLength(1);
    const data = events[0]?.data as ProviderChangeRequestEventV1Data;
    expect(events[0]?.type).toBe(CHANGE_REQUEST);
    expect(data.action).toBe("opened");
    expect(data.headRef).toBe("ofac/feature-branch");
    expect(data.reference.rootThreadId).toBe("8");
  });

  it("synchronize maps to the wire action synchronized", () => {
    const data = normalizePullRequest(pullRequestSynchronize as never, "d8", ctx)[0]
      ?.data as ProviderChangeRequestEventV1Data;
    expect(data.action).toBe("synchronized");
  });

  it("closed maps to merged when merged, closed otherwise", () => {
    const merged = structuredClone(pullRequestOpened) as Record<string, unknown>;
    merged["action"] = "closed";
    (merged["pull_request"] as { merged: boolean }).merged = true;
    expect(
      (
        normalizePullRequest(merged as never, "d9", ctx)[0]
          ?.data as ProviderChangeRequestEventV1Data
      ).action,
    ).toBe("merged");
    const closed = structuredClone(pullRequestOpened) as Record<string, unknown>;
    closed["action"] = "closed";
    expect(
      (
        normalizePullRequest(closed as never, "d10", ctx)[0]
          ?.data as ProviderChangeRequestEventV1Data
      ).action,
    ).toBe("closed");
  });

  it("unmapped actions drop", () => {
    expect(
      normalizePullRequest({ ...pullRequestOpened, action: "assigned" } as never, "d", ctx),
    ).toEqual([]);
  });

  it("echo law exempts the bot-opened PR (the marker resolver needs it)", () => {
    const payload = structuredClone(pullRequestOpened) as Record<string, unknown>;
    (payload["sender"] as { login: string }).login = "ofac-bot";
    expect(normalizePullRequest(payload as never, "d11", ctx)).toHaveLength(1);
    const comment = structuredClone(pullRequestSynchronize) as Record<string, unknown>;
    (comment["sender"] as { login: string }).login = "ofac-bot";
    expect(normalizePullRequest(comment as never, "d12", ctx)).toEqual([]);
  });
});

describe("normalizeReview", () => {
  it("a submitted verdict is a review.v1 decision with body, id and head", () => {
    const events = normalizeReview(pullRequestReviewSubmitted as never, "d13", ctx);
    const review = events.find((e) => e.type === REVIEW);
    expect(review).toBeDefined();
    const data = review?.data as ProviderReviewEventV1Data;
    expect(data.action).toBe("submitted");
    expect(data.state).toBe("changes-requested");
    expect(data.text).toContain("Please sort the table");
    expect(data.commentId).toBe("3671");
    expect(data.headRef).toBe("ofac/feature-branch");
    // A verdict that is not the CR state-change family anymore: the decision
    // rides review.v1 alone.
    expect(eventsOfType(events, CHANGE_REQUEST)).toHaveLength(0);
  });

  it("a comment-verdict (no formal state) still rides change_request reviewed", () => {
    const events = normalizeReview(
      {
        ...pullRequestReviewSubmitted,
        review: { ...(pullRequestReviewSubmitted as { review: object }).review, state: "comment" },
      } as never,
      "d13b",
      ctx,
    );
    const data = events.find((e) => e.type === CHANGE_REQUEST)
      ?.data as ProviderChangeRequestEventV1Data;
    expect(data?.action).toBe("reviewed");
  });

  it("a review mentioning the bot rides the mention companion", () => {
    expect(
      eventsOfType(normalizeReview(pullRequestReviewSubmitted as never, "d14", ctx), MENTION),
    ).toHaveLength(1);
  });

  it("`edited`/`dismissed` drop", () => {
    expect(
      normalizeReview({ ...pullRequestReviewSubmitted, action: "edited" } as never, "d", ctx),
    ).toEqual([]);
  });
});

describe("normalizeReviewComment", () => {
  it("maps to change_request commented with commentId and mention companion", () => {
    const events = normalizeReviewComment(reviewCommentCreated as never, "d15", ctx);
    expect(events).toHaveLength(2);
    const data = events.find((e) => e.type === CHANGE_REQUEST)
      ?.data as ProviderChangeRequestEventV1Data;
    expect(data.action).toBe("commented");
    expect(data.commentId).toBe("10");
    expect(data.text).toContain("Use a table here");
    expect(events.find((e) => e.type === MENTION)).toBeDefined();
  });

  it("no mention ⇒ single event", () => {
    const payload = structuredClone(reviewCommentCreated) as Record<string, unknown>;
    (payload["comment"] as { body: string }).body = "nit: typo";
    expect(normalizeReviewComment(payload as never, "d16", ctx)).toHaveLength(1);
  });
});

describe("pipeline normalization", () => {
  it("status maps state 1:1 with the caller-resolved number", () => {
    const events = normalizeStatus(statusSuccess as never, "d17", "8", ctx);
    expect(events).toHaveLength(1);
    const data = events[0]?.data as ProviderPipelineEventV1Data;
    expect(data.action).toBe("success");
    expect(data.description).toBe("The build looks good!");
    expect(data.reference.rootThreadId).toBe("8");
  });

  it("unresolved sha→number drops the delivery", () => {
    expect(normalizeStatus(statusSuccess as never, "d18", undefined, ctx)).toEqual([]);
  });

  it("unknown states drop", () => {
    expect(
      normalizeStatus({ ...statusSuccess, state: "error" } as never, "d", "8", ctx),
    ).toHaveLength(1);
    expect(normalizeStatus({ ...statusSuccess, state: "queued" } as never, "d", "8", ctx)).toEqual(
      [],
    );
  });

  it("check_suite completed maps the conclusion; other actions drop", () => {
    const data = normalizeCheckSuite(checkSuiteCompleted as never, "d19", "8", ctx)[0]
      ?.data as ProviderPipelineEventV1Data;
    expect(data.action).toBe("success");
    expect(
      normalizeCheckSuite({ ...checkSuiteCompleted, action: "requested" } as never, "d", "8", ctx),
    ).toEqual([]);
  });
});
