// The op law (decision 5, §5.4): every op ledger-first; replay of the same
// commandId answers the recorded provider object id with ZERO provider
// calls; an ambiguous timeout (504 after the mutation landed) leaves the row
// pending and the retry reconciles by READ — never a second mutation; a
// same-commandId payload that differs is a loud conflict; the ci op is a
// safe upsert.
import { mkdtempSync, rmSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { afterAll, afterEach, beforeAll, beforeEach, describe, expect, it } from "vitest";
import { ForgejoClient } from "@ofac/forgejo-adapter";
import { ActionLedger, ActionLedgerConflictError } from "../src/ledger.js";
import { createOps, payloadSha256, type OpTable } from "../src/ops.js";
import { startFakeForge, type FakeForge } from "./fake-forge.js";

let forge: FakeForge;
let dir: string;
let ledger: ActionLedger;
let ops: OpTable;

beforeAll(async () => {
  forge = await startFakeForge();
});

afterAll(async () => {
  await forge.close();
});

beforeEach(() => {
  dir = mkdtempSync(join(tmpdir(), "ofac-fjc-"));
  ledger = new ActionLedger(join(dir, "actions.sqlite3"));
  ops = createOps({ api: new ForgejoClient(forge.url, "tok"), ledger });
  forge.reset();
});

afterEach(() => {
  ledger.close();
  rmSync(dir, { recursive: true, force: true });
});

const prInputs = {
  repo: "demo/widgets",
  head: "bot/topic",
  base: "main",
  title: "Add summarizer",
  body: "generated",
};

describe("pr.create", () => {
  it("happy path: one POST, settled ledger row, wire-presented id", async () => {
    const result = await ops["vcs.change_request.create"]!("cmd-1", prInputs);
    expect(result.providerObjectId).toBe("PR demo/widgets#1");
    expect(forge.prPosts).toBe(1);
    const row = ledger.find("cmd-1");
    expect(row?.state).toBe("executed");
    expect(row?.providerObjectId).toBe("PR demo/widgets#1");
    expect(row?.payloadSha256).toBe(payloadSha256(prInputs));
  });

  it("replay of the same commandId returns the recorded id, zero second POST", async () => {
    await ops["vcs.change_request.create"]!("cmd-1", prInputs);
    const replay = await ops["vcs.change_request.create"]!("cmd-1", prInputs);
    expect(replay.providerObjectId).toBe("PR demo/widgets#1");
    expect(replay.structured["replayed"]).toBe(true);
    expect(forge.prPosts).toBe(1);
    expect(forge.prs).toHaveLength(1);
  });

  it("ambiguous timeout: failed answer leaves a pending row; the retry reads, never re-POSTs", async () => {
    forge.loseNextPrResponse = true;
    await expect(ops["vcs.change_request.create"]!("cmd-2", prInputs)).rejects.toThrow(/504/);
    // The PR EXISTS at the forge (the mutation landed) but the row is pending.
    expect(forge.prs).toHaveLength(1);
    expect(ledger.find("cmd-2")?.state).toBe("pending");

    const recovered = await ops["vcs.change_request.create"]!("cmd-2", prInputs);
    expect(recovered.providerObjectId).toBe("PR demo/widgets#1");
    expect(forge.prPosts).toBe(1); // still exactly one POST ever
    expect(forge.prs).toHaveLength(1); // zero second PR
    expect(ledger.find("cmd-2")?.state).toBe("executed");
  });

  it("a same-commandId DIFFERENT payload is a conflict, not a silent re-run", async () => {
    await ops["vcs.change_request.create"]!("cmd-3", prInputs);
    await expect(
      ops["vcs.change_request.create"]!("cmd-3", { ...prInputs, title: "drifted" }),
    ).rejects.toBeInstanceOf(ActionLedgerConflictError);
  });
});

describe("issue.comment / pr.comment", () => {
  it("posts once, stamping the command marker in the body", async () => {
    const result = await ops["vcs.issue.comment"]!("cmd-4", {
      repo: "demo/widgets",
      issue: 1,
      body: "Work complete.",
    });
    expect(result.providerObjectId).toBe("comment 1");
    expect(forge.comments[0]?.body).toContain("ofac-cmd:cmd-4");
  });

  it("ambiguous timeout reconciles by reading the marker back: zero double-post", async () => {
    // Simulate the lost answer: the comment exists, but the ledger is pending.
    ledger.insert(
      "cmd-5",
      "vcs.issue.comment",
      payloadSha256({ repo: "demo/widgets", issue: 1, body: "hi" }),
    );
    forge.comments.push({ id: 42, html_url: "u", body: "hi\n\n<!-- ofac-cmd:cmd-5 -->" });
    const result = await ops["vcs.issue.comment"]!("cmd-5", {
      repo: "demo/widgets",
      issue: 1,
      body: "hi",
    });
    expect(result.providerObjectId).toBe("comment 42");
    expect(forge.commentPosts).toBe(0);
    expect(ledger.find("cmd-5")?.state).toBe("executed");
  });
});

describe("issue.read", () => {
  it("reads once; replay costs zero provider calls", async () => {
    const first = await ops["vcs.issue.read"]!("cmd-6", { repo: "demo/widgets", issue: 7 });
    expect(first.structured["title"]).toBe("issue 7 in demo/widgets");
    const replay = await ops["vcs.issue.read"]!("cmd-6", { repo: "demo/widgets", issue: 7 });
    expect(replay.structured["replayed"]).toBe(true);
  });
});

describe("vcs.branch.push", () => {
  it("creates once; a replayed command after an ambiguous timeout accepts the 409 as truth", async () => {
    const result = await ops["vcs.branch.push"]!("cmd-7", {
      repo: "demo/widgets",
      branch: "bot/topic",
      from: "main",
    });
    expect(result.providerObjectId).toBe("branch demo/widgets/bot/topic");

    // Pending-row + 409 = the create landed before the answer was lost.
    ledger.insert(
      "cmd-8",
      "vcs.branch.push",
      payloadSha256({ repo: "demo/widgets", branch: "bot/topic", from: "main" }),
    );
    const recovered = await ops["vcs.branch.push"]!("cmd-8", {
      repo: "demo/widgets",
      branch: "bot/topic",
      from: "main",
    });
    expect(recovered.providerObjectId).toBe("branch demo/widgets/bot/topic");
  });

  it("a fresh conflict still fails loudly", async () => {
    await expect(
      ops["vcs.branch.push"]!("cmd-9", {
        repo: "demo/widgets",
        branch: "main",
        from: "main",
      }),
    ).rejects.toThrow(/409/);
  });
});

describe("ci.trigger", () => {
  it("posts a commit status; replay skips the provider", async () => {
    const inputs = {
      repo: "demo/widgets",
      sha: "abcdef1234567890",
      state: "failure",
      description: "red",
      context: "ofac",
    };
    const result = await ops["vcs.pipeline.trigger"]!("cmd-10", inputs);
    expect(forge.statuses).toEqual([
      { sha: "abcdef1234567890", state: "failure", context: "ofac" },
    ]);
    expect(String(result.providerObjectId)).toContain("status demo/widgets@abcdef1234:ofac");
    const replay = await ops["vcs.pipeline.trigger"]!("cmd-10", inputs);
    expect(replay.structured["replayed"]).toBe(true);
    expect(forge.statuses).toHaveLength(1);
  });
});

describe("ledger durability + input validation", () => {
  it("the row survives a reopen (same file)", async () => {
    await ops["vcs.change_request.create"]!("cmd-11", prInputs);
    ledger.close();
    const reopened = new ActionLedger(join(dir, "actions.sqlite3"));
    expect(reopened.find("cmd-11")?.state).toBe("executed");
    reopened.close();
    ledger = new ActionLedger(join(dir, "actions.sqlite3")); // so afterEach can close
    ops = createOps({ api: new ForgejoClient(forge.url, "tok"), ledger });
  });

  it("missing inputs fail per-op with the key named", async () => {
    await expect(
      ops["vcs.change_request.create"]!("cmd-12", { repo: "demo/widgets" }),
    ).rejects.toThrow(/inputs\.head: required string/);
    await expect(
      ops["vcs.issue.read"]!("cmd-13", { repo: "demo/widgets", issue: "x" }),
    ).rejects.toThrow(/inputs\.issue: required positive integer/);
  });
});
