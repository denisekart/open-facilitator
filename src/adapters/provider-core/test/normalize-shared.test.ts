// The shared normalization laws in isolation: the payload primitives accept
// only their own shape, the echo law is exact-match, the mention trigger is a
// word-boundary @-name, the review verdict map folds every vendor's vocabulary
// into the wire's two states, and labels flatten to lowercase names from
// either vendor shape.
import { describe, expect, it } from "vitest";
import type { ConversationReference } from "@ofac/protocol";
import {
  CHANGE_REQUEST_EVENT_TYPE,
  ISSUE_EVENT_TYPE,
  MENTION_EVENT_TYPE,
  PIPELINE_EVENT_TYPE,
  REVIEW_EVENT_TYPE,
  id,
  isBotAuthored,
  mentionsBot,
  normalizeLabels,
  num,
  obj,
  reviewEvent,
  reviewStateFor,
  str,
  workTriggerFor,
} from "../src/index.js";

const reference: ConversationReference = {
  provider: "forgejo",
  providerInstanceId: "https://forge.example",
  tenantOrOrganizationId: "team-a",
  channelOrResourceId: "repo/1",
  conversationKind: "comment",
  authorProviderAccountId: "alice",
  subjectRef: "forgejo:repo/1",
};

describe("payload primitives", () => {
  it("str: non-empty strings only", () => {
    expect(str("x")).toBe("x");
    for (const bad of ["", 1, null, undefined, {}, [], true]) {
      expect(str(bad)).toBeUndefined();
    }
  });

  it("id: strings and finite numbers, never NaN/Infinity", () => {
    expect(id("abc")).toBe("abc");
    expect(id(17)).toBe("17");
    expect(id(0)).toBe("0");
    for (const bad of ["", null, undefined, {}, [], NaN, Infinity, -Infinity, true]) {
      expect(id(bad)).toBeUndefined();
    }
  });

  it("num: finite numbers only, stringified", () => {
    expect(num(42)).toBe("42");
    expect(num(1.5)).toBe("1.5");
    for (const bad of ["42", "", NaN, Infinity, null, undefined, {}]) {
      expect(num(bad)).toBeUndefined();
    }
  });

  it("obj: non-array objects only", () => {
    expect(obj({ a: 1 })).toEqual({ a: 1 });
    for (const bad of [null, undefined, "x", [], 5]) {
      expect(obj(bad)).toBeUndefined();
    }
  });
});

describe("echo law", () => {
  it("exact-match only; undefined never echoes", () => {
    expect(isBotAuthored("bot", "bot")).toBe(true);
    expect(isBotAuthored("alice", "bot")).toBe(false);
    expect(isBotAuthored(undefined, "bot")).toBe(false);
    expect(isBotAuthored(undefined, "")).toBe(false);
    expect(isBotAuthored("Bot", "bot")).toBe(false);
  });
});

describe("@-mention trigger", () => {
  it("fires at a word boundary, case-insensitive", () => {
    expect(mentionsBot("@bot please help", "bot")).toBe(true);
    expect(mentionsBot("hey @BOT, ping", "bot")).toBe(true);
    expect(mentionsBot("ping me @bot", "bot")).toBe(true);
    expect(mentionsBot("@bot", "bot")).toBe(true);
  });

  it("does not fire inside a word, a path, or a longer handle", () => {
    expect(mentionsBot("email me at bot@example or a@botx", "bot")).toBe(false);
    expect(mentionsBot("see /at/bot/here", "bot")).toBe(false);
    expect(mentionsBot("@bot-user is different", "bot")).toBe(false);
    expect(mentionsBot("robot says hi", "bot")).toBe(false);
  });

  it("empty or absent body never matches; metacharacters in the name are escaped", () => {
    expect(mentionsBot(undefined, "bot")).toBe(false);
    expect(mentionsBot("", "bot")).toBe(false);
    expect(mentionsBot("@bot.name ping", "bot.name")).toBe(true);
    expect(mentionsBot("@botX ping", "bot.name")).toBe(false);
  });
});

describe("review verdict map", () => {
  it("every vendor vocabulary folds into the wire's two states", () => {
    for (const vendor of ["approved", "approve"]) {
      expect(reviewStateFor(vendor)).toBe("approved");
    }
    for (const vendor of ["changes_requested", "changes-requested", "request_changes", "reject"]) {
      expect(reviewStateFor(vendor)).toBe("changes-requested");
    }
  });

  it("a plain comment is not a fake review", () => {
    for (const vendor of [undefined, "", "commented", "APPROVED", "pending"]) {
      expect(reviewStateFor(vendor)).toBeUndefined();
    }
  });
});

describe("reviewEvent", () => {
  it("shapes the identical spine across vendors; optional fields stay absent", () => {
    const event = reviewEvent({
      reference,
      provider: "forgejo",
      providerDeliveryId: "dl-1",
      actorProviderAccountId: "alice",
      action: "submitted",
      state: "approved",
      text: "looks good",
    });
    expect(event.type).toBe(REVIEW_EVENT_TYPE);
    expect(event.data).toMatchObject({
      reference,
      provider: "forgejo",
      providerDeliveryId: "dl-1",
      action: "submitted",
      state: "approved",
      text: "looks good",
    });
    expect(event.data).not.toHaveProperty("commentId");
    expect(event.data).not.toHaveProperty("headRef");
    expect(event.data).not.toHaveProperty("payload");
  });

  it("optional fields ride when supplied", () => {
    const event = reviewEvent({
      reference,
      provider: "forgejo",
      providerDeliveryId: "dl-2",
      actorProviderAccountId: "alice",
      action: "changed",
      state: "changes-requested",
      text: "",
      commentId: "c-9",
      headRef: "refs/heads/main",
      payload: { raw: true },
    });
    expect(event.data).toMatchObject({
      commentId: "c-9",
      headRef: "refs/heads/main",
      payload: { raw: true },
    });
  });
});

describe("label laws", () => {
  it("workTriggerFor: case-insensitive intersection", () => {
    const triggers = new Set(["ready", "help wanted"]);
    expect(workTriggerFor(["READY"], triggers)).toBe(true);
    expect(workTriggerFor(["docs", "Help Wanted"], triggers)).toBe(true);
    expect(workTriggerFor(["docs"], triggers)).toBe(false);
    expect(workTriggerFor([], triggers)).toBe(false);
  });

  it("normalizeLabels flattens either vendor shape to lowercase names", () => {
    expect(normalizeLabels([{ name: "Bug" }, { name: "Docs" }])).toEqual(["bug", "docs"]);
    expect(normalizeLabels(["P1", "Reviewed"])).toEqual(["p1", "reviewed"]);
    expect(
      normalizeLabels(["A", { name: "B" }, 5, null, "", { name: "" }, {}, { name: 7 }]),
    ).toEqual(["a", "b"]);
  });

  it("a non-array (or missing) label set is the empty list", () => {
    for (const bad of [undefined, null, "ready", 7, {}]) {
      expect(normalizeLabels(bad)).toEqual([]);
    }
  });
});

describe("event-type vocabulary", () => {
  it("the frozen forge-domain names", () => {
    expect(ISSUE_EVENT_TYPE).toBe("org.facilitator.event.provider.issue.v1");
    expect(CHANGE_REQUEST_EVENT_TYPE).toBe("org.facilitator.event.provider.change_request.v1");
    expect(PIPELINE_EVENT_TYPE).toBe("org.facilitator.event.provider.pipeline.v1");
    expect(MENTION_EVENT_TYPE).toBe("org.facilitator.event.provider.mention.v1");
    expect(REVIEW_EVENT_TYPE).toBe("org.facilitator.event.provider.review.v1");
  });
});
