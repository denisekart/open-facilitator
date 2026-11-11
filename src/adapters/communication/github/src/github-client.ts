// GitHub REST (api.github.com v3 routes) with the bot token: the ONLY place
// that talks to GitHub (mirrors forgejo-client law). Plain global fetch;
// non-2xx answers a typed GitHubError naming the call. Everything the link
// handler, the webhook normalizer, and the connector need about GitHub is
// behind the GitHubApi interface so unit tests inject a fake (zero HTTP).
// Exported for the connector package too: one seam, so the privileged-write
// split reuses the same boundary.
import { trimSlash } from "@ofac/workspace-link";

export class GitHubError extends Error {
  constructor(
    readonly call: string,
    readonly status: number,
    detail?: string,
  ) {
    super(`github ${call} failed: HTTP ${status}${detail === undefined ? "" : ` (${detail})`}`);
    this.name = "GitHubError";
  }
}

export interface GitHubComment {
  id: number;
  html_url: string;
}

export interface GitHubPullRequest {
  number: number;
  head: { sha: string };
}

export interface GitHubIssue {
  number: number;
  state: string;
  title: string;
}

export interface GitHubPullRequestDetail {
  number: number;
  state: string;
  title: string;
  merged: boolean;
  head: { sha: string; ref: string };
}

export interface GitHubReviewComment {
  id: number;
  body: string;
  user: string;
}

export interface GitHubApi {
  /** POST issue/PR comment (issue comments and PR review-conversation
   *  comments share the issues route on GitHub). Returns the created id. */
  postComment(repo: string, number: number, body: string): Promise<GitHubComment>;
  /** Open an issue (C9: the human owner is assigned, never the bot alone).
   *  Returns the created number. */
  createIssue(repo: string, title: string, body: string, assignee?: string): Promise<number>;
  /** Close an issue or PR (the issues route serves both). */
  closeIssue(repo: string, number: number): Promise<void>;
  /** Merge a PR (the method the provider needs; squash/merge/rebase). */
  mergePr(repo: string, number: number, method: string): Promise<boolean>;
  /** React to an issue/PR comment (the comment-level route: a body-level
   *  reaction needs GraphQL, the kernel only ever reacts to a post). */
  reactToComment(repo: string, commentId: number, content: string): Promise<void>;
  /** Edit OUR comment (the update law: callers check the ledger first). */
  updateComment(repo: string, commentId: number, body: string): Promise<void>;
  /** Open a PR (connector-side privileged write; the adapter never calls it
   *  but the client module is shared). */
  openPr(
    repo: string,
    head: string,
    base: string,
    title: string,
    body: string,
  ): Promise<GitHubPullRequest>;
  /** Open PRs whose head BRANCH matches — the connector's read-before-reconcile. */
  openPrsForHead(repo: string, headRef: string): Promise<number[]>;
  /** List PRs whose head sha matches — the commit-status sha→number resolver. */
  prsForHeadSha(repo: string, sha: string): Promise<number[]>;
  /** First comment on an issue/PR whose body carries the marker — the
   *  connector's lost-response reconcile read (undefined when absent). */
  findCommentByMarker(
    repo: string,
    number: number,
    marker: string,
  ): Promise<GitHubComment | undefined>;
  /** Read an issue (connector `vcs.issue.read`). */
  getIssue(repo: string, number: number): Promise<GitHubIssue>;
  /** First BOT-created issue whose body carries the marker (the
   *  `vcs.issue.create` reconcile read: the creator list is strongly
   *  consistent, the search API is not, and a lost response must never
   *  mint a second ticket). */
  findIssueByMarker(repo: string, marker: string): Promise<number | undefined>;
  /** Read a PR (connector `vcs.change_request.read`). */
  getPullRequest(repo: string, number: number): Promise<GitHubPullRequestDetail>;
  /** Attach label NAMES to an issue (connector `vcs.issue.label`). */
  addLabels(repo: string, number: number, labels: readonly string[]): Promise<void>;
  /** Submit a review (connector `vcs.change_request.review`; event is the
   *  wire state: APPROVED | REQUEST_CHANGES | COMMENT). */
  reviewPullRequest(
    repo: string,
    number: number,
    event: string,
    body: string,
  ): Promise<{ id: number }>;
  /** List a PR's inline review comments (ids for the reply op). */
  listReviewComments(repo: string, number: number): Promise<GitHubReviewComment[]>;
  /** Reply to a review-comment thread via `in_reply_to` (connector
   *  `vcs.discussion.reply`). GitHub's create-review-comment route carries
   *  `in_reply_to` = the top-level comment id. */
  replyToReviewComment(
    repo: string,
    number: number,
    inReplyTo: number,
    body: string,
  ): Promise<GitHubComment>;
  /** Resolve a review thread over GraphQL (connector
   *  `vcs.discussion.resolve`; the NODE_ID form of the thread id). */
  resolveReviewThread(threadId: string): Promise<void>;
  /** Create a branch pointing at an existing ref's commit sha (connector
   *  `vcs.branch.push`: resolve the ref, then create-ref). */
  createBranch(repo: string, newBranch: string, oldRef: string): Promise<void>;
  /** POST a commit status (connector `vcs.pipeline.trigger`). */
  commitStatus(
    repo: string,
    sha: string,
    state: string,
    description: string,
    context: string,
  ): Promise<void>;
}

interface CommentDto {
  id: number;
  html_url: string;
}

interface IssueDto {
  number: number;
  state: string;
  title: string;
}

interface CommentListDto {
  id: number;
  html_url: string;
  body: string;
  user?: { login?: string } | undefined;
}

interface PrDto {
  number: number;
  head: { sha: string; ref?: string };
}

interface CommitDto {
  sha: string;
}

interface GraphQLErrorBody {
  errors?: { message: string }[];
}

export class GitHubClient implements GitHubApi {
  private readonly base: string;

  constructor(
    githubUrl: string,
    private readonly token: string,
    private readonly fetchImpl: typeof fetch = fetch,
  ) {
    this.base = trimSlash(githubUrl);
  }

  private async request<T>(call: string, method: string, path: string, body?: unknown): Promise<T> {
    const response = await this.fetchImpl(`${this.base}${path}`, {
      method,
      headers: {
        authorization: `Bearer ${this.token}`,
        "content-type": "application/json",
        accept: "application/vnd.github+json",
      },
      ...(body === undefined ? {} : { body: JSON.stringify(body) }),
    });
    if (!response.ok) {
      const text = await response.text().catch(() => "");
      throw new GitHubError(call, response.status, text.slice(0, 200));
    }

    if (response.status === 204) return undefined as T;
    return (await response.json()) as T;
  }

  async postComment(repo: string, number: number, body: string): Promise<GitHubComment> {
    const dto = await this.request<CommentDto>(
      "postComment",
      "POST",
      `/repos/${repo}/issues/${String(number)}/comments`,
      { body },
    );
    return { id: dto.id, html_url: dto.html_url };
  }

  async createIssue(repo: string, title: string, body: string, assignee?: string): Promise<number> {
    const dto = await this.request<IssueDto>(
      "createIssue",
      "POST",
      `/repos/${repo}/issues`,
      { title, body, ...(assignee === undefined ? {} : { assignee }) },
    );
    return dto.number;
  }

  async closeIssue(repo: string, number: number): Promise<void> {
    await this.request<void>(
      "closeIssue",
      "PATCH",
      `/repos/${repo}/issues/${String(number)}`,
      { state: "closed" },
    );
  }

  async mergePr(repo: string, number: number, method: string): Promise<boolean> {
    // GitHub answers a successful merge with 204 or 201 + {merged:true};
    // request<> tolerates the empty body, so either shape settles true.
    await this.request<{ merged?: boolean } | undefined>(
      "mergePr",
      "PUT",
      `/repos/${repo}/pulls/${String(number)}/merge`,
      { merge_method: method },
    );
    return true;
  }

  async reactToComment(repo: string, commentId: number, content: string): Promise<void> {
    await this.request<void>(
      "reactToComment",
      "POST",
      `/repos/${repo}/issues/comments/${String(commentId)}/reactions`,
      { content },
    );
  }

  async updateComment(repo: string, commentId: number, body: string): Promise<void> {
    await this.request<void>(
      "updateComment",
      "PATCH",
      `/repos/${repo}/issues/comments/${String(commentId)}`,
      { body },
    );
  }

  async openPr(
    repo: string,
    head: string,
    base: string,
    title: string,
    body: string,
  ): Promise<GitHubPullRequest> {
    const dto = await this.request<PrDto>("openPr", "POST", `/repos/${repo}/pulls`, {
      head,
      base,
      title,
      body,
    });
    return { number: dto.number, head: dto.head };
  }

  async prsForHeadSha(repo: string, sha: string): Promise<number[]> {
    const dtos = await this.request<PrDto[]>(
      "prsForHeadSha",
      "GET",
      `/repos/${repo}/pulls?state=all&per_page=50`,
    );
    return dtos.filter((pr) => pr.head?.sha === sha).map((pr) => pr.number);
  }

  async openPrsForHead(repo: string, headRef: string): Promise<number[]> {
    const dtos = await this.request<PrDto[]>(
      "openPrsForHead",
      "GET",
      `/repos/${repo}/pulls?state=open&per_page=50`,
    );
    return dtos.filter((pr) => pr.head?.ref === headRef).map((pr) => pr.number);
  }

  async findCommentByMarker(
    repo: string,
    number: number,
    marker: string,
  ): Promise<GitHubComment | undefined> {
    const dtos = await this.request<CommentListDto[]>(
      "findCommentByMarker",
      "GET",
      `/repos/${repo}/issues/${String(number)}/comments`,
    );
    const hit = dtos.find((dto) => dto.body.includes(marker));
    return hit === undefined ? undefined : { id: hit.id, html_url: hit.html_url };
  }

  async findIssueByMarker(repo: string, marker: string): Promise<number | undefined> {
    const dtos = await this.request<Array<{ number: number; body: string | null }>>(
      "findIssueByMarker",
      "GET",
      `/repos/${repo}/issues?creator=@me&state=all&per_page=100`,
    );
    const hit = dtos.find((dto) => (dto.body ?? "").includes(marker));
    return hit?.number;
  }

  async getIssue(repo: string, number: number): Promise<GitHubIssue> {
    const dto = await this.request<IssueDto>(
      "getIssue",
      "GET",
      `/repos/${repo}/issues/${String(number)}`,
    );
    return { number: dto.number, state: dto.state, title: dto.title };
  }

  async getPullRequest(repo: string, number: number): Promise<GitHubPullRequestDetail> {
    const dto = await this.request<PrDto & IssueDto & { merged?: boolean }>(
      "getPullRequest",
      "GET",
      `/repos/${repo}/pulls/${String(number)}`,
    );
    return {
      number: dto.number,
      state: dto.state,
      title: dto.title,
      merged: dto.merged === true,
      head: { sha: dto.head?.sha ?? "", ref: dto.head?.ref ?? "" },
    };
  }

  async addLabels(repo: string, number: number, labels: readonly string[]): Promise<void> {
    // POST issues/{n}/labels accepts names and answers the label array.
    await this.request<unknown>(
      "addLabels",
      "POST",
      `/repos/${repo}/issues/${String(number)}/labels`,
      { labels: [...labels] },
    );
  }

  async reviewPullRequest(
    repo: string,
    number: number,
    event: string,
    body: string,
  ): Promise<{ id: number }> {
    const dto = await this.request<{ id: number }>(
      "reviewPullRequest",
      "POST",
      `/repos/${repo}/pulls/${String(number)}/reviews`,
      { event, body },
    );
    return { id: dto.id };
  }

  async listReviewComments(repo: string, number: number): Promise<GitHubReviewComment[]> {
    const dtos = await this.request<CommentListDto[]>(
      "listReviewComments",
      "GET",
      `/repos/${repo}/pulls/${String(number)}/comments`,
    );
    return dtos.map((dto) => ({ id: dto.id, body: dto.body, user: dto.user?.login ?? "" }));
  }

  async replyToReviewComment(
    repo: string,
    number: number,
    inReplyTo: number,
    body: string,
  ): Promise<GitHubComment> {
    const dto = await this.request<CommentDto>(
      "replyToReviewComment",
      "POST",
      `/repos/${repo}/pulls/${String(number)}/comments`,
      { body, in_reply_to: inReplyTo },
    );
    return { id: dto.id, html_url: dto.html_url };
  }

  async resolveReviewThread(threadId: string): Promise<void> {
    const body = await this.request<GraphQLErrorBody>("resolveReviewThread", "POST", "/graphql", {
      query:
        "mutation($threadId: ID!) { resolveReviewThread(input: {threadId: $threadId}) { thread { isResolved } } }",
      variables: { threadId },
    });
    // GraphQL answers 200 with an `errors[]` array on a failed mutation.
    if (Array.isArray(body?.errors) && body.errors.length > 0) {
      throw new GitHubError("resolveReviewThread", 200, body.errors[0]?.message);
    }
  }

  async createBranch(repo: string, newBranch: string, oldRef: string): Promise<void> {
    // The commits route resolves any ref (branch/tag/sha) to its commit; the
    // commit DTO's top-level `sha` seeds the new ref.
    const commit = await this.request<CommitDto>(
      "createBranch",
      "GET",
      `/repos/${repo}/commits/${encodeURIComponent(oldRef)}`,
    );
    await this.request<void>("createBranch", "POST", `/repos/${repo}/git/refs`, {
      ref: `refs/heads/${newBranch}`,
      sha: commit.sha,
    });
  }

  async commitStatus(
    repo: string,
    sha: string,
    state: string,
    description: string,
    context: string,
  ): Promise<void> {
    await this.request<void>("commitStatus", "POST", `/repos/${repo}/statuses/${sha}`, {
      state,
      description,
      context,
    });
  }
}
