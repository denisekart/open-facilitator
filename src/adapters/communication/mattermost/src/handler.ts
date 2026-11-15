// The link command dispatcher: kernel → adapter side effects over the
// Mattermost REST client, under the idempotency law — the
// post-ledger is consulted BEFORE any provider mutation, so a delivery
// replay answers `accepted` with the stored result and never double-posts.
// After a successful reply/update the bot's own message.v1 echo rides back
// through ctx.publishEvent: the kernel stores it (provider_events dedupes by
// providerDeliveryId, so the later WS echo of the same post is a no-op) and
// records the outbound post id into communication_bindings.
import type {
  ArtifactRef,
  CapabilityDescriptor,
  CapabilityManifestV1,
  CommunicationAcknowledgeCommandV1Data,
  CommunicationReactCommandV1Data,
  CommunicationReplyCommandV1Data,
  CommunicationUpdateCommandV1Data,
  ConversationKind,
  ConversationReference,
  EnvelopeCommand,
  Media,
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
import { CONTEXT_FETCH_COMMAND, answerContextFetch } from "./context.js";
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

/** Outbound media download seam (media.ts fetchArtifactContent in
 *  production; a fake in tests). */
export interface ArtifactSource {
  download(artifactId: string): Promise<Uint8Array>;
}

export interface HandlerDeps {
  api: MattermostApi;
  state: AdapterState;
  identity: AdapterIdentity;
  /** Outbound artifact bytes source; absent ⇒ artifacts degrade to a link
   *  line (the honest text-only default). */
  media?: ArtifactSource;
  logger: LinkLogger;
}

/** The declared outbound media law, mirrored from the facet below: at most
 *  5 attachments per post, 50 MB each (the provider's own upload cap). */
const MEDIA_MAX_COUNT = 5;
const MEDIA_MAX_BYTES = 52428800;

/** The exact facet the kernel's outbound selection law reads before
 *  attaching anything (plan Wave 2 §2.1). Mattermost has no caption concept:
 *  text + files, hence caption:false, attachmentUpload:true. */
const MEDIA_FACET: Media = {
  directions: ["in", "out"],
  kinds: ["image/*", "application/pdf", "text/*", "application/*"],
  maxCount: MEDIA_MAX_COUNT,
  maxBytesPerItem: MEDIA_MAX_BYTES,
  semantics: { caption: false, attachmentUpload: true },
};

/** The capability manifest this adapter advertises (POST /api/link/v1/capabilities). */
export function communicationManifest(team: string): CapabilityManifestV1 {
  const descriptor = (id: string, withMedia = false): CapabilityDescriptor => ({
    id,
    version: 1,
    execution: "synchronous",
    inputSchema: dataschemaFor(id),
    // The declared media facet rides the two commands that carry artifacts
    // (reply/post; the plan's §2.1 facet). No facet ⇒ the honest text-only
    // default for the other communication commands.
    ...(withMedia ? { media: MEDIA_FACET } : {}),
  });
  // The tuple type demands a first element; the law is exactly these four
  // communication descriptors (the two media ones carry the facet) plus the
  // asynchronous context-fetch arm.
  return {
    apiVersion: "facilitator.framework/v1",
    kind: "CapabilityManifest",
    ownerTeam: team,
    capabilities: [
      descriptor(CMD_REPLY, true),
      ...COMMUNICATION_COMMANDS.filter((id) => id !== CMD_REPLY).map((id) =>
        descriptor(id, id === CMD_POST),
      ),
      // The context plane's read leg (plan 3.2): a routed thread:// fetch is
      // answered asynchronously with event.context.fetched.v1.
      {
        id: CONTEXT_FETCH_COMMAND,
        version: 1,
        execution: "asynchronous",
        inputSchema: dataschemaFor(CONTEXT_FETCH_COMMAND),
      },
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

/** The upload filename law (telegram's): an extension honest about the
 *  mediaType, named after the artifact, never the harness-local path. */
function fileNameFor(ref: ArtifactRef): string {
  const ext =
    ref.mediaType === "image/png"
      ? "png"
      : ref.mediaType === "image/jpeg"
        ? "jpg"
        : ref.mediaType === "application/pdf"
          ? "pdf"
          : ref.mediaType.startsWith("text/")
            ? "txt"
            : "bin";
  return `artifact-${ref.artifactId.slice(0, 8)}.${ext}`;
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

  /** The outbound artifact law (plan §2.1, mirror of the kernel degrade law):
   *  every ref under the local caps (the facet's own numbers) downloads from
   *  the link plane and uploads as a channel file; anything over cap, without
   *  a download seam, or failing to download/upload degrades to an honest
   *  `attachment available: <storageUrl>` text tail. Never a rejection: the
   *  text post always ships. Returns the final post text + the file ids. */
  async function attachArtifacts(
    channelId: string,
    text: string,
    artifacts: ArtifactRef[],
  ): Promise<{ message: string; fileIds: string[] }> {
    const fileIds: string[] = [];
    const degrades: string[] = [];
    for (const [index, ref] of artifacts.entries()) {
      const bytes =
        index < MEDIA_MAX_COUNT && deps.media !== undefined && ref.size <= MEDIA_MAX_BYTES
          ? await deps.media
              .download(ref.artifactId)
              .then((body) => (body.byteLength <= MEDIA_MAX_BYTES ? body : null))
              .catch((error: unknown) => {
                logger.warn("outbound artifact download failed: degrading to a link line", {
                  artifactId: ref.artifactId,
                  error: String(error),
                });
                return null;
              })
          : null;
      if (bytes === null) {
        degrades.push(ref.storageUrl);
        continue;
      }
      try {
        const uploaded = await api.uploadFile(channelId, fileNameFor(ref), bytes, ref.mediaType);
        fileIds.push(uploaded.id);
      } catch (error) {
        logger.warn("outbound artifact upload failed: degrading to a link line", {
          artifactId: ref.artifactId,
          error: String(error),
        });
        degrades.push(ref.storageUrl);
      }
    }
    const message =
      degrades.length === 0
        ? text
        : `${text}${degrades.map((url) => `\nattachment available: ${url}`).join("")}`;
    return { message, fileIds };
  }

  /** The shared send core of reply + post: attach under the caps, then one
   *  post carrying `file_ids` (or plain text when nothing attached). */
  async function deliver(
    channelId: string,
    text: string,
    artifacts: ArtifactRef[],
    rootId: string | undefined,
  ): Promise<{ postId: string; message: string }> {
    const { message, fileIds } = await attachArtifacts(channelId, text, artifacts);
    const postId =
      fileIds.length > 0
        ? await api.createPostWithFiles(channelId, message, fileIds, rootId)
        : await api.createPost(channelId, message, rootId);
    return { postId, message };
  }

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
          const { postId, message } = await deliver(
            channelId,
            data.text,
            data.artifacts ?? [],
            rootId,
          );
          state.recordPost({
            idempotencyKey: data.idempotencyKey,
            providerObjectId: postId,
            channelId,
            rootId: rootId ?? "",
            kind: "post",
            createdAt: Date.now(),
          });
          await publishEcho(ctx, channelId, postId, rootId, message, data.target.conversationKind);
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
          const { postId, message } = await deliver(
            channelId,
            data.text,
            data.artifacts ?? [],
            undefined,
          );
          state.recordPost({
            idempotencyKey: data.idempotencyKey,
            providerObjectId: postId,
            channelId,
            rootId: "",
            kind: "post",
            createdAt: Date.now(),
          });
          await publishEcho(
            ctx,
            channelId,
            postId,
            undefined,
            message,
            data.target.conversationKind,
          );
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
        case CONTEXT_FETCH_COMMAND:
          return await answerContextFetch(api, identity.providerInstanceId, cmd, ctx);
        default:
          return { kind: "unsupported" };
      }
    },
  };
}
