// @ofac/gitlab-adapter — the inbound git provider module: a link-plane
// endpoint on the @ofac/provider-core adapter face (enroll → capabilities →
// SignalR + webhook fan-out receiver). The kernel sink
// (`POST /api/ingress/webhooks/gitlab`, the only web-exposed point) rides
// verbatim GitLab system-hook deliveries here as `command.provider.webhook.v1`;
// the provider-core receiver seam verifies the `X-Gitlab-Token` shared secret
// (constant-time — GitLab has no body HMAC, verification is provider-local)
// and runs the GitLab→domain normalizer, publishing the central forge domain
// events (data.provider = "gitlab"). Outbound
// command.communication.reply.v1 executes as a bot-identity note under the
// comment-ledger idempotency law. Stateless apart from the SQLite comment
// ledger — deployable anywhere the kernel link plane is dialable.
import { createServer, type Server } from "node:http";
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
import type { LinkClient, LinkHandler, LinkLogger, CommandOutcome } from "@ofac/workspace-link";
import { deriveTenant, type AdapterConfig } from "./config.js";
import { AdapterState } from "./comment-ledger.js";
import { GitLabClient, type GitLabApi } from "./gitlab-client.js";
import {
  normalizeIssueHook,
  normalizeMergeRequestHook,
  normalizeNoteHook,
  normalizePipeline,
  type NormalizeContext,
  type NormalizedEvent,
  type Payload,
} from "./normalize.js";
import { obj, num, str } from "@ofac/provider-core";
import {
  runProviderAdapter,
  verifyTokenToken,
  type WebhookDelivery,
  type WebhookNormalizer,
} from "@ofac/provider-core/node";

export {
  AdapterConfigError,
  deriveTenant,
  parseEnvConfig,
  resolveBotToken,
  type AdapterConfig,
} from "./config.js";
export {
  GitLabError,
  GitLabClient,
  type GitLabApi,
  type GitLabIssue,
  type GitLabMergeRequest,
  type GitLabMergeRequestDetail,
  type GitLabNote,
} from "./gitlab-client.js";
export { AdapterState, type CommentLedgerEntry } from "./comment-ledger.js";
export {
  CHANGE_REQUEST_EVENT,
  ISSUE_EVENT,
  MENTION_EVENT,
  PIPELINE_EVENT,
  REVIEW_EVENT,
  normalizeIssueHook,
  normalizeMergeRequestHook,
  normalizeNoteHook,
  normalizePipeline,
  type NormalizeContext,
  type NormalizedEvent,
} from "./normalize.js";

export const CMD_REPLY = "org.facilitator.command.communication.reply.v1";
export const CMD_REACT = "org.facilitator.command.communication.react.v1";
export const CMD_UPDATE = "org.facilitator.command.communication.update.v1";
export const CMD_ACK = "org.facilitator.command.communication.acknowledge.v1";

/** GitLab award_emoji names (the bare subset every GitLab ships, no custom
 *  emojis); the kernel speaks emoji names. Unmapped emoji are refused
 *  honestly, never silently downgraded to a different face. */
const AWARD_MAP: Record<string, string> = {
  "+1": "thumbsup",
  "-1": "thumbsdown",
  laugh: "okay_hand",
  confused: "confused",
  hooray: "tada",
  rocket: "rocket",
  eyes: "eyes",
  white_check_mark: "white_check_mark",
};

function isNotFound(error: unknown): boolean {
  return (
    typeof error === "object" &&
    error !== null &&
    "status" in error &&
    (error as { status: unknown }).status === 404
  );
}

/** The capability manifest this adapter advertises (POST /api/link/v1/capabilities).
 *  The full communication command family (E3). */
export function gitlabAdapterManifest(team: string): CapabilityManifestV1 {
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

function replyData(cmd: EnvelopeCommand): CommunicationReplyCommandV1Data | undefined {
  const data = cmd.data;
  if (typeof data !== "object" || data === null) return undefined;
  for (const key of ["commandId", "idempotencyKey", "target", "text"]) {
    if ((data as Record<string, unknown>)[key] === undefined) return undefined;
  }

  return data as unknown as CommunicationReplyCommandV1Data;
}

export interface HandlerDeps {
  /** Undefined when the adapter has no bot token: replies then answer
   *  `unsupported` as a receipt instead of throwing. */
  api: GitLabApi | undefined;
  state: AdapterState;
  logger: LinkLogger;
}

/** The reply law: GitLab notes live on TWO resources (issues, MRs) with no
 *  unified endpoint, and the reply envelope cannot tell them apart — so the
 *  note is resolved provider-side exactly like forgejo's single endpoint:
 *  try the issue, a 404 re-tries the MR. The comment ledger (checked BEFORE
 *  any mutation) keeps a delivery replay free of provider writes. */
export function createGitLabHandler(deps: HandlerDeps): LinkHandler {
  const { state, logger } = deps;
  return {
    async onCommand(cmd: EnvelopeCommand): Promise<CommandOutcome> {
      if (cmd.type === CMD_REACT || cmd.type === CMD_UPDATE || cmd.type === CMD_ACK) {
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

      let note;
      try {
        note = await deps.api.postIssueNote(repo, Number(number), data.text);
      } catch (error) {
        const notAnIssue =
          typeof error === "object" &&
          error !== null &&
          "status" in error &&
          (error as { status: unknown }).status === 404;
        if (!notAnIssue) throw error;
        note = await deps.api.postMrNote(repo, Number(number), data.text);
      }

      state.recordComment({
        idempotencyKey: data.idempotencyKey,
        providerObjectId: String(note.id),
        repo,
        number: String(number),
        createdAt: Date.now(),
      });
      logger.info("note posted", {
        repo,
        number,
        noteId: note.id,
        commandId: data.commandId,
      });
      return { kind: "accepted" };
    },
  };
}

/** The react/update/acknowledge arms. GitLab notes live on TWO resources
 *  with no unified endpoint, so like the reply law the resource is resolved
 *  provider-side (try the issue, a 404 re-tries the MR); the ledger keeps a
 *  replay free of provider writes and the update law owns the edit check. */
async function nonReplyCommand(deps: HandlerDeps, cmd: EnvelopeCommand): Promise<CommandOutcome> {
  const { state, logger } = deps;
  const data = cmd.data as
    | (Partial<CommunicationReactCommandV1Data> &
        Partial<CommunicationUpdateCommandV1Data> &
        Partial<CommunicationAcknowledgeCommandV1Data>)
    | undefined;
  if (typeof data !== "object" || data === null || data.idempotencyKey === undefined) {
    return { kind: "rejected", reason: `malformed-${cmd.type.includes("react") ? "react" : cmd.type.includes("update") ? "update" : "acknowledge"}-data` };
  }
  if (cmd.type === CMD_ACK) {
    // No provider side effect exists for an acknowledgement on a code host;
    // the ledger check IS the law.
    void state.findComment(data.idempotencyKey);
    return { kind: "accepted" };
  }

  if (deps.api === undefined) {
    return { kind: "unsupported", reason: "no-bot-token" };
  }
  const api = deps.api;
  if (state.findComment(data.idempotencyKey) !== undefined) {
    return { kind: "accepted" };
  }
  const target = data.target;
  const repo = target?.channelOrResourceId;
  const iid = target?.rootThreadId ?? target?.subjectRef;
  const noteId = target?.parentMessageId;
  if (repo === undefined || iid === undefined || noteId === undefined) {
    return { kind: "rejected", reason: "target-missing-note" };
  }

  if (cmd.type === CMD_REACT) {
    const emoji = data.emojiName;
    const name = emoji === undefined ? undefined : AWARD_MAP[emoji];
    if (name === undefined) {
      return { kind: "rejected", reason: "unsupported-emoji" };
    }
    try {
      await api.awardIssueNoteEmoji(repo, Number(iid), Number(noteId), name);
    } catch (error) {
      if (!isNotFound(error)) throw error;
      await api.awardMrNoteEmoji(repo, Number(iid), Number(noteId), name);
    }

    state.recordComment({
      idempotencyKey: data.idempotencyKey,
      providerObjectId: `award:${noteId}:${name}`,
      repo,
      number: iid,
      createdAt: Date.now(),
    });
    logger.info("note awarded", { repo, iid, noteId, name, commandId: cmd.commandId });
    return { kind: "accepted" };
  }

  const text = data.text;
  if (text === undefined) {
    return { kind: "rejected", reason: "malformed-update-data" };
  }
  // The update law: only a note this adapter created may be edited.
  if (state.findCommentByObjectId(noteId) === undefined) {
    return { kind: "rejected", reason: "unknown-note" };
  }

  try {
    await api.editIssueNote(repo, Number(iid), Number(noteId), text);
  } catch (error) {
    if (!isNotFound(error)) throw error;
    await api.editMrNote(repo, Number(iid), Number(noteId), text);
  }

  state.recordComment({
    idempotencyKey: data.idempotencyKey,
    providerObjectId: noteId,
    repo,
    number: iid,
    createdAt: Date.now(),
  });
  logger.info("note updated", { repo, iid, noteId, commandId: cmd.commandId });
  return { kind: "accepted" };
}

export interface GitLabNormalizerDeps {
  ctx: NormalizeContext;
  /** Shared secret — the same `.env` key the webhook registration sends in
   *  `X-Gitlab-Token`. */
  webhookToken: string;
  /** Undefined when the adapter has no bot token: pipeline deliveries whose
   *  number needs the sha→MR lookup (the only I/O arm) then drop, everything
   *  else normalizes. */
  api: GitLabApi | undefined;
  logger: LinkLogger;
}

/** The webhook edge, fan-out form: the kernel sink rode verbatim bytes +
 *  headers here; the `X-Gitlab-Token` shared secret verifies constant-time
 *  BEFORE any parsing (GitLab has no body HMAC — the token IS the proof,
 *  which is exactly why verification is provider-local). Bad/missing token ⇒
 *  `bad-signature` (the receiver answers a `rejected` receipt). A
 *  well-tokened delivery that is unmappable ⇒ zero events (audit line).
 *  `X-Request-Id` (GitLab's per-delivery id) is the providerDeliveryId —
 *  passed through, never synthesized. */
export function createGitLabNormalizer(deps: GitLabNormalizerDeps): WebhookNormalizer {
  const { ctx, webhookToken, api, logger } = deps;

  return async (delivery: WebhookDelivery) => {
    if (!verifyTokenToken(delivery.headers["x-gitlab-token"], webhookToken)) {
      return { kind: "bad-signature" };
    }

    let parsed: unknown;
    try {
      parsed = JSON.parse(delivery.rawBody.toString("utf8"));
    } catch {
      return { kind: "normalized", events: [] };
    }

    if (typeof parsed !== "object" || parsed === null || Array.isArray(parsed)) {
      return { kind: "normalized", events: [] };
    }

    const payload = parsed as Payload;
    const deliveryId = delivery.headers["x-request-id"];
    const eventKind = delivery.headers["x-gitlab-event"];
    if (typeof deliveryId !== "string" || deliveryId === "" || typeof eventKind !== "string") {
      return { kind: "normalized", events: [] };
    }

    const events = await dispatch(payload, deliveryId, eventKind, ctx, api, logger);
    return { kind: "normalized", events };
  };
}

async function dispatch(
  payload: Payload,
  deliveryId: string,
  eventKind: string,
  ctx: NormalizeContext,
  api: GitLabApi | undefined,
  logger: LinkLogger,
): Promise<NormalizedEvent[]> {
  if (eventKind === "Issue Hook") return normalizeIssueHook(payload, deliveryId, ctx);
  if (eventKind === "Note Hook") return normalizeNoteHook(payload, deliveryId, ctx);
  if (eventKind === "Merge Request Hook") {
    return normalizeMergeRequestHook(payload, deliveryId, ctx);
  }

  if (eventKind === "Pipeline Hook" || eventKind === "Build Hook") {
    // The number: a pipeline bound to an MR carries `merge_request.iid`
    // directly; otherwise the sha→MR resolution is I/O, so it lives HERE,
    // not in normalize.
    const inline = num(obj(payload["merge_request"])?.["iid"]);
    let number = inline;
    if (number === undefined) {
      const repo = str(obj(payload["project"])?.["path_with_namespace"]);
      const sha =
        str(obj(payload["object_attributes"])?.["sha"]) ??
        str(payload["after"]) ??
        str(payload["sha"]);
      if (repo !== undefined && sha !== undefined && api !== undefined) {
        try {
          const mrs = await api.openMrsForSha(repo, sha);
          number = mrs[0] === undefined ? undefined : String(mrs[0]);
        } catch (error) {
          logger.warn("pipeline sha resolution failed", { error: String(error) });
        }
      }
    }

    return normalizePipeline(payload, deliveryId, number, ctx);
  }

  logger.info("webhook dropped", { eventKind, deliveryId });
  return [];
}

export interface RunHandle {
  client: LinkClient;
  ready: Promise<void>;
  shutdown(): Promise<void>;
}

export interface RunDeps {
  /** Injected provider client (tests run the whole loop against a fake). */
  api?: GitLabApi;
  fetchImpl?: typeof fetch;
}

export async function run(
  config: AdapterConfig,
  logger: LinkLogger,
  deps: RunDeps = {},
): Promise<RunHandle> {
  const tenant = deriveTenant(config.gitlabUrl);
  const api =
    deps.api ??
    (config.botToken === undefined
      ? undefined
      : new GitLabClient(config.gitlabUrl, config.botToken, deps.fetchImpl));
  const state = new AdapterState(config.statePath);
  const ctx: NormalizeContext = {
    providerInstanceId: config.providerInstanceId,
    tenantOrOrganizationId: tenant,
    workLabels: new Set(config.workLabels.map((label) => label.toLowerCase())),
    botUser: config.botUser,
  };

  const handle = await runProviderAdapter(
    {
      kernelUrl: config.kernelUrl,
      enrollSecret: config.enrollSecret,
      endpointId: config.endpointId,
      team: config.team,
      statePath: config.statePath,
      manifest: gitlabAdapterManifest(config.team),
      normalizeWebhook: createGitLabNormalizer({
        ctx,
        webhookToken: config.webhookToken,
        api,
        logger,
      }),
      handler: createGitLabHandler({ api, state, logger }),
      logger,
      ...(deps.fetchImpl === undefined ? {} : { fetchImpl: deps.fetchImpl }),
    },
    `urn:ofac:provider:gitlab:${tenant}`,
  );

  // GET /healthz only: there is no webhook HTTP server (the kernel sink owns
  // the web edge); the port stays published so the e2e boot leg has a probe.
  let health: Server | undefined;
  const ready = handle.ready.then(async () => {
    health = createServer((req, res) => {
      if (req.url === "/healthz") {
        res.writeHead(200, { "content-type": "application/json" });
        res.end(JSON.stringify({ ok: true, endpointId: config.endpointId }));
        return;
      }

      res.writeHead(404);
      res.end();
    });
    const listening = Promise.withResolvers<void>();
    health.once("listening", listening.resolve);
    health.listen(config.port);
    await listening.promise;
    logger.info("gitlab adapter ready", {
      endpointId: config.endpointId,
      port: config.port,
      kernel: config.kernelUrl,
    });
  });

  return {
    client: handle.client,
    ready,
    async shutdown() {
      if (health !== undefined) {
        const { promise, resolve } = Promise.withResolvers<void>();
        health.close(() => resolve());
        await promise;
      }

      await handle.shutdown();
      state.close();
    },
  };
}
