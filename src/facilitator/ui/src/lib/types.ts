/* Mirrors of the kernel's camelCase admin DTOs (src/facilitator/Kernel/Admin).
   No client-side aggregation: one type per wire shape (plan 21 decision 9). */

export interface AdminSummary {
  teams: number;
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
