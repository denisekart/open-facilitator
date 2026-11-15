// GitHub REST (api.github.com v3 routes) with the bot token: the ONLY place
// that talks to GitHub (mirrors forgejo-client law). Plain global fetch;
// non-2xx answers a typed GitHubError naming the call. Everything the link
// handler, the webhook normalizer, and the connector need about GitHub is
// behind the GitHubApi interface so unit tests inject a fake (zero HTTP).
// Exported for the connector package too: one seam, so the privileged-write
// split reuses the same boundary.
import { observeHttpStatus } from "@ofac/provider-core";
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
  /** Upload an image into an issue/PR conversation (the outbound media leg):
   *  the issue-image route on the UPLOADS host (`uploads.github.com` on the
   *  public API, the same host on GHE), the raw bytes as the body. The
   *  answer's `asset_url` rides the comment markdown the reply folds the
   *  image into. */
  uploadIssueImage(
    repo: string,
    number: number,
    filename: string,
    bytes: Uint8Array,
    mediaType: string,
  ): Promise<GitHubAsset>;
  /** GET an ABSOLUTE asset URL (an inbound markdown image URL on the
   *  uploads/assets host) with the same token header — the media leg's
   *  download seam. */
  downloadAsset(url: string): Promise<Uint8Array>;
  /** GET the comment window of an issue/PR thread (the context leg: what
   *  answers a routed `thread://github/…` fetch, the URI's `limit`). A
   *  thread here IS the issue/PR comment thread, so the window is the
   *  issue's comment list; the author is the comment author's login. */
  listIssueComments(
    repo: string,
    number: number,
    limit: number,
  ): Promise<{ author: string; body: string; created: string }[]>;
}

/** The created issue image (REST API shape, subset): the media leg folds the
 *  absolute `asset_url` into the comment markdown. */
export interface GitHubAsset {
  asset_url: string;
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

/** The context leg's window DTO (subset): the author's login, the body and
 *  the creation stamp the issue-comments route answers with. */
interface CommentWindowDto {
  body?: string;
  created_at?: string;
  user?: { login?: string };
}

interface AssetDto {
  asset_url?: string;
}

/** The context leg's window ceiling (GitHub's own page limit): what
 *  `listIssueComments` caps the URI's `limit` at. */
const CONTEXT_WINDOW_MAX = 50;

/** Derive the issue-image UPLOADS base from the configured REST base. The
 *  public API serves uploads on its OWN host (api.github.com →
 *  uploads.github.com, the same `/repos/…/issues/…/images` path law); every
 *  other install (GHE, a test fake) serves that route on the SAME host under
 *  the SAME base, so the REST base is the uploads base there. */
export function deriveUploadsBase(githubUrl: string): string {
  let url: URL;
  try {
    url = new URL(githubUrl);
  } catch {
    throw new Error(`GITHUB_URL: '${githubUrl}' is not a URL`);
  }

  if (url.hostname === "api.github.com") {
    return "https://uploads.github.com";
  }

  return trimSlash(githubUrl);
}

export class GitHubClient implements GitHubApi {
  private readonly base: string;
  /** The issue-image UPLOADS base (media leg): `uploads.github.com` for the
   *  public API, the same host/base for a GHE install (deriveUploadsBase). */
  private readonly uploadsBase: string;

  constructor(
    githubUrl: string,
    private readonly token: string,
    private readonly fetchImpl: typeof fetch = fetch,
  ) {
    this.base = trimSlash(githubUrl);
    this.uploadsBase = deriveUploadsBase(githubUrl);
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
      observeHttpStatus(response.status);
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
    const dto = await this.request<IssueDto>("createIssue", "POST", `/repos/${repo}/issues`, {
      title,
      body,
      ...(assignee === undefined ? {} : { assignee }),
    });
    return dto.number;
  }

  async closeIssue(repo: string, number: number): Promise<void> {
    await this.request<void>("closeIssue", "PATCH", `/repos/${repo}/issues/${String(number)}`, {
      state: "closed",
    });
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

  /** The raw-body sibling of `request`: the media leg's upload POST. Same
   *  token header, same GitHubError shaping; NO json content-type — the
   *  caller's media type rides, with the filename as the form `name`
   *  parameter (GitHub's uploads law for a non-multipart body). `absolute`
   *  urls bypass the REST base (the uploads host is a different origin). */
  private async requestForm(
    call: string,
    method: string,
    url: string,
    body: BodyInit,
    contentType: string,
  ): Promise<unknown> {
    const response = await this.fetchImpl(url, {
      method,
      headers: {
        authorization: `Bearer ${this.token}`,
        "content-type": contentType,
        accept: "application/vnd.github+json",
      },
      body,
    });
    if (!response.ok) {
      const text = await response.text().catch(() => "");
      observeHttpStatus(response.status);
      throw new GitHubError(call, response.status, text.slice(0, 200));
    }

    if (response.status === 204) return undefined;
    return (await response.json()) as unknown;
  }

  async uploadIssueImage(
    repo: string,
    number: number,
    filename: string,
    bytes: Uint8Array,
    mediaType: string,
  ): Promise<GitHubAsset> {
    // GitHub's issue-image route takes the RAW bytes as the body (not
    // multipart) with the media type as content-type and the filename as the
    // form `name` parameter; it lives on the uploads base, not the REST base.
    const dto = (await this.requestForm(
      "uploadIssueImage",
      "POST",
      `${this.uploadsBase}/repos/${repo}/issues/${String(number)}/images`,
      new Uint8Array(bytes),
      `${mediaType}; name="${filename.replace(/"/g, "")}"`,
    )) as AssetDto | undefined;
    return { asset_url: dto?.asset_url ?? "" };
  }

  async downloadAsset(url: string): Promise<Uint8Array> {
    const response = await this.fetchImpl(url, {
      headers: {
        authorization: `Bearer ${this.token}`,
        accept: "application/octet-stream",
      },
      redirect: "follow",
    });
    if (!response.ok) {
      const text = await response.text().catch(() => "");
      observeHttpStatus(response.status);
      throw new GitHubError("downloadAsset", response.status, text.slice(0, 200));
    }

    return new Uint8Array(await response.arrayBuffer());
  }

  async listIssueComments(
    repo: string,
    number: number,
    limit: number,
  ): Promise<{ author: string; body: string; created: string }[]> {
    const capped = Math.min(Math.max(Math.trunc(limit), 1), CONTEXT_WINDOW_MAX);
    const dtos = await this.request<CommentWindowDto[]>(
      "listIssueComments",
      "GET",
      `/repos/${repo}/issues/${String(number)}/comments?per_page=${String(capped)}`,
    );
    return dtos.slice(0, capped).map((dto) => ({
      author: dto.user?.login ?? "unknown",
      body: dto.body ?? "",
      created: dto.created_at ?? "",
    }));
  }
}
