// A minimal in-process GitLab: enough v4 REST surface for the op-law tests
// and the e2e. Routes mirror the real client paths (project id = the
// URL-encoded `group/proj` path); `loseNextMrResponse` toggles the
// ambiguous-timeout behavior (the POST answers 504 AFTER the MR is created
// server-side, i.e. the mutation landed but the answer was lost) — the
// fake-forge law.
import { createServer, type IncomingMessage, type Server, type ServerResponse } from "node:http";
import type { AddressInfo } from "node:net";

interface FakeMr {
  iid: number;
  source_branch: string;
  target_branch: string;
  title: string;
  state: "opened" | "closed" | "merged";
  sha: string;
}

interface FakeNote {
  id: number;
  web_url: string;
  body: string;
  mr: boolean;
  iid: number;
}

interface FakeDiscussion {
  id: string;
  resolved: boolean;
  replies: string[];
}

export interface FakeGitLab {
  url: string;
  mrs: FakeMr[];
  notes: FakeNote[];
  statuses: { sha: string; state: string; name: string }[];
  branches: string[];
  /** issue key `<repo>#<iid>` → label titles the instance holds. */
  labels: Map<string, string[]>;
  approvals: { repo: string; iid: number }[];
  reviews: { repo: string; iid: number; status: string }[];
  discussions: Map<string, FakeDiscussion>;
  /** When set, the NEXT mr-create POST answers 504 after creating the row. */
  loseNextMrResponse: boolean;
  /** Counts POSTs so tests prove zero-double-fire. */
  mrPosts: number;
  notePosts: number;
  labelPuts: number;
  approvePosts: number;
  reviewPosts: number;
  discussionPosts: number;
  discussionPuts: number;
  /** Seeds a discussion thread (e2e reply/resolve legs). */
  seedDiscussion(repo: string, iid: number, id: string): void;
  /** Clears rows AND sequence counters (test isolation). */
  reset(): void;
  close(): Promise<void>;
}

export async function startFakeGitLab(): Promise<FakeGitLab> {
  let nextMr = 1;
  let nextNote = 1;
  let nextReview = 1;

  const gitlab: FakeGitLab = {
    url: "",
    mrs: [],
    notes: [],
    statuses: [],
    branches: ["main"],
    labels: new Map(),
    approvals: [],
    reviews: [],
    discussions: new Map(),
    loseNextMrResponse: false,
    mrPosts: 0,
    notePosts: 0,
    labelPuts: 0,
    approvePosts: 0,
    reviewPosts: 0,
    discussionPosts: 0,
    discussionPuts: 0,
    seedDiscussion: (repo, iid, id) => {
      gitlab.discussions.set(`${repo}!${iid}:${id}`, { id, resolved: false, replies: [] });
    },
    reset: () => {
      gitlab.mrs.length = 0;
      gitlab.notes.length = 0;
      gitlab.statuses.length = 0;
      gitlab.branches.length = 1;
      gitlab.labels.clear();
      gitlab.approvals.length = 0;
      gitlab.reviews.length = 0;
      gitlab.discussions.clear();
      gitlab.mrPosts = 0;
      gitlab.notePosts = 0;
      gitlab.labelPuts = 0;
      gitlab.approvePosts = 0;
      gitlab.reviewPosts = 0;
      gitlab.discussionPosts = 0;
      gitlab.discussionPuts = 0;
      gitlab.loseNextMrResponse = false;
      nextMr = 1;
      nextNote = 1;
      nextReview = 1;
    },
    close: async () => undefined,
  };

  const json = (res: ServerResponse, status: number, body: unknown): void => {
    res.writeHead(status, { "content-type": "application/json" });
    res.end(JSON.stringify(body));
  };

  const server: Server = createServer((req: IncomingMessage, res) => {
    const url = new URL(req.url ?? "/", "http://fake");
    // Decode FIRST: an encoded `group%2Fproj` must re-split into two parts.
    const parts = decodeURIComponent(url.pathname)
      .split("/")
      .filter((p) => p !== "");
    const bodyParts: Buffer[] = [];
    req.on("data", (c: Buffer) => bodyParts.push(c));
    req.on("end", () => {
      const body = Buffer.concat(bodyParts).toString("utf8");
      // /api/v4/projects/<group>/<proj>/...
      if (
        parts[0] !== "api" ||
        parts[1] !== "v4" ||
        parts[2] !== "projects" ||
        parts[3] === undefined ||
        parts[4] === undefined
      ) {
        json(res, 404, { message: "no route" });
        return;
      }

      const repo = `${parts[3]}/${parts[4]}`;
      const rest = parts.slice(5);
      const input = (): Record<string, unknown> =>
        body === "" ? {} : (JSON.parse(body) as Record<string, unknown>);

      if (rest[0] === "merge_requests" && rest[1] === undefined && req.method === "GET") {
        const state = url.searchParams.get("state") ?? "opened";
        json(
          res,
          200,
          gitlab.mrs
            .filter((mr) => (state === "all" ? true : mr.state === state))
            .map((mr) => ({
              iid: mr.iid,
              state: mr.state,
              title: mr.title,
              source_branch: mr.source_branch,
              merged: mr.state === "merged",
              last_commit: { id: mr.sha },
            })),
        );
        return;
      }

      if (
        rest[0] === "merge_requests" &&
        rest[1] !== undefined &&
        rest[2] === undefined &&
        req.method === "GET"
      ) {
        const mr = gitlab.mrs.find((m) => m.iid === Number(rest[1]));
        if (mr === undefined) {
          json(res, 404, { message: "no MR" });
          return;
        }

        json(res, 200, {
          iid: mr.iid,
          state: mr.state,
          title: mr.title,
          merged: mr.state === "merged",
          source_branch: mr.source_branch,
          last_commit: { id: mr.sha },
        });
        return;
      }

      if (rest[0] === "merge_requests" && rest[1] !== undefined && rest[2] === "approve") {
        gitlab.approvePosts += 1;
        gitlab.approvals.push({ repo, iid: Number(rest[1]) });
        json(res, 201, { id: Number(rest[1]), approved: true });
        return;
      }

      if (rest[0] === "merge_requests" && rest[1] !== undefined && rest[2] === "reviews") {
        gitlab.reviewPosts += 1;
        const status = String(input()["status_option"] ?? "");
        gitlab.reviews.push({ repo, iid: Number(rest[1]), status });
        json(res, 201, { id: nextReview++ });
        return;
      }

      if (
        rest[0] === "merge_requests" &&
        rest[1] !== undefined &&
        rest[2] === "discussions" &&
        rest[3] !== undefined &&
        req.method === "POST"
      ) {
        gitlab.discussionPosts += 1;
        const key = `${repo}!${String(rest[1])}:${String(rest[3])}`;
        const thread = gitlab.discussions.get(key);
        const noteBody = String(input()["body"] ?? "");
        if (thread !== undefined) thread.replies.push(noteBody);
        const note: FakeNote = {
          id: nextNote,
          web_url: `http://fake/${key}#note_${String(nextNote)}`,
          body: noteBody,
          mr: true,
          iid: Number(rest[1]),
        };
        nextNote += 1;
        gitlab.notes.push(note);
        json(res, 201, { id: note.id, body: note.body });
        return;
      }

      if (
        rest[0] === "merge_requests" &&
        rest[1] !== undefined &&
        rest[2] === "discussions" &&
        rest[3] !== undefined &&
        req.method === "PUT"
      ) {
        gitlab.discussionPuts += 1;
        const key = `${repo}!${String(rest[1])}:${String(rest[3])}`;
        const thread = gitlab.discussions.get(key);
        if (thread === undefined) {
          json(res, 404, { message: "no discussion" });
          return;
        }

        thread.resolved = input()["resolved"] === true;
        json(res, 200, { id: thread.id, resolved: thread.resolved });
        return;
      }

      if (rest[0] === "merge_requests" && rest[1] !== undefined && rest[2] === "notes") {
        if (req.method === "POST") {
          gitlab.notePosts += 1;
          const note: FakeNote = {
            id: nextNote,
            web_url: `http://fake/${repo}#note_${String(nextNote)}`,
            body: String(input()["body"] ?? ""),
            mr: true,
            iid: Number(rest[1]),
          };
          nextNote += 1;
          gitlab.notes.push(note);
          json(res, 201, { id: note.id, web_url: note.web_url });
          return;
        }

        if (req.method === "GET") {
          json(
            res,
            200,
            gitlab.notes
              .filter((n) => n.mr && n.iid === Number(rest[1]))
              .map((n) => ({ id: n.id, body: n.body })),
          );
          return;
        }
      }

      if (rest[0] === "merge_requests" && req.method === "POST") {
        gitlab.mrPosts += 1;
        const created: FakeMr = {
          iid: nextMr++,
          source_branch: String(input()["source_branch"] ?? ""),
          target_branch: String(input()["target_branch"] ?? ""),
          title: String(input()["title"] ?? ""),
          state: "opened",
          sha: `sha-of-${String(input()["source_branch"] ?? "")}`,
        };
        gitlab.mrs.push(created);
        if (gitlab.loseNextMrResponse) {
          gitlab.loseNextMrResponse = false;
          json(res, 504, { message: "gateway timeout" });
          return;
        }

        json(res, 201, { iid: created.iid, source_branch: created.source_branch });
        return;
      }

      if (rest[0] === "issues" && rest[2] === "notes" && req.method === "POST") {
        gitlab.notePosts += 1;
        const note: FakeNote = {
          id: nextNote,
          web_url: `http://fake/${repo}#note_${String(nextNote)}`,
          body: String(input()["body"] ?? ""),
          mr: false,
          iid: Number(rest[1]),
        };
        nextNote += 1;
        gitlab.notes.push(note);
        json(res, 201, { id: note.id, web_url: note.web_url });
        return;
      }

      if (rest[0] === "issues" && rest[2] === "notes" && req.method === "GET") {
        json(
          res,
          200,
          gitlab.notes
            .filter((n) => !n.mr && n.iid === Number(rest[1]))
            .map((n) => ({ id: n.id, body: n.body })),
        );
        return;
      }

      if (
        rest[0] === "issues" &&
        rest[1] !== undefined &&
        rest[2] === undefined &&
        req.method === "GET"
      ) {
        json(res, 200, {
          iid: Number(rest[1]),
          state: "opened",
          title: `issue ${rest[1]} in ${repo}`,
        });
        return;
      }

      if (
        rest[0] === "issues" &&
        rest[1] !== undefined &&
        rest[2] === undefined &&
        req.method === "PUT"
      ) {
        gitlab.labelPuts += 1;
        const add = String(input()["add_labels"] ?? "")
          .split(",")
          .filter((s) => s !== "");
        const key = `${repo}#${String(rest[1])}`;
        const held = gitlab.labels.get(key) ?? [];
        gitlab.labels.set(key, [...new Set([...held, ...add])]);
        json(res, 200, { iid: Number(rest[1]), state: "opened", title: "labeled" });
        return;
      }

      if (rest[0] === "repository" && rest[1] === "branches" && req.method === "POST") {
        const branch = String(input()["branch"] ?? "");
        if (gitlab.branches.includes(branch)) {
          json(res, 409, { message: "Branch already exists" });
          return;
        }

        gitlab.branches.push(branch);
        json(res, 201, { name: branch });
        return;
      }

      if (rest[0] === "statuses" && rest[1] !== undefined && req.method === "POST") {
        const body = input();
        gitlab.statuses.push({
          sha: String(rest[1]),
          state: String(body["state"] ?? ""),
          name: String(body["name"] ?? ""),
        });
        json(res, 201, { id: gitlab.statuses.length });
        return;
      }

      json(res, 404, { message: `no route ${url.pathname}` });
    });
  });

  await new Promise<void>((resolve) => server.listen(0, "127.0.0.1", resolve));
  gitlab.url = `http://127.0.0.1:${String((server.address() as AddressInfo).port)}`;
  gitlab.close = () => new Promise<void>((resolve) => server.close(() => resolve()));
  return gitlab;
}
