// The command-execution law (decision 4): reply-only. A reply targeting a
// comment/thread posts exactly one bot comment; the comment-ledger check runs
// BEFORE any provider mutation, so a replayed command posts nothing; react/
// update/acknowledge are unsupported receipts, never throws; a non-comment
// conversation kind or a target without repo/number is rejected.
import { mkdtempSync, rmSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { describe, expect, it } from "vitest";
import type { CommandContext, CommandOutcome, LinkLogger } from "@ofac/workspace-link";
import type { EnvelopeCommand } from "@ofac/protocol";
import { AdapterState } from "../src/comment-ledger.js";
import { createForgejoHandler, forgejoAdapterManifest, CMD_REPLY } from "../src/handler.js";
import type { ForgejoApi, ForgejoComment, ForgejoPullRequest } from "../src/forgejo-client.js";

const silent: LinkLogger = {
  debug: () => undefined,
  info: () => undefined,
  warn: () => undefined,
  error: () => undefined,
};

class FakeApi implements ForgejoApi {
  posts: { repo: string; number: number; body: string }[] = [];
  nextId = 100;

  async postComment(repo: string, number: number, body: string): Promise<ForgejoComment> {
    this.posts.push({ repo, number, body });
    return { id: this.nextId++, html_url: `http://git.localhost/repos/${repo}/issues/${number}#c` };
  }

  async openPr(): Promise<ForgejoPullRequest> {
    throw new Error("unused");
  }

  async prsForHeadSha(): Promise<number[]> {
    return [];
  }

  async commitStatus(): Promise<void> {
    /* unused */
  }

  async openPrsForHead(): Promise<number[]> {
    throw new Error("unused");
  }

  async findCommentByMarker(): Promise<ForgejoComment | undefined> {
    throw new Error("unused");
  }

  async getIssue(): Promise<{ number: number; state: string; title: string }> {
    throw new Error("unused");
  }

  async createBranch(): Promise<void> {
    /* unused */
  }

  async addLabels(): Promise<void> {
    throw new Error("unused");
  }

  async getPullRequest(): Promise<never> {
    throw new Error("unused");
  }

  async reviewPullRequest(): Promise<never> {
    throw new Error("unused");
  }
}

function reply(commandId: string, kind = "comment"): EnvelopeCommand {
  return {
    type: CMD_REPLY,
    data: {
      commandId,
      idempotencyKey: commandId,
      text: "Work reached completed.",
      target: {
        provider: "forgejo",
        conversationKind: kind,
        channelOrResourceId: "demo/widgets",
        rootThreadId: "1",
      },
    },
  } as unknown as EnvelopeCommand;
}

const ctx = { publishEvent: async () => undefined } as unknown as CommandContext;

function harness(): {
  handler: ReturnType<typeof createForgejoHandler>;
  api: FakeApi;
  state: AdapterState;
  dir: string;
} {
  const dir = mkdtempSync(join(tmpdir(), "ofac-fj-"));
  const state = new AdapterState(join(dir, "state.sqlite3"));
  const api = new FakeApi();
  return { handler: createForgejoHandler({ api, state, logger: silent }), api, state, dir };
}

describe("forgejo handler", () => {
  it("posts one comment per fresh reply command", async () => {
    const { handler, api, state, dir } = harness();
    try {
      const outcome: CommandOutcome = await handler.onCommand(reply("cmd-1"), ctx);
      expect(outcome).toEqual({ kind: "accepted" });
      expect(api.posts).toHaveLength(1);
      expect(state.findComment("cmd-1")?.providerObjectId).toBe("100");
    } finally {
      state.close();
      rmSync(dir, { recursive: true, force: true });
    }
  });

  it("a replay of the same idempotencyKey posts nothing", async () => {
    const { handler, api, state, dir } = harness();
    try {
      await handler.onCommand(reply("cmd-2"), ctx);
      const replay = await handler.onCommand(reply("cmd-2"), ctx);
      expect(replay).toEqual({ kind: "accepted" });
      expect(api.posts).toHaveLength(1);
    } finally {
      state.close();
      rmSync(dir, { recursive: true, force: true });
    }
  });

  it("react/update/acknowledge answer unsupported (receipt, no throw)", async () => {
    const { handler, state, dir } = harness();
    try {
      for (const type of [
        "org.facilitator.command.communication.react.v1",
        "org.facilitator.command.communication.update.v1",
        "org.facilitator.command.communication.acknowledge.v1",
      ]) {
        expect(
          await handler.onCommand({ type, data: {} } as unknown as EnvelopeCommand, ctx),
        ).toEqual({ kind: "unsupported" });
      }
    } finally {
      state.close();
      rmSync(dir, { recursive: true, force: true });
    }
  });

  it("a dm-kind target or missing number is rejected without a provider call", async () => {
    const { handler, api, state, dir } = harness();
    try {
      expect(await handler.onCommand(reply("cmd-3", "dm"), ctx)).toEqual({
        kind: "rejected",
        reason: "unsupported-conversation-kind",
      });
      const noNumber = reply("cmd-4");
      delete ((noNumber.data as Record<string, unknown>).target as Record<string, unknown>)[
        "rootThreadId"
      ];
      expect(await handler.onCommand(noNumber, ctx)).toEqual({
        kind: "rejected",
        reason: "target-missing-repo-or-issue",
      });
      expect(api.posts).toHaveLength(0);
    } finally {
      state.close();
      rmSync(dir, { recursive: true, force: true });
    }
  });

  it("with no bot token a reply answers unsupported, never throws", async () => {
    const dir = mkdtempSync(join(tmpdir(), "ofac-fj-"));
    const state = new AdapterState(join(dir, "state.sqlite3"));
    try {
      const handler = createForgejoHandler({ api: undefined, state, logger: silent });
      expect(await handler.onCommand(reply("cmd-5"), ctx)).toEqual({
        kind: "unsupported",
        reason: "no-bot-token",
      });
    } finally {
      state.close();
      rmSync(dir, { recursive: true, force: true });
    }
  });

  it("advertises exactly the reply capability", () => {
    const manifest = forgejoAdapterManifest("team-alpha");
    expect(manifest.capabilities.map((c) => c.id)).toEqual([CMD_REPLY]);
    expect(manifest.ownerTeam).toBe("team-alpha");
  });
});
