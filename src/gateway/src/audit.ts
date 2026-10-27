// Policy audit lines (Plan 12 Step 4, spec §12.2). One structured info line
// per authorization decision the Gateway makes; the kernel-side counterpart
// (facilitator.audit rows, action `authorization.evaluated:approval`) is
// unchanged — this is the Gateway mirror. Long-lived tokens NEVER ride the
// line: subjects by canonical spelling, evidence by reference only.
import type { LinkLogger } from "@ofac/workspace-link";

export interface PolicyAuditFields {
  decision: "allow" | "deny" | "confirm";
  reason: string;
  workId?: string | undefined;
  assignmentId?: string | undefined;
  capabilityId?: string | undefined;
  resource: string;
  /** Principal 1: who sent the command (workload identity). */
  callerWorkload: string;
  /** Principal 2: the Gateway acting on it. */
  gatewayWorkload: string;
  /** Principal 3: the human behind verified evidence; absent = machine-only. */
  humanSubject?: string | undefined;
  /** `<owner>/<sha256-12>` — the policy revision consulted (§12.2). */
  policyId: string;
  actionDigest?: string | undefined;
  evidenceRef?: string | undefined;
  traceId?: string | undefined;
  correlationId?: string | undefined;
}

/** `authorization.evaluated` — the §12.2 field set, minus long-lived tokens. */
export function auditPolicy(logger: LinkLogger, fields: PolicyAuditFields): void {
  logger.info("authorization.evaluated", { ...fields });
}
