// Capability-manifest publication: digest + POST. The digest is over the
// EXACT bytes POSTed (server stores plain hex, LinkIngress.DigestOf; the
// client re-adds the `sha256:` prefix that control.v1.json's hello pattern
// requires). Validation runs against the frozen schema before send.
import { createHash } from "node:crypto";
import { validateAgainstSchemaId, type CapabilityManifestV1 } from "@ofac/protocol";
import { parseProblem, trimSlash } from "../core/enroll.js";
import { LinkError } from "../core/errors.js";
export const MANIFEST_SCHEMA_ID =
  "https://schemas.openfacilitator.org/v1/capability-manifest.v1.json";

/** `"sha256:<64hex>"` over the exact serialized manifest bytes. */
export function digestOfManifest(manifestBytes: string): string {
  return `sha256:${createHash("sha256").update(manifestBytes, "utf8").digest("hex")}`;
}

export interface CapabilitiesTransport {
  baseUrl: string;
  getToken(): Promise<string>;
  fetchImpl?: typeof fetch;
}

/** POST /api/link/v1/capabilities with the raw manifest text (204 expected). */
export async function postCapabilities(
  manifest: CapabilityManifestV1,
  transport: CapabilitiesTransport,
): Promise<{ digest: string; body: string }> {
  const verdict = validateAgainstSchemaId(MANIFEST_SCHEMA_ID, manifest);
  if (!verdict.ok) {
    throw new LinkError(
      verdict.errorClass,
      `manifest violates capability-manifest.v1.json: ${verdict.message}`,
    );
  }
  // Single canonical serialization: the digest below MUST cover these bytes.
  const body = JSON.stringify(manifest);
  const response = await (transport.fetchImpl ?? fetch)(
    `${trimSlash(transport.baseUrl)}/api/link/v1/capabilities`,
    {
      method: "POST",
      headers: {
        authorization: `Bearer ${await transport.getToken()}`,
        "content-type": "application/json",
      },
      body,
    },
  );
  if (response.status !== 204) {
    const problem = await parseProblem(response);
    throw new LinkError(
      "capabilities-refused",
      `POST /capabilities answered ${response.status}${problem.type ? ` (${problem.type})` : ""}: ${problem.detail ?? "no detail"}`,
    );
  }
  return { digest: digestOfManifest(body), body };
}
