// Inbound media: download → verified artifact → event refs. The pipeline
// runs BEFORE publish so the event carries its sourceRefs. The LAW lives in
// @ofac/adapter (`media-law.ts`: dedupe on the provider id via the `files`
// ledger, size gates that SKIP but never fail the event, upload through the
// reused artifact client, the GUID-free logical-URI ref, ledger-before-
// publish); this module is the GitHub leg of it. GitHub's webhook issue/
// comment payloads carry NO structured attachments field — the uploads route
// answers only an `asset_url` — so inbound media rides the markdown: the
// pipeline scans the delivered body for image URLs on the configured
// uploads/assets hosts (normalize.collectAssetUrls) and downloads each
// through the client's downloadAsset seam.
import type { ArtifactRef } from "@ofac/protocol";
import {
  createArtifactClient,
  fetchArtifactContent as fetchContentShared,
  ingestMedia,
  type ArtifactClient,
} from "@ofac/adapter";
import type { LinkLogger } from "@ofac/workspace-link";
import type { AdapterState } from "./comment-ledger.js";
import { deriveUploadsBase, type GitHubApi } from "./github-client.js";
import { collectAssetUrls, type Payload } from "./normalize.js";

export interface MediaPipelineDeps {
  api: GitHubApi;
  state: AdapterState;
  /** Kernel base URL — the link-plane artifact client + content GET ride it. */
  kernelUrl: string;
  /** The live link token (rotates on 401 with the enrollment). */
  token: () => Promise<string>;
  endpointId: string;
  maxBytes: number;
  enabled: boolean;
  /** The hosts an image URL must live on to be fetched (assetHostsFor):
   *  the uploads host plus the install's own asset host. */
  assetHosts: ReadonlySet<string>;
  logger: LinkLogger;
  fetchImpl?: typeof fetch;
}

/** The hosts whose image URLs this deployment's own uploads/assets ride:
 *  the uploads base host, the REST base host (GHE serves both), and — on
 *  the public API — the html host GitHub mints asset URLs on. */
export function assetHostsFor(githubUrl: string): ReadonlySet<string> {
  const hosts = new Set<string>();
  try {
    hosts.add(new URL(githubUrl).hostname);
    hosts.add(new URL(deriveUploadsBase(githubUrl)).hostname);
    if (new URL(githubUrl).hostname === "api.github.com") hosts.add("github.com");
  } catch {
    // deriveTenant/parseEnvConfig already failed closed on an unusable base.
  }
  return hosts;
}

export class MediaPipeline {
  private readonly deps: MediaPipelineDeps;
  private readonly artifactClient: ArtifactClient;
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
  }

  /** Upload every not-yet-known image of one webhook container (the
   *  `comment`, `issue`, `review` or `pull_request` object): its markdown
   *  body is scanned for asset URLs on the configured hosts. Returns the refs
   *  for THAT object (dedupe ledger + per-object cache). Never throws: media
   *  failures log and shrink the ref list, they never fail the event. */
  async processContainer(container: Payload): Promise<ArtifactRef[]> {
    const objectId = containerObjectId(container);
    const body = typeof container["body"] === "string" ? container["body"] : "";
    const refs: ArtifactRef[] = [];
    for (const asset of collectAssetUrls(body, this.deps.assetHosts)) {
      const ref = await this.refFor(objectId, asset.url, asset.uniqueId);
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
    url: string,
    uniqueId: string,
  ): Promise<ArtifactRef | null> {
    const outcome = await ingestMedia({
      loadBytes: async () => ({
        bytes: await this.deps.api.downloadAsset(url),
        mediaType: "image/png",
      }),
      // The uploads route is an IMAGE route (its answer is an image asset);
      // the payload carries no content type, so the declared kind is an
      // image and the kernel's stored digest/size settle the facts.
      mediaType: "image/png",
      uniqueId,
      declaredBytes: 0,
      maxBytes: this.deps.maxBytes,
      enabled: this.deps.enabled,
      ledger: this.deps.state.mediaLedger(),
      client: this.artifactClient,
      logger: this.deps.logger,
      endpointId: this.deps.endpointId,
      provenance: objectId === undefined ? {} : { messageId: objectId },
    });
    return outcome.kind === "stored" ? outcome.ref : null;
  }
}

/** The stable ledger/event key of one webhook container (comment, issue or
 *  PR object): its id, falling back to the issue/PR number. */
export function containerObjectId(container: Payload): string | undefined {
  const id = container["id"];
  if (typeof id === "number" && Number.isFinite(id)) return String(id);
  if (typeof id === "string" && id !== "") return id;
  const number = container["number"];
  if (typeof number === "number" && Number.isFinite(number)) return String(number);
  if (typeof number === "string" && number !== "") return number;
  return undefined;
}

/** The kernel answers the stored digest; a store that answers none gets the
 *  client-side sha256 (identical bytes, identical law). Re-exported from the
 *  shared law so import sites ride one module. */
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
