// Bootstrap enrollment (spec §16.2, OpenAPI POST /api/link/v1/enroll). Plain
// global fetch — no Node-only imports — guarded by the enrollment SECRET in
// the body (not a bearer header, docs/protocol.md "Auth").
import type { EnrollRequestV1, EnrollResponseV1 } from "@ofac/protocol";
import { validateAgainstSchemaId } from "@ofac/protocol";
import { EnrollmentError, type ProblemDetails } from "./errors.js";

export interface EnrollOptions {
  baseUrl: string;
  /** Mode (a): enrollment secret (config Link:EnrollmentToken; dev: `enroll-dev`). Mode (b): an OIDC client-credentials JWT. */
  token: string;
  endpointId: string;
  instanceId: string;
  trustMode: EnrollRequestV1["trustMode"];
  /** Workload role claim (plan 10); the kernel rejects contradictions with a mode-(b) binding or the gw. prefix law. */
  role?: "gateway" | "child";
  /** Team roster slug to bind (required for mode (a); a mode-(b) binding's team wins). */
  team?: string;
}

export const ENROLL_SCHEMA_ID = "https://schemas.openfacilitator.org/v1/enroll-request.v1.json";
export const ENROLL_RESPONSE_SCHEMA_ID =
  "https://schemas.openfacilitator.org/v1/enroll-response.v1.json";

export async function enroll(
  opts: EnrollOptions,
  fetchImpl: typeof fetch = fetch,
): Promise<EnrollResponseV1> {
  const body: EnrollRequestV1 = {
    token: opts.token,
    endpointId: opts.endpointId,
    instanceId: opts.instanceId,
    trustMode: opts.trustMode,
    ...(opts.role ? { role: opts.role } : {}),
    ...(opts.team ? { team: opts.team } : {}),
  };
  const verdict = validateAgainstSchemaId(ENROLL_SCHEMA_ID, body);
  if (!verdict.ok) {
    throw new EnrollmentError(400, { type: verdict.errorClass, detail: verdict.message });
  }
  const response = await fetchImpl(`${trimSlash(opts.baseUrl)}/api/link/v1/enroll`, {
    method: "POST",
    headers: { "content-type": "application/json" },
    body: JSON.stringify(body),
  });
  if (response.status !== 201) {
    throw new EnrollmentError(response.status, await parseProblem(response));
  }
  const payload = (await response.json()) as EnrollResponseV1;
  const responseVerdict = validateAgainstSchemaId(ENROLL_RESPONSE_SCHEMA_ID, payload);
  if (!responseVerdict.ok) {
    throw new EnrollmentError(502, {
      type: responseVerdict.errorClass,
      detail: `enroll response violates enroll-response.v1.json: ${responseVerdict.message}`,
    });
  }
  return payload;
}

export async function parseProblem(response: Response): Promise<ProblemDetails> {
  try {
    const text = await response.text();
    if (text.length === 0) return { status: response.status };
    const parsed = JSON.parse(text) as ProblemDetails;
    return { ...parsed, status: parsed.status ?? response.status };
  } catch {
    return { status: response.status, detail: "non-JSON error body" };
  }
}

export function trimSlash(url: string): string {
  return url.endsWith("/") ? url.slice(0, -1) : url;
}
