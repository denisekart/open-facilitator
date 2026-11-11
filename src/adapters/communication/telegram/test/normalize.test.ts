// The classification law over the hand-written Update fixtures: mention vs
// DM vs bound-thread vs topic-root vs ignored unconfigured post vs bot echo
// vs media-only vs reaction — the hybrid root law (forum topic beats a
// reply; a reply to a bound bot post binds), providerDeliveryId = update_id,
// the "(attachment)" text fallback, labels omitted when empty — plus
// envelope validity against the frozen schemas (validateEnvelope), the way
// mattermost's normalize tests do.
import { describe, expect, it } from "vitest";
import { validateEnvelope } from "@ofac/protocol";
import { buildEvent } from "@ofac/workspace-link";
import type { ArtifactRef } from "@ofac/protocol";
import {
  MESSAGE_EVENT,
  MENTION_EVENT,
  REACTION_EVENT,
  collectAttachments,
  normalizeUpdate,
  type NormalizeContext,
  type NormalizeResult,
} from "../src/normalize.js";
import {
  BOT_ID,
  BOT_USERNAME,
  DM_ID,
  GROUP_ID,
  HUMAN_ID,
  TOPIC_ID,
  customEmojiReactionUpdate,
  dmUpdate,
  echoUpdate,
  callbackUpdate,
  emptyReactionUpdate,
  forumTopicUpdate,
  mediaOnlyUpdate,
  mentionUpdate,
  mentionWithDocumentUpdate,
  reactionUpdate,
  replyToBotPostUpdate,
  serviceUpdate,
  unconfiguredMentionUpdate,
  unconfiguredSilentUpdate,
} from "./fixtures/updates";

const REFS: Record<string, ArtifactRef[]> = {
  "4247": [
    {
      artifactId: "art1",
      mediaType: "image/jpeg",
      size: 90,
      digest: `sha256:${"0".repeat(64)}`,
      storageUrl: "artifact://provider/uniqPNG/art1",
      createdBy: "link:tg-adapter",
      createdAt: new Date().toISOString(),
      kind: "chat.attachment",
    },
  ],
};

function ctx(bound: Set<string> = new Set()): NormalizeContext {
  return {
    botUserId: String(BOT_ID),
    botUsername: BOT_USERNAME,
    providerInstanceId: "local-telegram",
    tenantOrOrganizationId: "api.telegram.org",
    chatTypeFor: (chatId) =>
      chatId === String(GROUP_ID) ? "supergroup" : chatId === String(DM_ID) ? "private" : undefined,
    admissionFor: (chatId) =>
      chatId === String(GROUP_ID) || chatId === String(DM_ID)
        ? { admitted: true, labels: ["ofac", "coding"] }
        : null,
    boundRootIds: () => bound,
    mediaArtifactsFor: (messageId) => REFS[messageId] ?? [],
  };
}

function events(result: NormalizeResult): Extract<NormalizeResult, { kind: "events" }>["events"] {
  if (result.kind !== "events") throw new Error(`expected events, got ${result.kind}`);
  return result.events;
}

describe("the C7 callback law", () => {
  it("an inline-button press becomes a message.v1 authored by the presser", () => {
    const [event] = events(normalizeUpdate(callbackUpdate, ctx()));
    expect(event?.type).toBe(MESSAGE_EVENT);
    const data = event?.type === MESSAGE_EVENT ? event.data : undefined;
    expect(data?.providerDeliveryId).toBe("callback:cb-1");
    expect(data?.actorProviderAccountId).toBe(String(HUMAN_ID));
    expect(data?.text).toBe("/approve 6f1a accept");
    expect(data?.reference.messageId).toBe("callback:cb-1");
    expect(data?.reference.authorProviderAccountId).toBe(String(HUMAN_ID));
    // The press rides the ordinary envelope law.
    expect(
      validateEnvelope(
        buildEvent(MESSAGE_EVENT, {
          source: "urn:ofac:provider:telegram:api.telegram.org",
          subject: "callback:cb-1",
          data: data as unknown as Record<string, unknown>,
        }),
      ).ok,
    ).toBe(true);
  });

  it("a callback in an unadmitted chat is ignored", () => {
    const foreign = structuredClone(callbackUpdate) as typeof callbackUpdate;
    (foreign.callback_query as { message: { chat: { id: number } } }).message.chat.id = -9999999999999;
    expect(normalizeUpdate(foreign, ctx()).kind).toBe("ignored");
  });
});

describe("normalizeUpdate classification", () => {
  it("mention: @bot at a word boundary → mention.v1 with full reference", () => {
    const [event] = events(normalizeUpdate(mentionUpdate, ctx()));
    expect(event?.type).toBe(MENTION_EVENT);
    const data = event?.type === MENTION_EVENT ? event.data : undefined;
    expect(data?.provider).toBe("telegram");
    expect(data?.providerDeliveryId).toBe("9001");
    expect(data?.actorProviderAccountId).toBe(String(HUMAN_ID));
    expect(data?.text).toBe("@facilitator look at this");
    expect(data?.labels).toEqual(["ofac", "coding"]);
    expect(data?.reference).toEqual({
      provider: "telegram",
      providerInstanceId: "local-telegram",
      tenantOrOrganizationId: "api.telegram.org",
      channelOrResourceId: String(GROUP_ID),
      conversationKind: "channel",
      authorProviderAccountId: String(HUMAN_ID),
      messageId: "4242",
      // Rule 3: the mention opens its own conversation — root set explicitly.
      rootThreadId: "4242",
      parentMessageId: "4242",
    });
    expect(data?.payload?.["update_id"]).toBe(9001);
  });

  it("mention token must sit at a word boundary", () => {
    const result = normalizeUpdate(
      {
        update_id: 1,
        message: { ...mentionUpdate.message!, text: "a@facilitatorbot hi" },
      },
      ctx(),
    );
    expect(result.kind).toBe("ignored");
  });

  it("forum-topic root beats a reply: root = message_thread_id", () => {
    const [event] = events(normalizeUpdate(forumTopicUpdate, ctx(new Set(["4200"]))));
    expect(event?.type).toBe(MESSAGE_EVENT);
    if (event?.type === MESSAGE_EVENT) {
      // The thread id is a bound root ⇒ bound-thread classification.
      expect(event.data.reference.rootThreadId).toBe(String(TOPIC_ID));
      expect(event.data.reference.parentMessageId).toBe(String(TOPIC_ID));
      expect(event.data.reference.conversationKind).toBe("thread");
      expect(event.data.providerDeliveryId).toBe("9002");
    }
  });

  it("reply to a bot post the adapter created binds to the bot post", () => {
    const [event] = events(normalizeUpdate(replyToBotPostUpdate, ctx(new Set(["5001"]))));
    expect(event?.type).toBe(MESSAGE_EVENT);
    if (event?.type === MESSAGE_EVENT) {
      expect(event.data.reference.rootThreadId).toBe("5001");
      expect(event.data.reference.conversationKind).toBe("thread");
    }
  });

  it("reply to an UNBOUND message in an unbound chat publishes nothing", () => {
    const result = normalizeUpdate(
      {
        update_id: 2,
        message: {
          ...replyToBotPostUpdate.message!,
          from: { id: HUMAN_ID, is_bot: false },
          reply_to_message: { message_id: 4100, chat: replyToBotPostUpdate.message!.chat },
        },
      },
      ctx(new Set(["5001"])),
    );
    expect(result.kind).toBe("ignored");
  });

  it("dm message → message.v1 with conversationKind dm", () => {
    const [event] = events(normalizeUpdate(dmUpdate, ctx()));
    expect(event?.type).toBe(MESSAGE_EVENT);
    if (event?.type === MESSAGE_EVENT) {
      expect(event.data.reference.conversationKind).toBe("dm");
      expect(event.data.providerDeliveryId).toBe("9008");
    }
  });

  it("unconfigured group: a mention is the onboarding signal, nothing publishes", () => {
    const result = normalizeUpdate(unconfiguredMentionUpdate, ctx());
    expect(result).toEqual({
      kind: "unconfigured-mention",
      chatId: "-100999",
      text: "@facilitator hello?",
    });
    expect(normalizeUpdate(unconfiguredSilentUpdate, ctx()).kind).toBe("ignored");
  });

  it("echo: bot-authored post → message.v1 (kernel suppresses arm effects)", () => {
    const [event] = events(normalizeUpdate(echoUpdate, ctx()));
    expect(event?.type).toBe(MESSAGE_EVENT);
    if (event?.type === MESSAGE_EVENT) {
      expect(event.data.actorProviderAccountId).toBe(String(BOT_ID));
      expect(event.data.reference.messageId).toBe("5002");
      expect(event.data.providerDeliveryId).toBe("9006");
    }
  });

  it('media-only message: text falls back to "(attachment)" and refs ride', () => {
    const [event] = events(normalizeUpdate(mediaOnlyUpdate, ctx()));
    expect(event?.type).toBe(MESSAGE_EVENT);
    if (event?.type === MESSAGE_EVENT) {
      expect(event.data.text).toBe("(attachment)");
      expect(event.data.sourceRefs).toEqual(REFS["4247"]);
    }
  });

  it("service messages publish nothing", () => {
    expect(normalizeUpdate(serviceUpdate, ctx()).kind).toBe("ignored");
  });

  it("labels are OMITTED when the chat has none (never [])", () => {
    const noLabels = ctx();
    noLabels.admissionFor = (chatId) =>
      chatId === String(GROUP_ID) || chatId === String(DM_ID)
        ? { admitted: true, labels: [] }
        : null;
    const [event] = events(normalizeUpdate(mentionUpdate, noLabels));
    expect(event?.type === MENTION_EVENT && "labels" in event.data).toBe(false);
    expect(event?.type === MENTION_EVENT && "sourceRefs" in event.data).toBe(false);
  });

  it("text is clamped to 8192", () => {
    const long = {
      update_id: 3,
      message: { ...dmUpdate.message!, text: "x".repeat(9000) },
    };
    const [event] = events(normalizeUpdate(long, ctx()));
    if (event?.type === MESSAGE_EVENT) expect(event.data.text).toHaveLength(8192);
    else throw new Error("expected a message event");
  });
});

describe("reaction law", () => {
  it("message_reaction → reaction.v1 with the first plain emoji", () => {
    const [event] = events(normalizeUpdate(reactionUpdate, ctx(new Set(["4242"]))));
    expect(event?.type).toBe(REACTION_EVENT);
    if (event?.type === REACTION_EVENT) {
      expect(event.data.emojiName).toBe("👍");
      expect(event.data.providerDeliveryId).toBe("9010");
      expect(event.data.reference.messageId).toBe("4242");
      expect(event.data.actorProviderAccountId).toBe(String(HUMAN_ID));
    }
  });

  it("an empty new_reaction set (un-react) is ignored", () => {
    expect(normalizeUpdate(emptyReactionUpdate, ctx()).kind).toBe("ignored");
  });

  it("a custom-emoji-only reaction is ignored", () => {
    expect(normalizeUpdate(customEmojiReactionUpdate, ctx()).kind).toBe("ignored");
  });
});

describe("envelope validity", () => {
  it("normalized events build envelopes the frozen validator accepts", () => {
    for (const fixture of [
      mentionUpdate,
      dmUpdate,
      echoUpdate,
      mediaOnlyUpdate,
      forumTopicUpdate,
      mentionWithDocumentUpdate,
      reactionUpdate,
    ]) {
      const result = normalizeUpdate(fixture, ctx(new Set([String(TOPIC_ID)])));
      if (result.kind !== "events") throw new Error("fixture classified as nothing");
      for (const event of result.events) {
        const env = buildEvent(event.type, {
          source: "urn:ofac:provider:telegram:api.telegram.org",
          subject: `provider/telegram/${event.data.reference.messageId}`,
          data: event.data as unknown as Record<string, unknown>,
        });
        const verdict = validateEnvelope(env);
        expect(verdict.ok, `${event.type}: ${verdict.ok ? "" : verdict.message}`).toBe(true);
      }
    }
  });

  it("the fixture photo pipeline picks the LARGEST variant by file_size", () => {
    const attachments = collectAttachments(mediaOnlyUpdate.message!);
    expect(attachments).toHaveLength(1);
    expect(attachments[0]?.file_id).toBe("mediumB"); // 400 bytes is the largest
    expect(attachments[0]?.mime_type).toBe("image/jpeg");
  });
});
