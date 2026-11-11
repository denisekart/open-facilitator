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
} from "@ofac/protocol";
import { dataschemaFor } from "@ofac/workspace-link";
import type { LinkHandler, LinkLogger, CommandOutcome } from "@ofac/workspace-link";
import type { ForgejoApi } from "./forgejo-client.js";
import type { AdapterState } from "./comment-ledger.js";

export const CMD_REPLY = "org.facilitator.command.communication.reply.v1";
export const CMD_REACT = "org.facilitator.command.communication.react.v1";
export const CMD_UPDATE = "org.facilitator.command.communication.update.v1";
export const CMD_ACK = "org.facilitator.command.communication.acknowledge.v1";

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
 *  The full communication command family (E3). */
export function forgejoAdapterManifest(team: string): CapabilityManifestV1 {
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
    capabilities: [descriptor(CMD_REPLY), descriptor(CMD_REACT), descriptor(CMD_UPDATE), descriptor(CMD_ACK)],
  };
}

export interface HandlerDeps {
  /** Undefined when the adapter has no bot token (decision 6): replies then
   *  answer `unsupported` as a receipt instead of throwing. */
  api: ForgejoApi | undefined;
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
    async onCommand(cmd: EnvelopeCommand): Promise<CommandOutcome> {
      if (cmd.type === CMD_ACK) {
        // No provider-side effect for an acknowledgement on a code host, so
        // no token is needed: the ledger check IS the law.
        const ack = cmd.data as unknown as Partial<CommunicationAcknowledgeCommandV1Data> | undefined;
        if (ack?.idempotencyKey === undefined) {
          return { kind: "rejected", reason: "malformed-acknowledge-data" };
        }

        void state.findComment(ack.idempotencyKey);
        return { kind: "accepted" };
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

      const comment = await deps.api.postComment(repo, Number(number), data.text);
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
