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

export interface ChatMessage {
  messageId: string;
  role: "user" | "assistant";
  text: string;
  basis: string;
  createdAt: string;
  actions?: ChatAction[];
}

export interface ChatAction {
  actionId: string;
  method: string;
  path: string;
  body?: unknown;
  description: string;
  status: "pending" | "executed" | "failed" | "cancelled";
  statusCode?: number | null;
  result?: unknown;
}

export interface ChatReply {
  sessionId: string;
  messageId: string;
  reply: string;
  actions: ChatAction[];
}

export interface ChatPreferences {
  verbosity: ChatVerbosity;
}
