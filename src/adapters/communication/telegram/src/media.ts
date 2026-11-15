// Inbound media: download → verified artifact → event refs. The pipeline
// runs BEFORE publish so the event carries its sourceRefs. The LAW lives in
// @ofac/adapter (`media-law.ts`: dedupe on file_unique_id via the `files`
// ledger, size gates that SKIP but never fail the event, upload through the
// reused artifact client, the GUID-free logical-URI ref, ledger-before-
// publish); this module is the Telegram leg of it — it only knows how to
// fetch a Telegram file and where the ledger rows live.
import type { ArtifactRef } from "@ofac/protocol";
import {
  createArtifactClient,
  ensureDigest,
  fetchArtifactContent as fetchContentShared,
  ingestMedia,
  type ArtifactClient,
  type MediaLedger,
} from "@ofac/adapter";
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
  private readonly ledger: MediaLedger;
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
    this.ledger = {
      known: (uniqueId) => {
        const row = deps.state.findFile(uniqueId);
        return row === undefined
          ? undefined
          : {
              uniqueId: row.fileUniqueId,
              artifactId: row.artifactId,
              mediaType: row.mediaType,
              size: row.size,
              digest: row.digest,
            };
      },
      remember: (entry) =>
        deps.state.recordFile({
          fileUniqueId: entry.uniqueId,
          artifactId: entry.artifactId,
          mediaType: entry.mediaType,
          size: entry.size,
          digest: entry.digest,
        }),
    };
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
    const outcome = await ingestMedia({
      loadBytes: async () => {
        const file = await this.deps.api.getFile(attachment.file_id);
        if (file.file_path === undefined) throw new Error("getFile carried no file_path");
        const bytes = await this.deps.api.downloadFile(file.file_path);
        return {
          bytes,
          mediaType:
            attachment.mime_type ??
            (file.file_path.endsWith(".jpg") ? "image/jpeg" : "application/octet-stream"),
        };
      },
      mediaType: attachment.mime_type ?? "application/octet-stream",
      uniqueId: attachment.file_unique_id,
      declaredBytes: attachment.file_size ?? 0,
      maxBytes: this.deps.maxBytes,
      enabled: this.deps.enabled,
      ledger: this.ledger,
      client: this.artifactClient,
      logger: this.deps.logger,
      endpointId: this.deps.endpointId,
      provenance: { messageId },
    });
    return outcome.kind === "stored" ? outcome.ref : null;
  }
}

/** The kernel answers the stored digest; a store that answers none gets the
 *  client-side sha256 (identical bytes, identical law). Re-exported from the
 *  shared law so existing import sites keep compiling. */
export { ensureDigest };

/** Outbound download helper (the reply-with-artifact path): GET the
 *  link-plane content route with the link bearer token, following a 302 to
 *  a presigned URL when the kernel redirects. Shared-law wrapper keeping the
 *  old positional signature. */
export async function fetchArtifactContent(
  kernelUrl: string,
  artifactId: string,
  token: () => Promise<string>,
  fetchImpl: typeof fetch = fetch,
): Promise<Uint8Array> {
  return fetchContentShared({ baseUrl: kernelUrl, token, fetchImpl }, artifactId);
}
