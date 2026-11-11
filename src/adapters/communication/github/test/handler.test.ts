// The reply handler law: bot-identity comment on the issue/PR under the
// comment-ledger idempotency rule — a replayed command answers `accepted`
// WITHOUT a second provider mutation; missing pieces answer receipts, never
// throws; no bot token answers `unsupported`.
import { mkdtempSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { describe, expect, it } from "vitest";
import type { EnvelopeCommand } from "@ofac/protocol";
import type { CommandContext, LinkLogger } from "@ofac/workspace-link";
import {
  createGitHubHandler,
  githubAdapterManifest,
  CMD_ACK,
  CMD_REACT,
  CMD_REPLY,
  CMD_UPDATE,
} from "../src/handler.js";
import { AdapterState } from "../src/comment-ledger.js";
import type { GitHubApi } from "../src/github-client.js";

const logger: LinkLogger = {
  debug: () => undefined,
  info: () => undefined,
  warn: () => undefined,
  error: () => undefined,
};

const ctx = { publishEvent: async () => undefined } as unknown as CommandContext;
function freshState(): AdapterState {
  return new AdapterState(join(mkdtempSync(join(tmpdir(), "gh-state-")), "state.sqlite3"));
}

let nextId = 100;
function fakeApi(posted: string[] = [], reactions: string[] = [], edits: string[] = []): GitHubApi {
  return {
    postComment: async (repo: string, number: number, body: string) => {
      posted.push(`${repo}#${String(number)}:${body}`);
      return { id: nextId++, html_url: "https://github.com/x" };
    },
    reactToComment: async (repo: string, commentId: number, content: string) => {
      reactions.push(`${repo}/${String(commentId)}:${content}`);
    },
    updateComment: async (repo: string, commentId: number, body: string) => {
      edits.push(`${repo}/${String(commentId)}:${body}`);
    },
  } as unknown as GitHubApi;
}

function reply(idempotencyKey: string): EnvelopeCommand {
  return {
    id: `cmd-${idempotencyKey}`,
    type: CMD_REPLY,
    data: {
      commandId: `cmd-${idempotencyKey}`,
      idempotencyKey,
      target: {
        provider: "github",
        providerInstanceId: "github-com",
        channelOrResourceId: "octocat/Hello-World",
        rootThreadId: "7",
        conversationKind: "comment",
      },
      text: "On it.",
    },
  } as unknown as EnvelopeCommand;
}

describe("github handler", () => {
  it("advertises the full communication command family", () => {
    const manifest = githubAdapterManifest("team-alpha");
    expect(manifest.ownerTeam).toBe("team-alpha");
    expect(manifest.capabilities.map((c) => c.id)).toEqual([
      CMD_REPLY,
      CMD_REACT,
      CMD_UPDATE,
      CMD_ACK,
    ]);
  });

  it("posts the comment and accepts", async () => {
    const posted: string[] = [];
    const handler = createGitHubHandler({ api: fakeApi(posted), state: freshState(), logger });
    expect(await handler.onCommand(reply("k1"), ctx)).toEqual({ kind: "accepted" });
    expect(posted).toEqual(["octocat/Hello-World#7:On it."]);
  });

  it("ledger law: replayed command accepts WITHOUT a second comment", async () => {
    const posted: string[] = [];
    const state = freshState();
    const handler = createGitHubHandler({ api: fakeApi(posted), state, logger });
    await handler.onCommand(reply("dup"), ctx);
    await handler.onCommand(reply("dup"), ctx);
    expect(posted).toHaveLength(1);
  });

  it("no bot token answers unsupported", async () => {
    const handler = createGitHubHandler({ api: undefined, state: freshState(), logger });
    expect(await handler.onCommand(reply("k2"), ctx)).toEqual({
      kind: "unsupported",
      reason: "no-bot-token",
    });
  });

  it("malformed data and a missing target answer receipts", async () => {
    const handler = createGitHubHandler({ api: fakeApi(), state: freshState(), logger });
    expect(
      await handler.onCommand({ id: "x", type: CMD_REPLY, data: { commandId: "x" } } as never, ctx),
    ).toEqual({ kind: "rejected", reason: "malformed-reply-data" });
    const noRepo = reply("k3");
    delete (noRepo.data as { target: Record<string, unknown> }).target["channelOrResourceId"];
    expect(await handler.onCommand(noRepo, ctx)).toEqual({
      kind: "rejected",
      reason: "target-missing-repo-or-issue",
    });
  });

  it("reacts to a bot comment with the mapped content, replays ledger-free", async () => {
    const reactions: string[] = [];
    const state = freshState();
    const handler = createGitHubHandler({ api: fakeApi([], reactions), state, logger });
    const react = (key: string): EnvelopeCommand =>
      ({
        id: `cmd-${key}`,
        type: CMD_REACT,
        data: {
          commandId: `cmd-${key}`,
          idempotencyKey: key,
          emojiName: "white_check_mark",
          target: {
            provider: "github",
            providerInstanceId: "github-com",
            channelOrResourceId: "octocat/Hello-World",
            rootThreadId: "7",
            parentMessageId: "42",
            conversationKind: "comment",
          },
        },
      }) as unknown as EnvelopeCommand;
    expect(await handler.onCommand(react("r1"), ctx)).toEqual({ kind: "accepted" });
    expect(await handler.onCommand(react("r1"), ctx)).toEqual({ kind: "accepted" });
    expect(reactions).toEqual(["octocat/Hello-World/42:hooray"]);

    // An unmapped emoji is refused honestly.
    const bogus = react("r2");
    (bogus.data as { emojiName: string }).emojiName = "pineapple";
    expect(await handler.onCommand(bogus, ctx)).toEqual({
      kind: "rejected",
      reason: "unsupported-emoji",
    });
  });

  it("updates only its own comments (the update law)", async () => {
    const edits: string[] = [];
    const state = freshState();
    const handler = createGitHubHandler({ api: fakeApi([], [], edits), state, logger });
    const update = (key: string, parentMessageId: string): EnvelopeCommand =>
      ({
        id: `cmd-${key}`,
        type: CMD_UPDATE,
        data: {
          commandId: `cmd-${key}`,
          idempotencyKey: key,
          text: "corrected",
          target: {
            provider: "github",
            providerInstanceId: "github-com",
            channelOrResourceId: "octocat/Hello-World",
            rootThreadId: "7",
            parentMessageId,
            conversationKind: "comment",
          },
        },
      }) as unknown as EnvelopeCommand;
    // Nobody's comment: the first reply records a real object id - edit
    // the recorded one instead.
    await handler.onCommand(reply("u0"), ctx); // records provider object id
    const postedId = String(nextId - 1);
    expect(await handler.onCommand(update("u1", postedId), ctx)).toEqual({ kind: "accepted" });
    expect(edits).toEqual([`octocat/Hello-World/${postedId}:corrected`]);
    expect(await handler.onCommand(update("u2", "999999"), ctx)).toEqual({
      kind: "rejected",
      reason: "unknown-comment",
    });
  });

  it("acknowledges without a provider call, no token needed", async () => {
    const handler = createGitHubHandler({ api: undefined, state: freshState(), logger });
    const ack = {
      id: "cmd-a",
      type: CMD_ACK,
      data: {
        commandId: "cmd-a",
        idempotencyKey: "a1",
        target: {
          provider: "github",
          providerInstanceId: "github-com",
          conversationKind: "comment",
        },
      },
    } as unknown as EnvelopeCommand;
    expect(await handler.onCommand(ack, ctx)).toEqual({ kind: "accepted" });
  });

  it("other commands answer unsupported", async () => {
    const handler = createGitHubHandler({ api: fakeApi(), state: freshState(), logger });
    expect(
      await handler.onCommand(
        { id: "x", type: "org.facilitator.command.other.v1", data: {} } as EnvelopeCommand,
        ctx,
      ),
    ).toEqual({ kind: "unsupported" });
  });
});
