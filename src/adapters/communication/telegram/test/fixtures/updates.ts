// Hand-written Telegram `Update` JSON subset (the mattermost fixtures
// pattern): the wire shapes are exact for the fields we classify on —
// update_id, message{message_id, chat{id,type}, from{id,username}, text,
// caption, message_thread_id, reply_to_message, photo, document},
// message_reaction{chat, message_id, user, old_reaction, new_reaction}.
import type { TgChat, TgMessage, TgUpdate } from "../../src/normalize.js";

export const BOT_ID = 777001;
export const BOT_USERNAME = "facilitator";
export const HUMAN_ID = 555001;
export const GROUP_ID = -1001234567890;
export const TOPIC_ID = 42;
export const DM_ID = 555002;

const groupChat: TgChat = { id: GROUP_ID, type: "supergroup", title: "OFAC Lab" };
const dmChat: TgChat = { id: DM_ID, type: "private" };
const human = { id: HUMAN_ID, is_bot: false, first_name: "Human" };
const bot = { id: BOT_ID, is_bot: true, username: BOT_USERNAME };

/** A 1×1 PNG's three photo variants (largest last — Telegram's order). */
export const PNG_VARIANTS = [
  { file_id: "smallA", file_unique_id: "uniqPNG", file_size: 100 },
  { file_id: "mediumB", file_unique_id: "uniqPNG", file_size: 400 },
  { file_id: "largeC", file_unique_id: "uniqPNG", file_size: 90 },
];

export const mentionUpdate: TgUpdate = {
  update_id: 9001,
  message: {
    message_id: 4242,
    chat: groupChat,
    from: human,
    date: 1730000000,
    text: "@facilitator look at this",
  } as TgMessage,
};

/** Forum topic: message_thread_id present AND a reply — rule 1 must win. */
export const forumTopicUpdate: TgUpdate = {
  update_id: 9002,
  message: {
    message_id: 4243,
    chat: groupChat,
    from: human,
    date: 1730000001,
    text: "follow-up inside the topic",
    message_thread_id: TOPIC_ID,
    is_topic_message: true,
    reply_to_message: {
      message_id: 4200,
      chat: groupChat,
      from: bot,
      text: "earlier bot post",
    },
  } as TgMessage,
};

/** Reply to a bot post the adapter created (id in boundRootIds): rule 2. */
export const replyToBotPostUpdate: TgUpdate = {
  update_id: 9003,
  message: {
    message_id: 4244,
    chat: groupChat,
    from: human,
    date: 1730000002,
    text: "yes, go ahead",
    reply_to_message: {
      message_id: 5001,
      chat: groupChat,
      from: bot,
      text: "On it.",
    },
  } as TgMessage,
};

/** Unconfigured group: mention text ⇒ the onboarding signal, no event. */
export const unconfiguredMentionUpdate: TgUpdate = {
  update_id: 9004,
  message: {
    message_id: 4245,
    chat: { id: -100999, type: "supergroup" },
    from: human,
    date: 1730000003,
    text: "@facilitator hello?",
  } as TgMessage,
};

/** Unconfigured group, no mention: fully silent. */
export const unconfiguredSilentUpdate: TgUpdate = {
  update_id: 9005,
  message: {
    message_id: 4246,
    chat: { id: -100999, type: "supergroup" },
    from: human,
    date: 1730000004,
    text: "just chatting",
  } as TgMessage,
};

/** The bot's own post (getUpdates redelivers what we sent): echo. */
export const echoUpdate: TgUpdate = {
  update_id: 9006,
  message: {
    message_id: 5002,
    chat: groupChat,
    from: bot,
    date: 1730000005,
    text: "On it.",
    reply_to_message: {
      message_id: 4242,
      chat: groupChat,
      from: human,
      text: "@facilitator look at this",
    },
  } as TgMessage,
};

/** Media-only message (photo, no caption): text = "(attachment)". */
export const mediaOnlyUpdate: TgUpdate = {
  update_id: 9007,
  message: {
    message_id: 4247,
    chat: groupChat,
    from: human,
    date: 1730000006,
    photo: PNG_VARIANTS,
    message_thread_id: TOPIC_ID,
  } as TgMessage,
};

/** DM message: message.v1 with conversationKind dm. */
export const dmUpdate: TgUpdate = {
  update_id: 9008,
  message: {
    message_id: 6001,
    chat: dmChat,
    from: human,
    date: 1730000007,
    text: "my code is ABC123",
  } as TgMessage,
};

/** A mention carrying a document too. */
export const mentionWithDocumentUpdate: TgUpdate = {
  update_id: 9009,
  message: {
    message_id: 4248,
    chat: groupChat,
    from: human,
    date: 1730000008,
    text: "@facilitator look at this",
    document: {
      file_id: "docA",
      file_unique_id: "uniqPDF",
      file_size: 4096,
      mime_type: "application/pdf",
      file_name: "spec.pdf",
    },
  } as TgMessage,
};

export const reactionUpdate: TgUpdate = {
  update_id: 9010,
  message_reaction: {
    chat: groupChat,
    message_id: 4242,
    user: human,
    date: 1730000009,
    old_reaction: [],
    new_reaction: [{ type: "emoji", emoji: "👍" }],
  },
};

/** Un-react: empty new_reaction set ⇒ ignored. */
export const emptyReactionUpdate: TgUpdate = {
  update_id: 9011,
  message_reaction: {
    chat: groupChat,
    message_id: 4242,
    user: human,
    date: 1730000010,
    old_reaction: [{ type: "emoji", emoji: "👍" }],
    new_reaction: [],
  },
};

/** Custom-emoji-only reaction ⇒ ignored (the allowlist law). */
export const customEmojiReactionUpdate: TgUpdate = {
  update_id: 9012,
  message_reaction: {
    chat: groupChat,
    message_id: 4242,
    user: human,
    date: 1730000011,
    old_reaction: [],
    new_reaction: [{ type: "custom_emoji", custom_emoji_id: "abc" }],
  },
};

/** Service message: ignored. */
export const serviceUpdate: TgUpdate = {
  update_id: 9013,
  message: {
    message_id: 4249,
    chat: groupChat,
    from: human,
    date: 1730000012,
    new_chat_members: [bot],
  } as TgMessage,
};
