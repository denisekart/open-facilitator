// Inbound media: download → verified artifact → event refs. The pipeline
// runs BEFORE publish so the event carries its sourceRefs:
//   1. collect attachments (photos: the LARGEST of Telegram's 3 variants);
//   2. dedupe on file_unique_id via the `files` ledger — a known id reuses
//      the stored artifactId (the same image forwarded twice costs one
//      upload and one ref);
//   3. SKIP (never fail the event) when the declared or actual size exceeds
//      the cap, or when media is disabled — always a structured log;
//   4. getFile → download <apiBase>/file/bot<token>/<file_path>;
//   5. upload with the REUSED @ofac/adapter artifact client — deliberately
//      NO workId/assignmentId (no Work exists yet for this message); the
//      row lands on the adapter's enrollment team, which is what keeps the
//      team-scoped link-plane PUT authorized;
//   6. build the ArtifactRef exactly per common.v1: storageUrl is the
//      GUID-free logical URI artifact://provider/<fileUniqueId>/<artifactId>,
//      kind chat.attachment;
//   7. record in `files` BEFORE publish so a redelivered update converges
//      without a second upload.
import { createHash } from "node:crypto";
import type { ArtifactRef } from "@ofac/protocol";
import { createArtifactClient, type ArtifactClient } from "@ofac/adapter";
import type { LinkLogger } from "@ofac/workspace-link";
import type { ChatLedger } from "./chat-ledger.js";
import { collectAttachments, type TgFileRef, type TgMessage } from "./normalize.js";
import type { TelegramApi } from "./tg-client.js";

export interface MediaPipelineDeps {
  api: TelegramApi;
  state: ChatLedger;
  /** Kernel base URL — the link-plane artifact client + content GET ride it. */
  kernelUrl: string;
  /** The live link token (rotates on 401 with the enrollment). */
  token: () => Promise<string>;
  endpointId: string;
  maxBytes: number;
  enabled: boolean;
  logger: LinkLogger;
  fetchImpl?: typeof fetch;
}

export class MediaPipeline {
  private readonly deps: MediaPipelineDeps;
  private readonly artifactClient: ArtifactClient;
  /** Refs collected for messages seen since boot: the normalizer reads them
   *  through ctx.mediaArtifactsFor. */
  private readonly refsByMessage = new Map<string, ArtifactRef[]>();

  constructor(deps: MediaPipelineDeps) {
    this.deps = deps;
    this.artifactClient = createArtifactClient({
      baseUrl: deps.kernelUrl,
      token: deps.token,
      ...(deps.fetchImpl === undefined ? {} : { fetchImpl: deps.fetchImpl }),
    });
  }

  /** Upload every not-yet-known attachment of one message; returns the refs
   *  for THAT message (dedupe ledger + per-message cache). Never throws:
   *  media failures log and shrink the ref list, they never fail the event. */
  async processMessage(message: TgMessage): Promise<ArtifactRef[]> {
    const messageId = String(message.message_id);
    const refs: ArtifactRef[] = [];
    for (const attachment of collectAttachments(message)) {
      const ref = await this.refFor(messageId, attachment);
      if (ref !== null) refs.push(ref);
    }
    if (refs.length > 0) this.refsByMessage.set(messageId, refs);
    return refs;
  }

  /** All refs the pipeline holds for a message (deduped re-forward included). */
  artifactsFor(messageId: string): ArtifactRef[] {
    return this.refsByMessage.get(messageId) ?? [];
  }

  private async refFor(messageId: string, attachment: TgFileRef): Promise<ArtifactRef | null> {
    const { state, logger, maxBytes, enabled } = this.deps;
    if (!enabled) {
      logger.info("media disabled: noted and skipped", {
        fileUniqueId: attachment.file_unique_id,
        messageId,
      });
      return null;
    }
    const known = state.findFile(attachment.file_unique_id);
    if (known !== undefined) {
      return refOf(
        known.fileUniqueId,
        known.artifactId,
        known.mediaType,
        known.size,
        known.digest,
        this.deps.endpointId,
      );
    }
    if ((attachment.file_size ?? 0) > maxBytes) {
      logger.warn("media oversized at declaration: skipped", {
        fileUniqueId: attachment.file_unique_id,
        messageId,
        declared: attachment.file_size ?? 0,
        maxBytes,
      });
      return null;
    }
    try {
      const file = await this.deps.api.getFile(attachment.file_id);
      if (file.file_path === undefined) throw new Error("getFile carried no file_path");
      const bytes = await this.deps.api.downloadFile(file.file_path);
      if (bytes.byteLength > maxBytes) {
        logger.warn("media oversized on download: skipped", {
          fileUniqueId: attachment.file_unique_id,
          messageId,
          actual: bytes.byteLength,
          maxBytes,
        });
        return null;
      }
      const mediaType =
        attachment.mime_type ??
        (file.file_path.endsWith(".jpg") ? "image/jpeg" : "application/octet-stream");
      const uploaded = await this.artifactClient.upload(bytes, {
        mediaType,
        retentionPolicy: "90d",
        actorEvidenceRef: `workload:${this.deps.endpointId}`,
      });
      const digest = ensureDigest(bytes, uploaded.digest);
      const ref = refOf(
        attachment.file_unique_id,
        uploaded.artifactId,
        mediaType,
        bytes.byteLength,
        digest,
        this.deps.endpointId,
      );
      state.recordFile({
        fileUniqueId: attachment.file_unique_id,
        artifactId: uploaded.artifactId,
        mediaType,
        size: bytes.byteLength,
        digest,
      });
      return ref;
    } catch (error) {
      // The never-fail-the-event law: media errors are a log, the text
      // event still publishes (its text falls back to "(attachment)").
      logger.warn("inbound media upload failed: event publishes without the ref", {
        fileUniqueId: attachment.file_unique_id,
        messageId,
        error: String(error),
      });
      return null;
    }
  }
}

/** The ArtifactRef construction law (common.v1 artifactRef): the seven
 *  required fields + kind chat.attachment; the storageUrl is a logical URI
 *  keyed by the PROVIDER file_unique_id, so it stays non-authoritative and
 *  stable across re-forwards. */
function refOf(
  fileUniqueId: string,
  artifactId: string,
  mediaType: string,
  size: number,
  digest: string,
  endpointId: string,
): ArtifactRef {
  return {
    artifactId,
    mediaType,
    size,
    digest,
    storageUrl: `artifact://provider/${fileUniqueId}/${artifactId}`,
    createdBy: `link:${endpointId}`,
    createdAt: new Date().toISOString(),
    kind: "chat.attachment",
  };
}

/** The kernel answers the stored digest; a store that answers none gets the
 *  client-side sha256 (identical bytes, identical law). */
function ensureDigest(bytes: Uint8Array, digest: string): string {
  if (/^sha256:[0-9a-f]{64}$/.test(digest)) return digest;
  return "sha256:" + createHash("sha256").update(bytes).digest("hex");
}

/** Outbound download helper (the reply-with-artifact path): GET the
 *  link-plane content route with the link bearer token, following a 302 to
 *  a presigned URL when the kernel redirects. */
export async function fetchArtifactContent(
  kernelUrl: string,
  artifactId: string,
  token: () => Promise<string>,
  fetchImpl: typeof fetch = fetch,
): Promise<Uint8Array> {
  const response = await fetchImpl(
    `${kernelUrl.replace(/\/+$/, "")}/api/link/v1/artifacts/${artifactId}/content`,
    { headers: { authorization: `Bearer ${await token()}` }, redirect: "follow" },
  );
  if (!response.ok) {
    throw new Error(`artifact ${artifactId} content GET failed: HTTP ${response.status}`);
  }
  return new Uint8Array(await response.arrayBuffer());
}
