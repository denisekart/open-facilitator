/* Mirrors of the kernel's camelCase admin DTOs (src/facilitator/Kernel/Admin).
   No client-side aggregation: one type per wire shape. */

export interface AdminSummary {
  teams: number;
  /** Roster slugs (sorted); drives the mint-card team dropdown. */
  teamSlugs: string[];
  projects: number;
  workByState: Record<string, number>;
  assignmentsByState: Record<string, number>;
  executionsByState: Record<string, number>;
  gatewaysOnline: number;
  gatewaysOffline: number;
  inboxByState: Record<string, number>;
  outboxByState: Record<string, number>;
  deadLettersTotal: number;
  oldestPendingOutboxAt: string | null;
  approvalsByState: Record<string, number>;
  artifactsTotal: number;
  manifestsByResultState: Record<string, number>;
  memoryByStatus: Record<string, number>;
  linkSessionsLive: number;
}

export interface AdminSearchHit {
  id: string;
  display: string;
  state: string;
  at: string;
}

export interface AdminSearchResults {
  query: string;
  works: AdminSearchHit[];
  projects: AdminSearchHit[];
  assignments: AdminSearchHit[];
  artifacts: AdminSearchHit[];
  memories: AdminSearchHit[];
  messages: AdminSearchHit[];
}

export const SEARCH_SECTIONS = [
  "works",
  "projects",
  "assignments",
  "artifacts",
  "memories",
  "messages",
] as const;
export type SearchSection = (typeof SEARCH_SECTIONS)[number];

export interface AdminLinkRow {
  endpointId: string;
  team: string;
  trustMode: string;
  capabilitiesDigest: string | null;
  online: boolean;
  sessionEpoch: number | null;
  leaseUntil: string | null;
  lastHeartbeat: string | null;
  replicaId: string | null;
  liveSockets: number;
  /** Credential identity (the lifecycle plan's columns): class, active key, fences. */
  credentialClass: string | null;
  endpointKeyId: string | null;
  epoch: number | null;
  seq: number | null;
  /** Last fence check rejected a proof (audit link.replay-suspect after enrollment). */
  replaySuspect: boolean;
}

export interface AdminAffectedAssignment {
  assignmentId: string;
  workId: string;
  status: string;
}

export interface AdminLinksView {
  gateways: AdminLinkRow[];
  affectedAssignments: AdminAffectedAssignment[];
}

/** One row of GET /api/admin/enroll-tokens (the token literal is never returned). */
export interface EnrollmentTokenRow {
  id: string;
  label: string;
  team: string;
  role: string;
  createdAt: string;
  expiresAt: string;
  consumedAt: string | null;
  consumedEndpointId: string | null;
  revokedAt: string | null;
}

/** The 201 body of POST /api/admin/enroll-tokens — the only place the token literal appears. */
export interface MintedEnrollmentToken {
  id: string;
  token: string;
  expiresAt: string;
}

export interface AdminDeliveryRow {
  plane: string;
  id: string;
  state: string;
  attempts: number;
  lastError: string | null;
  availableAt: string | null;
  endpointId: string | null;
  sessionEpoch: number | null;
  redeliverAfter: string | null;
  reason: string | null;
  updatedAt: string;
}

export interface AdminHourBucket {
  hour: string;
  count: number;
}

export interface AdminMetrics {
  outboxPending: number;
  inboxPending: number;
  deadLetters: number;
  linkSessions: number;
  deadLettersPerHour: AdminHourBucket[];
  auditDeniesPerHour: AdminHourBucket[];
  artifactsStoredPerHour: AdminHourBucket[];
  artifactsRejectedPerHour: AdminHourBucket[];
  counters: Record<string, number>;
}

export interface AdminWorkRow {
  workId: string;
  objective: string;
  state: string;
  version: number;
  createdAt: string;
  updatedAt: string;
  /** Detail payloads omit it; GET /api/admin/works always fills it. */
  teamSlug?: string | null;
}

export interface AdminAttemptRow {
  executionId: string;
  generation: number;
  status: string;
  startedAt: string;
  endedAt: string | null;
}

export interface AdminAssignmentRow {
  assignmentId: string;
  status: string;
  currentExecutionGeneration: number;
  createdAt: string;
  updatedAt: string;
  attempts: AdminAttemptRow[];
}

export interface AdminApprovalRow {
  approvalId: string;
  state: string;
  actionDigest: string;
  createdAt: string;
  decidedAt: string | null;
}

export interface AdminManifestRow {
  manifestId: string;
  revision: number;
  revisionOf: string | null;
  resultState: string;
  state: string;
  artifactId: string;
  outputArtifactIds: string[];
  recordedAt: string;
}

export interface AdminWorkDetail {
  work: AdminWorkRow;
  projectId: string | null;
  assignments: AdminAssignmentRow[];
  approvals: AdminApprovalRow[];
  manifests: AdminManifestRow[];
}

export interface WorkEventRow {
  eventId: string;
  workId: string;
  workVersion: number;
  kind: string;
  actor: string;
  detail: string;
  correlationId: string | null;
  traceId?: string | null;
  createdAt: string;
}

export interface AdminProjectDetail {
  projectId: string;
  name: string;
  teamSlug: string | null;
  config: unknown;
  createdAt: string;
  works: AdminWorkRow[];
}

/** One row of GET /api/admin/audit — gate and feature audit writes,
    newest first, the trace correlation included. */
export interface AdminAuditRow {
  auditId: string;
  occurredAt: string;
  actor: string | null;
  action: string;
  resource: string | null;
  decision: string;
  reason: string | null;
  detail: unknown;
  traceId: string | null;
  correlationId: string | null;
}

/** One replica row of GET /api/admin/replicas. */
export interface AdminReplicaRow {
  replicaId: string;
  liveSessions: number;
  socketsHere: number;
}

/** GET /api/admin/replicas: who answers, who leases, fleet queue depth. */
export interface AdminReplicasView {
  answeringReplicaId: string;
  replicas: AdminReplicaRow[];
  outboxPending: number;
}

/** One provisioning_operations ledger row (GET /api/admin/provisioning). */
export interface AdminProvisioningRow {
  operationId: string;
  workloadId: string;
  team: string;
  operation: string;
  state: string;
  workId: string | null;
  assignmentId: string | null;
  generation: number | null;
  reason: string | null;
  createdAt: string;
  updatedAt: string;
}

// ---- config surface ----
export interface AdminConfigListRow {
  resource: string;
  exists: boolean;
  rowVersion: number;
}

export interface AdminConfigView {
  resource: string;
  payload: Record<string, unknown>;
  rowVersion: number;
  author: string;
  reason: string;
  updatedAt: string;
}

export interface AdminConfigHistoryEntry {
  version: number;
  payload: Record<string, unknown>;
  author: string;
  reason: string;
  updatedAt: string;
}

// ---- SPA bootstrap (GET /ui-config, anonymous) ----

export interface UiConfig {
  oidcAuthority: string;
  oidcClientId: string;
}

// ---- AiDeck chat plane (Kernel/Ai/ChatEndpoints) ----

export type ChatVerbosity = "terse" | "balanced" | "proactive";

export interface ChatSession {
  sessionId: string;
  title: string;
  lastMessageAt: string | null;
  actor: string;
}

/** One entry of the read-phase trail a message may carry. */
export interface ChatReadEntry {
  method: string;
  path: string;
  status: number;
}

/** The jsonb shape stored on a message: null when the turn read nothing. */
export interface ChatReadsTrail {
  reads: ChatReadEntry[];
}

export interface ChatMessage {
  messageId: string;
  role: "user" | "assistant";
  text: string;
  basis: string;
  createdAt: string;
  actions?: ChatAction[];
  /** Read-phase trail ({"reads":[{method,path,status}]}), null when the turn read nothing. */
  reads?: ChatReadsTrail | null;
}

export interface ChatAction {
  actionId: string;
  method: string;
  path: string;
  body?: unknown;
  description: string;
  status: "pending" | "executed" | "failed" | "cancelled" | "expired";
  statusCode?: number | null;
  result?: unknown;
  /** Operator decision note (confirm/cancel), when given. */
  note?: string | null;
  /** When a pending proposal stops being confirmable (null = never). */
  expiresAt?: string | null;
  /** Human summary of the target endpoint (catalog line), falling back to the description. */
  summary?: string | null;
}

/** One row of GET /api/chat/catalog `catalog` — the principal's action slice. */
export interface ChatCatalogEntry {
  method: string;
  path: string;
  summary: string;
  bodyHint: string;
  notes: string | null;
  effect: "read" | "write";
}

/** One argument spec of a registry verb. */
export interface ChatVerbArg {
  name: string;
  required: boolean;
}

/** One row of GET /api/chat/catalog `verbs` — the command registry. */
export interface ChatVerbSpec {
  verb: string;
  description: string;
  args: ChatVerbArg[];
}

/** GET /api/chat/catalog: action catalog + verb registry, principal-filtered. */
export interface ChatCatalogView {
  catalog: ChatCatalogEntry[];
  verbs: ChatVerbSpec[];
}

export interface ChatReply {
  sessionId: string;
  messageId: string;
  reply: string;
  actions: ChatAction[];
  /** Read-phase trail ({"reads":[{method,path,status}]}), null when the turn read nothing. */
  reads?: ChatReadsTrail | null;
}

export interface ChatPreferences {
  verbosity: ChatVerbosity;
}

/** One row of GET /api/admin/workflows/definitions (latest version folded). */
export interface WorkflowDefinitionRow {
  id: string;
  version: number;
  title: string;
  description: string;
  source: string;
  enabled: boolean;
  shadow: boolean;
  team: string | null;
  digest: string;
  instancesRunning: number;
}

/** One row of GET /api/admin/workflows/instances (newest first). */
export interface WorkflowInstanceRow {
  instanceId: string;
  definitionId: string;
  definitionVersion: number;
  status: string;
  currentStep: string | null;
  depth: number;
  parentInstanceId: string | null;
  primaryWorkId: string | null;
  endReason: string | null;
  startedAt: string;
  updatedAt: string;
}

/** GET /api/admin/workflows/instances/{id}: lineage around one instance. */
export interface WorkflowInstanceDetail extends Omit<WorkflowInstanceRow, "parentInstanceId"> {
  invocationPath: string;
  output: unknown;
  ancestors: {
    instanceId: string;
    definitionId: string;
    definitionVersion: number;
    status: string;
    currentStep: string | null;
  }[];
  children: {
    instanceId: string;
    definitionId: string;
    definitionVersion: number;
    status: string;
    currentStep: string | null;
    depth: number;
    endReason: string | null;
  }[];
}

/** One step-log row: the vertical checklist behind an instance. */
export interface WorkflowStepLogRow {
  seq: number;
  stepId: string;
  status: string;
  effectCommandId: string | null;
  output: string | null;
  startedAt: string;
  updatedAt: string;
}

/** GET /api/admin/workflows/registry: the closed sets the editor selects from. */
export interface WorkflowRegistry {
  eventTypes: string[];
  commands: string[];
  capabilities: string[];
  callable: { id: string; version: number; title: string }[];
  teams: string[];
}
