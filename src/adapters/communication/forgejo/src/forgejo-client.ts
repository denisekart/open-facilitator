// Forgejo REST v1 with the bot token: the ONLY place that talks to the forge
// (mirrors mm-client law). Plain global fetch; non-2xx answers a typed
// ForgejoError naming the call. Everything the link handler and the webhook
// edge need about the forge is behind the ForgejoApi interface so unit tests
// inject a fake (zero HTTP). Exported for the connector package too: one
// seam, so a later privileged-write split reuses the same boundary.
import { observeHttpStatus } from "@ofac/provider-core";
import { trimSlash } from "@ofac/workspace-link";

export class ForgejoError extends Error {
  constructor(
    readonly call: string,
    readonly status: number,
    detail?: string,
  ) {
    super(`forgejo ${call} failed: HTTP ${status}${detail === undefined ? "" : ` (${detail})`}`);
    this.name = "ForgejoError";
  }
}

export interface ForgejoComment {
  id: number;
  html_url: string;
}

export interface ForgejoPullRequest {
  number: number;
  head: { sha: string };
}

export interface ForgejoIssue {
  number: number;
  state: string;
  title: string;
}

export interface ForgejoApi {
  /** POST issue/PR comment (decision 4: bot-identity comments only).
   *  Returns the created comment id. */
  postComment(repo: string, number: number, body: string): Promise<ForgejoComment>;
  /** Open an issue (C9: assigned to the HUMAN owner). */
  createIssue(repo: string, title: string, body: string, assignee: string): Promise<number>;
  /** Close an issue or PR (the issues route serves both). */
  closeIssue(repo: string, number: number): Promise<void>;
  /** First bot-created issue whose body carries the marker (create-reconcile). */
  findIssueByMarker(repo: string, marker: string): Promise<number | undefined>;
  /** Merge a PR (connector `vcs.change_request.merge`). */
  mergePr(repo: string, number: number, method: string): Promise<boolean>;
  /** Read a PR enough to answer "already merged?" (merge-reconcile). */
  getPrState(repo: string, number: number): Promise<{ number: number; merged: boolean }>;
  /** React to an issue/PR comment (Forgejo speaks the GitHub reaction set). */
  reactToComment(repo: string, commentId: number, content: string): Promise<void>;
  /** Edit OUR comment (the update law: callers check the ledger first). */
  updateComment(repo: string, commentId: number, body: string): Promise<void>;
  /** Open a PR (connector-side privileged write; the adapter never calls it
   *  but the client module is shared, decision 6). */
  openPr(
    repo: string,
    head: string,
    base: string,
    title: string,
    body: string,
  ): Promise<ForgejoPullRequest>;
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
  ): Promise<ForgejoComment | undefined>;
  /** Read an issue (connector `issue.read`). */
  getIssue(repo: string, number: number): Promise<ForgejoIssue>;
  /** Create a branch from an existing ref (connector `vcs.branch.push`). */
  createBranch(repo: string, newBranch: string, oldRef: string): Promise<void>;
  /** POST a commit status (live CI-leg proof without an Actions runner). */
  commitStatus(
    repo: string,
    sha: string,
    state: string,
    description: string,
    context: string,
  ): Promise<void>;
  /** Attach label NAMES to an issue (connector `vcs.issue.label`; verified
   *  against the pinned 16.0.7 source: POST issues/{n}/labels accepts names). */
  addLabels(repo: string, number: number, labels: readonly string[]): Promise<void>;
  /** Read a PR (connector `vcs.change_request.read`). */
  getPullRequest(repo: string, number: number): Promise<ForgejoPullRequestDetail>;
  /** Submit a review (connector `vcs.change_request.review`; event is the
   *  wire state: APPROVED | REQUEST_CHANGES | COMMENT). */
  reviewPullRequest(
    repo: string,
    number: number,
    event: string,
    body: string,
  ): Promise<{ id: number }>;
  /** Upload an issue/PR attachment (multipart POST issues/{n}/attachments).
   *  The outbound media leg: the answer's `download_url` rides the comment
   *  markdown the reply folds the attachment into. */
  uploadIssueAttachment(
    repo: string,
    number: number,
    filename: string,
    bytes: Uint8Array,
    mediaType: string,
  ): Promise<ForgejoAttachment>;
  /** GET an ABSOLUTE forge URL (an inbound attachment's download URL) with
   *  the same token header — the media leg's download seam. */
  downloadAsset(url: string): Promise<Uint8Array>;
  /** GET the comment window of an issue/PR thread (the context leg: what
   *  answers a routed `thread://forgejo/…` fetch, the URI's `limit`). A
   *  thread here IS the issue/PR comment thread, so the window is the
   *  issue's comment list; the author is the comment author's login. */
  listIssueComments(
    repo: string,
    number: number,
    limit: number,
  ): Promise<{ author: string; body: string; created: string }[]>;
}

/** The created attachment (REST API shape, subset): the media leg reads the
 *  id as the dedupe key and the download URL for the comment markdown. */
export interface ForgejoAttachment {
  id: number;
  url: string;
  download_url: string;
}

export interface ForgejoPullRequestDetail {
  number: number;
  state: string;
  title: string;
  merged: boolean;
  head: { sha: string; ref: string };
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
}

/** The context leg's window DTO (subset): the author's login, the body and
 *  the creation stamp the issue-comments route answers with. */
interface CommentWindowDto {
  body?: string;
  created_at?: string;
  user?: { login?: string };
}

interface PrDto {
  number: number;
  head: { sha: string; ref?: string };
}

export class ForgejoClient implements ForgejoApi {
  private readonly base: string;

  constructor(
    forgejoUrl: string,
    private readonly token: string,
    private readonly fetchImpl: typeof fetch = fetch,
  ) {
    this.base = trimSlash(forgejoUrl) + "/api/v1";
  }

  private async request<T>(call: string, method: string, path: string, body?: unknown): Promise<T> {
    const response = await this.fetchImpl(`${this.base}${path}`, {
      method,
      headers: {
        authorization: `token ${this.token}`,
        "content-type": "application/json",
      },
      ...(body === undefined ? {} : { body: JSON.stringify(body) }),
    });
    if (!response.ok) {
      const text = await response.text().catch(() => "");
      observeHttpStatus(response.status);
      throw new ForgejoError(call, response.status, text.slice(0, 200));
    }

    if (response.status === 204 || response.status === 205) {
      // 205: the label-add route answers Reset-Content (pinned 16.0.7 source).
      return undefined as T;
    }

    return (await response.json()) as T;
  }

  async postComment(repo: string, number: number, body: string): Promise<ForgejoComment> {
    const dto = await this.request<CommentDto>(
      "postComment",
      "POST",
      `/repos/${repo}/issues/${number}/comments`,
      { body },
    );
    return { id: dto.id, html_url: dto.html_url };
  }

  async createIssue(repo: string, title: string, body: string, assignee: string): Promise<number> {
    const dto = await this.request<CommentDto & { number: number }>(
      "createIssue",
      "POST",
      `/repos/${repo}/issues`,
      { title, body, assignee },
    );
    return dto.number;
  }

  async closeIssue(repo: string, number: number): Promise<void> {
    await this.request<void>("closeIssue", "PATCH", `/repos/${repo}/issues/${number}`, {
      state: "closed",
    });
  }

  async findIssueByMarker(repo: string, marker: string): Promise<number | undefined> {
    const dtos = await this.request<Array<{ number: number; body: string | null }>>(
      "findIssueByMarker",
      "GET",
      `/repos/${repo}/issues?creator=@me&state=all&limit=100`,
    );
    const hit = dtos.find((dto) => (dto.body ?? "").includes(marker));
    return hit?.number;
  }

  async mergePr(repo: string, number: number, method: string): Promise<boolean> {
    await this.request<{ merged?: boolean } | undefined>(
      "mergePr",
      "POST",
      `/repos/${repo}/pulls/${number}/merge`,
      { Do: method === "squash" ? "squash" : method === "rebase" ? "rebase" : "merge" },
    );
    return true;
  }

  async getPrState(repo: string, number: number): Promise<{ number: number; merged: boolean }> {
    const dto = await this.request<{ number: number; merged?: boolean; mergeable?: boolean }>(
      "getPrState",
      "GET",
      `/repos/${repo}/pulls/${number}`,
    );
    return { number: dto.number, merged: dto.merged === true };
  }

  async reactToComment(repo: string, commentId: number, content: string): Promise<void> {
    await this.request<void>(
      "reactToComment",
      "POST",
      `/repos/${repo}/issues/comments/${commentId}/reactions`,
      { content },
    );
  }

  async updateComment(repo: string, commentId: number, body: string): Promise<void> {
    await this.request<void>(
      "updateComment",
      "PATCH",
      `/repos/${repo}/issues/comments/${commentId}`,
      { body },
    );
  }

  async openPr(
    repo: string,
    head: string,
    base: string,
    title: string,
    body: string,
  ): Promise<ForgejoPullRequest> {
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
      `/repos/${repo}/pulls?state=all&limit=50`,
    );
    return dtos.filter((pr) => pr.head?.sha === sha).map((pr) => pr.number);
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
  async openPrsForHead(repo: string, headRef: string): Promise<number[]> {
    const dtos = await this.request<PrDto[]>(
      "openPrsForHead",
      "GET",
      `/repos/${repo}/pulls?state=open&limit=50`,
    );
    // Forgejo head refs are `owner:branch` for forks; plain branch otherwise.
    return dtos.filter((pr) => pr.head?.ref === headRef).map((pr) => pr.number);
  }

  async getIssue(repo: string, number: number): Promise<ForgejoIssue> {
    const dto = await this.request<IssueDto>("getIssue", "GET", `/repos/${repo}/issues/${number}`);
    return { number: dto.number, state: dto.state, title: dto.title };
  }

  async createBranch(repo: string, newBranch: string, oldRef: string): Promise<void> {
    await this.request<void>("createBranch", "POST", `/repos/${repo}/refs/branches`, {
      new_branch_name: newBranch,
      old_ref_name: oldRef,
    });
  }

  async findCommentByMarker(
    repo: string,
    number: number,
    marker: string,
  ): Promise<ForgejoComment | undefined> {
    const dtos = await this.request<CommentListDto[]>(
      "findCommentByMarker",
      "GET",
      `/repos/${repo}/issues/${number}/comments`,
    );
    const hit = dtos.find((dto) => dto.body.includes(marker));
    return hit === undefined ? undefined : { id: hit.id, html_url: hit.html_url };
  }

  async addLabels(repo: string, number: number, labels: readonly string[]): Promise<void> {
    await this.request<void>("addLabels", "POST", `/repos/${repo}/issues/${number}/labels`, {
      labels: [...labels],
    });
  }

  async getPullRequest(repo: string, number: number): Promise<ForgejoPullRequestDetail> {
    const dto = await this.request<ForgejoPullRequestDetail>(
      "getPullRequest",
      "GET",
      `/repos/${repo}/pulls/${number}`,
    );
    return {
      number: dto.number,
      state: dto.state,
      title: dto.title,
      merged: dto.merged === true,
      head: { sha: dto.head?.sha ?? "", ref: dto.head?.ref ?? "" },
    };
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
      `/repos/${repo}/pulls/${number}/reviews`,
      { event, body },
    );
    return { id: dto.id };
  }

  /** The FormData sibling of `request`: same token header, same ForgejoError
   *  shaping; no content-type header — fetch mints the multipart boundary.
   *  `absolute` paths bypass the /api/v1 base (downloadAsset's law). */
  private async requestForm(
    call: string,
    method: string,
    path: string,
    body: FormData,
  ): Promise<unknown> {
    const response = await this.fetchImpl(path.startsWith("http") ? path : `${this.base}${path}`, {
      method,
      headers: { authorization: `token ${this.token}` },
      body,
    });
    if (!response.ok) {
      const text = await response.text().catch(() => "");
      observeHttpStatus(response.status);
      throw new ForgejoError(call, response.status, text.slice(0, 200));
    }
    return response.status === 204 || response.status === 205 ? undefined : response.json();
  }

  async uploadIssueAttachment(
    repo: string,
    number: number,
    filename: string,
    bytes: Uint8Array,
    mediaType: string,
  ): Promise<ForgejoAttachment> {
    const form = new FormData();
    form.append("file", new Blob([new Uint8Array(bytes)], { type: mediaType }), filename);
    const dto = (await this.requestForm(
      "uploadIssueAttachment",
      "POST",
      `/repos/${repo}/issues/${number}/attachments`,
      form,
    )) as Partial<ForgejoAttachment>;
    return { id: dto.id ?? 0, url: dto.url ?? "", download_url: dto.download_url ?? "" };
  }

  async downloadAsset(url: string): Promise<Uint8Array> {
    const response = await this.fetchImpl(url, {
      headers: { authorization: `token ${this.token}` },
      redirect: "follow",
    });
    if (!response.ok) {
      const text = await response.text().catch(() => "");
      observeHttpStatus(response.status);
      throw new ForgejoError("downloadAsset", response.status, text.slice(0, 200));
    }
    return new Uint8Array(await response.arrayBuffer());
  }

  async listIssueComments(
    repo: string,
    number: number,
    limit: number,
  ): Promise<{ author: string; body: string; created: string }[]> {
    const dtos = await this.request<CommentWindowDto[]>(
      "listIssueComments",
      "GET",
      `/repos/${repo}/issues/${number}/comments?limit=${limit}`,
    );
    return dtos.map((dto) => ({
      author: dto.user?.login ?? "unknown",
      body: dto.body ?? "",
      created: dto.created_at ?? "",
    }));
  }
}
