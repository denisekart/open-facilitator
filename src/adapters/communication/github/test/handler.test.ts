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
import { createGitHubHandler, githubAdapterManifest, CMD_REPLY } from "../src/handler.js";
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
function fakeApi(posted: string[] = []): GitHubApi {
  return {
    postComment: async (repo: string, number: number, body: string) => {
      posted.push(`${repo}#${String(number)}:${body}`);
      return { id: nextId++, html_url: "https://github.com/x" };
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
  it("advertises only the reply command", () => {
    const manifest = githubAdapterManifest("team-alpha");
    expect(manifest.ownerTeam).toBe("team-alpha");
    expect(manifest.capabilities.map((c) => c.id)).toEqual([CMD_REPLY]);
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
