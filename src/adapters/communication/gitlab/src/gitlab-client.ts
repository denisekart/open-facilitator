// GitLab REST v4 with the bot token: the ONLY place that talks to GitLab
// (mirrors the forgejo-client law). Plain global fetch; non-2xx answers a
// typed GitLabError naming the call. Everything the link handler and the
// webhook edge need is behind the GitLabApi interface so unit tests inject a
// fake (zero HTTP). Exported for the connector package too: one seam, so a
// later privileged-write split reuses the same boundary. The `repo` inputs
// are `group/proj` paths — v4 addresses a project by its URL-encoded
// `path_with_namespace`, so every path segment here encodes the whole repo.
import { trimSlash } from "@ofac/workspace-link";

export class GitLabError extends Error {
  constructor(
    readonly call: string,
    readonly status: number,
    detail?: string,
  ) {
    super(`gitlab ${call} failed: HTTP ${status}${detail === undefined ? "" : ` (${detail})`}`);
    this.name = "GitLabError";
  }
}

export interface GitLabNote {
  id: number;
  web_url: string;
}

export interface GitLabMergeRequest {
  iid: number;
  source_branch: string;
}

export interface GitLabIssue {
  iid: number;
  state: string;
  title: string;
}

export interface GitLabMergeRequestDetail {
  iid: number;
  state: string;
  title: string;
  merged: boolean;
  sha: string;
  source_branch: string;
}

export interface GitLabApi {
  /** POST an issue note (decision 4 mirror: bot-identity notes only) or an
   *  MR note — GitLab has no unified comment endpoint, so the caller picks
   *  the domain. Returns the created note id. */
  postIssueNote(repo: string, iid: number, body: string): Promise<GitLabNote>;
  postMrNote(repo: string, iid: number, body: string): Promise<GitLabNote>;
  /** Award emoji on a note (the resource is resolved caller-side like every
   *  other two-domain GitLab verb). */
  awardIssueNoteEmoji(repo: string, iid: number, noteId: number, emoji: string): Promise<void>;
  awardMrNoteEmoji(repo: string, iid: number, noteId: number, emoji: string): Promise<void>;
  /** Edit OUR note (the update law: callers check the ledger first). */
  editIssueNote(repo: string, iid: number, noteId: number, body: string): Promise<void>;
  editMrNote(repo: string, iid: number, noteId: number, body: string): Promise<void>;
  /** Open an MR (connector-side privileged write; the adapter never calls
   *  it but the client module is shared). */
  openMr(
    repo: string,
    source: string,
    target: string,
    title: string,
    body: string,
  ): Promise<GitLabMergeRequest>;
  /** Open MRs whose SOURCE BRANCH matches — the connector's
   *  read-before-reconcile. */
  openMrsForSource(repo: string, sourceBranch: string): Promise<number[]>;
  /** MRs whose head commit matches — the pipeline-hook sha→number resolver. */
  openMrsForSha(repo: string, sha: string): Promise<number[]>;
  /** First note on an issue or MR whose body carries the marker — the
   *  connector's lost-response reconcile read (undefined when absent). */
  findNoteByMarker(
    repo: string,
    iid: number,
    marker: string,
    mr: boolean,
  ): Promise<GitLabNote | undefined>;
  /** Read an issue (connector `vcs.issue.read`). */
  getIssue(repo: string, iid: number): Promise<GitLabIssue>;
  /** Open an issue (C9: assigned to the HUMAN owner, never the bot alone). */
  createIssue(repo: string, title: string, description: string, assignee: string): Promise<number>;
  /** Close an issue (the state field is the whole mutation). */
  closeIssue(repo: string, iid: number): Promise<void>;
  /** First bot-created issue whose description carries the marker (the
   *  `vcs.issue.create` reconcile read). */
  findIssueByMarker(repo: string, marker: string): Promise<number | undefined>;
  /** Accept (merge) an MR (connector `vcs.change_request.merge`; GitLab
   *  merges asynchronously - true means the accept was accepted). */
  mergeMr(repo: string, iid: number, method: string): Promise<boolean>;
  /** Upsert label titles on an issue (connector `vcs.issue.label`; GitLab
   *  `PUT issues` with `add_labels` is a set-union upsert). */
  addLabels(repo: string, iid: number, labels: readonly string[]): Promise<void>;
  /** Read an MR (connector `vcs.change_request.read`). */
  getMergeRequest(repo: string, iid: number): Promise<GitLabMergeRequestDetail>;
  /** Create a branch from an existing ref (connector `vcs.branch.push`). */
  createBranch(repo: string, branch: string, ref: string): Promise<void>;
  /** POST a commit status (the pipeline-note leg proof without a runner). */
  commitStatus(
    repo: string,
    sha: string,
    state: string,
    description: string,
    context: string,
  ): Promise<void>;
  /** Submit a review (connector `vcs.change_request.review`; event is the
   *  wire-agnostic state APPROVED | REQUEST_CHANGES | COMMENT — approve
   *  rides POST /approve, request-changes rides POST /reviews, comment rides
   *  a plain note). GitLab has no review id on the approve route; the note /
   *  review ids answer where the route provides one. */
  reviewMergeRequest(
    repo: string,
    iid: number,
    event: string,
    body: string,
  ): Promise<{ id: number }>;
  /** Reply to an MR discussion thread (connector `vcs.discussion.reply`). */
  replyDiscussion(repo: string, iid: number, discussionId: string, body: string): Promise<string>;
  /** Resolve an MR discussion thread (connector `vcs.discussion.resolve` —
   *  GitLab has native resolve, so this op is advertised). */
  resolveDiscussion(repo: string, iid: number, discussionId: string): Promise<void>;
}

interface NoteDto {
  id: number;
  web_url: string;
}

interface IssueDto {
  iid: number;
  state: string;
  title: string;
}

interface MrDto {
  iid: number;
  state?: string;
  title?: string;
  source_branch?: string;
  merged?: boolean;
  last_commit?: { id?: string };
  sha?: string;
}

interface NoteListDto {
  id: number;
  body: string;
}

interface DiscussionDto {
  id: string;
}

export class GitLabClient implements GitLabApi {
  private readonly base: string;

  constructor(
    gitlabUrl: string,
    private readonly token: string,
    private readonly fetchImpl: typeof fetch = fetch,
  ) {
    this.base = trimSlash(gitlabUrl) + "/api/v4";
  }

  /** v4 project addressing: the URL-encoded `path_with_namespace`. */
  private project(repo: string): string {
    return encodeURIComponent(repo);
  }

  private async request<T>(call: string, method: string, path: string, body?: unknown): Promise<T> {
    const response = await this.fetchImpl(`${this.base}${path}`, {
      method,
      headers: {
        "private-token": this.token,
        "content-type": "application/json",
      },
      ...(body === undefined ? {} : { body: JSON.stringify(body) }),
    });
    if (!response.ok) {
      const text = await response.text().catch(() => "");
      throw new GitLabError(call, response.status, text.slice(0, 200));
    }

    if (response.status === 204) {
      return undefined as T;
    }

    return (await response.json()) as T;
  }

  async postIssueNote(repo: string, iid: number, body: string): Promise<GitLabNote> {
    const dto = await this.request<NoteDto>(
      "postIssueNote",
      "POST",
      `/projects/${this.project(repo)}/issues/${iid}/notes`,
      { body },
    );
    return { id: dto.id, web_url: dto.web_url };
  }

  async postMrNote(repo: string, iid: number, body: string): Promise<GitLabNote> {
    const dto = await this.request<NoteDto>(
      "postMrNote",
      "POST",
      `/projects/${this.project(repo)}/merge_requests/${iid}/notes`,
      { body },
    );
    return { id: dto.id, web_url: dto.web_url };
  }

  async createIssue(
    repo: string,
    title: string,
    description: string,
    assignee: string,
  ): Promise<number> {
    const dto = await this.request<IssueDto>(
      "createIssue",
      "POST",
      `/projects/${this.project(repo)}/issues`,
      { title, description, assignee_username: assignee },
    );
    return dto.iid;
  }

  async closeIssue(repo: string, iid: number): Promise<void> {
    await this.request<void>("closeIssue", "PUT", `/projects/${this.project(repo)}/issues/${iid}`, {
      state_event: "close",
    });
  }

  async findIssueByMarker(repo: string, marker: string): Promise<number | undefined> {
    const dtos = await this.request<Array<{ iid: number; description: string | null }>>(
      "findIssueByMarker",
      "GET",
      `/projects/${this.project(repo)}/issues?author_username=@me&state=all&per_page=100`,
    );
    const hit = dtos.find((dto) => (dto.description ?? "").includes(marker));
    return hit?.iid;
  }

  async mergeMr(repo: string, iid: number, method: string): Promise<boolean> {
    // GitLab merges asynchronously; when_sha is the strictest read-back the
    // accept response gives. `method` rides squash when the caller asked.
    const dto = await this.request<{ merged?: boolean; merge_status?: string; sha?: string }>(
      "mergeMr",
      "PUT",
      `/projects/${this.project(repo)}/merge_requests/${iid}/merge`,
      { squash: method === "squash", should_remove_source_branch: false },
    );
    return dto.merged === true || dto.merge_status === "can_be_merged" || dto.sha !== undefined;
  }

  async awardIssueNoteEmoji(
    repo: string,
    iid: number,
    noteId: number,
    emoji: string,
  ): Promise<void> {
    await this.request<void>(
      "awardIssueNoteEmoji",
      "POST",
      `/projects/${this.project(repo)}/issues/${iid}/notes/${noteId}/award_emoji`,
      { name: emoji },
    );
  }

  async awardMrNoteEmoji(repo: string, iid: number, noteId: number, emoji: string): Promise<void> {
    await this.request<void>(
      "awardMrNoteEmoji",
      "POST",
      `/projects/${this.project(repo)}/merge_requests/${iid}/notes/${noteId}/award_emoji`,
      { name: emoji },
    );
  }

  async editIssueNote(repo: string, iid: number, noteId: number, body: string): Promise<void> {
    await this.request<void>(
      "editIssueNote",
      "PUT",
      `/projects/${this.project(repo)}/issues/${iid}/notes/${noteId}`,
      { body },
    );
  }

  async editMrNote(repo: string, iid: number, noteId: number, body: string): Promise<void> {
    await this.request<void>(
      "editMrNote",
      "PUT",
      `/projects/${this.project(repo)}/merge_requests/${iid}/notes/${noteId}`,
      { body },
    );
  }

  async openMr(
    repo: string,
    source: string,
    target: string,
    title: string,
    body: string,
  ): Promise<GitLabMergeRequest> {
    const dto = await this.request<MrDto>(
      "openMr",
      "POST",
      `/projects/${this.project(repo)}/merge_requests`,
      {
        source_branch: source,
        target_branch: target,
        title,
        description: body,
      },
    );
    return { iid: dto.iid, source_branch: dto.source_branch ?? source };
  }

  async openMrsForSource(repo: string, sourceBranch: string): Promise<number[]> {
    const dtos = await this.request<MrDto[]>(
      "openMrsForSource",
      "GET",
      `/projects/${this.project(repo)}/merge_requests?state=opened&per_page=50`,
    );
    return dtos.filter((mr) => mr.source_branch === sourceBranch).map((mr) => mr.iid);
  }

  async openMrsForSha(repo: string, sha: string): Promise<number[]> {
    const dtos = await this.request<MrDto[]>(
      "openMrsForSha",
      "GET",
      `/projects/${this.project(repo)}/merge_requests?state=all&per_page=50`,
    );
    return dtos.filter((mr) => mr.last_commit?.id === sha || mr.sha === sha).map((mr) => mr.iid);
  }

  async getIssue(repo: string, iid: number): Promise<GitLabIssue> {
    const dto = await this.request<IssueDto>(
      "getIssue",
      "GET",
      `/projects/${this.project(repo)}/issues/${iid}`,
    );
    return { iid: dto.iid, state: dto.state, title: dto.title };
  }

  async addLabels(repo: string, iid: number, labels: readonly string[]): Promise<void> {
    await this.request<void>("addLabels", "PUT", `/projects/${this.project(repo)}/issues/${iid}`, {
      add_labels: labels.join(","),
    });
  }

  async getMergeRequest(repo: string, iid: number): Promise<GitLabMergeRequestDetail> {
    const dto = await this.request<MrDto>(
      "getMergeRequest",
      "GET",
      `/projects/${this.project(repo)}/merge_requests/${iid}`,
    );
    return {
      iid: dto.iid,
      state: dto.state ?? "opened",
      title: dto.title ?? "",
      merged: dto.merged === true,
      sha: dto.last_commit?.id ?? dto.sha ?? "",
      source_branch: dto.source_branch ?? "",
    };
  }

  async createBranch(repo: string, branch: string, ref: string): Promise<void> {
    await this.request<void>(
      "createBranch",
      "POST",
      `/projects/${this.project(repo)}/repository/branches`,
      {
        id: repo,
        branch,
        ref,
      },
    );
  }

  async commitStatus(
    repo: string,
    sha: string,
    state: string,
    description: string,
    context: string,
  ): Promise<void> {
    // GitLab commit statuses are keyed by `name` (the GitHub `context`
    // equivalent); success/failed/pending map 1:1 with the wire's states,
    // `error` rides `failed`.
    const wire = state === "error" ? "failed" : state;
    await this.request<void>(
      "commitStatus",
      "POST",
      `/projects/${this.project(repo)}/statuses/${sha}`,
      {
        state: wire,
        description,
        name: context,
      },
    );
  }

  async findNoteByMarker(
    repo: string,
    iid: number,
    marker: string,
    mr: boolean,
  ): Promise<GitLabNote | undefined> {
    const dtos = await this.request<NoteListDto[]>(
      "findNoteByMarker",
      "GET",
      `/projects/${this.project(repo)}/${mr ? "merge_requests" : "issues"}/${iid}/notes`,
    );
    const hit = dtos.find((dto) => dto.body.includes(marker));
    return hit === undefined ? undefined : { id: hit.id, web_url: "" };
  }

  async reviewMergeRequest(
    repo: string,
    iid: number,
    event: string,
    body: string,
  ): Promise<{ id: number }> {
    const project = this.project(repo);
    if (event === "APPROVED") {
      // GitLab's approve route takes no comment body; the approval itself is
      // the directive.
      const dto = await this.request<{ approval_rules_id?: number; id?: number }>(
        "approveMr",
        "POST",
        `/projects/${project}/merge_requests/${iid}/approve`,
        {},
      );
      return { id: dto.id ?? dto.approval_rules_id ?? iid };
    }

    if (event === "REQUEST_CHANGES") {
      const dto = await this.request<{ id: number }>(
        "reviewMr",
        "POST",
        `/projects/${project}/merge_requests/${iid}/reviews`,
        { status_option: "request_changes", comment: { note: body } },
      );
      return { id: dto.id };
    }

    const note = await this.postMrNote(repo, iid, body);
    return { id: note.id };
  }

  async replyDiscussion(
    repo: string,
    iid: number,
    discussionId: string,
    body: string,
  ): Promise<string> {
    const dto = await this.request<NoteDto>(
      "replyDiscussion",
      "POST",
      `/projects/${this.project(repo)}/merge_requests/${iid}/discussions/${discussionId}`,
      { body },
    );
    return String(dto.id);
  }

  async resolveDiscussion(repo: string, iid: number, discussionId: string): Promise<void> {
    const dto = await this.request<DiscussionDto>(
      "resolveDiscussion",
      "PUT",
      `/projects/${this.project(repo)}/merge_requests/${iid}/discussions/${discussionId}`,
      { resolved: true },
    );
    // GitLab echoes the discussion (its id proves the route took the write).
    if (dto.id !== discussionId) {
      throw new GitLabError("resolveDiscussion", 200, "discussion id mismatch");
    }
  }
}
