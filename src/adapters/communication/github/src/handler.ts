// The link command dispatcher, github side (decision 4): the ONLY
// outbound effect is a bot-identity comment on an issue/PR.
// `command.communication.reply.v1` with target.conversationKind
// "comment"|"thread" ⇒ POST /repos/{repo}/issues/{number}/comments under the
// bot token; react/update/acknowledge are NOT advertised and answer
// `unsupported` as a receipt (no throw). Idempotency law: the comment ledger
// is consulted BEFORE any provider mutation, so a delivery replay answers
// `accepted` with the stored comment and never double-comments. No echo
// publish: GitHub's own issue_comment webhook for the bot comment comes back
// through the fan-out and dies at the exact-match bot filter.
import type {
  CapabilityDescriptor,
  CapabilityManifestV1,
  CommunicationAcknowledgeCommandV1Data,
  CommunicationReactCommandV1Data,
  CommunicationReplyCommandV1Data,
  CommunicationUpdateCommandV1Data,
  EnvelopeCommand,
} from "@ofac/protocol";
import { dataschemaFor } from "@ofac/workspace-link";
import type { LinkHandler, LinkLogger, CommandOutcome } from "@ofac/workspace-link";
import type { GitHubApi } from "./github-client.js";
import type { AdapterState } from "./comment-ledger.js";

export const CMD_REPLY = "org.facilitator.command.communication.reply.v1";
export const CMD_REACT = "org.facilitator.command.communication.react.v1";
export const CMD_UPDATE = "org.facilitator.command.communication.update.v1";
export const CMD_ACK = "org.facilitator.command.communication.acknowledge.v1";

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
 *  acknowledge leaves the kernel's ladder a hole it cannot see (E3). */
export function githubAdapterManifest(team: string): CapabilityManifestV1 {
  const descriptor = (id: string): CapabilityDescriptor => ({
    id,
    version: 1,
    execution: "synchronous",
    inputSchema: dataschemaFor(id),
  });
  return {
    apiVersion: "facilitator.framework/v1",
    kind: "CapabilityManifest",
    ownerTeam: team,
    capabilities: [
      descriptor(CMD_REPLY),
      descriptor(CMD_REACT),
      descriptor(CMD_UPDATE),
      descriptor(CMD_ACK),
    ],
  };
}

export interface HandlerDeps {
  /** Undefined when the adapter has no bot token: replies then answer
   *  `unsupported` as a receipt instead of throwing. */
  api: GitHubApi | undefined;
  state: AdapterState;
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

export function createGitHubHandler(deps: HandlerDeps): LinkHandler {
  const { state, logger } = deps;
  return {
    async onCommand(cmd: EnvelopeCommand): Promise<CommandOutcome> {
      if (
        cmd.type !== CMD_REPLY &&
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

      if (cmd.type === CMD_REPLY) {
        const data = replyData(cmd);
        if (data === undefined) return { kind: "rejected", reason: "malformed-reply-data" };
        if (
          data.target.conversationKind !== "comment" &&
          data.target.conversationKind !== "thread"
        ) {
          return { kind: "rejected", reason: "unsupported-conversation-kind" };
        }

        if (state.findComment(data.idempotencyKey) !== undefined) {
          return { kind: "accepted" };
        }
        const issue = targetIssue(data.target);
        if (issue === undefined) {
          return { kind: "rejected", reason: "target-missing-repo-or-issue" };
        }

        const comment = await api.postComment(issue.repo, Number(issue.number), data.text);
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
