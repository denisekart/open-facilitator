// A minimal in-process GitHub: enough REST surface for the op law tests and
// the e2e (routes mirror the real client paths, repo = `owner/name` rides as
// two path segments). `loseNextPrResponse` toggles the ambiguous-timeout
// behavior (the POST answers 504 AFTER the PR is created server-side, i.e.
// the mutation landed but the answer was lost). When `webhook` is configured
// the fake POSTs a signed `pull_request` opened delivery to that sink on
// every PR creation — the e2e's loop-closer (the kernel's head-branch
// resolver needs the event; real GitHub does exactly this).
import { createHmac, randomUUID } from "node:crypto";
import { createServer, type IncomingMessage, type Server, type ServerResponse } from "node:http";
import type { AddressInfo } from "node:net";

interface FakePr {
  number: number;
  head: { sha: string; ref: string };
  base: string;
  title: string;
  state: "open" | "closed";
  merged: boolean;
}

interface FakeComment {
  id: number;
  html_url: string;
  body: string;
}

interface FakeReviewComment {
  id: number;
  pull_request_number: number;
  in_reply_to_id: number | null;
  body: string;
  user: string;
}

export interface FakeGithubOptions {
  /** When set, PR creation POSTs a signed pull_request-opened webhook here. */
  webhook?: { url: string; secret: string };
}

export interface FakeGithub {
  url: string;
  prs: FakePr[];
  comments: FakeComment[];
  reviewComments: FakeReviewComment[];
  statuses: { sha: string; state: string; context: string }[];
  branches: { name: string; sha: string }[];
  /** issue key `<repo>#<n>` → label names GitHub holds. */
  labels: Map<string, string[]>;
  reviews: { repo: string; number: number; event: string; body: string }[];
  resolvedThreads: string[];
  /** Webhook deliveries the fake itself fired (PR-created events). */
  webhookPosts: { event: string; deliveryId: string; body: string }[];
  /** When set, the NEXT pr-create POST answers 504 after creating the row. */
  loseNextPrResponse: boolean;
  /** Counts POSTs so tests prove zero-double-fire. */
  prPosts: number;
  commentPosts: number;
  replyPosts: number;
  labelPosts: number;
  reviewPosts: number;
  statusPosts: number;
  /** Clears rows AND sequence counters (test isolation). */
  reset(): void;
  close(): Promise<void>;
}

const REPO_SEG = 2; // `owner/name` occupies two path segments under /repos/

export async function startFakeGithub(options: FakeGithubOptions = {}): Promise<FakeGithub> {
  let nextPr = 1;
  let nextComment = 1;
  let nextReview = 1;

  const github: FakeGithub = {
    url: "",
    prs: [],
    comments: [],
    reviewComments: [],
    statuses: [],
    branches: [],
    labels: new Map(),
    reviews: [],
    resolvedThreads: [],
    webhookPosts: [],
    loseNextPrResponse: false,
    prPosts: 0,
    commentPosts: 0,
    replyPosts: 0,
    labelPosts: 0,
    reviewPosts: 0,
    statusPosts: 0,
    reset() {
      github.prs.length = 0;
      github.comments.length = 0;
      github.reviewComments.length = 0;
      github.statuses.length = 0;
      github.branches.length = 0;
      github.labels.clear();
      github.reviews.length = 0;
      github.resolvedThreads.length = 0;
      github.webhookPosts.length = 0;
      github.loseNextPrResponse = false;
      github.prPosts = 0;
      github.commentPosts = 0;
      github.replyPosts = 0;
      github.labelPosts = 0;
      github.reviewPosts = 0;
      github.statusPosts = 0;
      nextPr = 1;
      nextComment = 1;
      nextReview = 1;
    },
    close: () => Promise.resolve(),
  };

  const json = (res: ServerResponse, status: number, body: unknown): void => {
    const text = JSON.stringify(body);
    res.writeHead(status, { "content-type": "application/json" });
    res.end(text);
  };

  const readBody = async (req: IncomingMessage): Promise<string> => {
    const chunks: Buffer[] = [];
    for await (const chunk of req) chunks.push(chunk as Buffer);
    return Buffer.concat(chunks).toString("utf8");
  };

  const firePrWebhook = (pr: FakePr, repo: string): void => {
    if (options.webhook === undefined) return;
    const body = JSON.stringify({
      action: "opened",
      number: pr.number,
      pull_request: {
        number: pr.number,
        state: pr.state,
        title: pr.title,
        merged: false,
        head: { ref: pr.head.ref, sha: pr.head.sha },
        base: { ref: pr.base },
      },
      repository: { full_name: repo },
      sender: { login: "ofac-bot" },
    });
    const deliveryId = `fake-pr-${String(pr.number)}-${randomUUID()}`;
    github.webhookPosts.push({ event: "pull_request", deliveryId, body });
    const signature = `sha256=${createHmac("sha256", options.webhook.secret)
      .update(body, "utf8")
      .digest("hex")}`;
    void fetch(options.webhook.url, {
      method: "POST",
      headers: {
        "content-type": "application/json",
        "x-github-event": "pull_request",
        "x-github-delivery": deliveryId,
        "x-hub-signature-256": signature,
      },
      body,
    }).catch(() => undefined);
  };

  async function handle(req: IncomingMessage, res: ServerResponse): Promise<void> {
    const url = new URL(req.url ?? "/", "http://fake");
    const path = url.pathname;
    const method = req.method ?? "GET";
    const seg = path.split("/").filter((s) => s !== "");

    // POST /graphql — resolveReviewThread mutation.
    if (method === "POST" && path === "/graphql") {
      const parsed = JSON.parse(await readBody(req)) as {
        query?: string;
        variables?: { threadId?: string };
      };
      const threadId = parsed.variables?.["threadId"] ?? "";
      if (!String(parsed.query ?? "").includes("resolveReviewThread") || threadId === "") {
        json(res, 200, { errors: [{ message: "unsupported mutation" }] });
        return;
      }

      github.resolvedThreads.push(threadId);
      json(res, 200, { data: { resolveReviewThread: { thread: { isResolved: true } } } });
      return;
    }

    if (seg[0] !== "repos" || seg.length <= REPO_SEG) {
      json(res, 404, { message: "Not Found" });
      return;
    }
    const repo = `${seg[1]}/${seg[2]}`;
    const rest = seg.slice(REPO_SEG + 1);

    // /repos/{owner}/{repo}/…
    if (rest[0] === "commits" && method === "GET") {
      // Any known ref (branch/sha) resolves; the fake mints a stable sha per
      // branch name, unknown refs answer a deterministic sha (GitHub's
      // "No commit found" 404 is unreachable when the caller seeded a branch).
      const ref = decodeURIComponent(rest[1] ?? "");
      const branch = github.branches.find((b) => b.name === ref);
      const pr = github.prs.find((p) => p.head.ref === ref);
      const sha = branch?.sha ?? pr?.head.sha ?? `sha-of-${ref}`;
      json(res, 200, { sha });
      return;
    }

    if (rest[0] === "git" && rest[1] === "refs" && method === "POST") {
      const body = JSON.parse(await readBody(req)) as { ref?: string; sha?: string };
      const name = (body.ref ?? "").replace(/^refs\/heads\//, "");
      if (github.branches.some((b) => b.name === name)) {
        // GitHub answers create-ref conflicts as 422 "Reference already exists".
        json(res, 422, { message: "Reference already exists" });
        return;
      }

      github.branches.push({ name, sha: body.sha ?? "" });
      json(res, 201, { ref: body.ref, object: { sha: body.sha ?? "" } });
      return;
    }

    if (rest[0] === "statuses" && method === "POST") {
      const body = JSON.parse(await readBody(req)) as { state?: string; context?: string };
      github.statuses.push({
        sha: rest[1] ?? "",
        state: body.state ?? "",
        context: body.context ?? "",
      });
      github.statusPosts += 1;
      json(res, 201, { id: github.statuses.length });
      return;
    }

    if (rest[0] === "pulls" && method === "POST" && rest.length === 1) {
      const body = JSON.parse(await readBody(req)) as {
        head?: string;
        base?: string;
        title?: string;
        body?: string;
      };
      github.prPosts += 1;
      const pr: FakePr = {
        number: nextPr++,
        head: { sha: `sha-${randomUUID().slice(0, 8)}`, ref: body.head ?? "" },
        base: body.base ?? "main",
        title: body.title ?? "",
        state: "open",
        merged: false,
      };
      github.prs.push(pr);
      firePrWebhook(pr, repo);
      if (github.loseNextPrResponse) {
        github.loseNextPrResponse = false;
        json(res, 504, { message: "gateway timeout (mutation landed)" });
        return;
      }

      json(res, 201, {
        number: pr.number,
        state: pr.state,
        title: pr.title,
        merged: false,
        head: pr.head,
        base: { ref: pr.base },
      });
      return;
    }

    if (rest[0] === "pulls" && rest.length === 1 && method === "GET") {
      const state = url.searchParams.get("state") ?? "open";
      const all = github.prs.filter((pr) => state === "all" || pr.state === state);
      json(
        res,
        200,
        all.map((pr) => ({
          number: pr.number,
          state: pr.state,
          title: pr.title,
          merged: pr.merged,
          head: pr.head,
          base: { ref: pr.base },
        })),
      );
      return;
    }

    if (rest[0] === "pulls" && rest.length >= 2) {
      const number = Number(rest[1]);
      const pr = github.prs.find((p) => p.number === number);
      if (pr === undefined) {
        json(res, 404, { message: "Not Found" });
        return;
      }

      if (rest[2] === undefined && method === "GET") {
        json(res, 200, {
          number: pr.number,
          state: pr.state,
          title: pr.title,
          merged: pr.merged,
          head: pr.head,
          base: { ref: pr.base },
        });
        return;
      }

      if (rest[2] === "reviews" && method === "POST") {
        const body = JSON.parse(await readBody(req)) as { event?: string; body?: string };
        github.reviewPosts += 1;
        github.reviews.push({ repo, number, event: body.event ?? "", body: body.body ?? "" });
        json(res, 201, { id: nextReview++ });
        return;
      }

      if (rest[2] === "comments" && method === "GET") {
        json(
          res,
          200,
          github.reviewComments
            .filter((c) => c.pull_request_number === number)
            .map((c) => ({
              id: c.id,
              body: c.body,
              user: { login: c.user },
              in_reply_to_id: c.in_reply_to_id,
            })),
        );
        return;
      }

      if (rest[2] === "comments" && method === "POST") {
        const body = JSON.parse(await readBody(req)) as { body?: string; in_reply_to?: number };
        github.replyPosts += 1;
        const comment: FakeReviewComment = {
          id: nextComment++,
          pull_request_number: number,
          in_reply_to_id: body.in_reply_to ?? null,
          body: body.body ?? "",
          user: "ofac-bot",
        };
        github.reviewComments.push(comment);
        json(res, 201, { id: comment.id, html_url: `${github.url}#${String(comment.id)}` });
        return;
      }
    }

    if (rest[0] === "issues") {
      const number = Number(rest[1]);
      if (rest[2] === undefined && method === "GET") {
        json(res, 200, {
          number,
          state: "open",
          title: `issue ${String(number)}`,
          labels: [...(github.labels.get(`${repo}#${String(number)}`) ?? [])].map((name) => ({
            name,
          })),
        });
        return;
      }

      if (rest[2] === "comments" && method === "GET") {
        json(
          res,
          200,
          github.comments.filter((c) => c.html_url.endsWith(`/${String(number)}/comments`)),
        );
        return;
      }

      if (rest[2] === "comments" && method === "POST") {
        const body = JSON.parse(await readBody(req)) as { body?: string };
        github.commentPosts += 1;
        const comment: FakeComment = {
          id: nextComment++,
          html_url: `${github.url}/repos/${repo}/issues/${String(number)}/comments`,
          body: body.body ?? "",
        };
        github.comments.push(comment);
        json(res, 201, { id: comment.id, html_url: comment.html_url });
        return;
      }

      if (rest[2] === "labels" && method === "POST") {
        const body = JSON.parse(await readBody(req)) as { labels?: string[] };
        github.labelPosts += 1;
        const key = `${repo}#${String(number)}`;
        const held = new Set(github.labels.get(key) ?? []);
        for (const label of body.labels ?? []) held.add(label);
        github.labels.set(key, [...held]);
        json(
          res,
          200,
          [...held].map((name) => ({ name })),
        );
        return;
      }
    }

    json(res, 404, { message: `fake-github: no route ${method} ${path}` });
  }

  const server: Server = createServer((req, res) => {
    handle(req, res).catch((error: unknown) => {
      json(res, 500, { message: String(error) });
    });
  });

  await new Promise<void>((resolve) => server.listen(0, "127.0.0.1", resolve));
  github.url = `http://127.0.0.1:${String((server.address() as AddressInfo).port)}`;
  github.close = () => new Promise<void>((resolve) => server.close(() => resolve()));
  return github;
}
