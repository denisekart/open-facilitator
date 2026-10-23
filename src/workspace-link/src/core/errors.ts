// Typed error surface for the Link client (Plan 04 locked decision). Server
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

export class LinkError extends Error {
  readonly code: string;

  constructor(code: string, message: string) {
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

/** A superseding session (newer leaseEpoch) fenced this one's session. */
export class SessionSupersededError extends LinkError {
  readonly problem?: ProblemDetails;

  constructor(problem?: ProblemDetails) {
    super(
      "session-superseded",
      `session superseded by a newer connection${problem?.type ? ` (${problem.type})` : ""}`,
    );
    this.problem = problem;
  }
}

/** POST /api/link/v1/enroll refused the enrollment secret (401) or the body (400). */
export class EnrollmentError extends LinkError {
  readonly status: number;
  readonly problem: ProblemDetails;

  constructor(status: number, problem: ProblemDetails) {
    const suffix = problem.type ? ` (${problem.type})` : "";
    super(
      problem.type?.split("/").pop() ?? "enrollment-failed",
      `enrollment refused with HTTP ${status}${suffix}: ${problem.detail ?? problem.title ?? "no detail"}`,
    );
    this.status = status;
    this.problem = problem;
  }
}

/** A client-sent envelope was refused by the Facilitator (ack status rejected). */
export class DeliveryRejectedError extends LinkError {
  readonly errorClass: ErrorClass | "rejected";
  readonly problem?: ProblemDetails;

  constructor(errorClass: ErrorClass | "rejected", problem?: ProblemDetails) {
    const suffix = problem?.type ? ` (${problem.type})` : "";
    super(errorClass, `envelope rejected: ${errorClass}${suffix}`);
    this.errorClass = errorClass;
    this.problem = problem;
  }
}

/** Transport-level failure (connect/send). `unauthorized` ⇒ token refresh path. */
export class TransportError extends LinkError {
  readonly problem?: ProblemDetails;

  constructor(message: string, opts: { unauthorized?: boolean; problem?: ProblemDetails } = {}) {
    super(opts.unauthorized ? "unauthorized" : "transport", message);
    this.problem = opts.problem;
  }

  get unauthorized(): boolean {
    return this.code === "unauthorized";
  }
}
