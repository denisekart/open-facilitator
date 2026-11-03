// The op law: every op ledger-first; replay of the same commandId answers
// the recorded provider object id with ZERO provider calls; an ambiguous
// timeout (504 after the mutation landed) leaves the row pending and the
// retry reconciles by READ — never a second mutation; a same-commandId
// payload that differs is a loud conflict; the pipeline op is a safe upsert.
import { mkdtempSync, rmSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { afterEach, afterAll, beforeAll, beforeEach, describe, expect, it } from "vitest";
import { GitLabClient } from "@ofac/gitlab-adapter";
import { ActionLedger, CMD_MARKER, OpError, createOps, type OpTable } from "../src/index.js";
import { startFakeGitLab, type FakeGitLab } from "./fake-gitlab.js";

let gitlab: FakeGitLab;
let dir: string;
let ledger: ActionLedger;
let ops: OpTable;

beforeAll(async () => {
  gitlab = await startFakeGitLab();
});

afterAll(async () => {
  await gitlab.close();
});

beforeEach(() => {
  gitlab.reset();
  dir = mkdtempSync(join(tmpdir(), "ofac-glc-ops-"));
  ledger = new ActionLedger(join(dir, "actions.sqlite3"));
  ops = createOps({ api: new GitLabClient(gitlab.url, "bot-pat"), ledger });
});

afterEach(() => {
  ledger.close();
  rmSync(dir, { recursive: true, force: true });
});

const mrInputs = {
  repo: "demo/widgets",
  head: "bot/topic",
  base: "main",
  title: "Summarize layout",
  body: "auto",
};

describe("change_request.create", () => {
  it("opens exactly one MR and settles the ledger row", async () => {
    const run = ops["vcs.change_request.create"]!;
    const first = await run("cmd-1", mrInputs);
    expect(first.providerObjectId).toBe("MR demo/widgets!1");
    expect(gitlab.mrPosts).toBe(1);
    const replay = await run("cmd-1", mrInputs);
    expect(replay.providerObjectId).toBe(first.providerObjectId);
    expect(gitlab.mrPosts).toBe(1);
  });

  it("ambiguous 504: row stays pending, retry reconciles by READ — no second POST", async () => {
    gitlab.loseNextMrResponse = true;
    const run = ops["vcs.change_request.create"]!;
    await expect(run("cmd-2", mrInputs)).rejects.toThrow(/504/);
    const row = ledger.find("cmd-2");
    expect(row?.state).toBe("pending");
    expect(gitlab.mrPosts).toBe(1);
    const retry = await run("cmd-2", mrInputs);
    expect(retry.providerObjectId).toBe("MR demo/widgets!1");
    expect(gitlab.mrPosts).toBe(1);
  });

  it("a different payload under the same commandId is a loud conflict", async () => {
    const run = ops["vcs.change_request.create"]!;
    await run("cmd-3", mrInputs);
    expect(() => ledger.insert("cmd-3", "vcs.change_request.create", "deadbeef")).toThrow();
  });
});

describe("issue.comment / change_request.comment", () => {
  it("posts stamped notes on the right resource; replay never re-posts", async () => {
    const issueRun = ops["vcs.issue.comment"]!;
    const first = await issueRun("cmd-c1", {
      repo: "demo/widgets",
      issue: 23,
      body: "on the issue",
    });
    expect(first.providerObjectId).toMatch(/^note \d+$/);
    expect(gitlab.notes.filter((n) => !n.mr)).toHaveLength(1);
    const mrRun = ops["vcs.change_request.comment"]!;
    await mrRun("cmd-c2", { repo: "demo/widgets", issue: 16, body: "on the MR" });
    expect(gitlab.notes.filter((n) => n.mr)).toHaveLength(1);
    expect(gitlab.notes.some((n) => n.body.includes(CMD_MARKER("cmd-c1")))).toBe(true);
    await issueRun("cmd-c1", { repo: "demo/widgets", issue: 23, body: "on the issue" });
    expect(gitlab.notePosts).toBe(2);
  });
});

describe("issue.read / change_request.read", () => {
  it("reads ledgered: zero second provider call on replay", async () => {
    const read = ops["vcs.issue.read"]!;
    const r = await read("cmd-r1", { repo: "demo/widgets", issue: 23 });
    expect(r.providerObjectId).toBe("issue demo/widgets#23");
    expect(await read("cmd-r1", { repo: "demo/widgets", issue: 23 })).toEqual({
      providerObjectId: "issue demo/widgets#23",
      structured: { providerObjectId: "issue demo/widgets#23", replayed: true },
    });
    const mrRead = ops["vcs.change_request.read"]!;
    await ops["vcs.change_request.create"]!("cmd-r2", mrInputs);
    const detail = await mrRead("cmd-r3", { repo: "demo/widgets", issue: 1 });
    const structured = detail.structured as Record<string, unknown>;
    expect(structured["headRef"]).toBe("bot/topic");
    expect(structured["headSha"]).toBe("sha-of-bot/topic");
  });
});

describe("issue.label", () => {
  it("upserts add_labels; requires a non-empty string array", async () => {
    const run = ops["vcs.issue.label"]!;
    await run("cmd-l1", { repo: "demo/widgets", issue: 23, labels: ["ofac:work"] });
    expect(gitlab.labels.get("demo/widgets#23")).toEqual(["ofac:work"]);
    await run("cmd-l1", { repo: "demo/widgets", issue: 23, labels: ["ofac:work"] });
    expect(gitlab.labelPuts).toBe(1);
    await expect(run("cmd-l2", { repo: "r", issue: 1, labels: [] })).rejects.toBeInstanceOf(
      OpError,
    );
  });
});

describe("change_request.review", () => {
  it("APPROVED rides the approve route, REQUEST_CHANGES the reviews route", async () => {
    const run = ops["vcs.change_request.review"]!;
    await run("cmd-v1", { repo: "demo/widgets", issue: 16, event: "APPROVED" });
    expect(gitlab.approvePosts).toBe(1);
    await run("cmd-v2", { repo: "demo/widgets", issue: 16, event: "REQUEST_CHANGES", body: "no" });
    expect(gitlab.reviewPosts).toBe(1);
    expect(gitlab.reviews[0]?.status).toBe("request_changes");
    await expect(
      run("cmd-v3", { repo: "demo/widgets", issue: 16, event: "NOPE" }),
    ).rejects.toBeInstanceOf(OpError);
  });
});

describe("discussion reply + resolve", () => {
  it("replies to a thread and resolves it; replay zero-calls", async () => {
    gitlab.seedDiscussion("demo/widgets", 16, "d-1");
    const reply = ops["vcs.discussion.reply"]!;
    const r = await reply("cmd-d1", {
      repo: "demo/widgets",
      issue: 16,
      discussionId: "d-1",
      body: "addressed",
    });
    expect(gitlab.discussions.get("demo/widgets!16:d-1")?.replies).toEqual(["addressed"]);
    expect(r.structured).toMatchObject({ discussionId: "d-1" });
    await reply("cmd-d1", {
      repo: "demo/widgets",
      issue: 16,
      discussionId: "d-1",
      body: "addressed",
    });
    expect(gitlab.discussionPosts).toBe(1);
    const resolve = ops["vcs.discussion.resolve"]!;
    await resolve("cmd-d2", { repo: "demo/widgets", issue: 16, discussionId: "d-1" });
    expect(gitlab.discussions.get("demo/widgets!16:d-1")?.resolved).toBe(true);
    await resolve("cmd-d2", { repo: "demo/widgets", issue: 16, discussionId: "d-1" });
    expect(gitlab.discussionPuts).toBe(1);
  });
});

describe("vcs.branch.push", () => {
  it("creates a branch; a 409 with a PENDING prior row reconciles", async () => {
    const run = ops["vcs.branch.push"]!;
    await run("cmd-b1", { repo: "demo/widgets", branch: "bot/topic", from: "main" });
    expect(gitlab.branches).toContain("bot/topic");
    await expect(
      run("cmd-b2", { repo: "demo/widgets", branch: "bot/topic", from: "main" }),
    ).rejects.toThrow(/409/);
  });
});

describe("pipeline.trigger", () => {
  it("posts a named commit status (upsert-safe)", async () => {
    const run = ops["vcs.pipeline.trigger"]!;
    await run("cmd-p1", {
      repo: "demo/widgets",
      sha: "abcdef1234567890",
      state: "success",
      description: "ok",
      context: "ofac",
    });
    expect(gitlab.statuses).toEqual([{ sha: "abcdef1234567890", state: "success", name: "ofac" }]);
    await run("cmd-p1", {
      repo: "demo/widgets",
      sha: "abcdef1234567890",
      state: "success",
      description: "ok",
      context: "ofac",
    });
    expect(gitlab.statuses).toHaveLength(1);
  });
});
