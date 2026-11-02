// The memory capability of `@ofac/adapter`: retain/recall against
// the kernel broker over the link plane — the same in-band-ack law as the
// artifact client. The child link token is the
// credential; `actorEvidenceRef` is schema-required, so the client always
// rides a pointer (default `workload:adapter`, non-authoritative unless an
// `oidc:`/`kernel:` evidence rides along). A workload retain lands
// `candidate` kernel-side (D4); promotion is a human REST op.
import { createHash, randomUUID } from "node:crypto";
import type { EnvelopeCommand, EnvelopeEvent } from "@ofac/protocol";
import { buildEvent, dataschemaFor, trimSlash } from "@ofac/workspace-link";
import type { KernelHttpOptions } from "./artifacts.js";

const RETAIN_TYPE = "org.facilitator.command.memory.retain.v1";
const RECALL_TYPE = "org.facilitator.command.memory.recall.v1";
const CANDIDATE_TYPE = "org.facilitator.event.memory.candidate.v1";
const USED_TYPE = "org.facilitator.event.memory.used.v1";

/** The classifications (schema enum). */
export type MemoryType =
  | "observed_fact"
  | "experience"
  | "decision"
  | "lesson_candidate"
  | "lesson_verified"
  | "hypothesis"
  | "preference";

/** The recall purposes (schema enum). */
export type RecallPurpose =
  "assignment.context" | "requirements.clarification" | "report.synthesis";

export type MemoryPolicyLabel = "private" | "team-only" | "facilitator-visible" | "evidence-only";

export interface MemoryProvenance {
  readonly source: string;
  readonly evidenceRef: string;
  readonly createdBy: string;
  readonly createdAt: string;
  readonly workId?: string;
  readonly executionId?: string;
  readonly sourceDigest?: string;
}

/** The frozen candidate provenance: exactly four fields, no extras. */
export type CandidateProvenance = Pick<
  MemoryProvenance,
  "source" | "evidenceRef" | "createdBy" | "createdAt"
>;

export interface RetainRequest {
  /** `project:<id>|team:<slug>|work:<id>|private:<actor>` hint. */
  readonly scopeSelector: string;
  readonly type: MemoryType;
  readonly content: string;
  readonly mediaType?: string;
  readonly contentSummary?: string;
  readonly provenance: MemoryProvenance;
  readonly policyLabel?: MemoryPolicyLabel;
  readonly classification?: "internal" | "public";
  readonly observedAt?: string;
  /** Delegated-human evidence when the retain must carry one (D4: a workload retain lands `candidate` regardless). */
  readonly actorEvidenceRef?: string;
}

export interface RetainReceipt {
  readonly entryId: string;
  readonly status: string;
  readonly duplicate: boolean;
}

export interface RecallRequest {
  readonly query: string;
  readonly purpose: RecallPurpose;
  readonly workId?: string;
  readonly scopeSelectors?: string[];
  readonly maxItems?: number;
  readonly maxTokens?: number;
  readonly includeSuperseded?: boolean;
  readonly actorEvidenceRef?: string;
}

/** One D8 entry; `content` may be the summary when the row is `evidence-only`. */
export interface RecallEntry {
  readonly entryId: string;
  readonly scopeId: string;
  readonly type: string;
  readonly content: string;
  readonly contentHash: string;
  readonly provenance: Record<string, unknown>;
  readonly observedAt: string;
  readonly effectiveFrom?: string | null;
  readonly revision: number;
  readonly status: string;
  readonly policyLabel: string;
  readonly retrievalReason?: string | null;
}

export interface RecallResponse {
  readonly requestId: string;
  readonly providerProfile: string;
  readonly entries: RecallEntry[];
  readonly truncated: boolean;
  readonly asOf: string;
}

export interface UsageAttestation {
  readonly memoryId: string;
  readonly consumer: string;
  readonly justification: string;
}

export interface MemoryCandidateInput {
  readonly scope: "project" | "team" | "workspace" | "private";
  readonly contentSummary: string;
  readonly provenance: CandidateProvenance;
  readonly policyLabel?: MemoryPolicyLabel;
}

export interface MemoryClient {
  /** Synchronous retain (D1): the receipt rides the ack `data`. Idempotent by content: the commandId derives from the content hash, so a retry re-runs the kernel's dedupe and re-returns the stored identity with `duplicate: true`. */
  retain(req: RetainRequest): Promise<RetainReceipt>;
  /** Recall: fresh uuid commandId per call (a read; the honest duplicate answer is a re-run). */
  recall(req: RecallRequest): Promise<RecallResponse>;
  /** Attest that a recalled entry was used: publishes the frozen `event.memory.used` envelope. */
  attest(usage: UsageAttestation): Promise<void>;
  /** Build (not send) the frozen candidate envelope — the caller publishes it on its own transport. */
  buildMemoryCandidateEvent(input: MemoryCandidateInput): EnvelopeEvent;
  /** No reflection provider ships: honest `unsupported`, never a silent no-op. */
  reflect(): { unsupported: true };
}

export function createMemoryClient(opts: KernelHttpOptions): MemoryClient {
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

  function envelope(
    type: string,
    commandId: string,
    data: Record<string, unknown>,
  ): EnvelopeCommand {
    return {
      specversion: "1.0",
      id: randomUUID(),
      source: "urn:ofac:adapter",
      type,
      subject: `memory/${commandId}`,
      time: new Date().toISOString(),
      datacontenttype: "application/json",
      dataschema: dataschemaFor(type),
      data,
    };
  }

  return {
    async retain(req) {
      // Content-derived identity: a retry of the same content hits the
      // kernel's content-hash dedupe and re-returns the stored receipt.
      const hash = createHash("sha256").update(req.content).digest("hex");
      const commandId = `mem-retain-${hash.slice(0, 16)}`;
      const ack = await postEvent(
        envelope(RETAIN_TYPE, commandId, {
          commandId,
          actorEvidenceRef: req.actorEvidenceRef ?? "workload:adapter",
          scopeSelector: req.scopeSelector,
          type: req.type,
          content: req.content,
          provenance: stripUndefined({ ...req.provenance }),
          ...(req.mediaType !== undefined ? { mediaType: req.mediaType } : {}),
          ...(req.contentSummary !== undefined ? { contentSummary: req.contentSummary } : {}),
          ...(req.policyLabel !== undefined ? { policyLabel: req.policyLabel } : {}),
          ...(req.classification !== undefined ? { classification: req.classification } : {}),
          ...(req.observedAt !== undefined ? { observedAt: req.observedAt } : {}),
        }),
      );
      const data = (ack["data"] ?? {}) as Record<string, unknown>;
      const entryId = String(data["entryId"] ?? "");
      if (entryId === "") {
        throw new Error(`retain ack carried no receipt: ${JSON.stringify(ack).slice(0, 256)}`);
      }
      return {
        entryId,
        status: String(data["status"] ?? "candidate"),
        duplicate: data["duplicate"] === true,
      };
    },

    async recall(req) {
      const commandId = `mem-recall-${randomUUID()}`;
      const ack = await postEvent(
        envelope(RECALL_TYPE, commandId, {
          commandId,
          actorEvidenceRef: req.actorEvidenceRef ?? "workload:adapter",
          query: req.query,
          purpose: req.purpose,
          ...(req.workId !== undefined ? { workId: req.workId } : {}),
          ...(req.scopeSelectors !== undefined ? { scopeSelectors: req.scopeSelectors } : {}),
          ...(req.maxItems !== undefined ? { maxItems: req.maxItems } : {}),
          ...(req.maxTokens !== undefined ? { maxTokens: req.maxTokens } : {}),
          ...(req.includeSuperseded !== undefined
            ? { includeSuperseded: req.includeSuperseded }
            : {}),
        }),
      );
      const data = (ack["data"] ?? {}) as Record<string, unknown>;
      if (typeof data["requestId"] !== "string") {
        throw new Error(`recall ack carried no response: ${JSON.stringify(ack).slice(0, 256)}`);
      }
      return data as unknown as RecallResponse;
    },

    async attest(usage) {
      await postEvent({
        specversion: "1.0",
        id: randomUUID(),
        source: "urn:ofac:adapter",
        type: USED_TYPE,
        subject: `memory/${usage.memoryId}`,
        time: new Date().toISOString(),
        datacontenttype: "application/json",
        dataschema: dataschemaFor(USED_TYPE),
        data: { ...usage },
      } satisfies EnvelopeCommand);
    },

    buildMemoryCandidateEvent(input) {
      return buildEvent(CANDIDATE_TYPE, {
        source: "urn:ofac:adapter",
        subject: `memory/${randomUUID()}`,
        data: {
          scope: input.scope,
          contentSummary: input.contentSummary,
          provenance: { ...input.provenance },
          ...(input.policyLabel !== undefined ? { policyLabel: input.policyLabel } : {}),
        },
      });
    },

    reflect() {
      return { unsupported: true as const };
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

function stripUndefined(record: Record<string, unknown>): Record<string, unknown> {
  return Object.fromEntries(Object.entries(record).filter(([, v]) => v !== undefined));
}
