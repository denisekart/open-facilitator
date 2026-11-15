// The link command dispatcher, forgejo side (decision 4): the ONLY
// outbound effect is a bot-identity comment on an issue/PR.
// `command.communication.reply.v1` with target.conversationKind
// "comment"|"thread" ⇒ POST /repos/{repo}/issues/{number}/comments under the
// bot token; react/update/acknowledge are NOT advertised and answer
// `unsupported` as a receipt (no throw). Idempotency law: the comment ledger
// is consulted BEFORE any provider mutation, so a delivery replay answers
// `accepted` with the stored comment and never double-comments. No echo
// publish: the forge's own issue_comment webhook for the bot comment comes
// back through the webhook edge and dies at the exact-match bot filter.
import type {
  CapabilityDescriptor,
  CapabilityManifestV1,
  CommunicationAcknowledgeCommandV1Data,
  CommunicationReactCommandV1Data,
  CommunicationReplyCommandV1Data,
  CommunicationUpdateCommandV1Data,
  EnvelopeCommand,
  ArtifactRef,
  Media,
} from "@ofac/protocol";
import { dataschemaFor } from "@ofac/workspace-link";
import type { CommandContext, CommandOutcome, LinkHandler, LinkLogger } from "@ofac/workspace-link";
import { CONTEXT_FETCH_COMMAND, answerContextFetch } from "./context.js";
import type { ForgejoApi } from "./forgejo-client.js";
import type { AdapterState } from "./comment-ledger.js";

export const CMD_REPLY = "org.facilitator.command.communication.reply.v1";
export const CMD_REACT = "org.facilitator.command.communication.react.v1";
export const CMD_UPDATE = "org.facilitator.command.communication.update.v1";
export const CMD_ACK = "org.facilitator.command.communication.acknowledge.v1";

/** The declared media facet (the reply leg's own local caps read the same
 *  numbers): 8 items of at most 8 MiB, images/PDFs/text; a forgejo comment
 *  has no caption concept — the attachment rides the comment markdown. */
export const MEDIA_FACET: Media = {
  directions: ["in", "out"],
  kinds: ["image/*", "application/pdf", "text/*"],
  maxCount: 8,
  maxBytesPerItem: 8388608,
  semantics: { caption: false, attachmentUpload: true },
};

/** Outbound media download seam (media.ts fetchArtifactContent in
 *  production; a fake in tests) — telegram's ArtifactSource law. */
export interface ArtifactSource {
  download(artifactId: string): Promise<Uint8Array>;
}

/** Forgejo ships the GitHub reaction set (verified against 16.x). */
const REACTION_MAP: Record<string, string> = {
  "+1": "+1",
  "-1": "-1",
  laugh: "laugh",
  confused: "confused",
  hooray: "hooray",
  rocket: "rocket",
  eyes: "eyes",
  white_check_mark: "hooray",
};

/** The capability manifest this adapter advertises (POST /api/link/v1/capabilities).
 *  The full communication command family (E3); the reply leg carries the
 *  media facet (attachments upload + markdown), the others stay honestly
 *  text-only; the context plane's read leg (plan 3.2) is the asynchronous
 *  arm: a routed thread:// fetch answered with event.context.fetched.v1. */
export function forgejoAdapterManifest(team: string): CapabilityManifestV1 {
  const descriptor = (id: string, withMedia = false): CapabilityDescriptor => ({
    id,
    version: 1,
    execution: "synchronous",
    inputSchema: dataschemaFor(id),
    // Honest declaration: only the reply leg has the attachment leg (an
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
  /** Undefined when the adapter has no bot token (decision 6): replies then
   *  answer `unsupported` as a receipt instead of throwing. */
  api: ForgejoApi | undefined;
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
 *  maxBytesPerItem) downloads from the kernel and uploads as an issue
 *  attachment, folded into the comment body as `![name](download_url)` —
 *  forgejo's native comment-media semantic. Anything the caps drop or that
 *  fails degrades to a link line and NEVER fails the reply. */
async function artifactsMarkdown(
  api: ForgejoApi,
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
      const attachment = await api.uploadIssueAttachment(
        repo,
        number,
        fileNameFor(ref),
        bytes,
        ref.mediaType,
      );
      if (attachment.download_url === "") {
        markdown += linkLines([ref]);
        continue;
      }
      markdown += `\n\n![${fileNameFor(ref)}](${attachment.download_url})`;
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

async function nonReplyCommand(deps: HandlerDeps, cmd: EnvelopeCommand): Promise<CommandOutcome> {
  const { state, logger } = deps;
  if (deps.api === undefined) {
    return { kind: "unsupported", reason: "no-bot-token" };
  }
  const api = deps.api;
  const data = cmd.data as
    | (Partial<CommunicationReactCommandV1Data> & Partial<CommunicationUpdateCommandV1Data>)
    | undefined;
  if (typeof data !== "object" || data === null || data.idempotencyKey === undefined) {
    return {
      kind: "rejected",
      reason: cmd.type === CMD_REACT ? "malformed-react-data" : "malformed-update-data",
    };
  }
  if (state.findComment(data.idempotencyKey) !== undefined) {
    return { kind: "accepted" };
  }
  const repo = data.target?.channelOrResourceId;
  const commentId = data.target?.parentMessageId ?? data.target?.subjectRef;
  if (repo === undefined || commentId === undefined) {
    return { kind: "rejected", reason: "target-missing-comment" };
  }

  if (cmd.type === CMD_REACT) {
    const content = data.emojiName === undefined ? undefined : REACTION_MAP[data.emojiName];
    if (content === undefined) {
      return { kind: "rejected", reason: "unsupported-emoji" };
    }
    await api.reactToComment(repo, Number(commentId), content);
    state.recordComment({
      idempotencyKey: data.idempotencyKey,
      providerObjectId: `reaction:${commentId}:${content}`,
      repo,
      number: data.target?.rootThreadId ?? "",
      createdAt: Date.now(),
    });
    logger.info("reaction added", { repo, commentId, content, commandId: cmd.commandId });
    return { kind: "accepted" };
  }

  if (data.text === undefined) {
    return { kind: "rejected", reason: "malformed-update-data" };
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
    number: data.target?.rootThreadId ?? "",
    createdAt: Date.now(),
  });
  logger.info("comment updated", { repo, commentId, commandId: cmd.commandId });
  return { kind: "accepted" };
}

export function createForgejoHandler(deps: HandlerDeps): LinkHandler {
  const { state, logger } = deps;
  return {
    async onCommand(cmd: EnvelopeCommand, ctx: CommandContext): Promise<CommandOutcome> {
      if (cmd.type === CMD_ACK) {
        // No provider-side effect for an acknowledgement on a code host, so
        // no token is needed: the ledger check IS the law.
        const ack = cmd.data as unknown as
          Partial<CommunicationAcknowledgeCommandV1Data> | undefined;
        if (ack?.idempotencyKey === undefined) {
          return { kind: "rejected", reason: "malformed-acknowledge-data" };
        }

        void state.findComment(ack.idempotencyKey);
        return { kind: "accepted" };
      }
      if (cmd.type === CONTEXT_FETCH_COMMAND) {
        // The context plane's read leg (plan 3.2): the window comes FROM THE
        // PLATFORM; anything unanswerable (no token included) rides the
        // event plane as an honest error item, never a failed command.
        return await answerContextFetch(deps.api, deps.providerInstanceId ?? "unknown", cmd, ctx);
      }
      if (cmd.type === CMD_REACT || cmd.type === CMD_UPDATE) {
        return await nonReplyCommand(deps, cmd);
      }
      if (cmd.type !== CMD_REPLY) return { kind: "unsupported" };
      const data = replyData(cmd);
      if (data === undefined) return { kind: "rejected", reason: "malformed-reply-data" };
      if (data.target.conversationKind !== "comment" && data.target.conversationKind !== "thread") {
        return { kind: "rejected", reason: "unsupported-conversation-kind" };
      }
      if (deps.api === undefined) {
        return { kind: "unsupported", reason: "no-bot-token" };
      }

      if (state.findComment(data.idempotencyKey) !== undefined) {
        return { kind: "accepted" };
      }
      const repo = data.target.channelOrResourceId;
      const number = data.target.rootThreadId ?? data.target.subjectRef;
      if (repo === undefined || number === undefined) {
        return { kind: "rejected", reason: "target-missing-repo-or-issue" };
      }

      // Outbound media: artifacts ride the comment as uploaded attachments
      // folded in as `![name](download_url)` markdown. No issue context the
      // attachment route can serve (a non-numeric target) or no content
      // route ⇒ degrade to a public link line appended to the text.
      const artifacts = data.artifacts ?? [];
      const issueNumber = Number(number);
      const hasIssueContext = Number.isInteger(issueNumber) && issueNumber > 0;
      let body = data.text;
      if (artifacts.length > 0) {
        body +=
          hasIssueContext && deps.media !== undefined
            ? await artifactsMarkdown(deps.api, logger, deps.media, repo, issueNumber, artifacts)
            : linkLines(artifacts.slice(0, MEDIA_FACET.maxCount ?? artifacts.length));
      }

      const comment = await deps.api.postComment(repo, Number(number), body);
      state.recordComment({
        idempotencyKey: data.idempotencyKey,
        providerObjectId: String(comment.id),
        repo,
        number: String(number),
        createdAt: Date.now(),
      });
      logger.info("comment posted", {
        repo,
        number,
        commentId: comment.id,
        commandId: data.commandId,
      });
      return { kind: "accepted" };
    },
  };
}
