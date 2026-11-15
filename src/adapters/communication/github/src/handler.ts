// The link command dispatcher, github side (decision 4): the outbound
// effects are a bot-identity comment on an issue/PR and the asynchronous
// context-fetch answer.
// `command.communication.reply.v1` with target.conversationKind
// "comment"|"thread" ⇒ POST /repos/{repo}/issues/{number}/comments under the
// bot token; react/update/acknowledge are NOT advertised and answer
// `unsupported` as a receipt (no throw). Idempotency law: the comment ledger
// is consulted BEFORE any provider mutation, so a delivery replay answers
// `accepted` with the stored comment and never double-comments. No echo
// publish: GitHub's own issue_comment webhook for the bot comment comes back
// through the fan-out and dies at the exact-match bot filter.
import type {
  ArtifactRef,
  CapabilityDescriptor,
  CapabilityManifestV1,
  CommunicationAcknowledgeCommandV1Data,
  CommunicationReactCommandV1Data,
  CommunicationReplyCommandV1Data,
  CommunicationUpdateCommandV1Data,
  EnvelopeCommand,
  Media,
} from "@ofac/protocol";
import { dataschemaFor } from "@ofac/workspace-link";
import type { LinkHandler, LinkLogger, CommandOutcome } from "@ofac/workspace-link";
import { CONTEXT_FETCH_COMMAND, answerContextFetch } from "./context.js";
import type { GitHubApi } from "./github-client.js";
import type { AdapterState } from "./comment-ledger.js";

export const CMD_REPLY = "org.facilitator.command.communication.reply.v1";
export const CMD_REACT = "org.facilitator.command.communication.react.v1";
export const CMD_UPDATE = "org.facilitator.command.communication.update.v1";
export const CMD_ACK = "org.facilitator.command.communication.acknowledge.v1";
export const CMD_POST = "org.facilitator.command.communication.post.v1";

/** The declared media facet (the reply/post legs' own local caps read the
 *  same numbers): 5 items of at most 5 MiB (GitHub's real image-upload cap),
 *  images/PDFs/text; a GitHub comment has no caption concept — the upload
 *  rides the comment markdown as an image link. */
export const MEDIA_FACET: Media = {
  directions: ["in", "out"],
  kinds: ["image/*", "application/pdf", "text/*"],
  maxCount: 5,
  maxBytesPerItem: 5242880,
  semantics: { caption: false, attachmentUpload: true },
};

/** Outbound media download seam (media.ts fetchArtifactContent in
 *  production; a fake in tests) — telegram's ArtifactSource law. */
export interface ArtifactSource {
  download(artifactId: string): Promise<Uint8Array>;
}

/** GitHub's reaction content set is small and named; the kernel speaks
 *  emoji names. Unmapped emoji are refused honestly, never silently
 *  downgraded to a different face. */
const REACTION_MAP: Record<string, string> = {
  "+1": "+1",
  "-1": "-1",
  laugh: "laugh",
  confused: "confused",
  hooray: "hooray",
  rocket: "rocket",
  eyes: "eyes",
  white_check_mark: "hooray",
  eyes_look: "eyes",
};

/** The capability manifest this adapter advertises (POST /api/link/v1/capabilities).
 *  The full communication command family: a code host that cannot react or
 *  acknowledge leaves the kernel's ladder a hole it cannot see (E3). The
 *  reply/post legs carry the media facet (issue-image upload + markdown);
 *  the context plane's read leg (plan 3.2) is the asynchronous arm: a routed
 *  thread:// fetch answered with event.context.fetched.v1. */
export function githubAdapterManifest(team: string): CapabilityManifestV1 {
  const descriptor = (id: string, withMedia = false): CapabilityDescriptor => ({
    id,
    version: 1,
    execution: "synchronous",
    inputSchema: dataschemaFor(id),
    // Honest declaration: only the reply/post legs have the upload leg (an
    // empty allow-list means text-only for the rest).
    media: withMedia ? MEDIA_FACET : { directions: ["in", "out"], kinds: [] },
  });
  return {
    apiVersion: "facilitator.framework/v1",
    kind: "CapabilityManifest",
    ownerTeam: team,
    capabilities: [
      descriptor(CMD_REPLY, true),
      descriptor(CMD_REACT),
      descriptor(CMD_UPDATE),
      descriptor(CMD_ACK),
      descriptor(CMD_POST, true),
      {
        id: CONTEXT_FETCH_COMMAND,
        version: 1,
        execution: "asynchronous",
        inputSchema: dataschemaFor(CONTEXT_FETCH_COMMAND),
      },
    ],
  };
}

export interface HandlerDeps {
  /** Undefined when the adapter has no bot token: replies then answer
   *  `unsupported` as a receipt instead of throwing. */
  api: GitHubApi | undefined;
  state: AdapterState;
  /** Outbound media source (reply-with-artifact path): undefined when the
   *  adapter cannot reach the kernel content route; artifacts then degrade
   *  to the link line, never fail the reply. */
  media?: ArtifactSource | undefined;
  /** The provider instance the context leg answers under (the event's
   *  responder/source identity); mirrors the normalizer's context value. */
  providerInstanceId?: string | undefined;
  logger: LinkLogger;
}

function replyData(cmd: EnvelopeCommand): CommunicationReplyCommandV1Data | undefined {
  const data = cmd.data;
  if (typeof data !== "object" || data === null) return undefined;
  for (const key of ["commandId", "idempotencyKey", "target", "text"]) {
    if ((data as Record<string, unknown>)[key] === undefined) return undefined;
  }

  return data as unknown as CommunicationReplyCommandV1Data;
}

function targetIssue(target: {
  channelOrResourceId?: string;
  rootThreadId?: string;
  subjectRef?: string;
}) {
  const repo = target.channelOrResourceId;
  const number = target.rootThreadId ?? target.subjectRef;
  return repo === undefined || number === undefined ? undefined : { repo, number };
}

function fileNameFor(ref: ArtifactRef): string {
  const ext =
    ref.mediaType === "image/png"
      ? ".png"
      : ref.mediaType === "image/jpeg"
        ? ".jpg"
        : ref.mediaType === "image/gif"
          ? ".gif"
          : ref.mediaType === "image/webp"
            ? ".webp"
            : ref.mediaType === "application/pdf"
              ? ".pdf"
              : ref.mediaType.startsWith("text/")
                ? ".txt"
                : ".bin";
  const tail = (ref.storageUrl.split("/").pop() ?? ref.artifactId).split("?")[0] ?? "attachment";
  const base = tail.replace(/[^A-Za-z0-9._-]/g, "_").slice(0, 64) || "attachment";
  return base.includes(".") ? base : base + ext;
}

/** The degrade law's answer (the kernel's mirror): a dropped artifact appends
 *  a plain link line to the comment text — never a silent loss, never a
 *  failed reply. Both "no issue context" and per-item upload failures ride
 *  this single form. */
function linkLines(artifacts: readonly ArtifactRef[], reason?: string): string {
  const suffix = reason === undefined ? "" : ` (${reason})`;
  return artifacts.map((ref) => `\n\nattachment available: ${ref.storageUrl}${suffix}`).join("");
}

/** The outbound media leg: every ref (up to the facet's maxCount, each under
 *  maxBytesPerItem) downloads from the kernel and uploads as an issue image,
 *  folded into the comment body as `![name](asset_url)` — the comment's
 *  native comment-media semantic. Anything the caps drop or that fails
 *  degrades to a link line and NEVER fails the reply. */
async function artifactsMarkdown(
  api: GitHubApi,
  logger: LinkLogger,
  media: ArtifactSource,
  repo: string,
  number: number,
  artifacts: readonly ArtifactRef[],
): Promise<string> {
  const maxCount = MEDIA_FACET.maxCount ?? Number.POSITIVE_INFINITY;
  const maxBytes = MEDIA_FACET.maxBytesPerItem ?? Number.POSITIVE_INFINITY;
  let markdown = "";
  for (const [index, ref] of artifacts.entries()) {
    if (index >= maxCount) {
      markdown += linkLines([ref], `over the ${String(maxCount)}-item cap`);
      continue;
    }
    try {
      const bytes = await media.download(ref.artifactId);
      if (bytes.byteLength > maxBytes) {
        markdown += linkLines([ref], "over the size cap");
        continue;
      }
      const asset = await api.uploadIssueImage(
        repo,
        number,
        fileNameFor(ref),
        bytes,
        ref.mediaType,
      );
      if (asset.asset_url === "") {
        markdown += linkLines([ref]);
        continue;
      }
      markdown += `\n\n![${fileNameFor(ref)}](${asset.asset_url})`;
    } catch (error) {
      logger.warn("artifact upload failed: degrading to a link line", {
        artifactId: ref.artifactId,
        error: String(error),
      });
      markdown += linkLines([ref]);
    }
  }
  return markdown;
}

export function createGitHubHandler(deps: HandlerDeps): LinkHandler {
  const { state, logger } = deps;
  return {
    async onCommand(cmd: EnvelopeCommand, ctx): Promise<CommandOutcome> {
      if (cmd.type === CONTEXT_FETCH_COMMAND) {
        // The context plane's read leg (plan 3.2): the window comes FROM THE
        // PLATFORM; anything unanswerable (no token included) rides the
        // event plane as an honest error item, never a failed command.
        return await answerContextFetch(deps.api, deps.providerInstanceId ?? "unknown", cmd, ctx);
      }
      if (
        cmd.type !== CMD_REPLY &&
        cmd.type !== CMD_POST &&
        cmd.type !== CMD_REACT &&
        cmd.type !== CMD_UPDATE &&
        cmd.type !== CMD_ACK
      ) {
        return { kind: "unsupported" };
      }
      if (cmd.type === CMD_ACK) {
        // No provider-side effect exists for an acknowledgement on a code
        // host, so no token is needed: the ledger check IS the law (a
        // replayed ack is a no-op accepted, exactly like the first pass).
        const ack = cmd.data as unknown as
          Partial<CommunicationAcknowledgeCommandV1Data> | undefined;
        if (ack?.idempotencyKey === undefined) {
          return { kind: "rejected", reason: "malformed-acknowledge-data" };
        }

        void state.findComment(ack.idempotencyKey);
        return { kind: "accepted" };
      }
      if (deps.api === undefined) {
        return { kind: "unsupported", reason: "no-bot-token" };
      }
      const api = deps.api;

      if (cmd.type === CMD_REPLY || cmd.type === CMD_POST) {
        const data = replyData(cmd);
        if (data === undefined) {
          return {
            kind: "rejected",
            reason: cmd.type === CMD_REPLY ? "malformed-reply-data" : "malformed-post-data",
          };
        }
        if (
          cmd.type === CMD_REPLY &&
          data.target.conversationKind !== "comment" &&
          data.target.conversationKind !== "thread"
        ) {
          return { kind: "rejected", reason: "unsupported-conversation-kind" };
        }
        // A notice (CMD_POST) addresses a top-level conversation (the wire's
        // dm|channel kinds); on a code host the only top-level conversation
        // with an addressable thread IS the issue/PR itself, so the target's
        // repo+number law below decides, not the kind token.

        if (state.findComment(data.idempotencyKey) !== undefined) {
          return { kind: "accepted" };
        }
        const issue = targetIssue(data.target);
        if (issue === undefined) {
          return { kind: "rejected", reason: "target-missing-repo-or-issue" };
        }

        // Outbound media: artifacts ride the comment as uploaded issue
        // images folded in as `![name](asset_url)` markdown. No issue
        // context the upload route can serve (a non-numeric target) or no
        // content route ⇒ degrade to a public link line appended to the text.
        const artifacts = data.artifacts ?? [];
        const issueNumber = Number(issue.number);
        const hasIssueContext = Number.isInteger(issueNumber) && issueNumber > 0;
        let body = data.text;
        if (artifacts.length > 0) {
          body +=
            hasIssueContext && deps.media !== undefined
              ? await artifactsMarkdown(api, logger, deps.media, issue.repo, issueNumber, artifacts)
              : linkLines(artifacts.slice(0, MEDIA_FACET.maxCount ?? artifacts.length));
        }

        const comment = await api.postComment(issue.repo, Number(issue.number), body);
        state.recordComment({
          idempotencyKey: data.idempotencyKey,
          providerObjectId: String(comment.id),
          repo: issue.repo,
          number: issue.number,
          createdAt: Date.now(),
        });
        logger.info("comment posted", {
          repo: issue.repo,
          number: issue.number,
          commentId: comment.id,
          commandId: data.commandId,
        });
        return { kind: "accepted" };
      }

      if (cmd.type === CMD_REACT) {
        const data = cmd.data as unknown as Partial<CommunicationReactCommandV1Data> | undefined;
        if (
          data?.idempotencyKey === undefined ||
          data.target === undefined ||
          data.emojiName === undefined
        ) {
          return { kind: "rejected", reason: "malformed-react-data" };
        }
        if (state.findComment(data.idempotencyKey) !== undefined) {
          return { kind: "accepted" };
        }
        const content = REACTION_MAP[data.emojiName];
        if (content === undefined) {
          return { kind: "rejected", reason: "unsupported-emoji" };
        }
        // Reactions attach to a comment: the target's own message id is the
        // provider comment id (the kernel replies in-thread and reacts to a
        // post it knows).
        const commentId = data.target.parentMessageId ?? data.target.subjectRef;
        const repo = data.target.channelOrResourceId;
        if (commentId === undefined || repo === undefined) {
          return { kind: "rejected", reason: "target-missing-comment" };
        }

        await api.reactToComment(repo, Number(commentId), content);
        state.recordComment({
          idempotencyKey: data.idempotencyKey,
          providerObjectId: `reaction:${commentId}:${content}`,
          repo,
          number: data.target.rootThreadId ?? "",
          createdAt: Date.now(),
        });
        logger.info("reaction added", { repo, commentId, content, commandId: data.commandId });
        return { kind: "accepted" };
      }

      if (cmd.type === CMD_UPDATE) {
        const data = cmd.data as unknown as Partial<CommunicationUpdateCommandV1Data> | undefined;
        if (
          data?.idempotencyKey === undefined ||
          data.target === undefined ||
          data.text === undefined
        ) {
          return { kind: "rejected", reason: "malformed-update-data" };
        }
        if (state.findComment(data.idempotencyKey) !== undefined) {
          return { kind: "accepted" };
        }
        const commentId = data.target.parentMessageId ?? data.target.subjectRef;
        const repo = data.target.channelOrResourceId;
        if (commentId === undefined || repo === undefined) {
          return { kind: "rejected", reason: "target-missing-comment" };
        }
        // The update law: only a comment this adapter created may be edited.
        if (state.findCommentByObjectId(commentId) === undefined) {
          return { kind: "rejected", reason: "unknown-comment" };
        }

        await api.updateComment(repo, Number(commentId), data.text);
        state.recordComment({
          idempotencyKey: data.idempotencyKey,
          providerObjectId: commentId,
          repo,
          number: data.target.rootThreadId ?? "",
          createdAt: Date.now(),
        });
        logger.info("comment updated", { repo, commentId, commandId: data.commandId });
        return { kind: "accepted" };
      }

      return { kind: "unsupported" };
    },
  };
}
