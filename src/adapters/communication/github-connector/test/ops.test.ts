// The op law (decision 5): every op ledger-first; replay of the same
// commandId answers the recorded provider object id with ZERO provider
// calls; an ambiguous timeout (504 after the mutation landed) leaves the row
// pending and the retry reconciles by READ — never a second mutation; a
// same-commandId payload that differs is a loud conflict; the pipeline op is
// a safe upsert; the dialogue ops reconcile on the stamped reply.
import { mkdtempSync, rmSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { afterAll, afterEach, beforeAll, beforeEach, describe, expect, it } from "vitest";
import { ActionLedger, ActionLedgerConflictError } from "@ofac/provider-core";
import { GitHubClient } from "@ofac/github-adapter";
import { createOps, OpError, type OpTable } from "../src/ops.js";
import { startFakeGithub, type FakeGithub } from "./fake-github.js";

let github: FakeGithub;
let dir: string;
let ledger: ActionLedger;
let ops: OpTable;

beforeAll(async () => {
  github = await startFakeGithub();
});

afterAll(async () => {
  await github.close();
});

beforeEach(() => {
  dir = mkdtempSync(join(tmpdir(), "ghc-ops-"));
  ledger = new ActionLedger(join(dir, "actions.sqlite3"));
  ops = createOps({ api: new GitHubClient(github.url, "bot-pat"), ledger });
});

afterEach(() => {
  ledger.close();
  rmSync(dir, { recursive: true, force: true });
  github.reset();
});

const prInputs = {
  repo: "demo/widgets",
  head: "bot/topic",
  base: "main",
  title: "Add summarizer",
  body: "generated",
};

async function run(op: string, commandId: string, inputs: Record<string, unknown>) {
  const runner = ops[op];
  if (runner === undefined) throw new Error(`no op ${op}`);
  return await runner(commandId, inputs);
}

describe("pr.create", () => {
  it("opens exactly one PR and settles the ledger", async () => {
    const first = await run("vcs.change_request.create", "c1", prInputs);
    expect(first.providerObjectId).toBe("PR demo/widgets#1");
    const replay = await run("vcs.change_request.create", "c1", prInputs);
    expect(replay.providerObjectId).toBe(first.providerObjectId);
    expect(github.prPosts).toBe(1);
  });

  it("ambiguous timeout: row stays pending, the retry reconciles by READ", async () => {
    github.loseNextPrResponse = true;
    await expect(run("vcs.change_request.create", "c2", prInputs)).rejects.toThrow(/504/);
    const row = ledger.find("c2");
    expect(row?.state).toBe("pending");
    expect(row?.providerObjectId).toBeNull();
    expect(github.prs).toHaveLength(1);

    const retry = await run("vcs.change_request.create", "c2", prInputs);
    expect(retry.providerObjectId).toBe("PR demo/widgets#1");
    // Read-before-fire + the settled reconcile: still exactly ONE POST.
    expect(github.prPosts).toBe(1);
    expect(ledger.find("c2")?.state).toBe("executed");
  });

  it("a concurrent duplicate open PR satisfies the intent without POSTing", async () => {
    github.prs.push({
      number: 9,
      head: { sha: "s", ref: "bot/topic" },
      base: "main",
      title: "existing",
      state: "open",
      merged: false,
    });
    const result = await run("vcs.change_request.create", "c3", prInputs);
    expect(result.providerObjectId).toBe("PR demo/widgets#9");
    expect(github.prPosts).toBe(0);
  });

  it("a differing payload for the same commandId is a loud conflict", async () => {
    await run("vcs.change_request.create", "c4", prInputs);
    await expect(
      run("vcs.change_request.create", "c4", { ...prInputs, title: "different" }),
    ).rejects.toBeInstanceOf(ActionLedgerConflictError);
  });
});

describe("issue.comment / pr.comment", () => {
  it("posts once, replays without a second post", async () => {
    const inputs = { repo: "demo/widgets", issue: 7, body: "done" };
    const first = await run("vcs.issue.comment", "k1", inputs);
    expect(github.commentPosts).toBe(1);
    expect((first.structured as { issue: number }).issue).toBe(7);
    const replay = await run("vcs.issue.comment", "k1", inputs);
    expect(replay.providerObjectId).toBe(first.providerObjectId);
    expect(github.commentPosts).toBe(1);
  });

  it("lost comment response reconciles on the stamped marker", async () => {
    const inputs = { repo: "demo/widgets", issue: 7, body: "done" };
    // Simulate the lost answer: the stamped comment stands provider-side
    // (the first attempt POSTed then died before settling).
    github.comments.push({
      id: 55,
      html_url: `${github.url}/repos/demo/widgets/issues/7/comments`,
      body: "done\n\n<!-- ofac-cmd:k2 -->",
    });
    const result = await run("vcs.change_request.comment", "k2", inputs);
    // Reconcile found the stamped comment: NO POST at all this attempt.
    expect(github.commentPosts).toBe(0);
    expect(result.providerObjectId).toBe("comment 55");
    expect(ledger.find("k2")?.state).toBe("executed");
  });

  it("pr.comment shares the issues route (issue == PR number)", async () => {
    await run("vcs.change_request.comment", "k3", {
      repo: "demo/widgets",
      issue: 2,
      body: "review reply",
    });
    expect(github.comments[0]?.html_url).toContain("/issues/2/comments");
  });
});

describe("issue.read", () => {
  it("reads through the ledger", async () => {
    github.labels.set("demo/widgets#7", ["ofac:work"]);
    const first = await run("vcs.issue.read", "r1", { repo: "demo/widgets", issue: 7 });
    expect((first.structured as { number: number }).number).toBe(7);
    const replay = await run("vcs.issue.read", "r1", { repo: "demo/widgets", issue: 7 });
    expect(replay.providerObjectId).toBe(first.providerObjectId);
    expect((replay.structured as { replayed?: boolean }).replayed).toBe(true);
  });
});

describe("issue.label", () => {
  it("adds labels once (set-union upsert)", async () => {
    const inputs = { repo: "demo/widgets", issue: 7, labels: ["ofac:work", "bug"] };
    await run("vcs.issue.label", "l1", inputs);
    await run("vcs.issue.label", "l1", inputs);
    expect(github.labelPosts).toBe(1);
    expect(github.labels.get("demo/widgets#7")).toEqual(
      expect.arrayContaining(["ofac:work", "bug"]),
    );
  });

  it("rejects an empty labels array", async () => {
    await expect(run("vcs.issue.label", "l2", { repo: "r", issue: 1, labels: [] })).rejects.toThrow(
      OpError,
    );
  });
});

describe("change_request.read + review", () => {
  it("reads a PR with head info", async () => {
    await run("vcs.change_request.create", "p1", prInputs);
    const read = await run("vcs.change_request.read", "p2", { repo: "demo/widgets", issue: 1 });
    const structured = read.structured as { headRef: string; number: number };
    expect(structured.number).toBe(1);
    expect(structured.headRef).toBe("bot/topic");
  });

  it("submits a review; invalid events fail closed", async () => {
    await run("vcs.change_request.create", "p3", prInputs);
    await run("vcs.change_request.review", "p4", {
      repo: "demo/widgets",
      issue: 1,
      event: "APPROVED",
      body: "ship",
    });
    expect(github.reviews[0]).toEqual({
      repo: "demo/widgets",
      number: 1,
      event: "APPROVED",
      body: "ship",
    });
    await expect(
      run("vcs.change_request.review", "p5", {
        repo: "demo/widgets",
        issue: 1,
        event: "DISMISSED",
      }),
    ).rejects.toThrow(OpError);
    expect(github.reviewPosts).toBe(1);
  });
});

describe("review-thread dialogue", () => {
  // Review-thread routes are PR-scoped upstream: the PR must exist.
  const seedPr = (): void => {
    github.prs.push({
      number: 1,
      head: { sha: "s1", ref: "bot/topic" },
      base: "main",
      title: "t",
      state: "open",
      merged: false,
    });
  };

  it("discussion.reply posts with in_reply_to and replays clean", async () => {
    seedPr();
    const inputs = { repo: "demo/widgets", issue: 1, commentId: "55", body: "addressed" };
    const first = await run("vcs.discussion.reply", "d1", inputs);
    expect(github.replyPosts).toBe(1);
    const posted = github.reviewComments[0];
    expect(posted?.in_reply_to_id).toBe(55);
    expect(posted?.body).toContain("addressed");
    const replay = await run("vcs.discussion.reply", "d1", inputs);
    expect(replay.providerObjectId).toBe(first.providerObjectId);
  });

  it("discussion.reply reconciles a lost answer on the stamped reply", async () => {
    seedPr();
    github.reviewComments.push({
      id: 77,
      pull_request_number: 1,
      in_reply_to_id: 55,
      body: "addressed\n\n<!-- ofac-cmd:d2 -->",
      user: "ofac-bot",
    });
    const result = await run("vcs.discussion.reply", "d2", {
      repo: "demo/widgets",
      issue: 1,
      commentId: "55",
      body: "addressed",
    });
    // The reconcile reads the review-thread route: the stamped reply
    // already exists provider-side — zero POSTs this attempt.
    expect(github.replyPosts).toBe(0);
    expect(result.providerObjectId).toBe("reply 77");
    expect(ledger.find("d2")?.state).toBe("executed");
  });

  it("discussion.resolve hits the GraphQL mutation once", async () => {
    const inputs = { repo: "demo/widgets", issue: 1, commentId: "PRRT_kwDO1234" };
    await run("vcs.discussion.resolve", "s1", inputs);
    expect(github.resolvedThreads).toEqual(["PRRT_kwDO1234"]);
    await run("vcs.discussion.resolve", "s1", inputs);
    expect(github.resolvedThreads).toHaveLength(1);
  });
});

describe("vcs.branch.push", () => {
  it("creates the ref once; a pending-row retry reconciles the 422", async () => {
    await run("vcs.branch.push", "b1", {
      repo: "demo/widgets",
      branch: "bot/new",
      from: "main",
    });
    expect(github.branches.map((b) => b.name)).toContain("bot/new");
    await run("vcs.branch.push", "b1", {
      repo: "demo/widgets",
      branch: "bot/new",
      from: "main",
    });

    // Fresh commandId, branch exists → real conflict (loud throw).
    await expect(
      run("vcs.branch.push", "b2", { repo: "demo/widgets", branch: "bot/new", from: "main" }),
    ).rejects.toThrow(/422/);
  });
});

describe("pipeline.trigger", () => {
  it("posts a commit status (safe upsert)", async () => {
    await run("vcs.pipeline.trigger", "t1", {
      repo: "demo/widgets",
      sha: "abcdef0123456789",
      state: "success",
      context: "ofac",
    });
    expect(github.statuses).toEqual([
      { sha: "abcdef0123456789", state: "success", context: "ofac" },
    ]);
    await run("vcs.pipeline.trigger", "t1", {
      repo: "demo/widgets",
      sha: "abcdef0123456789",
      state: "success",
      context: "ofac",
    });
    expect(github.statusPosts).toBe(1);
  });
});

describe("input validation", () => {
  it("missing required inputs name the op and key", async () => {
    await expect(run("vcs.issue.read", "v1", { repo: "demo/widgets" })).rejects.toThrow(
      /vcs\.issue\.read[^\n]*issue/,
    );
  });
});
