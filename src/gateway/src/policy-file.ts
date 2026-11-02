// Team policy file loader. The schema lives in the
// contract tree (protocol/json-schema/policy-file.v1.json) for third-party
// Gateway builders, but it is a Gateway-local artifact: it never rides a wire
// envelope. Structural validation reuses @ofac/protocol's registered-schema
// path (fail-closed, pointer-carrying ajv messages) — no new validator dep.
import { createHash } from "node:crypto";

import { validateAgainstSchemaId } from "@ofac/protocol";
import { parse as parseYaml } from "yaml";

export const POLICY_FILE_SCHEMA_ID = "https://schemas.openfacilitator.org/v1/policy-file.v1.json";

export type PolicyEffect = "allow" | "deny" | "confirm";

export interface PolicyRow {
  readonly capability: string;
  readonly resources: readonly string[];
  readonly actorAnyOf: readonly string[];
  readonly effect: PolicyEffect;
  readonly conditions?: Readonly<Record<string, string | number | boolean>> | undefined;
}

export interface PolicyFile {
  readonly owner: string;
  readonly policies: readonly PolicyRow[];
}

export interface LoadedPolicy {
  readonly file: PolicyFile;
  /** `sha256:<hex>` over the file bytes as read (whitespace-sensitive). */
  readonly revision: string;
  /** `<owner>/<first-12-of-digest>` — cited in every audit line. */
  readonly policyId: string;
}

export class PolicyFileError extends Error {
  constructor(pointer: string, message: string) {
    super(`${pointer}: ${message}`);
    this.name = "PolicyFileError";
  }
}

/**
 * Parse + validate the YAML text of a policy file. Any malformation throws
 * (Gateway boot aborts — a half-parsed policy is a silently wrong one).
 */
export function loadPolicyFile(text: string): LoadedPolicy {
  let doc: unknown;
  try {
    doc = parseYaml(text);
  } catch (error) {
    throw new PolicyFileError(
      "yaml",
      `cannot parse: ${error instanceof Error ? error.message : String(error)}`,
    );
  }
  const verdict = validateAgainstSchemaId(POLICY_FILE_SCHEMA_ID, doc);
  if (!verdict.ok) {
    throw new PolicyFileError(verdict.errorClass, verdict.message);
  }
  // SAFETY: the schema pass guarantees the shape; the cast only erases the
  // `unknown` the untyped registry hands back.
  const file = doc as PolicyFile;
  const revision = "sha256:" + createHash("sha256").update(text, "utf8").digest("hex");
  return {
    file,
    revision,
    policyId: `${file.owner}/${revision.slice("sha256:".length, "sha256:".length + 12)}`,
  };
}
