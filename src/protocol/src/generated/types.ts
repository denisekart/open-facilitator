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
 * One durable delivery: the envelope plus the delivery metadata a receipt references (spec
 * §5.2 — the receipt refers to the durable deliveryId, not the socket). Identical shape on
 * the SignalR Deliver client method and the HTTP GET /deliveries items. The envelope $ref
 * resolves by $id (same style as envelope-command.json); idToken/rfc3339DateTime
 * constraints are inlined verbatim from common.v1.json#$defs because this file lives beside
 * envelope.json in cloudevents-schemas/ and the OpenAPI validator resolves refs physically.
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

export interface WorkAssignCommandV1Data {
  actorEvidenceRef: string;
  assignmentId: string;
  capabilityId: string;
  commandId: string;
  deadline: string;
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
 * interim link-token credential until Plan 05 OIDC principals; a server MAY omit both when
 * it issues no bearer credential.
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
   * The exact action payload covered by actionDigest; the kernel digests its canonical form
   * (appendix §7.2).
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
   * Optional handling/classification label (e.g. internal). Plan 15 widened artifactRef
   * additively; opaque to the kernel.
   */
  classification?: string;
  createdAt: string;
  createdBy: string;
  digest: string;
  /**
   * Optional producer-owned taxonomy label (e.g. analysis.report). Plan 15 widened
   * artifactRef additively; the kernel treats unknown values as opaque (spec 15.6
   * extensibility).
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

export interface ProviderForgejoCiEventV1Data {
  /**
   * Commit-status state (Forgejo `status` webhook event).
   */
  action: ProviderForgejoCiEventV1DataAction;
  actorProviderAccountId: string;
  /**
   * Commit-status description verbatim.
   */
  description?: string;
  /**
   * Verbatim provider event payload as received by the adapter.
   */
  payload?: { [key: string]: unknown };
  providerDeliveryId: string;
  reference: ConversationReference;
}

/**
 * Commit-status state (Forgejo `status` webhook event).
 */
export type ProviderForgejoCiEventV1DataAction = "pending" | "success" | "failure" | "error";

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

export interface ProviderForgejoIssueEventV1Data {
  action: ProviderForgejoIssueEventV1DataAction;
  actorProviderAccountId: string;
  /**
   * Verbatim provider event payload as received by the adapter.
   */
  payload?: { [key: string]: unknown };
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

export type ProviderForgejoIssueEventV1DataAction = "opened" | "labeled" | "commented" | "closed";

export interface ProviderForgejoPullRequestEventV1Data {
  action: ProviderForgejoPullRequestEventV1DataAction;
  actorProviderAccountId: string;
  /**
   * Verbatim provider event payload as received by the adapter.
   */
  payload?: { [key: string]: unknown };
  providerDeliveryId: string;
  reference: ConversationReference;
  /**
   * Review/comment body excerpt (for action=reviewed|commented).
   */
  text?: string;
}

export type ProviderForgejoPullRequestEventV1DataAction =
  "opened" | "synchronized" | "reopened" | "reviewed" | "commented" | "merged" | "closed";

export interface ProviderMattermostMentionEventV1Data {
  actorProviderAccountId: string;
  /**
   * Verbatim provider event payload as received by the adapter.
   */
  payload?: { [key: string]: unknown };
  providerDeliveryId: string;
  reference: ConversationReference;
  text: string;
}

export interface ProviderMattermostMessageEventV1Data {
  actorProviderAccountId: string;
  /**
   * Verbatim provider event payload as received by the adapter.
   */
  payload?: { [key: string]: unknown };
  providerDeliveryId: string;
  reference: ConversationReference;
  text: string;
}

export interface ProviderMattermostReactionEventV1Data {
  actorProviderAccountId: string;
  emojiName: string;
  /**
   * Verbatim provider event payload as received by the adapter.
   */
  payload?: { [key: string]: unknown };
  providerDeliveryId: string;
  reference: ConversationReference;
}

export interface WorkAcceptedEventV1Data {
  acceptedAt: string;
  assignmentId: string;
  workId: string;
}

export interface WorkInputRequiredEventV1Data {
  assignmentId: string;
  question: string;
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
