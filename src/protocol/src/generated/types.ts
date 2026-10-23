type SharedDefinitions = unknown;

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

export interface ApprovalRespondCommandV1Data {
  actorEvidenceRef: string;
  approvalId: string;
  commandId: string;
  comment?: string;
  decision: Decision;
}

export type Decision = "approved" | "denied";

export interface ContextFetchCommandV1Data {
  actorEvidenceRef: string;
  commandId: string;
  contextRefs: [string, ...string[]];
  deadline: string;
  targetId: string;
  workId: string;
}

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
  token: string;
  trustMode: TrustMode;
}

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

export interface ApprovalRequiredEventV1Data {
  actionDigest: string;
  approvalId: string;
  createdAt: string;
  eligibleActors: [string, ...string[]];
  expiresAt: string;
  nonce: string;
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
  resultManifest: ResultManifestObject;
  workId: string;
}

export interface ResultManifestObject {
  manifestId: string;
  notes?: string;
  outputs: ArtifactRef[];
  revisionOf?: string;
  [property: string]: unknown;
}

export interface ArtifactRef {
  artifactId: string;
  createdAt: string;
  createdBy: string;
  digest: string;
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
  provenance: Provenance;
  scope: Scope;
}

export type PolicyLabel = "private" | "team-only" | "facilitator-visible" | "evidence-only";

export interface Provenance {
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
  manifestId: string;
  notes?: string;
  outputs: ArtifactRef[];
  revisionOf?: string;
  [property: string]: unknown;
}
