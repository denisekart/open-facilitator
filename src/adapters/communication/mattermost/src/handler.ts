// The link command dispatcher: kernel → adapter side effects over the
// Mattermost REST client, under the idempotency law — the
// post-ledger is consulted BEFORE any provider mutation, so a delivery
// replay answers `accepted` with the stored result and never double-posts.
// After a successful reply/update the bot's own message.v1 echo rides back
// through ctx.publishEvent: the kernel stores it (provider_events dedupes by
// providerDeliveryId, so the later WS echo of the same post is a no-op) and
// records the outbound post id into communication_bindings.
import type {
  CapabilityDescriptor,
  CapabilityManifestV1,
  CommunicationAcknowledgeCommandV1Data,
  CommunicationReactCommandV1Data,
  CommunicationReplyCommandV1Data,
  CommunicationUpdateCommandV1Data,
  ConversationKind,
  ConversationReference,
  EnvelopeCommand,
  ProviderMessageEventV1Data,
} from "@ofac/protocol";
import {
  buildEvent,
  dataschemaFor,
  type CommandContext,
  type CommandOutcome,
  type LinkHandler,
  type LinkLogger,
} from "@ofac/workspace-link";
import { MESSAGE_EVENT } from "./normalize.js";
import type { MattermostApi } from "./mm-client.js";
import type { AdapterState } from "./post-ledger.js";

export const CMD_REPLY = "org.facilitator.command.communication.reply.v1";
export const CMD_REACT = "org.facilitator.command.communication.react.v1";
export const CMD_UPDATE = "org.facilitator.command.communication.update.v1";
export const CMD_ACK = "org.facilitator.command.communication.acknowledge.v1";
export const CMD_POST = "org.facilitator.command.communication.post.v1";

export const COMMUNICATION_COMMANDS: readonly string[] = [
  CMD_REPLY,
  CMD_REACT,
  CMD_UPDATE,
  CMD_ACK,
  CMD_POST,
];

/** The provider-side identity the echo events are authored under. */
export interface AdapterIdentity {
  botUserId: string;
  providerInstanceId: string;
  tenantOrOrganizationId: string;
}

export interface HandlerDeps {
  api: MattermostApi;
  state: AdapterState;
  identity: AdapterIdentity;
  logger: LinkLogger;
}

/** The capability manifest this adapter advertises (POST /api/link/v1/capabilities). */
export function communicationManifest(team: string): CapabilityManifestV1 {
  const descriptor = (id: string): CapabilityDescriptor => ({
    id,
    version: 1,
    execution: "synchronous",
    inputSchema: dataschemaFor(id),
  });
  // The tuple type demands a first element; the law is exactly these four.
  return {
    apiVersion: "facilitator.framework/v1",
    kind: "CapabilityManifest",
    ownerTeam: team,
    capabilities: [
      descriptor(CMD_REPLY),
      ...COMMUNICATION_COMMANDS.filter((id) => id !== CMD_REPLY).map(descriptor),
    ],
  };
}

function dataOf<T>(cmd: EnvelopeCommand): T | undefined {
  const data = cmd.data;
  if (typeof data !== "object" || data === null) return undefined;
  const required = ["commandId", "idempotencyKey", "target"];
  for (const key of required) {
    if (data[key] === undefined) return undefined;
  }
  return data as unknown as T;
}

/** The provider object a command acts ON: the post the kernel targets. */
function targetPostId(target: {
  parentMessageId?: string;
  subjectRef?: string;
  rootThreadId?: string;
}): string | undefined {
  return target.parentMessageId ?? target.subjectRef ?? target.rootThreadId;
}

/**
 * Where a command posts: an explicit channel, else — for a dm target naming
 * only its counterparty — the bot-DM channel (created + cached on demand).
 */
async function resolveChannel(
  api: MattermostApi,
  target: { channelOrResourceId?: string; actorProviderAccountId?: string },
): Promise<string | undefined> {
  if (target.channelOrResourceId !== undefined) return target.channelOrResourceId;
  if (target.actorProviderAccountId !== undefined) {
    return await api.dmChannel(target.actorProviderAccountId);
  }
  return undefined;
}

export function createAdapterHandler(deps: HandlerDeps): LinkHandler {
  const { api, state, identity, logger } = deps;

  const publishEcho = async (
    ctx: CommandContext,
    channelId: string,
    postId: string,
    rootId: string | undefined,
    text: string,
    kind: ConversationKind,
  ): Promise<void> => {
    const reference: ConversationReference = {
      provider: "mattermost",
      providerInstanceId: identity.providerInstanceId,
      tenantOrOrganizationId: identity.tenantOrOrganizationId,
      channelOrResourceId: channelId,
      conversationKind: kind,
      authorProviderAccountId: identity.botUserId,
      messageId: postId,
      ...(rootId === undefined ? {} : { rootThreadId: rootId, parentMessageId: rootId }),
    };
    const data: ProviderMessageEventV1Data = {
      reference,
      provider: "mattermost",
      providerDeliveryId: postId,
      actorProviderAccountId: identity.botUserId,
      text,
      payload: { echoOf: String(ctx.envelope.id) },
    };
    await ctx.publishEvent(
      buildEvent(MESSAGE_EVENT, {
        source: `urn:ofac:provider:mattermost:${identity.tenantOrOrganizationId}`,
        subject: postId,
        data: data as unknown as Record<string, unknown>,
        causationid: ctx.envelope.id,
      }),
    );
  };

  return {
    async onCommand(cmd: EnvelopeCommand, ctx: CommandContext): Promise<CommandOutcome> {
      switch (cmd.type) {
        case CMD_REPLY: {
          const data = dataOf<CommunicationReplyCommandV1Data>(cmd);
          if (data === undefined) return { kind: "rejected", reason: "malformed-reply-data" };
          if (state.findPost(data.idempotencyKey) !== undefined) {
            return { kind: "accepted" };
          }
          const channelId = await resolveChannel(api, data.target);
          if (channelId === undefined) {
            return { kind: "rejected", reason: "target-missing-channel" };
          }
          const rootId = data.target.rootThreadId ?? data.target.parentMessageId;
          const postId = await api.createPost(channelId, data.text, rootId);
          state.recordPost({
            idempotencyKey: data.idempotencyKey,
            providerObjectId: postId,
            channelId,
            rootId: rootId ?? "",
            kind: "post",
            createdAt: Date.now(),
          });
          await publishEcho(
            ctx,
            channelId,
            postId,
            rootId,
            data.text,
            data.target.conversationKind,
          );
          logger.info("reply posted", { postId, channelId, commandId: data.commandId });
          return { kind: "accepted" };
        }
        case CMD_POST: {
          // The B4 law: a team notice is a TOP-LEVEL post (a team-channel
          // post or a DM), never a thread reply wearing a costume.
          const data = dataOf<CommunicationReplyCommandV1Data>(cmd);
          if (data === undefined) return { kind: "rejected", reason: "malformed-post-data" };
          if (data.target.conversationKind !== "channel" && data.target.conversationKind !== "dm") {
            return { kind: "rejected", reason: "unsupported-conversation-kind" };
          }

          if (state.findPost(data.idempotencyKey) !== undefined) {
            return { kind: "accepted" };
          }
          const channelId = await resolveChannel(api, data.target);
          if (channelId === undefined) {
            return { kind: "rejected", reason: "target-missing-channel" };
          }
          const postId = await api.createPost(channelId, data.text, undefined);
          state.recordPost({
            idempotencyKey: data.idempotencyKey,
            providerObjectId: postId,
            channelId,
            rootId: "",
            kind: "post",
            createdAt: Date.now(),
          });
          await publishEcho(ctx, channelId, postId, undefined, data.text, data.target.conversationKind);
          logger.info("notice posted", { postId, channelId, commandId: data.commandId });
          return { kind: "accepted" };
        }
        case CMD_REACT: {
          const data = dataOf<CommunicationReactCommandV1Data>(cmd);
          if (data === undefined) return { kind: "rejected", reason: "malformed-react-data" };
          if (state.findPost(data.idempotencyKey) !== undefined) {
            return { kind: "accepted" };
          }
          const postId = targetPostId(data.target);
          if (postId === undefined) {
            return { kind: "rejected", reason: "target-missing-post" };
          }
          const reactionId = await api.createReaction(postId, data.emojiName);
          state.recordPost({
            idempotencyKey: data.idempotencyKey,
            providerObjectId: reactionId,
            channelId: data.target.channelOrResourceId ?? "",
            rootId: postId,
            kind: "reaction",
            createdAt: Date.now(),
          });
          logger.info("reaction added", { reactionId, postId, commandId: data.commandId });
          return { kind: "accepted" };
        }
        case CMD_UPDATE: {
          const data = dataOf<CommunicationUpdateCommandV1Data>(cmd);
          if (data === undefined) return { kind: "rejected", reason: "malformed-update-data" };
          if (state.findPost(data.idempotencyKey) !== undefined) {
            return { kind: "accepted" };
          }
          const postId = data.target.parentMessageId ?? data.target.subjectRef;
          if (postId === undefined) {
            return { kind: "rejected", reason: "target-missing-post" };
          }
          // The update law: only a post this adapter created may be edited.
          if (state.findPostByObjectId(postId) === undefined) {
            return { kind: "rejected", reason: "unknown-post" };
          }
          await api.editPost(postId, data.text);
          state.recordPost({
            idempotencyKey: data.idempotencyKey,
            providerObjectId: postId,
            channelId: data.target.channelOrResourceId ?? "",
            rootId: data.target.rootThreadId ?? "",
            kind: "post",
            createdAt: Date.now(),
          });
          const channelId = data.target.channelOrResourceId ?? "";
          if (channelId !== "") {
            await publishEcho(
              ctx,
              channelId,
              postId,
              data.target.rootThreadId,
              data.text,
              data.target.conversationKind,
            );
          }
          logger.info("post updated", { postId, commandId: data.commandId });
          return { kind: "accepted" };
        }
        case CMD_ACK: {
          const data = dataOf<CommunicationAcknowledgeCommandV1Data>(cmd);
          if (data === undefined) return { kind: "rejected", reason: "malformed-acknowledge-data" };
          // No provider side effect: the ledger check is the whole law (a
          // replayed ack stays a no-op accepted, same as the first pass).
          void state.findPost(data.idempotencyKey);
          return { kind: "accepted" };
        }
        default:
          return { kind: "unsupported" };
      }
    },
  };
}
