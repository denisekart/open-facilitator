// Runtime validation for the Open Facilitator wire contract. Ajv (draft
// 2020-12 + formats) compiles every shipped schema once per process; the
// fixture harnesses (vitest + Validate.Fixtures) are the conformance gate.
// Class is derived from the CloudEvents `type` segment — never a guess.
import Ajv2020, { type ValidateFunction } from "ajv/dist/2020.js";
import addFormats from "ajv-formats";
import {
  ALL_SCHEMAS,
  CONTROL_SCHEMA_ID,
  ENVELOPE_COMMAND_SCHEMA_ID,
  ENVELOPE_EVENT_SCHEMA_ID,
  RECEIPT_SCHEMA_ID,
  type JsonSchema,
  schemaById,
} from "./schemas.js";

export type ErrorClass =
  | "envelope-violation"
  | "schema-violation"
  | "payload-too-large"
  | "unknown-command"
  | "no-common-version";

export type ValidationResult =
  { ok: true } | { ok: false; errorClass: ErrorClass; message: string };

const fail = (errorClass: ErrorClass, message: string): ValidationResult => ({
  ok: false,
  errorClass,
  message,
});

const ajv = new Ajv2020({ allErrors: false, strict: false });
addFormats(ajv);
for (const schema of ALL_SCHEMAS) {
  if (!ajv.getSchema(schema["$id"] as string)) {
    ajv.addSchema(schema);
  }
}

function validator(id: string): ValidateFunction {
  const v = ajv.getSchema(id);
  if (!v) throw new Error(`schema not compiled: ${id}`);
  return v;
}

const envelopeCommandValidator = validator(ENVELOPE_COMMAND_SCHEMA_ID);
const envelopeEventValidator = validator(ENVELOPE_EVENT_SCHEMA_ID);
const receiptValidator = validator(RECEIPT_SCHEMA_ID);
const controlValidator = validator(CONTROL_SCHEMA_ID);

/** All command payload-schema $ids the contract knows (fail-closed table source). */
export const KNOWN_COMMAND_TYPES: readonly string[] = Object.freeze(
  ALL_SCHEMAS.filter((s) => (s["$id"] as string).includes("/command."))
    .map((s) => {
      // https://…/v1/command.work.assign.v1.json -> org.facilitator.command.work.assign.v1
      const file = (s["$id"] as string).split("/").pop() as string;
      return `org.facilitator.${file.replace(/\.json$/, "")}`;
    })
    .sort(),
);

function classify(v: ValidateFunction, structural: ErrorClass): ErrorClass {
  // Format-constraint failures are payload/schema-level (both harnesses must
  // agree); structural envelope failures (required/pattern/const) are
  // envelope violations.
  const e = v.errors?.[0];
  return e?.keyword === "format" ? "schema-violation" : structural;
}

function firstMessage(v: ValidateFunction): string {
  const e = v.errors?.[0];
  if (!e) return "validation failed";
  return `${e.instancePath || "(root)"} ${e.message ?? "invalid"}`;
}

/**
 * Validate a bare CloudEvents envelope payload: envelope schema (by class from
 * `type`), then the `data` payload against the schema advertised in
 * `dataschema`. An unknown `dataschema` $id is a schema-violation (fail closed).
 */
export function validateEnvelope(env: unknown): ValidationResult {
  if (typeof env !== "object" || env === null || Array.isArray(env)) {
    return fail("envelope-violation", "envelope must be a JSON object");
  }
  const type = (env as Record<string, unknown>)["type"];
  const dataschema = (env as Record<string, unknown>)["dataschema"];
  if (typeof type !== "string") {
    return fail("envelope-violation", "envelope.type must be a string");
  }
  const isCommand = type.startsWith("org.facilitator.command.");
  const isEvent = type.startsWith("org.facilitator.event.");
  if (!isCommand && !isEvent) {
    return fail("envelope-violation", `unknown message class in type '${type}'`);
  }
  const envelopeValidator = isCommand ? envelopeCommandValidator : envelopeEventValidator;
  if (!envelopeValidator(env)) {
    return fail(classify(envelopeValidator, "envelope-violation"), firstMessage(envelopeValidator));
  }
  if (isCommand && !KNOWN_COMMAND_TYPES.includes(type)) {
    // Fail closed: unknown commands get a typed `unsupported`.
    return fail("unknown-command", `command type '${type}' is not in the contract`);
  }
  if (typeof dataschema !== "string") {
    return fail("envelope-violation", "envelope.dataschema must be a string");
  }
  const dataSchema: JsonSchema | undefined = schemaById(dataschema);
  if (!dataSchema) {
    return fail("schema-violation", `no registered schema for dataschema '${dataschema}'`);
  }
  const dataValidator = validator(dataschema);
  const data = (env as Record<string, unknown>)["data"];
  if (!dataValidator(data)) {
    return fail("schema-violation", firstMessage(dataValidator));
  }
  return { ok: true };
}

/** Validate a plain-JSON receipt (not a CloudEvent). */
export function validateReceipt(v: unknown): ValidationResult {
  if (!receiptValidator(v)) {
    return fail("schema-violation", firstMessage(receiptValidator));
  }
  return { ok: true };
}

/** Validate a plain-JSON link control frame (hello/welcome/…). */
export function validateControlFrame(v: unknown): ValidationResult {
  if (!controlValidator(v)) {
    return fail("schema-violation", firstMessage(controlValidator));
  }
  return { ok: true };
}

/**
 * Validate a plain-JSON payload against any registered schema by $id (fixture
 * classes enroll/delivery and other non-envelope wire objects). An unknown
 * $id fails closed as schema-violation, mirroring the envelope path.
 */
export function validateAgainstSchemaId(schemaId: string, v: unknown): ValidationResult {
  if (!schemaById(schemaId)) {
    return fail("schema-violation", `unknown schema $id: ${schemaId}`);
  }
  const validate = validator(schemaId);
  if (!validate(v)) {
    return fail("schema-violation", firstMessage(validate));
  }
  return { ok: true };
}

/**
 * Pure size gate: harnesses count wire bytes themselves
 * (`Buffer.byteLength(JSON.stringify(payload))` / UTF-8 on .NET side).
 */
export function enforceSizeLimit(bytes: number, max: number): boolean {
  return bytes <= max;
}

/**
 * Handshake negotiation: pick the highest version both sides speak,
 * or null when there is no common version (`no-common-version`).
 */
export function negotiateProtocolVersion(
  offered: readonly string[],
  supported: readonly string[],
): string | null {
  const num = (v: string): number | null => {
    const m = /^v([0-9]+)$/.exec(v);
    return m ? Number(m[1]) : null;
  };
  const common = offered
    .filter((v) => supported.includes(v))
    .map(num)
    .filter((n): n is number => n !== null)
    .sort((a, b) => b - a);
  return common.length > 0 ? `v${common[0]}` : null;
}
