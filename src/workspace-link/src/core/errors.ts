// Typed error surface for the Link client (locked decision). Server
// originated failures always carry the ProblemDetails `type` URI so callers
// can branch on the frozen error taxonomy (docs/protocol.md "Error URIs").
import type { ErrorClass } from "@ofac/protocol";

export interface ProblemDetails {
  type?: string;
  title?: string;
  status?: number;
  detail?: string;
  [key: string]: unknown;
}

/**
 * Every code `LinkError` and its subclasses can carry: the validator taxonomy
 * (ErrorClass), the client lifecycle, the transport surface, and the leaf
 * names of the declared server ProblemDetails types.
 */
export type LinkErrorCode =
  | ErrorClass
  | "already-started"
  | "closed"
  | "reconnect-exhausted"
  | "pipeline"
  | "receipt-failed"
  | "capabilities-refused"
  | "unauthorized"
  | "transport"
  | "rejected"
  | "unsupported"
  | "session-not-found"
  | "delivery-not-found"
  | "capabilities-not-found"
  | "team-mismatch"
  | "enrollment-secret-invalid"
  | "enrollment-failed";

// ProblemDetails `type` URIs map to codes by leaf name; an undeclared type
// falls back to the generic enrollment failure.
const PROBLEM_TYPE_CODE: Record<string, LinkErrorCode | undefined> = {
  unsupported: "unsupported",
  "session-not-found": "session-not-found",
  "delivery-not-found": "delivery-not-found",
  "capabilities-not-found": "capabilities-not-found",
  "team-mismatch": "team-mismatch",
  "enrollment-secret-invalid": "enrollment-secret-invalid",
  "no-common-version": "no-common-version",
};

export class LinkError extends Error {
  readonly code: LinkErrorCode;

  constructor(code: LinkErrorCode, message: string) {
    super(message);
    this.name = new.target.name;
    this.code = code;
  }
}

/** Terminal: hello/welcome negotiation found no common protocol version. */
export class ProtocolMismatchError extends LinkError {
  constructor(message = "server answered hello with no-compatible-protocol-version") {
    super("no-common-version", message);
  }
}

/** POST /api/link/v1/enroll refused the enrollment secret (401) or the body (400). */
export class EnrollmentError extends LinkError {
  readonly status: number;
  readonly problem: ProblemDetails;

  constructor(status: number, problem: ProblemDetails) {
    const suffix = problem.type ? ` (${problem.type})` : "";
    super(
      problem.type !== undefined
        ? (PROBLEM_TYPE_CODE[problem.type.split("/").pop() ?? ""] ?? "enrollment-failed")
        : "enrollment-failed",
      `enrollment refused with HTTP ${status}${suffix}: ${problem.detail ?? problem.title ?? "no detail"}`,
    );
    this.status = status;
    this.problem = problem;
  }
}

/** A client-sent envelope was refused by the Facilitator (ack status rejected). */
export class DeliveryRejectedError extends LinkError {
  readonly errorClass: ErrorClass | "rejected";
  readonly problem?: ProblemDetails | undefined;

  constructor(errorClass: ErrorClass | "rejected", problem?: ProblemDetails) {
    const suffix = problem?.type ? ` (${problem.type})` : "";
    super(errorClass, `envelope rejected: ${errorClass}${suffix}`);
    this.errorClass = errorClass;
    this.problem = problem;
  }
}

/** Transport-level failure (connect/send). `unauthorized` ⇒ token refresh path. */
export class TransportError extends LinkError {
  readonly problem?: ProblemDetails | undefined;

  constructor(message: string, opts: { unauthorized?: boolean; problem?: ProblemDetails } = {}) {
    super(opts.unauthorized ? "unauthorized" : "transport", message);
    this.problem = opts.problem;
  }

  get unauthorized(): boolean {
    return this.code === "unauthorized";
  }
}
