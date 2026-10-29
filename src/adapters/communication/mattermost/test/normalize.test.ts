// The D1 classification law over the recorded WS fixture shapes:
// mention vs DM-message vs bound-thread message vs ignored public post vs
// bot echo — plus providerDeliveryId/text/emoji extraction and envelope
// validity against the frozen schemas.
import { describe, expect, it } from "vitest";
import { validateEnvelope } from "@ofac/protocol";
import { buildEvent } from "@ofac/workspace-link";
import {
  MESSAGE_EVENT,
  MENTION_EVENT,
  REACTION_EVENT,
  asPost,
  asReaction,
  normalizePost,
  normalizeReaction,
  parseJsonField,
  type NormalizeContext,
} from "../src/normalize.js";
import {
  BOT_ID,
  BOT_USERNAME,
  HUMAN_ID,
  postedBoundThreadReply,
  postedBotEcho,
  postedDmReply,
  postedIgnoredPublic,
  postedMention,
  reactionAdded,
} from "./fixtures/mm-events";

const CHANNELS: Record<string, "O" | "P" | "D"> = {
  chan0000000000000000000001: "O",
  chan0000000000000000000002: "O",
  dmchan00000000000000000000d1: "D",
};

function ctx(bound: Set<string> = new Set()): NormalizeContext {
  return {
    botUserId: BOT_ID,
    botUsername: BOT_USERNAME,
    botMention: "@facilitator",
    providerInstanceId: "local-chat",
    tenantOrOrganizationId: "chat.localhost",
    channelTypeFor: async (channelId) => CHANNELS[channelId],
    boundRootIds: () => bound,
  };
}

function eventOf(fixture: { data?: Record<string, unknown> }) {
  const raw = parseJsonField(fixture.data?.["post"] as string | undefined);
  if (raw === undefined) throw new Error("fixture missing data.post JSON string");
  const post = asPost(raw);
  if (post === undefined) throw new Error("fixture post shape invalid");
  return { raw, post };
}

describe("normalizePost classification", () => {
  it("mention: @facilitator prefix → mention.v1 with full reference", async () => {
    const { raw, post } = eventOf(postedMention);
    const event = await normalizePost(post, raw, ctx());
    expect(event?.type).toBe(MENTION_EVENT);
    const data = event?.type === MENTION_EVENT ? event.data : undefined;
    expect(data?.providerDeliveryId).toBe("post00000000000000000000m1");
    expect(data?.actorProviderAccountId).toBe(HUMAN_ID);
    expect(data?.text).toBe("@facilitator please open a work item for this");
    expect(data?.reference).toEqual({
      provider: "mattermost",
      providerInstanceId: "local-chat",
      tenantOrOrganizationId: "chat.localhost",
      channelOrResourceId: "chan0000000000000000000001",
      conversationKind: "thread",
      authorProviderAccountId: HUMAN_ID,
      messageId: "post00000000000000000000m1",
      rootThreadId: "root00000000000000000000r1",
      parentMessageId: "root00000000000000000000r1",
    });
    expect(data?.payload?.["id"]).toBe("post00000000000000000000m1");
  });

  it("dm reply: DM channel message → message.v1 with kind dm", async () => {
    const { raw, post } = eventOf(postedDmReply);
    const event = await normalizePost(post, raw, ctx());
    expect(event?.type).toBe(MESSAGE_EVENT);
    if (event?.type === MESSAGE_EVENT) {
      expect(event.data.reference.conversationKind).toBe("dm");
      expect(event.data.providerDeliveryId).toBe("post00000000000000000000d1");
    }
  });

  it("bound-thread reply: root in the post ledger → message.v1", async () => {
    const { raw, post } = eventOf(postedBoundThreadReply);
    const event = await normalizePost(post, raw, ctx(new Set(["root00000000000000000000r1"])));
    expect(event?.type).toBe(MESSAGE_EVENT);
    if (event?.type === MESSAGE_EVENT) {
      expect(event.data.reference.conversationKind).toBe("thread");
      expect(event.data.reference.rootThreadId).toBe("root00000000000000000000r1");
    }
  });

  it("bound-thread classification is OFF during startup replay", async () => {
    const { raw, post } = eventOf(postedBoundThreadReply);
    const event = await normalizePost(post, raw, ctx(new Set(["root00000000000000000000r1"])), {
      replay: true,
    });
    expect(event).toBeNull();
  });

  it("ignored: public unbound post normalizes to null", async () => {
    const { raw, post } = eventOf(postedIgnoredPublic);
    expect(await normalizePost(post, raw, ctx(new Set()))).toBeNull();
  });

  it("ignored: system posts and empty messages", async () => {
    const { raw, post } = eventOf(postedIgnoredPublic);
    expect(await normalizePost({ ...post, type: "system_join_channel" }, raw, ctx())).toBeNull();
    expect(await normalizePost({ ...post, message: "" }, raw, ctx())).toBeNull();
  });

  it("echo: bot-authored post → message.v1 (kernel suppresses arm effects)", async () => {
    const { raw, post } = eventOf(postedBotEcho);
    const event = await normalizePost(post, raw, ctx());
    expect(event?.type).toBe(MESSAGE_EVENT);
    if (event?.type === MESSAGE_EVENT) {
      expect(event.data.actorProviderAccountId).toBe(BOT_ID);
      expect(event.data.reference.messageId).toBe("post00000000000000000000b1");
      expect(event.data.reference.parentMessageId).toBe("root00000000000000000000r1");
      expect(event.data.providerDeliveryId).toBe("post00000000000000000000b1");
    }
  });

  it("mention of the bot username also classifies", async () => {
    const { raw, post } = eventOf(postedMention);
    const event = await normalizePost({ ...post, message: "@facilitator run it" }, raw, {
      ...ctx(),
      botMention: "@bot",
    });
    expect(event?.type).toBe(MENTION_EVENT);
  });

  it("mention token must not match a longer handle", async () => {
    const { raw, post } = eventOf(postedMention);
    const event = await normalizePost(
      { ...post, message: "@facilitatorbot hi" },
      raw,
      ctx(new Set()),
    );
    expect(event).toBeNull();
  });
});

describe("normalizeReaction", () => {
  it("reaction_added → reaction.v1 keyed by the (user, post, emoji) composite", async () => {
    const raw = parseJsonField(reactionAdded.data?.["reaction"] as string | undefined);
    if (raw === undefined) throw new Error("fixture missing data.reaction");
    const reaction = asReaction(raw);
    if (reaction === undefined) throw new Error("fixture reaction shape invalid");
    const event = await normalizeReaction(reaction, raw, "chan0000000000000000000001", ctx());
    expect(event?.type).toBe(REACTION_EVENT);
    if (event?.type === REACTION_EVENT) {
      // Live 11.11 Reaction objects carry no id; the ledger identity is the
      // synthesized composite, matching the outbound createReaction echo.
      expect(event.data.providerDeliveryId).toBe(`${HUMAN_ID}:post00000000000000000000b1:thumbsup`);
      expect(event.data.emojiName).toBe("thumbsup");
      expect(event.data.actorProviderAccountId).toBe(HUMAN_ID);
      expect(event.data.reference.messageId).toBe("post00000000000000000000b1");
    }
  });
});

describe("envelope validity", () => {
  it("normalized events build envelopes the frozen validator accepts", async () => {
    const fixtures = [postedMention, postedDmReply, postedBoundThreadReply, postedBotEcho];
    for (const fixture of fixtures) {
      const { raw, post } = eventOf(fixture);
      const event = await normalizePost(post, raw, ctx(new Set(["root00000000000000000000r1"])));
      if (event === null) throw new Error(`${fixture.event} fixture classified as null`);
      const env = buildEvent(event.type, {
        source: "urn:ofac:provider:mattermost:chat.localhost",
        subject: String(event.data.reference.messageId ?? event.data.providerDeliveryId),
        data: event.data as unknown as Record<string, unknown>,
      });
      const verdict = validateEnvelope(env);
      expect(verdict.ok, `${event.type}: ${verdict.ok ? "" : verdict.message}`).toBe(true);
    }
  });
});
