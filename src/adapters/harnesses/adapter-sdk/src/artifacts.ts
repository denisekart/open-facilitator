// The artifact capability of `@ofac/adapter` (Plan 15): upload bytes to the
// kernel over the link plane and build/complete a Result Manifest — so a
// third-party harness gets spec §15.3's completion truth without touching
// wire details (envelopes, tickets, canonicalization, the digest law).
//
// Credential law (as-built Plan 13/14): the CHILD LINK TOKEN IS the workload
// credential. `POST {base}/api/link/v1/events` with a request_upload command
// (actorEvidenceRef omitted ⇒ workload-only allow, the AdmissionGate's
// absent-evidence law) answers 202 with the upload ticket IN-BAND on `data`;
// bytes then travel to `uploadUrl` when the kernel minted a presigned URL,
// else to `uploadLocation` on the same base (the kernel-proxied PUT, which
// counts + hashes + auto-confirms while storing).
import { createHash, randomUUID } from "node:crypto";
import {
  canonicalizeManifest,
  computeManifestDigest,
  type EnvelopeCommand,
  type EnvelopeEvent,
} from "@ofac/protocol";
import {
  buildEvent,
  dataschemaFor,
  trimSlash,
} from "@ofac/workspace-link";

const REQUEST_UPLOAD_TYPE = "org.facilitator.command.artifact.request_upload.v1";
const COMPLETED_TYPE = "org.facilitator.event.assignment.completed.v1";

export interface KernelHttpOptions {
  /** Kernel base URL, e.g. `http://kernel:8080` (link-plane surfaces live under `/api/link/v1`). */
  readonly baseUrl: string;
  /** Link token (child enrollment secret) or a factory for a rotating one. */
  readonly token: string | (() => Promise<string>);
  /** Injectable fetch (tests run against `startStubKernel`); default global fetch. */
  readonly fetchImpl?: typeof fetch;
}

export interface UploadOptions {
  readonly mediaType: string;
  readonly workId?: string;
  readonly assignmentId?: string;
  /** `permanent` | `archive` | `<n>d` (schema vocabulary). */
  readonly retentionPolicy?: string;
}

export interface ArtifactClient {
  /**
   * request_upload → PUT/presigned PUT the bytes → returns the kernel-verified
   * `{artifactId, digest}`. Idempotent by content: the commandId is derived
   * from the digest (kernel duplicate-ack law re-attaches the live ticket on a
   * re-delivery, so an interrupted upload resumes by calling again).
   */
  upload(bytes: Uint8Array, opts: UploadOptions): Promise<{ artifactId: string; digest: string }>;
}

export function createArtifactClient(opts: KernelHttpOptions): ArtifactClient {
  const base = trimSlash(opts.baseUrl);
  const doFetch: typeof fetch = opts.fetchImpl ?? ((...args) => fetch(...args));

  async function authHeaders(): Promise<Record<string, string>> {
    const token = typeof opts.token === "function" ? await opts.token() : opts.token;
    return { authorization: `Bearer ${token}` };
  }

  async function postEvent(command: EnvelopeCommand): Promise<Record<string, unknown>> {
    const response = await doFetch(`${base}/api/link/v1/events`, {
      method: "POST",
      headers: { ...(await authHeaders()), "content-type": "application/json" },
      body: JSON.stringify(command),
    });
    if (!response.ok && response.status !== 202) {
      throw new Error(
        `link-plane event post failed: ${response.status} ${await safeBody(response)}`,
      );
    }
    return (await response.json()) as Record<string, unknown>;
  }

  return {
    async upload(bytes, up) {
      const digest = "sha256:" + createHash("sha256").update(bytes).digest("hex");
      // Dedupe identity = content + provenance: the kernel's inbox answers a
      // re-delivery with `status:"duplicate"` + the still-live ticket
      // re-attached on `data`, so the same call simply resumes.
      const commandId = `cmd-art-${digest.slice(7, 39)}-${up.workId ?? "x"}-${up.assignmentId ?? "x"}`;
      const payload: Record<string, unknown> = {
        commandId,
        mediaType: up.mediaType,
        size: bytes.byteLength,
        digest,
        ...(up.workId !== undefined ? { workId: up.workId } : {}),
        ...(up.assignmentId !== undefined ? { assignmentId: up.assignmentId } : {}),
        ...(up.retentionPolicy !== undefined ? { retentionPolicy: up.retentionPolicy } : {}),
      };
      const command: EnvelopeCommand = {
        specversion: "1.0",
        id: randomUUID(),
        source: "urn:ofac:adapter",
        type: REQUEST_UPLOAD_TYPE,
        subject: `artifact/${commandId}`,
        time: new Date().toISOString(),
        datacontenttype: "application/json",
        dataschema: dataschemaFor(REQUEST_UPLOAD_TYPE),
        data: payload,
      };
      const ack = await postEvent(command);
      const ticket = (ack["data"] ?? {}) as Record<string, unknown>;
      const artifactId = String(ticket["artifactId"] ?? "");
      const uploadLocation = ticket["uploadLocation"];
      const uploadUrl = ticket["uploadUrl"];
      if (artifactId === "" || typeof uploadLocation !== "string") {
        throw new Error(
          `request_upload ack carried no ticket: ${JSON.stringify(ack).slice(0, 256)}`,
        );
      }
      const presigned = typeof uploadUrl === "string" && uploadUrl.length > 0;
      const target = presigned ? (uploadUrl as string) : `${base}${uploadLocation}`;
      const put = await doFetch(target, {
        method: "PUT",
        // A presigned URL carries its own authority; the proxied route
        // authenticates the link token.
        headers: presigned ? {} : await authHeaders(),
        body: new Blob([new Uint8Array(bytes)]),
      });
      if (!put.ok) {
        throw new Error(`artifact PUT failed: ${put.status} ${await safeBody(put)}`);
      }
      const stored = (await put.json().catch(() => ({}))) as Record<string, unknown>;
      return {
        artifactId,
        digest: typeof stored["digest"] === "string" ? stored["digest"] : digest,
      };
    },
  };
}

async function safeBody(response: Response): Promise<string> {
  try {
    return (await response.text()).slice(0, 256);
  } catch {
    return "<no body>";
  }
}

/** One `outputs[]` entry: an `artifactRef` (common.v1.json) — the 7 frozen
 *  fields plus the Plan 15 optional `kind`/`classification` taxonomy labels. */
export interface ResultManifestOutput {
  readonly artifactId: string;
  readonly mediaType: string;
  readonly size: number;
  readonly digest: string;
  /** Logical URI; the LAST path segment is the kernel artifact GUID. */
  readonly artifactUri: string;
  readonly createdBy: string;
  readonly createdAt: string;
  readonly kind?: string;
  readonly classification?: string;
}

export interface ResultManifestInput {
  manifestId: string;
  workId: string;
  assignmentId: string;
  revision?: number;
  revisionOf?: string;
  capability?: string;
  summary?: string;
  /** Compact structured class (spec §15.3 class 1). */
  structuredResult?: Record<string, unknown>;
  /** Document/binary class (class 2). */
  outputs: ResultManifestOutput[];
  /** External-resource class (class 3) — opaque pointers, never fetched. */
  externalRefs?: string[];
  sourceRefs?: string[];
  notes?: string;
  createdAt: string;
}

export interface ResultManifest {
  /** The document bytes — ALREADY the RFC 8785 canonical form (key-sorted, no insignificant whitespace, NFC). */
  document: Uint8Array;
  /** RFC 8785 digest of `document` (kernel recomputes + compares). */
  digest: string;
  /** The manifest as a parsed object (for inspection/tests). */
  manifest: Record<string, unknown>;
}

/** `artifact://{workId}/{assignmentId}/{artifactId}` — the logical-URI law:
 *  the resolver reads the kernel artifact GUID off the LAST path segment. */
export function artifactUri(workId: string, assignmentId: string, artifactId: string): string {
  return `artifact://${workId}/${assignmentId}/${artifactId}`;
}

/**
 * Build the immutable Result Manifest: serialize WITH the canonicalizer so
 * the emitted bytes equal the canonical form the kernel byte-checks (a
 * non-canonical submission is rejected), and stamp the digest so
 * `computeManifestDigest(parsed bytes) === digest` by construction. The
 * caller uploads `document` and completes with
 * `artifactUri(workId, assignmentId, upload.artifactId)` as the ref.
 */
export function buildResultManifest(input: ResultManifestInput): ResultManifest {
  const doc: Record<string, unknown> = {
    manifestId: input.manifestId,
    workId: input.workId,
    assignmentId: input.assignmentId,
    outputs: input.outputs.map((o) => ({
      artifactId: o.artifactId,
      mediaType: o.mediaType,
      size: o.size,
      digest: o.digest,
      storageUrl: o.artifactUri,
      createdBy: o.createdBy,
      createdAt: o.createdAt,
      ...(o.kind !== undefined ? { kind: o.kind } : {}),
      ...(o.classification !== undefined ? { classification: o.classification } : {}),
    })),
    ...(input.revision !== undefined ? { revision: input.revision } : {}),
    ...(input.revisionOf !== undefined ? { revisionOf: input.revisionOf } : {}),
    ...(input.capability !== undefined ? { capability: input.capability } : {}),
    ...(input.summary !== undefined ? { summary: input.summary } : {}),
    ...(input.structuredResult !== undefined ? { structuredResult: input.structuredResult } : {}),
    ...(input.externalRefs !== undefined ? { externalRefs: input.externalRefs } : {}),
    ...(input.sourceRefs !== undefined ? { sourceRefs: input.sourceRefs } : {}),
    ...(input.notes !== undefined ? { notes: input.notes } : {}),
    createdAt: input.createdAt,
  };
  const canonical = canonicalizeManifest(doc);
  return {
    document: new TextEncoder().encode(canonical),
    digest: computeManifestDigest(doc),
    manifest: JSON.parse(canonical) as Record<string, unknown>,
  };
}

/** Narrow view of a RunContext (works with the SDK's ctx and plain test objects). */
export type CompletedEventContext = {
  readonly executionId: string;
  readonly executionGeneration: number;
  readonly command: EnvelopeCommand;
};

/**
 * The completed-event payload law (spec §15.3 + Plan 15 amendment): four
 * frozen fields + the deprecated inline `{manifestId, outputs: []}` +
 * `resultManifestRef` (the uploaded manifest doc's logical URI) +
 * `resultDigest`. `ref` and `digest` ride together (schema if/then).
 */
export function buildCompletedEvent(
  ctx: CompletedEventContext,
  input: Pick<ResultManifestInput, "manifestId" | "workId" | "assignmentId">,
  result: { manifestRef: string; digest: string },
): EnvelopeEvent {
  return buildEvent(COMPLETED_TYPE, {
    source: sourceOf(ctx.command),
    subject: ctx.command.subject,
    ...(ctx.command.traceparent !== undefined ? { traceparent: ctx.command.traceparent } : {}),
    data: {
      workId: input.workId,
      assignmentId: input.assignmentId,
      executionId: ctx.executionId,
      executionGeneration: ctx.executionGeneration,
      resultManifest: { manifestId: input.manifestId, outputs: [] },
      resultManifestRef: result.manifestRef,
      resultDigest: result.digest,
    },
  });
}

function sourceOf(command: EnvelopeCommand): string {
  // The completed event speaks with the child's own identity, mirroring the
  // SDK's progress-publish law (urn:workspace:<childWorkspaceId>).
  const data = command.data as unknown as Record<string, unknown>;
  const child = data["childWorkspaceId"];
  return typeof child === "string" && child.length > 0
    ? `urn:workspace:${child}`
    : "urn:ofac:adapter";
}
