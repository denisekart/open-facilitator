// Inbound media: download → verified artifact → event refs. The pipeline
// runs BEFORE publish so the event carries its sourceRefs. The LAW lives in
// @ofac/adapter (`media-law.ts`: dedupe on the provider file id via the
// adapter's `files` ledger, size gates that SKIP but never fail the event,
// upload through the reused artifact client, the GUID-free logical-URI ref,
// ledger-before-publish); this module is the Mattermost leg of it — it only
// knows how to fetch a Mattermost file (mm-client downloadFile) and where
// the ledger rows live (post-ledger `files`).
import type { ArtifactRef } from "@ofac/protocol";
import {
  createArtifactClient,
  fetchArtifactContent as fetchContentShared,
  ingestMedia,
  type ArtifactClient,
} from "@ofac/adapter";
import type { LinkLogger } from "@ofac/workspace-link";
import { collectFileInfos, type MmFileInfo, type MmPost } from "./normalize.js";
import type { MattermostApi } from "./mm-client.js";
import type { AdapterState } from "./post-ledger.js";

export interface MediaPipelineDeps {
  api: MattermostApi;
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

export class MediaPipeline {
  private readonly deps: MediaPipelineDeps;
  private readonly artifactClient: ArtifactClient;
  /** Refs collected for posts seen since boot: the normalizer reads them
   *  through ctx.mediaArtifactsFor. */
  private readonly refsByPost = new Map<string, ArtifactRef[]>();

  constructor(deps: MediaPipelineDeps) {
    this.deps = deps;
    this.artifactClient = createArtifactClient({
      baseUrl: deps.kernelUrl,
      token: deps.token,
      ...(deps.fetchImpl === undefined ? {} : { fetchImpl: deps.fetchImpl }),
    });
  }

  /** Upload every not-yet-known attachment of one post; returns the refs for
   *  THAT post (dedupe ledger + per-post cache). Never throws: media failures
   *  log and shrink the ref list, they never fail the event. */
  async processPost(post: MmPost): Promise<ArtifactRef[]> {
    const refs: ArtifactRef[] = [];
    for (const file of collectFileInfos(post)) {
      const ref = await this.refFor(post.id, file);
      if (ref !== null) refs.push(ref);
    }
    if (refs.length > 0) this.refsByPost.set(post.id, refs);
    return refs;
  }

  /** All refs the pipeline holds for a post (deduped re-sighting included). */
  artifactsFor(postId: string): ArtifactRef[] {
    return this.refsByPost.get(postId) ?? [];
  }

  private async refFor(postId: string, file: MmFileInfo): Promise<ArtifactRef | null> {
    const outcome = await ingestMedia({
      loadBytes: async () => {
        const bytes = await this.deps.api.downloadFile(file.id);
        return {
          bytes,
          mediaType: file.mime_type ?? "application/octet-stream",
        };
      },
      mediaType: file.mime_type ?? "application/octet-stream",
      // The Mattermost file id IS the provider's stable per-file identity.
      uniqueId: file.id,
      declaredBytes: file.size ?? 0,
      maxBytes: this.deps.maxBytes,
      enabled: this.deps.enabled,
      ledger: this.deps.state.mediaLedger(),
      client: this.artifactClient,
      logger: this.deps.logger,
      endpointId: this.deps.endpointId,
      provenance: { messageId: postId },
    });
    return outcome.kind === "stored" ? outcome.ref : null;
  }
}

/** The kernel answers the stored digest; a store that answers none gets the
 *  client-side sha256 (identical bytes, identical law). Re-exported from the
 *  shared law so import sites ride one module. */
export { ensureDigest } from "@ofac/adapter";

/** Outbound download helper (the reply-with-artifact path): GET the
 *  link-plane content route with the link bearer token, following a 302 to
 *  a presigned URL when the kernel redirects. Shared-law wrapper keeping the
 *  telegram adapter's positional signature. */
export async function fetchArtifactContent(
  kernelUrl: string,
  artifactId: string,
  token: () => Promise<string>,
  fetchImpl: typeof fetch = fetch,
): Promise<Uint8Array> {
  return fetchContentShared({ baseUrl: kernelUrl, token, fetchImpl }, artifactId);
}
