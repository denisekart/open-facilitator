// Inbound media: download → verified artifact → event refs. The pipeline
// runs BEFORE publish so the event carries its sourceRefs. The LAW lives in
// `media-law.ts` in @ofac/adapter (dedupe on the provider id via the `files`
// ledger, size gates that SKIP but never fail the event, upload through the
// reused artifact client, the GUID-free logical-URI ref, ledger-before-
// publish); this module is the GitLab leg of it. GitLab differs from its
// forge siblings in ONE shape: a webhook payload carries NO structured
// attachment list — a pasted image exists only as the `![name](/uploads/…)`
// markdown the web UI wrote into the body — so the file refs are EXTRACTED
// from the note/issue markdown (normalize.collectUploadUrls) and the
// provider's stable identity for the dedupe ledger is the sha256 of the full
// upload URL (GitLab mints a unique secret per upload, so the URL IS the
// per-file identity).
import { createHash } from "node:crypto";
import type { ArtifactRef } from "@ofac/protocol";
import {
  createArtifactClient,
  fetchArtifactContent as fetchContentShared,
  ingestMedia,
  type ArtifactClient,
  type MediaLedger,
} from "@ofac/adapter";
import type { LinkLogger } from "@ofac/workspace-link";
import type { AdapterState } from "./comment-ledger.js";
import type { GitLabApi } from "./gitlab-client.js";
import { collectUploadUrls, type Payload } from "./normalize.js";

export interface MediaPipelineDeps {
  api: GitLabApi;
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

/** One inbound upload extracted from a body's markdown: the dedupe key is
 *  the hex-first-16 of the sha256 of the FULL URL (GitLab's `/uploads/<secre
 *  t>/<name>` path is unique per upload), the URL the download seam. */
export interface GitLabFileRef {
  id: string;
  /** The markdown URL as written — root-relative (`/uploads/…`) or absolute;
   *  downloadAsset joins root-relative paths with the configured base. */
  downloadUrl: string;
}

/** The ledger/event key of one webhook container: GitLab's own object id
 *  (the note or issue `object_attributes.id`). */
export function containerObjectId(container: Payload): string | undefined {
  const id = container["id"];
  if (typeof id === "number" && Number.isFinite(id)) return String(id);
  if (typeof id === "string" && id !== "") return id;
  return undefined;
}

/** The stable dedupe key of one upload URL: sha256 hex, first 16 chars. */
export function uploadUniqueId(url: string): string {
  return createHash("sha256").update(url).digest("hex").slice(0, 16);
}

/** The uploads one webhook body (a note's text or an issue/MR description)
 *  carries, each with its ledger key. */
export function collectUploads(container: Payload): GitLabFileRef[] {
  const body =
    typeof container["note"] === "string"
      ? container["note"]
      : typeof container["description"] === "string"
        ? container["description"]
        : undefined;
  return collectUploadUrls(body).map((url) => ({ id: uploadUniqueId(url), downloadUrl: url }));
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
    this.ledger = deps.state.mediaLedger();
  }

  /** Upload every not-yet-known image of one webhook container (the
   *  `object_attributes` the note/issue body rides in); returns the refs for
   *  THAT object (dedupe ledger + per-object cache). Never throws: media
   *  failures log and shrink the ref list, they never fail the event. */
  async processContainer(container: Payload): Promise<ArtifactRef[]> {
    const objectId = containerObjectId(container);
    const refs: ArtifactRef[] = [];
    for (const upload of collectUploads(container)) {
      const ref = await this.refFor(objectId, upload);
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
    upload: GitLabFileRef,
  ): Promise<ArtifactRef | null> {
    const outcome = await ingestMedia({
      // GitLab declares no size on a markdown URL — the gate runs on the
      // actual bytes (ingestMedia's `actual` leg).
      loadBytes: async () => ({
        bytes: await this.deps.api.downloadAsset(upload.downloadUrl),
        mediaType: mediaTypeFor(upload.downloadUrl),
      }),
      mediaType: mediaTypeFor(upload.downloadUrl),
      uniqueId: upload.id,
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

/** The markdown URL carries no content-type; GitLab's own upload naming law
 *  keeps the extension, so the media type is inferred from the path. */
export function mediaTypeFor(url: string): string {
  const path = url.split("?")[0] ?? url;
  const name = path.split("/").pop()?.toLowerCase() ?? "";
  if (name.endsWith(".png")) return "image/png";
  if (name.endsWith(".jpg") || name.endsWith(".jpeg")) return "image/jpeg";
  if (name.endsWith(".gif")) return "image/gif";
  if (name.endsWith(".webp")) return "image/webp";
  if (name.endsWith(".svg")) return "image/svg+xml";
  if (name.endsWith(".pdf")) return "application/pdf";
  if (name.endsWith(".txt") || name.endsWith(".md")) return "text/plain";
  return "application/octet-stream";
}

/** The kernel answers the stored digest; a store that answers none gets the
 *  client-side sha256 (identical bytes, identical law). Re-exported from the
 *  shared law so import sites share one definition. */
export { ensureDigest } from "@ofac/adapter";

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
