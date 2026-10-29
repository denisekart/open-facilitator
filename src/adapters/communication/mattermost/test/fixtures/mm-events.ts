// Recorded-shape Mattermost WebSocket event payloads (hand-written but exact:
// `data.post` / `data.reaction` are JSON STRINGS of the full provider
// objects, per the MM v4 WS protocol).
export const BOT_ID = "botuser000000000000000000aa";
export const BOT_USERNAME = "facilitator";
export const HUMAN_ID = "humanuser000000000000000000bb";

const basePost = {
  create_at: 1730000000000,
  update_at: 0,
  edit_at: 0,
  delete_at: 0,
  is_pinned: false,
  username: "someone",
  props: {},
};

export const postedMention = {
  event: "posted",
  broadcast: { channel_id: "chan0000000000000000000001", team_id: "team1" },
  seq: 12,
  data: {
    channel_type: "O",
    sender_name: "someone",
    post: JSON.stringify({
      ...basePost,
      id: "post00000000000000000000m1",
      channel_id: "chan0000000000000000000001",
      user_id: HUMAN_ID,
      root_id: "root00000000000000000000r1",
      message: "@facilitator please open a work item for this",
      type: "",
    }),
  },
};

export const postedDmReply = {
  event: "posted",
  broadcast: { channel_id: "dmchan00000000000000000000d1" },
  seq: 13,
  data: {
    channel_type: "D",
    post: JSON.stringify({
      ...basePost,
      id: "post00000000000000000000d1",
      channel_id: "dmchan00000000000000000000d1",
      user_id: HUMAN_ID,
      root_id: "",
      message: "my code is ABC123",
      type: "",
    }),
  },
};

export const postedBoundThreadReply = {
  event: "posted",
  broadcast: { channel_id: "chan0000000000000000000001" },
  seq: 14,
  data: {
    channel_type: "O",
    post: JSON.stringify({
      ...basePost,
      id: "post00000000000000000000t1",
      channel_id: "chan0000000000000000000001",
      user_id: HUMAN_ID,
      root_id: "root00000000000000000000r1",
      message: "yes, go ahead with that",
      type: "",
    }),
  },
};

export const postedIgnoredPublic = {
  event: "posted",
  broadcast: { channel_id: "chan0000000000000000000002" },
  seq: 15,
  data: {
    channel_type: "O",
    post: JSON.stringify({
      ...basePost,
      id: "post00000000000000000000p9",
      channel_id: "chan0000000000000000000002",
      user_id: HUMAN_ID,
      root_id: "",
      message: "unrelated chatter in an unbound channel",
      type: "",
    }),
  },
};

export const postedBotEcho = {
  event: "posted",
  broadcast: { channel_id: "chan0000000000000000000001" },
  seq: 16,
  data: {
    channel_type: "O",
    post: JSON.stringify({
      ...basePost,
      id: "post00000000000000000000b1",
      channel_id: "chan0000000000000000000001",
      user_id: BOT_ID,
      username: BOT_USERNAME,
      root_id: "root00000000000000000000r1",
      message: "Work 7f2a accepted.",
      type: "",
    }),
  },
};

export const reactionAdded = {
  event: "reaction_added",
  broadcast: { channel_id: "chan0000000000000000000001" },
  seq: 17,
  data: {
    sender_name: "someone",
    reaction: JSON.stringify({
      user_id: HUMAN_ID,
      post_id: "post00000000000000000000b1",
      emoji_name: "thumbsup",
      create_at: 1730000001000,
      update_at: 1730000001000,
      delete_at: 0,
      remote_id: "",
      channel_id: "chan0000000000000000000001",
    }),
  },
};
