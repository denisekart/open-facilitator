export interface EnvelopeCommand {
  causationid?: string;
  correlationid?: string;
  data?: { [key: string]: unknown };
  datacontenttype: Datacontenttype;
  dataschema: string;
  id: string;
  source: string;
  specversion: Specversion;
  subject: string;
  time: string;
  traceparent?: string;
  type: string;
  [property: string]: unknown;
}

export type Datacontenttype = "application/json";

export type Specversion = "1.0";

export interface EnvelopeEvent {
  causationid?: string;
  correlationid?: string;
  data?: { [key: string]: unknown };
  datacontenttype: Datacontenttype;
  dataschema: string;
  id: string;
  source: string;
  specversion: Specversion;
  subject: string;
  time: string;
  traceparent?: string;
  type: string;
  [property: string]: unknown;
}

export interface Envelope {
  causationid?: string;
  correlationid?: string;
  data?: { [key: string]: unknown };
  datacontenttype: Datacontenttype;
  dataschema: string;
  id: string;
  source: string;
  specversion: Specversion;
  subject: string;
  time: string;
  traceparent?: string;
  type: string;
  [property: string]: unknown;
}

/**
 * One durable delivery: the envelope plus the delivery metadata a receipt references (the
 * receipt refers to the durable deliveryId, not the socket). Identical shape on the SignalR
 * Deliver client method and the HTTP GET /deliveries items. The envelope $ref resolves by
 * $id (same style as envelope-command.json); idToken/rfc3339DateTime constraints are
 * inlined verbatim from common.v1.json#$defs because this file lives beside envelope.json
 * in cloudevents-schemas/ and the OpenAPI validator resolves refs physically.
 */
export interface LinkDeliveryV1 {
  deliveredAt: string;
  deliveryId: string;
  envelope: EnvelopeObject;
}

export interface EnvelopeObject {
  causationid?: string;
  correlationid?: string;
  data?: { [key: string]: unknown };
  datacontenttype: Datacontenttype;
  dataschema: string;
  id: string;
  source: string;
  specversion: Specversion;
  subject: string;
  time: string;
  traceparent?: string;
  type: string;
  [property: string]: unknown;
}

export interface CapabilityManifestV1 {
  apiVersion: APIVersion;
  capabilities: [CapabilityDescriptor, ...CapabilityDescriptor[]];
  kind: CapabilityManifestV1Kind;
  ownerTeam: string;
}

export type APIVersion = "facilitator.framework/v1";

export interface CapabilityDescriptor {
  acceptedContexts?: AcceptedContext[];
  execution: Execution;
  id: string;
  inputSchema?: string;
  outputKinds?: string[];
  outputMediaTypes?: string[];
  supportsCancellation?: boolean;
  supportsInteraction?: boolean;
  supportsNudge?: boolean;
  supportsSteering?: boolean;
  version: number;
}

export type AcceptedContext = "uri-reference" | "inline-text" | "artifact-reference";

export type Execution = "synchronous" | "asynchronous";

export type CapabilityManifestV1Kind = "CapabilityManifest";

export interface ApprovalConsumeCommandV1Data {
  /**
   * The consuming workload's own evidence ref (e.g. workload:<gatewayId>); the kernel
   * authorizes on the caller endpoint owning the approval, not on this claim.
   */
  actorEvidenceRef: string;
  approvalId: string;
  commandId: string;
  comment?: string;
}

export interface ApprovalRespondCommandV1Data {
  actorEvidenceRef: string;
  approvalId: string;
  commandId: string;
  comment?: string;
  decision: ApprovalRespondCommandV1DataDecision;
}

export type ApprovalRespondCommandV1DataDecision = "approved" | "denied";

export interface ArtifactConfirmUploadCommandV1Data {
  artifactId: string;
  commandId: string;
  comment?: string;
}

export interface ArtifactRequestUploadCommandV1Data {
  actorEvidenceRef: string;
  assignmentId?: string;
  commandId: string;
  comment?: string;
  digest: string;
  mediaType: string;
  retentionPolicy?: string;
  size: number;
  workId?: string;
}

/**
 * A human answer to a pending event.assignment.input.required.v1. Routed to the Gateway
 * bound to the assignment, which delivers a respond child message to the live run.
 */
export interface AssignmentInputCommandV1Data {
  actorEvidenceRef: string;
  answer: string;
  commandId: string;
  requestId: string;
}

export interface CommunicationAcknowledgeCommandV1Data {
  commandId: string;
  idempotencyKey: string;
  target: ConversationTarget;
}

export interface ConversationTarget {
  actorProviderAccountId?: string;
  channelOrResourceId?: string;
  conversationKind: ConversationKind;
  parentMessageId?: string;
  provider: string;
  rootThreadId?: string;
  subjectRef?: string;
  tenantOrOrganizationId?: string;
}

export type ConversationKind = "thread" | "channel" | "dm" | "comment";

export interface CommunicationReactCommandV1Data {
  commandId: string;
  emojiName: string;
  idempotencyKey: string;
  target: ConversationTarget;
}

export interface CommunicationReplyCommandV1Data {
  commandId: string;
  idempotencyKey: string;
  target: ConversationTarget;
  text: string;
}

export interface CommunicationUpdateCommandV1Data {
  commandId: string;
  idempotencyKey: string;
  target: ConversationTarget;
  text: string;
}

export interface ContextFetchCommandV1Data {
  actorEvidenceRef: string;
  commandId: string;
  contextRefs: [string, ...string[]];
  deadline: string;
  targetId: string;
  workId: string;
}

export interface MemoryRecallCommandV1Data {
  actorEvidenceRef: string;
  commandId: string;
  includeSuperseded?: boolean;
  maxItems?: number;
  maxTokens?: number;
  purpose: Purpose;
  query: string;
  scopeSelectors?: string[];
  workId?: string;
}

export type Purpose = "assignment.context" | "requirements.clarification" | "report.synthesis";

export interface MemoryRetainCommandV1Data {
  actorEvidenceRef: string;
  classification?: Classification;
  commandId: string;
  content: string;
  contentSummary?: string;
  mediaType?: string;
  observedAt?: string;
  policyLabel?: PolicyLabel;
  provenance: MemoryRetainCommandV1DataProvenance;
  scopeSelector: string;
  type: Type;
}

export type Classification = "internal" | "public";

export type PolicyLabel = "private" | "team-only" | "facilitator-visible" | "evidence-only";

export interface MemoryRetainCommandV1DataProvenance {
  createdAt: string;
  createdBy: string;
  evidenceRef: string;
  executionId?: string;
  source: string;
  sourceDigest?: string;
  workId?: string;
}

export type Type =
  | "observed_fact"
  | "experience"
  | "decision"
  | "lesson_candidate"
  | "lesson_verified"
  | "hypothesis"
  | "preference";

export interface ProviderWebhookCommandV1Data {
  /**
   * Raw request body, base64-encoded. Verbatim bytes preserve HMAC signature verifiability
   * provider-side.
   */
  body: string;
  commandId: string;
  /**
   * Verbatim Content-Type of the inbound HTTP delivery.
   */
  contentType: string;
  /**
   * Vendor delivery id when the kernel could extract one; adapters derive their own dedupe
   * key otherwise.
   */
  deliveryId?: string;
  /**
   * Verbatim inbound webhook headers (signature, event, delivery-id headers included).
   * Signature verification is provider-local in the adapter.
   */
  headers: { [key: string]: string };
  /**
   * Provider token the sink routed this delivery under (path segment {provider}).
   */
  provider: string;
}

export interface ProvisionStartCommandV1Data {
  actorEvidenceRef: string;
  commandId: string;
  deadline: string;
  operationId: string;
  reason?: string;
  spec: Spec;
  team: string;
  workloadId: string;
}

export interface Spec {
  entrypoint?: string[];
  env?: { [key: string]: string };
  image: string;
  labels?: { [key: string]: string };
  network?: string;
  stopGraceSeconds?: number;
  volumes?: Volume[];
}

export interface Volume {
  readOnly?: boolean;
  source: string;
  target: string;
}

export interface ProvisionStopCommandV1Data {
  actorEvidenceRef: string;
  commandId: string;
  deadline: string;
  operationId: string;
  reason?: string;
  team: string;
  workloadId: string;
}

export interface WorkAssignCommandV1Data {
  actorEvidenceRef: string;
  assignmentId: string;
  capabilityId: string;
  commandId: string;
  deadline: string;
  executionGeneration: number;
  expectedContextVersion: number;
  expectedDeliverables: [Deliverable, ...Deliverable[]];
  inputs: { [key: string]: unknown };
  targetId: string;
  workId: string;
}

export interface Deliverable {
  kind: string;
  mediaType: string;
}

export interface WorkCancelCommandV1Data {
  actorEvidenceRef: string;
  assignmentId: string;
  commandId: string;
  deadline: string;
  reason: string;
  targetId: string;
  workId: string;
}

export interface WorkNudgeCommandV1Data {
  actorEvidenceRef: string;
  assignmentId: string;
  commandId: string;
  deadline: string;
  executionGeneration: number;
  stallEpisode?: string;
  targetId: string;
  workId: string;
}

export interface WorkStatusRequestCommandV1Data {
  actorEvidenceRef: string;
  assignmentId: string;
  commandId: string;
  deadline: string;
  targetId: string;
  workId: string;
}

export interface WorkSteerCommandV1Data {
  actorEvidenceRef: string;
  assignmentId: string;
  commandId: string;
  deadline: string;
  directive: string;
  executionGeneration: number;
  expectedContextVersion: number;
  targetId: string;
  workId: string;
}

export interface ControlFrame {
  capabilityManifestDigest?: string;
  endpointId?: string;
  heartbeatSeconds?: number;
  instanceId?: string;
  kind: ControlFrameKind;
  lastKnownAck?: string;
  leaseEpoch?: number;
  maxInFlight?: number;
  protocolVersion?: string;
  protocolVersions?: [string, ...string[]];
  reason?: string;
  sessionId?: string;
  supportedTransports?: [SupportedTransport, ...SupportedTransport[]];
}

export type ControlFrameKind = "hello" | "welcome" | "heartbeat" | "flow_control" | "goodbye";

export type SupportedTransport = "signalr" | "websocket" | "http-poll";

export interface EnrollRequestV1 {
  endpointId: string;
  instanceId: string;
  role?: Role;
  team?: string;
  token: string;
  trustMode: TrustMode;
}

export type Role = "gateway" | "child";

export type TrustMode = "shared-runtime" | "separate-process-same-host" | "isolated-executor";

/**
 * Bootstrap enrollment response. linkToken/expiresAt are additive (OpenAPI 1.0.1): the
 * interim link-token credential the Link server issues at enrollment; a server MAY omit
 * both when it issues no bearer credential.
 */
export interface EnrollResponseV1 {
  enrollmentId: string;
  expiresAt?: string;
  linkToken?: string;
  trustMode: TrustMode;
}

export interface ApprovalDecidedEventV1Data {
  approvalId: string;
  /**
   * Echo of the recorded createdAt so the Gateway can bind the push to its own pending row's
   * exact digest record.
   */
  createdAt: string;
  /**
   * Canonical actor that decided; empty string for lazy expiry.
   */
  decidedBy: string;
  decision: ApprovalDecidedEventV1DataDecision;
  /**
   * kernel: receipt on approved (law D5); empty string otherwise.
   */
  evidenceRef: string;
  expiresAt: string;
  /**
   * Work the approval belongs to, when one exists.
   */
  workId?: string;
}

export type ApprovalDecidedEventV1DataDecision = "approved" | "denied" | "expired" | "revoked";

export interface ApprovalRequiredEventV1Data {
  /**
   * The exact action payload covered by actionDigest; the kernel digests its canonical form.
   */
  action?: { [key: string]: unknown };
  actionDigest: string;
  approvalId: string;
  createdAt: string;
  eligibleActors: [string, ...string[]];
  expiresAt: string;
  nonce: string;
  /**
   * Work the approval belongs to, when one exists.
   */
  workId?: string;
}

export interface ApprovalRevokedEventV1Data {
  approvalId: string;
  reason: string;
  revokedBy: string;
}

export interface ArtifactAvailableEventV1Data {
  artifactId: string;
  createdAt?: string;
  createdBy?: string;
  digest: string;
  mediaType: string;
  size: number;
  storageUrl?: string;
  workId?: string;
}

export interface AssignmentCompletedEventV1Data {
  assignmentId: string;
  executionGeneration: number;
  executionId: string;
  resultDigest?: string;
  resultManifest: ResultManifestObject;
  resultManifestRef?: string;
  workId: string;
}

export interface ResultManifestObject {
  assignmentId?: string;
  capability?: string;
  createdAt?: string;
  externalRefs?: string[];
  manifestId: string;
  notes?: string;
  outputs: ArtifactRef[];
  revision?: number;
  revisionOf?: string;
  sourceRefs?: string[];
  structuredResult?: { [key: string]: unknown };
  summary?: string;
  workId?: string;
  [property: string]: unknown;
}

export interface ArtifactRef {
  artifactId: string;
  /**
   * Optional handling/classification label (e.g. internal). artifactRef carries it
   * additively; opaque to the kernel.
   */
  classification?: string;
  createdAt: string;
  createdBy: string;
  digest: string;
  /**
   * Optional producer-owned taxonomy label (e.g. analysis.report). artifactRef carries it
   * additively; the kernel treats unknown values as opaque (open extensibility).
   */
  kind?: string;
  mediaType: string;
  size: number;
  storageUrl: string;
}

export interface AssignmentFailedEventV1Data {
  assignmentId: string;
  executionGeneration: number;
  executionId?: string;
  reason: string;
  retryable: boolean;
  workId: string;
}

/**
 * An assignment paused mid-execution waiting for human input. Published by the adapter SDK
 * when a harness calls requestInput; the kernel parks the assignment in the input_required
 * status and fans the question out to the humans. Answered by command.assignment.input.v1,
 * which unblocks the live run.
 */
export interface AssignmentInputRequiredEventV1Data {
  assignmentId: string;
  choices?: string[];
  contextRef?: string;
  createdAt: string;
  deadline?: string;
  executionGeneration: number;
  executionId: string;
  question: string;
  requestId: string;
  workId: string;
}

export interface MemoryCandidateEventV1Data {
  contentSummary: string;
  policyLabel?: PolicyLabel;
  provenance: MemoryCandidateEventV1DataProvenance;
  scope: Scope;
}

export interface MemoryCandidateEventV1DataProvenance {
  createdAt: string;
  createdBy: string;
  evidenceRef: string;
  source: string;
}

export type Scope = "project" | "team" | "workspace" | "private";

export interface MemoryUsedEventV1Data {
  consumer: string;
  justification: string;
  memoryId: string;
}

export interface ProviderChangeRequestEventV1Data {
  action: ProviderChangeRequestEventV1DataAction;
  actorProviderAccountId: string;
  /**
   * Adapter-normalized review/discussion comment id for action=commented|reviewed, so
   * dialogue replies (`vcs.discussion.reply`) need no vendor payload digging.
   */
  commentId?: string;
  /**
   * Adapter-normalized source/head branch of the change request. The kernel MUST prefer this
   * over digging into `payload`.
   */
  headRef?: string;
  /**
   * Adapter-normalized label names on the change request. The kernel MUST prefer these over
   * digging into `payload`.
   */
  labels?: string[];
  /**
   * Verbatim provider event payload as received by the adapter.
   */
  payload?: { [key: string]: unknown };
  /**
   * Provider token owning this event; MUST equal reference.provider.
   */
  provider: string;
  providerDeliveryId: string;
  reference: ConversationReference;
  /**
   * Review/comment body excerpt (for action=reviewed|commented).
   */
  text?: string;
}

export type ProviderChangeRequestEventV1DataAction =
  "opened" | "synchronized" | "reopened" | "reviewed" | "commented" | "merged" | "closed";

export interface ConversationReference {
  authorProviderAccountId: string;
  channelOrResourceId: string;
  conversationKind: ConversationKind;
  messageId?: string;
  parentMessageId?: string;
  provider: string;
  providerInstanceId: string;
  rootThreadId?: string;
  subjectRef?: string;
  tenantOrOrganizationId: string;
}

export interface ProviderIssueEventV1Data {
  action: ProviderIssueEventV1DataAction;
  actorProviderAccountId: string;
  /**
   * Adapter-normalized head ref of the conversation's branch context, when the provider
   * exposes one. The kernel MUST prefer this over digging into `payload`.
   */
  headRef?: string;
  /**
   * Adapter-normalized label names on the issue (providers shape labels differently;
   * normalization is the adapter's job). The kernel MUST prefer these over digging into
   * `payload`.
   */
  labels?: string[];
  /**
   * Verbatim provider event payload as received by the adapter.
   */
  payload?: { [key: string]: unknown };
  /**
   * Provider token owning this event; MUST equal reference.provider.
   */
  provider: string;
  providerDeliveryId: string;
  reference: ConversationReference;
  /**
   * Issue/comment body excerpt feeding Work input (for action=commented).
   */
  text?: string;
  /**
   * Set by the adapter from its label config: the issue carries a Work-triggering label. The
   * kernel reads the flag, it never interprets labels.
   */
  workTrigger: boolean;
}

export type ProviderIssueEventV1DataAction = "opened" | "labeled" | "commented" | "closed";

export interface ProviderMentionEventV1Data {
  actorProviderAccountId: string;
  /**
   * Verbatim provider event payload as received by the adapter.
   */
  payload?: { [key: string]: unknown };
  /**
   * Provider token owning this event; MUST equal reference.provider.
   */
  provider: string;
  providerDeliveryId: string;
  reference: ConversationReference;
  text: string;
}

export interface ProviderMessageEventV1Data {
  actorProviderAccountId: string;
  /**
   * Verbatim provider event payload as received by the adapter.
   */
  payload?: { [key: string]: unknown };
  /**
   * Provider token owning this event; MUST equal reference.provider.
   */
  provider: string;
  providerDeliveryId: string;
  reference: ConversationReference;
  text: string;
}

export interface ProviderPipelineEventV1Data {
  /**
   * Pipeline/commit-status verdict as normalized by the adapter.
   */
  action: ProviderPipelineEventV1DataAction;
  actorProviderAccountId: string;
  /**
   * Status description verbatim.
   */
  description?: string;
  /**
   * Verbatim provider event payload as received by the adapter.
   */
  payload?: { [key: string]: unknown };
  /**
   * Provider token owning this event; MUST equal reference.provider.
   */
  provider: string;
  providerDeliveryId: string;
  reference: ConversationReference;
}

/**
 * Pipeline/commit-status verdict as normalized by the adapter.
 */
export type ProviderPipelineEventV1DataAction = "pending" | "success" | "failure" | "error";

export interface ProviderReactionEventV1Data {
  actorProviderAccountId: string;
  emojiName: string;
  /**
   * Verbatim provider event payload as received by the adapter.
   */
  payload?: { [key: string]: unknown };
  /**
   * Provider token owning this event; MUST equal reference.provider.
   */
  provider: string;
  providerDeliveryId: string;
  reference: ConversationReference;
}

/**
 * Kernel-internal routing intent: a Work needs an assignment for a capability. Emitted by
 * every Work-creating arm (REST ingress and provider ingest); consumed by the kernel
 * routing funnel, which resolves candidates, chooses one, and writes the work.assign row as
 * the routing effect. Never leaves the kernel.
 */
export interface EventRoutingRequestedV1 {
  actorEvidenceRef: string;
  capabilityId: string;
  /**
   * Base of the chosen assignment's command id: the effect row is '<base>:<effectKey>'
   * (effectKey defaults to the workId), keeping routing replay-idempotent.
   */
  commandIdBase: string;
  dependsOnJson?: string;
  /**
   * Effect-identity override: the assign effect row is '<commandIdBase>:<effectKey>' instead
   * of '<commandIdBase>:<workId>'. A re-dispatch intent uses the steer command id, preserving
   * the envelope read-back command-id ledger (work-redispatch:<steerId>) across the routing
   * cutover.
   */
  effectKey?: string;
  executionGeneration?: number;
  /**
   * Rule-configured extra inputs (JSON object) merged into the assign's inputs.extras.
   */
  extraInputsJson?: string;
  intentId: string;
  objective: string;
  /**
   * Caller's preferred endpoint (e.g. a provider config's fixed AssignEndpointId). Honored
   * only when the endpoint is an allowed candidate; otherwise the deterministic order decides
   * and the audit records the fallback.
   */
  preferenceEndpointId?: string;
  /**
   * JSON array serialized to inputs.sourceRefs of the assign effect (provider re-hydration
   * lineage).
   */
  referenceJson?: string;
  /**
   * Envelope targetId resolved by the caller (rule target, provider default); rides verbatim
   * into the assign effect.
   */
  targetId: string;
  /**
   * Soft team context (slug). Omitted when the work carries no team; the funnel then routes
   * team-agnostically. Cross-team exclusion always applies against the work's owning team.
   */
  team?: string;
  workId: string;
}

export interface WorkAcceptedEventV1Data {
  acceptedAt: string;
  assignmentId: string;
  workId: string;
}

export interface WorkProgressEventV1Data {
  assignmentId: string;
  executionGeneration: number;
  progressNote: string;
  workId: string;
}

export interface WorkspaceHeartbeatEventV1Data {
  activeAssignments?: number;
  status: WorkspaceHeartbeatEventV1DataStatus;
  workspaceId: string;
}

export type WorkspaceHeartbeatEventV1DataStatus = "idle" | "busy" | "draining" | "error";

export interface ReceiptV1 {
  deliveryId: string;
  detail?: { [key: string]: unknown };
  executionGeneration?: number;
  processedAt?: string;
  reason?: string;
  status: ReceiptV1Status;
}

export type ReceiptV1Status = "accepted" | "duplicate" | "rejected" | "unsupported";

export interface ResultManifestV1 {
  assignmentId?: string;
  capability?: string;
  createdAt?: string;
  externalRefs?: string[];
  manifestId: string;
  notes?: string;
  outputs: ArtifactRef[];
  revision?: number;
  revisionOf?: string;
  sourceRefs?: string[];
  structuredResult?: { [key: string]: unknown };
  summary?: string;
  workId?: string;
  [property: string]: unknown;
}
