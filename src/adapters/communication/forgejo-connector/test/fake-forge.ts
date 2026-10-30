// A minimal in-process forge: enough REST surface for the op law tests.
// Routes mirror the real client paths; `loseNextPrResponse` toggles the
// ambiguous-timeout behavior (the POST answers 504 AFTER the PR is created
// server-side, i.e. the mutation landed but the answer was lost).
import { createServer, type IncomingMessage, type Server, type ServerResponse } from "node:http";
import type { AddressInfo } from "node:net";

interface FakePr {
  number: number;
  head: { sha: string; ref: string };
  base: string;
  title: string;
  state: "open" | "closed";
}

interface FakeComment {
  id: number;
  html_url: string;
  body: string;
}

export interface FakeForge {
  url: string;
  prs: FakePr[];
  comments: FakeComment[];
  statuses: { sha: string; state: string; context: string }[];
  branches: string[];
  /** When set, the NEXT pr-create POST answers 504 after creating the row. */
  loseNextPrResponse: boolean;
  /** Counts POSTs so tests prove zero-double-fire. */
  prPosts: number;
  commentPosts: number;
  /** Clears rows AND sequence counters (test isolation). */
  reset(): void;
  close(): Promise<void>;
}

export async function startFakeForge(): Promise<FakeForge> {
  let nextPr = 1;
  let nextComment = 1;

  const forge: FakeForge = {
    url: "",
    prs: [],
    comments: [],
    statuses: [],
    branches: ["main"],
    loseNextPrResponse: false,
    prPosts: 0,
    commentPosts: 0,
    reset: () => {
      forge.prs.length = 0;
      forge.comments.length = 0;
      forge.statuses.length = 0;
      forge.branches.length = 1;
      forge.prPosts = 0;
      forge.commentPosts = 0;
      forge.loseNextPrResponse = false;
      nextPr = 1;
      nextComment = 1;
    },
    close: async () => undefined,
  };

  const json = (res: ServerResponse, status: number, body: unknown): void => {
    res.writeHead(status, { "content-type": "application/json" });
    res.end(JSON.stringify(body));
  };

  const server: Server = createServer((req: IncomingMessage, res) => {
    const url = new URL(req.url ?? "/", "http://fake");
    const parts = url.pathname.split("/").filter((p) => p !== "");
    const bodyParts: Buffer[] = [];
    req.on("data", (c: Buffer) => bodyParts.push(c));
    req.on("end", () => {
      const body = Buffer.concat(bodyParts).toString("utf8");
      // /api/v1/repos/<owner>/<repo>/...
      if (
        parts[0] !== "api" ||
        parts[1] !== "v1" ||
        parts[2] !== "repos" ||
        parts[3] === undefined ||
        parts[4] === undefined
      ) {
        json(res, 404, { message: "no route" });
        return;
      }

      const repo = `${parts[3]}/${parts[4]}`;
      const rest = parts.slice(5);
      if (rest[0] === "pulls" && req.method === "GET") {
        json(
          res,
          200,
          forge.prs.filter((pr) => pr.state === "open"),
        );
        return;
      }

      if (rest[0] === "pulls" && req.method === "POST") {
        forge.prPosts += 1;
        const input = JSON.parse(body) as { head: string; base: string; title: string };
        const created: FakePr = {
          number: nextPr++,
          head: { sha: `sha-of-${input.head}`, ref: input.head },
          base: input.base,
          title: input.title,
          state: "open",
        };
        forge.prs.push(created);
        if (forge.loseNextPrResponse) {
          forge.loseNextPrResponse = false;
          json(res, 504, { message: "gateway timeout" });
          return;
        }

        json(res, 201, created);
        return;
      }

      if (rest[0] === "issues" && rest[2] === "comments" && req.method === "GET") {
        json(res, 200, forge.comments);
        return;
      }

      if (rest[0] === "issues" && rest[2] === "comments" && req.method === "POST") {
        forge.commentPosts += 1;
        const input = JSON.parse(body) as { body: string };
        const created: FakeComment = {
          id: nextComment,
          html_url: `http://fake/${repo}/comments/${String(nextComment)}`,
          body: input.body,
        };
        nextComment += 1;
        forge.comments.push(created);
        json(res, 201, created);
        return;
      }

      if (
        rest[0] === "issues" &&
        rest[1] !== undefined &&
        rest[2] === undefined &&
        req.method === "GET"
      ) {
        json(res, 200, {
          number: Number(rest[1]),
          state: "open",
          title: `issue ${rest[1]} in ${repo}`,
        });
        return;
      }

      if (rest[0] === "refs" && rest[1] === "branches" && req.method === "POST") {
        const input = JSON.parse(body) as { new_branch_name: string };
        if (forge.branches.includes(input.new_branch_name)) {
          json(res, 409, { message: "already exists" });
          return;
        }

        forge.branches.push(input.new_branch_name);
        json(res, 201, { name: input.new_branch_name });
        return;
      }

      if (rest[0] === "statuses" && rest[1] !== undefined && req.method === "POST") {
        const input = JSON.parse(body) as { state: string; context: string };
        forge.statuses.push({ sha: rest[1], state: input.state, context: input.context });
        json(res, 201, {});
        return;
      }

      json(res, 404, { message: `no route ${url.pathname}` });
    });
  });

  await new Promise<void>((resolve) => server.listen(0, "127.0.0.1", resolve));
  forge.url = `http://127.0.0.1:${String((server.address() as AddressInfo).port)}`;
  forge.close = () => new Promise<void>((resolve) => server.close(() => resolve()));
  return forge;
}
