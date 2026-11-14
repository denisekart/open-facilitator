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

export interface CommunicationPostCommandV1Data {
  /**
   * Outbound media posted alongside the notice (same law as the reply command).
   */
  artifacts?: ArtifactRef[];
  /**
   * Interactive affordances (C7). Every button carries the EXACT command text a decision ;
   * 1-3 ride; a surface without buttons renders nothing extra - the text always stands alone.
   */
  buttons?: [CommunicationPostCommandV1DataButton, ...CommunicationPostCommandV1DataButton[]];
  commandId: string;
  idempotencyKey: string;
  /**
   * A conversation a reply cannot address: a top-level post into a dm or a channel (a team
   * notice is a channel post, not a thread reply wearing a costume - B4).
   */
  target: PostConversationTarget;
  text: string;
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

export interface CommunicationPostCommandV1DataButton {
  label: string;
  /**
   * Exact text offered to the presser to send as their own message (Telegram reply keyboard,
   * Slack button copy): the human sends it, so the message carries the presser's proven
   * authorship on the existing authority plane (C7).
   */
  prefill?: string;
  /**
   * Exact text to submit as the presser's message.
   */
  value?: string;
}

/**
 * A conversation a reply cannot address: a top-level post into a dm or a channel (a team
 * notice is a channel post, not a thread reply wearing a costume - B4).
 */
export interface PostConversationTarget {
  actorProviderAccountId?: string;
  channelOrResourceId?: string;
  conversationKind: PostConversationKind;
  parentMessageId?: string;
  provider: string;
  rootThreadId?: string;
  subjectRef?: string;
  tenantOrOrganizationId?: string;
}

export type PostConversationKind = "dm" | "channel";

export interface CommunicationReactCommandV1Data {
  commandId: string;
  emojiName: string;
  idempotencyKey: string;
  target: ConversationTarget;
}

export interface CommunicationReplyCommandV1Data {
  /**
   * Outbound media the adapter should post alongside the reply text (the kernel emits these
   * for harness-produced artifacts; the adapter consumes them).
   */
  artifacts?: ArtifactRef[];
  /**
   * Interactive affordances (C7). Every button carries the EXACT command text a decision ;
   * 1-3 ride; a surface without buttons renders nothing extra - the text always stands alone.
   */
  buttons?: [CommunicationReplyCommandV1DataButton, ...CommunicationReplyCommandV1DataButton[]];
  commandId: string;
  idempotencyKey: string;
  target: ConversationTarget;
  text: string;
}

export interface CommunicationReplyCommandV1DataButton {
  label: string;
  /**
   * Exact text offered to the presser to send as their own message (Telegram reply keyboard,
   * Slack button copy): the human sends it, so the message carries the presser's proven
   * authorship on the existing authority plane (C7).
   */
  prefill?: string;
  /**
   * Exact text to submit as the presser's message.
   */
  value?: string;
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
  /**
   * The command envelope held until consent (persist-first law D3): the kernel stores it and
   * hands it back on consume, so execution runs exactly what consent covered.
   */
  command?: { [key: string]: unknown };
  createdAt: string;
  eligibleActors: [string, ...string[]];
  expiresAt: string;
  nonce: string;
  /**
   * Rule flag: the requester side (the recording workload's team) may not give sole consent.
   */
  notRequester?: boolean;
  /**
   * Canonical actor behind the request (the verified human subject, or the workload when
   * unverified): the consent-authority rules evaluate against it.
   */
  requestedBy?: string;
  /**
   * The policy rule that made this consent necessary (name or locator): consent must be
   * attributable to a rule a human can point at.
   */
  ruleId?: string;
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

export interface ProviderDegradedEventV1Data {
  /**
   * The link endpoint whose deliveries stall (429: only THAT endpoint's queue backs off).
   */
  endpointId?: string;
  observedAt: string;
  /**
   * Provider token of the integration reporting its own trouble.
   */
  provider: string;
  /**
   * Adapter-chosen reason token (permission-denied, rate-limited, ...).
   */
  reason?: string;
  /**
   * True when a later attempt may succeed (429 backoff); false when a human must fix
   * something (403).
   */
  retryable: boolean;
  /**
   * The provider status code observed (403 discloses to team operators; 429 to system
   * operators).
   */
  status: number;
  /**
   * Team slug whose operators the disclosure addresses (optional; the kernel routes to that
   * team's channel when set).
   */
  team?: string;
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
   * Adapter-normalized label names on the conversation (providers shape labels differently;
   * normalization is the adapter's job). The kernel reads the flag, it never interprets
   * labels.
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
   * Inbound media attached to this message, already stored as kernel artifacts; carried
   * verbatim into provider_events.body.
   */
  sourceRefs?: ArtifactRef[];
  text: string;
}

export interface ProviderMessageEventV1Data {
  actorProviderAccountId: string;
  /**
   * Adapter-normalized label names on the conversation (providers shape labels differently;
   * normalization is the adapter's job). The kernel reads the flag, it never interprets
   * labels.
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
   * Inbound media attached to this message, already stored as kernel artifacts; carried
   * verbatim into provider_events.body.
   */
  sourceRefs?: ArtifactRef[];
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

export interface ProviderReviewEventV1Data {
  /**
   * What happened to the review itself: submitted the verdict, edited it, or dismissed it.
   */
  action: ProviderReviewEventV1DataAction;
  actorProviderAccountId: string;
  /**
   * Provider review id, when the provider exposes one (GitHub review id, GitLab discussion
   * id).
   */
  commentId?: string;
  /**
   * Head branch of the reviewed change request, when the provider carries it.
   */
  headRef?: string;
  /**
   * Verbatim provider event payload as received (audit + reconcile evidence).
   */
  payload?: { [key: string]: unknown };
  /**
   * Provider token owning this event; MUST equal reference.provider.
   */
  provider: string;
  providerDeliveryId: string;
  reference: ConversationReference;
  /**
   * The review verdict. A comment is not a review (it normalizes to mention/change_request);
   * only a formal review carries a state.
   */
  state: State;
  /**
   * The review body ('' when the reviewer submitted no comment).
   */
  text: string;
}

/**
 * What happened to the review itself: submitted the verdict, edited it, or dismissed it.
 */
export type ProviderReviewEventV1DataAction = "submitted" | "changed" | "dismissed";

/**
 * The review verdict. A comment is not a review (it normalizes to mention/change_request);
 * only a formal review carries a state.
 */
export type State = "approved" | "changes-requested";

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

/**
 * Kernel-internal workflow completion event: a workflow instance ended and publishes its
 * projected output. Emitted by the workflow runner on the instance's final advance (to
 * workflow.kernel); consumed by the workflow sink, which first attempts wake correlation
 * against a parked parent invoke step (payload instanceId + parentInstanceId) and only
 * otherwise offers the row to ordinary on.event trigger matching. Never leaves the kernel.
 */
export interface WorkflowCompletedEventV1Data {
  /**
   * Definition the instance ran.
   */
  definitionId: string;
  /**
   * Pinned definition version the instance ran.
   */
  definitionVersion: number;
  /**
   * The completed instance (workflow_instances.id).
   */
  instanceId: string;
  /**
   * The definition's output.project projection over the instance step log; {} when the
   * definition declares no output block.
   */
  output: { [key: string]: unknown };
  /**
   * Owning parent instance when the child was started by an invoke step; null for roots.
   * Required key: the sink discriminates wake-vs-trigger on it.
   */
  parentInstanceId: null | string;
  /**
   * Terminal state of the instance.
   */
  status: WorkflowCompletedEventV1DataStatus;
}

/**
 * Terminal state of the instance.
 */
export type WorkflowCompletedEventV1DataStatus = "completed" | "failed" | "cancelled";

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

/**
 * Workflow definition plane artifact: a durable, versioned workflow definition stored in
 * the kernel and executed by the workflow sink on the synthetic `workflow.kernel` channel.
 * JSON is canonical; YAML is a serialization view only. Field-level semantics are the
 * interpreter contract: the palette is closed, `decide` always carries a `fallback`, and
 * every `invoke` carries a mandatory `timeout`.
 */
export interface WorkflowDefinitionV1 {
  /**
   * Sub-workflow budgets for this definition as a callee.
   */
  composition?: Composition;
  /**
   * Operator-facing law of the flow.
   */
  description: string;
  /**
   * Enable-time gate. Disabled definitions never contribute trigger subscriptions; enabling a
   * definition whose palette contains a not-yet-executable step kind is refused (validator
   * `step-kind-not-supported`).
   */
  enabled?: boolean;
  /**
   * Definition identity (stable across versions); the store keys on (id, version).
   */
  id: string;
  /**
   * Exactly one trigger source.
   */
  on: On;
  /**
   * What a completed instance publishes to a waiting parent.
   */
  output?: Output;
  /**
   * Flat step list; execution order comes from the `on:` edge selectors, the array order
   * breaks ties deterministically.
   */
  steps: [StepElement, ...StepElement[]];
  /**
   * Optional team scope (slug): the definition only fires for work/events in that team.
   */
  team?: string;
  /**
   * Operator-facing name.
   */
  title: string;
  /**
   * Monotonic version; a PUT of an existing id stores a new row, instances pin the version
   * they started on.
   */
  version: number;
}

/**
 * Sub-workflow budgets for this definition as a callee.
 */
export interface Composition {
  /**
   * Per-definition occurrence cap along a single ancestor chain.
   */
  maxInvocations?: number;
  /**
   * Whether this definition may appear more than once in one ancestor chain.
   */
  reentrant?: boolean;
}

/**
 * Exactly one trigger source.
 */
export interface On {
  /**
   * Wire event type this definition triggers on; the validator requires the mirrored schema
   * id to exist.
   */
  event?: string;
  /**
   * Callable-only marker: startable exclusively by a parent's `invoke` step, never by events
   * or a schedule.
   */
  invoked?: Invoked;
  /**
   * Cadence spec, verbatim law of the schedule plane (`interval:<n>m` | `daily:HH:mm` |
   * `cron:<5 fields>`), evaluated by the shared schedule-spec parser.
   */
  schedule?: string;
  /**
   * Map of dotted `data.*` paths to matcher values; all must match (AND). Only meaningful
   * with `event`.
   */
  where?: { [key: string]: boolean | number | string };
}

/**
 * Callable-only marker: startable exclusively by a parent's `invoke` step, never by events
 * or a schedule.
 */
export interface Invoked {}

/**
 * What a completed instance publishes to a waiting parent.
 */
export interface Output {
  /**
   * Select-law projection over the instance step log: what a completed instance publishes to
   * a caller. Absent block publishes {}.
   */
  project: { [key: string]: string };
}

export interface StepElement {
  /**
   * Raise a human decision through the existing approval ledger, park, resume on
   * `approval.decided` correlated by approval id.
   */
  approval?: Approval;
  /**
   * Branch on a model answer or a deterministic guard predicate. Every branch target is a
   * step id or "end".
   */
  decide?: Decide;
  /**
   * Append one effect: a named wire command, or a capability routed through the funnel
   * (optionally parking a binding marker).
   */
  emit?: Emit;
  /**
   * Write a notification-ledger row for a human.
   */
  escalate?: Escalate;
  /**
   * Declared in v1, executed when the fan-out consumer lands (enable-time
   * `step-kind-not-supported` until then).
   */
  "fan-out"?: FanOut;
  /**
   * Port of the fork law: successor Work + `created`/`work.linked` rows + binding repoint +
   * reply, with a deterministic successor work id.
   */
  "fork-work"?: ForkWork;
  /**
   * Step identity, unique within the document; step-log rows and edges reference it.
   */
  id: string;
  /**
   * Sub-workflow call: starts a child instance in the parent's transaction, optionally awaits
   * its output.
   */
  invoke?: Invoke;
  /**
   * Declared in v1, executed with fan-out.
   */
  join?: Join;
  /**
   * Declared in v1, executed when the memory step consumer lands.
   */
  memory?: Memory;
  /**
   * Edge this step is reached by. The entry step(s) use `always` or omit `on`.
   */
  on?: string;
  /**
   * Pure projection: copy fields out of earlier outputs, the trigger envelope, or the
   * digest-verified stored manifest.
   */
  select?: Select;
  /**
   * Park until the delay elapses; the sweeper is the clock.
   */
  timer?: Timer;
  /**
   * Operator-facing step label (the console checklist line).
   */
  title: string;
  /**
   * Park the instance (`waiting`) until a matching event wakes it or the timeout edge fires.
   */
  wait?: Wait;
  /**
   * Create a Work and append its routing intent; the created work carries this instance in
   * `works.workflow_instance_id`.
   */
  "work-create"?: WorkCreate;
}

/**
 * Raise a human decision through the existing approval ledger, park, resume on
 * `approval.decided` correlated by approval id.
 */
export interface Approval {
  /**
   * Approver actor refs (comma-separated D4 actors or a ref resolving to them).
   */
  approvers: string;
  /**
   * Approval prompt (ref templates allowed).
   */
  question: string;
}

/**
 * Branch on a model answer or a deterministic guard predicate. Every branch target is a
 * step id or "end".
 */
export interface Decide {
  /**
   * Always required: branch taken when the answer is unresolvable or its branch is unknown. A
   * step id, or the literal "end".
   */
  fallback: string;
  /**
   * Deterministic predicate; required when source is `guard`.
   */
  guard?: Guard;
  /**
   * Model question + closed option set; required when source is `model`.
   */
  question?: Question;
  /**
   * `model` consults the decision gateway with a closed option list (degrade to `fallback` on
   * low confidence or AI outage); `guard` is a deterministic field predicate with no model
   * and no telemetry.
   */
  source: DecideSource;
}

/**
 * Deterministic predicate; required when source is `guard`.
 */
export interface Guard {
  /**
   * Exact scalar match; null matches a field that resolves to absent/null (presence
   * branching).
   */
  equals?: boolean | number | null | string;
  /**
   * Field path in the built-in context (`work.state`, `event.data.action`, ...).
   */
  field: string;
  /**
   * Any-of scalar membership.
   */
  in?: [boolean | number | string, ...(boolean | number | string)[]];
  /**
   * Presence branch: true hits when the field resolves to a present, non-null value (works
   * for arrays/objects too); false hits on absent/null. Exactly one of equals/in/present.
   */
  present?: boolean;
}

/**
 * Model question + closed option set; required when source is `model`.
 */
export interface Question {
  options: [Option, ...Option[]];
  prompt: string;
}

export interface Option {
  /**
   * Step id, or the literal "end": completes the instance right there.
   */
  branch: string;
  /**
   * Closed answer the model may choose.
   */
  value: string;
}

/**
 * `model` consults the decision gateway with a closed option list (degrade to `fallback` on
 * low confidence or AI outage); `guard` is a deterministic field predicate with no model
 * and no telemetry.
 */
export type DecideSource = "model" | "guard";

/**
 * Append one effect: a named wire command, or a capability routed through the funnel
 * (optionally parking a binding marker).
 */
export interface Emit {
  /**
   * Side effect of a capability emit: park a communication binding alongside the routing
   * intent (the pr-request pending-PR marker law).
   */
  binding?: Binding;
  /**
   * Capability id: dispatch appends a routing intent (the routing funnel chooses the
   * endpoint).
   */
  capability?: string;
  /**
   * Wire command appended verbatim as the effect; the validator requires membership in the
   * fail-closed command table.
   */
  command?: string;
  /**
   * Envelope inputs: values are literals or `#/$defs/ref` templates.
   */
  inputs: { [key: string]: unknown };
  /**
   * Envelope target reference; resolved from run context (defaults to the trigger's own
   * target).
   */
  to?: string;
}

/**
 * Side effect of a capability emit: park a communication binding alongside the routing
 * intent (the pr-request pending-PR marker law).
 */
export interface Binding {
  /**
   * Binding channel (the repo), templated.
   */
  channel: string;
  /**
   * `branch`: a pending-PR marker binding (the head-branch resolver's key), inserted ON
   * CONFLICT DO NOTHING so redelivery is inert.
   */
  kind: BindingKind;
  /**
   * Provider token: a literal (importers bake the provider into scoped copies).
   */
  provider: string;
  /**
   * Binding root (for example `branch:` + the head ref), interpolated.
   */
  root: string;
}

/**
 * `branch`: a pending-PR marker binding (the head-branch resolver's key), inserted ON
 * CONFLICT DO NOTHING so redelivery is inert.
 */
export type BindingKind = "branch";

/**
 * Write a notification-ledger row for a human.
 */
export interface Escalate {
  message: string;
  /**
   * Escalation target (actor/team ref).
   */
  to: string;
}

/**
 * Declared in v1, executed when the fan-out consumer lands (enable-time
 * `step-kind-not-supported` until then).
 */
export interface FanOut {
  /**
   * Reference to the array to iterate.
   */
  over: string;
  /**
   * Inline emit payload executed once per element.
   */
  step: Step;
}

/**
 * Inline emit payload executed once per element.
 */
export interface Step {
  emit: Emit;
}

/**
 * Port of the fork law: successor Work + `created`/`work.linked` rows + binding repoint +
 * reply, with a deterministic successor work id.
 */
export interface ForkWork {
  /**
   * Note for the successor work's objective (first line wins), matching the review-arm law.
   */
  noteRef: string;
}

/**
 * Sub-workflow call: starts a child instance in the parent's transaction, optionally awaits
 * its output.
 */
export interface Invoke {
  /**
   * True: the parent parks and resumes with the child's projected output. False:
   * fire-and-forget (the child is cascade-cancelled only if the parent fails or is cancelled).
   */
  await?: boolean;
  /**
   * Child inputs; stored as the child's initial step-log row.
   */
  inputs?: { [key: string]: unknown };
  /**
   * Step id taken on timeout; absent means the `failed` edge.
   */
  onTimeout?: string;
  /**
   * Mandatory: breach cancels the non-terminal descendant subtree and takes `onTimeout`.
   */
  timeout: string;
  /**
   * Pinned callee version; absent = the callee's live enabled version, pinned into the child
   * at creation.
   */
  version?: number;
  /**
   * Callable definition id (must exist, be enabled, and keep the invoke-call graph acyclic).
   */
  workflow: string;
}

/**
 * Declared in v1, executed with fan-out.
 */
export interface Join {
  /**
   * Number of awaited child effects before advancing.
   */
  expect: number;
  timeout: string;
}

/**
 * Declared in v1, executed when the memory step consumer lands.
 */
export interface Memory {
  op: Op;
  scope: string;
  textRef: string;
}

export type Op = "recall" | "retain" | "promote";

/**
 * Pure projection: copy fields out of earlier outputs, the trigger envelope, or the
 * digest-verified stored manifest.
 */
export interface Select {
  /**
   * Fill projected fields that resolved to null: name -> literal or template (resolved
   * against the run context). Can only fill, never introduce, fields.
   */
  defaults?: { [key: string]: string };
  /**
   * Output name -> jsonPath over earlier step outputs / the trigger envelope. Missing paths
   * yield null (never an error).
   */
  project: { [key: string]: string };
  /**
   * Document the paths project over: the trigger envelope (default) or, for an event carrying
   * `data.resultManifestRef` + `data.resultDigest`, the STORED manifest artifact re-read by
   * the kernel (the record arm verified the digest pre-commit; a vanished artifact fails the
   * leg). Paths are relative to the manifest document.
   */
  source?: SelectSource;
}

/**
 * Document the paths project over: the trigger envelope (default) or, for an event carrying
 * `data.resultManifestRef` + `data.resultDigest`, the STORED manifest artifact re-read by
 * the kernel (the record arm verified the digest pre-commit; a vanished artifact fails the
 * leg). Paths are relative to the manifest document.
 */
export type SelectSource = "event" | "manifest";

/**
 * Park until the delay elapses; the sweeper is the clock.
 */
export interface Timer {
  for: string;
}

/**
 * Park the instance (`waiting`) until a matching event wakes it or the timeout edge fires.
 */
export interface Wait {
  /**
   * Correlation key resolved at park time (e.g. "$steps.assign.output.workId"); matched
   * against the waking envelope.
   */
  correlate: string;
  /**
   * Event type awaited; the definition joins the subscription set while the instance is
   * parked.
   */
  event: string;
  /**
   * Step id taken when the deadline passes (the sweeper drives it).
   */
  onTimeout: string;
  timeout: string;
}

/**
 * Create a Work and append its routing intent; the created work carries this instance in
 * `works.workflow_instance_id`.
 */
export interface WorkCreate {
  /**
   * Capability of the routing intent appended for the new work (there is no
   * `command.work.create` wire command: this maps onto the shared CreateWork +
   * AppendRoutingIntent law).
   */
  capability: string;
  /**
   * Objective text for the created work.
   */
  objectiveRef: string;
  /**
   * Owning team slug; defaults to the instance's team.
   */
  team?: string;
}
