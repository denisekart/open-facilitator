// Inbound media: download → verified artifact → event refs. The pipeline
// runs BEFORE publish so the event carries its sourceRefs. The LAW lives in
// @ofac/adapter (`media-law.ts`: dedupe on the provider id via the `files`
// ledger, size gates that SKIP but never fail the event, upload through the
// reused artifact client, the GUID-free logical-URI ref, ledger-before-
// publish); this module is the Forgejo leg of it — it only knows how to
// fetch a forge attachment and where the ledger rows live.
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
import type { AdapterState } from "./comment-ledger.js";
import type { ForgejoApi } from "./forgejo-client.js";
import type { Payload } from "./normalize.js";

export interface MediaPipelineDeps {
  api: ForgejoApi;
  state: AdapterState;
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

/** One webhook attachment (Forgejo/Gitea Attachment JSON, subset): the id is
 *  the dedupe key, the absolute URL the download seam. */
export interface ForgejoFileRef {
  id: string;
  name: string;
  size: number;
  /** Absolute download URL: `full_url` where the forge sends it, else the
   *  REST-name copy `download_url` (both shapes occur across 16.x). */
  downloadUrl: string;
  mediaType: string;
}

/** The attachments a container (webhook `comment` or `issue` object) carries. */
export function collectAttachments(container: Payload): ForgejoFileRef[] {
  const raw = container["attachments"];
  if (!Array.isArray(raw)) return [];
  const refs: ForgejoFileRef[] = [];
  for (const item of raw) {
    if (typeof item !== "object" || item === null) continue;
    const row = item as Record<string, unknown>;
    const id =
      typeof row["id"] === "number"
        ? String(row["id"])
        : typeof row["id"] === "string"
          ? row["id"]
          : undefined;
    const downloadUrl =
      typeof row["full_url"] === "string"
        ? row["full_url"]
        : typeof row["download_url"] === "string"
          ? row["download_url"]
          : undefined;
    if (id === undefined || downloadUrl === undefined) continue;
    refs.push({
      id,
      name: typeof row["name"] === "string" ? row["name"] : `attachment-${id}`,
      size: typeof row["size"] === "number" && Number.isFinite(row["size"]) ? row["size"] : 0,
      downloadUrl,
      mediaType:
        typeof row["content_type"] === "string" ? row["content_type"] : "application/octet-stream",
    });
  }
  return refs;
}

/** The stable ledger/event key of one webhook container (comment or issue). */
export function containerObjectId(container: Payload): string | undefined {
  const id = container["id"];
  if (typeof id === "number" && Number.isFinite(id)) return String(id);
  if (typeof id === "string" && id !== "") return id;
  const number = container["number"];
  if (typeof number === "number" && Number.isFinite(number)) return String(number);
  if (typeof number === "string" && number !== "") return number;
  return undefined;
}

export class MediaPipeline {
  private readonly deps: MediaPipelineDeps;
  private readonly artifactClient: ArtifactClient;
  private readonly ledger: MediaLedger;
  /** Refs collected for containers seen since boot: the normalizer reads them
   *  through ctx.mediaArtifactsFor. */
  private readonly refsByObject = new Map<string, ArtifactRef[]>();

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

  /** Upload every not-yet-known attachment of one webhook container (the
   *  `comment` or `issue` object); returns the refs for THAT object (dedupe
   *  ledger + per-object cache). Never throws: media failures log and shrink
   *  the ref list, they never fail the event. */
  async processContainer(container: Payload): Promise<ArtifactRef[]> {
    const objectId = containerObjectId(container);
    const refs: ArtifactRef[] = [];
    for (const attachment of collectAttachments(container)) {
      const ref = await this.refFor(objectId, attachment);
      if (ref !== null) refs.push(ref);
    }
    if (objectId !== undefined && refs.length > 0) this.refsByObject.set(objectId, refs);
    return refs;
  }

  /** All refs the pipeline holds for a container id (redelivery included). */
  artifactsFor(objectId: string): ArtifactRef[] {
    return this.refsByObject.get(objectId) ?? [];
  }

  private async refFor(
    objectId: string | undefined,
    attachment: ForgejoFileRef,
  ): Promise<ArtifactRef | null> {
    const outcome = await ingestMedia({
      loadBytes: async () => ({
        bytes: await this.deps.api.downloadAsset(attachment.downloadUrl),
        mediaType: attachment.mediaType,
      }),
      mediaType: attachment.mediaType,
      uniqueId: attachment.id,
      declaredBytes: attachment.size,
      maxBytes: this.deps.maxBytes,
      enabled: this.deps.enabled,
      ledger: this.ledger,
      client: this.artifactClient,
      logger: this.deps.logger,
      endpointId: this.deps.endpointId,
      provenance: objectId === undefined ? {} : { messageId: objectId },
    });
    return outcome.kind === "stored" ? outcome.ref : null;
  }
}

/** The kernel answers the stored digest; a store that answers none gets the
 *  client-side sha256 (identical bytes, identical law). Re-exported from the
 *  shared law so import sites share one definition. */
export { ensureDigest };

/** Outbound download helper (the reply-with-artifact path): GET the
 *  link-plane content route with the link bearer token, following a 302 to
 *  a presigned URL when the kernel redirects. Shared-law wrapper with the
 *  telegram-leg-compatible positional signature. */
export async function fetchArtifactContent(
  kernelUrl: string,
  artifactId: string,
  token: () => Promise<string>,
  fetchImpl: typeof fetch = fetch,
): Promise<Uint8Array> {
  return fetchContentShared({ baseUrl: kernelUrl, token, fetchImpl }, artifactId);
}
