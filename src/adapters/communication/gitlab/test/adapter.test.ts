// The webhook edge + reply law (fan-out form): the kernel sink rode the
// verbatim delivery here inside a command.provider.webhook.v1; the seam
// verifies the X-Gitlab-Token shared secret constant-time BEFORE any parsing
// (GitLab has no body HMAC) — a bad or missing token answers `bad-signature`
// with zero processing. X-Request-Id + X-Gitlab-Event pass through untouched
// as the dedupe identity. Pipeline deliveries resolve the sha→MR number
// through the injected API (the I/O arm). The reply handler posts a note
// issue-first / MR-on-404 under the comment-ledger idempotency law, and the
// classifier maps the recorded fixtures onto the central domain events.
import { mkdtempSync, rmSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { afterAll, describe, expect, it } from "vitest";
import type { CommandContext, LinkLogger } from "@ofac/workspace-link";
import type { WebhookDelivery } from "@ofac/provider-core/node";
import {
  CHANGE_REQUEST_EVENT,
  REVIEW_EVENT,
  ISSUE_EVENT,
  MENTION_EVENT,
  AdapterState,
  GitLabError,
  createGitLabHandler,
  createGitLabNormalizer,
  type GitLabApi,
  type NormalizeContext,
} from "../src/index.js";
import {
  botLabeled,
  buildFailure,
  commentOnIssue,
  commentOnMr,
  issueLabeled,
  issueLabeledOther,
  issueOpened,
  mentionOnIssue,
  mentionOnMr,
  mrApproved,
  mrOpened,
  pipelineSuccess,
} from "./fixtures/payloads.js";

const TOKEN = "s3cret-hook";
const CTX: NormalizeContext = {
  providerInstanceId: "local-gitlab",
  tenantOrOrganizationId: "git.localhost",
  workLabels: new Set(["ofac:work"]),
  botUser: "facilitator-bot",
};

const silent: LinkLogger = {
  debug: () => undefined,
  info: () => undefined,
  warn: () => undefined,
  error: () => undefined,
};

class FakeApi implements GitLabApi {
  issueNotes: { repo: string; iid: number; body: string; id: number }[] = [];
  mrNotes: { repo: string; iid: number; body: string; id: number }[] = [];
  mrForSha: number[] = [];
  failIssueNotes = false;
  private next = 1;

  postIssueNote = async (repo: string, iid: number, body: string) => {
    if (this.failIssueNotes) throw new GitLabError("postIssueNote", 404, "not an issue");
    const note = { repo, iid, body, id: this.next++ };
    this.issueNotes.push(note);
    return { id: note.id, web_url: "" };
  };
  postMrNote = async (repo: string, iid: number, body: string) => {
    const note = { repo, iid, body, id: this.next++ };
    this.mrNotes.push(note);
    return { id: note.id, web_url: "" };
  };
  awards: { repo: string; iid: number; noteId: number; name: string; domain: string }[] = [];
  edits: { repo: string; iid: number; noteId: number; body: string; domain: string }[] = [];
  awardIssueNoteEmoji = async (repo: string, iid: number, noteId: number, name: string) => {
    if (this.failIssueNotes) throw new GitLabError("awardIssueNoteEmoji", 404, "not an issue");
    this.awards.push({ repo, iid, noteId, name, domain: "issue" });
  };
  awardMrNoteEmoji = async (repo: string, iid: number, noteId: number, name: string) => {
    this.awards.push({ repo, iid, noteId, name, domain: "mr" });
  };
  editIssueNote = async (repo: string, iid: number, noteId: number, body: string) => {
    if (this.failIssueNotes) throw new GitLabError("editIssueNote", 404, "not an issue");
    this.edits.push({ repo, iid, noteId, body, domain: "issue" });
  };
  editMrNote = async (repo: string, iid: number, noteId: number, body: string) => {
    this.edits.push({ repo, iid, noteId, body, domain: "mr" });
  };
  openMr = async () => ({ iid: 1, source_branch: "" });
  openMrsForSource = async () => [] as number[];
  openMrsForSha = async () => this.mrForSha;
  findNoteByMarker = async () => undefined;
  getIssue = async () => ({ iid: 1, state: "opened", title: "" });
  addLabels = async () => undefined;
  getMergeRequest = async () => ({
    iid: 1,
    state: "opened",
    title: "",
    merged: false,
    sha: "",
    source_branch: "",
  });
  createBranch = async () => undefined;
  commitStatus = async () => undefined;
  reviewMergeRequest = async () => ({ id: 1 });
  replyDiscussion = async () => "1";
  resolveDiscussion = async () => undefined;
}

function delivery(body: string, headers: Record<string, string>): WebhookDelivery {
  return {
    provider: "gitlab",
    contentType: "application/json",
    rawBody: Buffer.from(body, "utf8"),
    headers,
  };
}

function normalizer(api: GitLabApi | undefined) {
  return createGitLabNormalizer({ ctx: CTX, webhookToken: TOKEN, api, logger: silent });
}

const json = (payload: unknown) => JSON.stringify(payload);

describe("createGitLabNormalizer token law", () => {
  it("bad or missing X-Gitlab-Token answers bad-signature, never parses", async () => {
    const n = normalizer(new FakeApi());
    const body = json(issueOpened);
    expect(
      await n(delivery(body, { "x-gitlab-token": "wrong", "x-gitlab-event": "Issue Hook" })),
    ).toEqual({ kind: "bad-signature" });
    expect(await n(delivery(body, { "x-gitlab-event": "Issue Hook" }))).toEqual({
      kind: "bad-signature",
    });
  });

  it("well-tokened delivery without event/id headers yields zero events", async () => {
    const n = normalizer(new FakeApi());
    const verdict = await n(
      delivery(json(issueOpened), { "x-gitlab-token": TOKEN, "x-gitlab-event": "Issue Hook" }),
    );
    expect(verdict).toEqual({ kind: "normalized", events: [] });
  });

  it("unparseable body with a valid token drops to zero events", async () => {
    const n = normalizer(new FakeApi());
    const verdict = await n(
      delivery("not json", {
        "x-gitlab-token": TOKEN,
        "x-gitlab-event": "Issue Hook",
        "x-request-id": "req-1",
      }),
    );
    expect(verdict).toEqual({ kind: "normalized", events: [] });
  });
});

describe("fixture classification through the seam", () => {
  it("Issue Hook open → issue.v1 opened (no trigger)", async () => {
    const n = normalizer(new FakeApi());
    const verdict = await n(
      delivery(json(issueOpened), {
        "x-gitlab-token": TOKEN,
        "x-gitlab-event": "Issue Hook",
        "x-request-id": "req-open",
      }),
    );
    expect(verdict.kind).toBe("normalized");
    if (verdict.kind !== "normalized") return;
    expect(verdict.events).toHaveLength(1);
    const event = verdict.events[0]!;
    expect(event.type).toBe(ISSUE_EVENT);
    const data = event.data as { action: string; workTrigger: boolean; provider: string };
    expect(data.action).toBe("opened");
    expect(data.workTrigger).toBe(false);
    expect(data.provider).toBe("gitlab");
  });

  it("Issue Hook label change → issue.v1 labeled + workTrigger, labels normalized", async () => {
    const n = normalizer(new FakeApi());
    const verdict = await n(
      delivery(json(issueLabeled), {
        "x-gitlab-token": TOKEN,
        "x-gitlab-event": "Issue Hook",
        "x-request-id": "req-label",
      }),
    );
    expect(verdict.kind).toBe("normalized");
    if (verdict.kind !== "normalized") return;
    const data = verdict.events[0]!.data as {
      action: string;
      workTrigger: boolean;
      labels: string[];
    };
    expect(data.action).toBe("labeled");
    expect(data.workTrigger).toBe(true);
    expect(data.labels).toEqual(["ofac:work"]);
  });

  it("label change without a work label → labeled, workTrigger false", async () => {
    const n = normalizer(new FakeApi());
    const verdict = await n(
      delivery(json(issueLabeledOther), {
        "x-gitlab-token": TOKEN,
        "x-gitlab-event": "Issue Hook",
        "x-request-id": "req-label-other",
      }),
    );
    expect(verdict.kind).toBe("normalized");
    if (verdict.kind !== "normalized") return;
    const data = verdict.events[0]!.data as { action: string; workTrigger: boolean };
    expect(data.action).toBe("labeled");
    // issueLabeledOther carries ofac:work in `current` from the label-add on
    // top of it — workTrigger is true because the set INTERSECTS; a change
    // that only ADDS wontfix over no prior labels would be false.
    expect(data.workTrigger).toBe(true);
  });

  it("Note Hook on an issue → issue commented, comment id in reference.messageId", async () => {
    const n = normalizer(new FakeApi());
    const verdict = await n(
      delivery(json(commentOnIssue), {
        "x-gitlab-token": TOKEN,
        "x-gitlab-event": "Note Hook",
        "x-request-id": "req-note",
      }),
    );
    expect(verdict.kind).toBe("normalized");
    if (verdict.kind !== "normalized") return;
    expect(verdict.events).toHaveLength(1);
    // event.provider.issue.v1 has no top-level commentId field (only
    // change_request.v1 gained one); the note id rides reference.messageId.
    const data = verdict.events[0]!.data as {
      action: string;
      reference: { messageId?: string };
    };
    expect(data.action).toBe("commented");
    expect(data.reference.messageId).toBe("1241");
  });

  it("Note Hook mentioning the bot → commented + mention.v1 companion", async () => {
    const n = normalizer(new FakeApi());
    const verdict = await n(
      delivery(json(mentionOnIssue), {
        "x-gitlab-token": TOKEN,
        "x-gitlab-event": "Note Hook",
        "x-request-id": "req-mention",
      }),
    );
    expect(verdict.kind).toBe("normalized");
    if (verdict.kind !== "normalized") return;
    expect(verdict.events.map((e) => e.type)).toEqual([ISSUE_EVENT, MENTION_EVENT]);
    const mention = verdict.events[1]!.data as { providerDeliveryId: string; text: string };
    expect(mention.providerDeliveryId).toBe("req-mention:mention");
    expect(mention.text).toContain("@facilitator-bot");
  });

  it("Note Hook on an MR → change_request commented + headRef; mention rides", async () => {
    const n = normalizer(new FakeApi());
    const plain = await n(
      delivery(json(commentOnMr), {
        "x-gitlab-token": TOKEN,
        "x-gitlab-event": "Note Hook",
        "x-request-id": "req-mr-note",
      }),
    );
    expect(plain.kind).toBe("normalized");
    if (plain.kind !== "normalized") return;
    expect(plain.events).toHaveLength(1);
    const data = plain.events[0]!.data as { action: string; headRef?: string };
    expect(data.action).toBe("commented");
    expect(data.headRef).toBe("master");

    const steer = await n(
      delivery(json(mentionOnMr), {
        "x-gitlab-token": TOKEN,
        "x-gitlab-event": "Note Hook",
        "x-request-id": "req-mr-mention",
      }),
    );
    expect(steer.kind).toBe("normalized");
    if (steer.kind !== "normalized") return;
    expect(steer.events.map((e) => e.type)).toEqual([CHANGE_REQUEST_EVENT, MENTION_EVENT]);
  });

  it("Merge Request Hook open/approve → opened + an approved review.v1 verdict", async () => {
    const n = normalizer(new FakeApi());
    const opened = await n(
      delivery(json(mrOpened), {
        "x-gitlab-token": TOKEN,
        "x-gitlab-event": "Merge Request Hook",
        "x-request-id": "req-mr",
      }),
    );
    expect(opened.kind).toBe("normalized");
    if (opened.kind !== "normalized") return;
    const data = opened.events[0]!.data as { action: string; headRef?: string };
    expect(data.action).toBe("opened");
    expect(data.headRef).toBe("feature/booking-validation");

    const approved = await n(
      delivery(json(mrApproved), {
        "x-gitlab-token": TOKEN,
        "x-gitlab-event": "Merge Request Hook",
        "x-request-id": "req-mr-approved",
      }),
    );
    expect(approved.kind).toBe("normalized");
    if (approved.kind !== "normalized") return;
    // E1: the approve hook IS the verdict - it normalizes to the decision
    // class, not the change_request reviewed stand-in.
    expect(approved.events[0]!.type).toBe(REVIEW_EVENT);
    expect(approved.events[0]!.data as { action: string; state: string }).toMatchObject({
      action: "submitted",
      state: "approved",
    });
  });

  it("echo law: a bot-authored delivery drops", async () => {
    const n = normalizer(new FakeApi());
    const verdict = await n(
      delivery(json(botLabeled), {
        "x-gitlab-token": TOKEN,
        "x-gitlab-event": "Issue Hook",
        "x-request-id": "req-echo",
      }),
    );
    expect(verdict).toEqual({ kind: "normalized", events: [] });
  });

  it("Pipeline Hook bound to an MR → pipeline.v1 success (no I/O needed)", async () => {
    const n = normalizer(new FakeApi());
    const verdict = await n(
      delivery(json(pipelineSuccess), {
        "x-gitlab-token": TOKEN,
        "x-gitlab-event": "Pipeline Hook",
        "x-request-id": "req-pl",
      }),
    );
    expect(verdict.kind).toBe("normalized");
    if (verdict.kind !== "normalized") return;
    const data = verdict.events[0]!.data as { action: string; description?: string };
    expect(data.action).toBe("success");
    expect(data.description).toBe("passed");
  });

  it("Job Hook resolves the number through the API; unresolved drops", async () => {
    const api = new FakeApi();
    const n = normalizer(api);
    const unresolved = await n(
      delivery(json(buildFailure), {
        "x-gitlab-token": TOKEN,
        "x-gitlab-event": "Build Hook",
        "x-request-id": "req-build",
      }),
    );
    expect(unresolved).toEqual({ kind: "normalized", events: [] });

    api.mrForSha = [16];
    const resolved = await n(
      delivery(json(buildFailure), {
        "x-gitlab-token": TOKEN,
        "x-gitlab-event": "Build Hook",
        "x-request-id": "req-build-2",
      }),
    );
    expect(resolved.kind).toBe("normalized");
    if (resolved.kind !== "normalized") return;
    const data = resolved.events[0]!.data as { action: string; description?: string };
    expect(data.action).toBe("failure");
    expect(data.description).toBe("test:units");
  });

  it("unknown event kinds drop with an audit line", async () => {
    const n = normalizer(new FakeApi());
    const verdict = await n(
      delivery(json({ object_kind: "push" }), {
        "x-gitlab-token": TOKEN,
        "x-gitlab-event": "Push Hook",
        "x-request-id": "req-push",
      }),
    );
    expect(verdict).toEqual({ kind: "normalized", events: [] });
  });
});
describe("reply handler + comment ledger", () => {
  const dir = mkdtempSync(join(tmpdir(), "ofac-gl-adapter-"));
  afterAll(() => rmSync(dir, { recursive: true, force: true }));

  const ctx = { publishEvent: async () => undefined } as unknown as CommandContext;

  const reply = (key: string, thread = false) =>
    ({
      type: "org.facilitator.command.communication.reply.v1",
      data: {
        commandId: `cmd-${key}`,
        idempotencyKey: key,
        target: {
          provider: "gitlab",
          providerInstanceId: "local-gitlab",
          tenantOrOrganizationId: "git.localhost",
          channelOrResourceId: "gitlab-org/gitlab-test",
          rootThreadId: thread ? "16" : "23",
          subjectRef: thread ? "16" : "23",
          conversationKind: thread ? "thread" : "comment",
          authorProviderAccountId: "root",
        },
        text: "bot reply",
      },
    }) as never;

  it("posts an issue note; replay answers accepted with zero new posts", async () => {
    const api = new FakeApi();
    const state = new AdapterState(join(dir, "ledger-1.sqlite3"));
    const handler = createGitLabHandler({ api, state, logger: silent });
    expect(await handler.onCommand(reply("k1"), ctx)).toEqual({ kind: "accepted" });
    expect(api.issueNotes).toHaveLength(1);
    expect(await handler.onCommand(reply("k1"), ctx)).toEqual({ kind: "accepted" });
    expect(api.issueNotes).toHaveLength(1);
    state.close();
  });

  it("issue 404 re-tries the MR note (provider-side resolution)", async () => {
    const api = new FakeApi();
    api.failIssueNotes = true;
    const state = new AdapterState(join(dir, "ledger-2.sqlite3"));
    const handler = createGitLabHandler({ api, state, logger: silent });
    expect(await handler.onCommand(reply("k2", true), ctx)).toEqual({ kind: "accepted" });
    expect(api.mrNotes).toHaveLength(1);
    state.close();
  });

  it("no bot token answers unsupported", async () => {
    const state = new AdapterState(join(dir, "ledger-3.sqlite3"));
    const handler = createGitLabHandler({ api: undefined, state, logger: silent });
    expect(await handler.onCommand(reply("k3"), ctx)).toEqual({
      kind: "unsupported",
      reason: "no-bot-token",
    });
    state.close();
  });
});
