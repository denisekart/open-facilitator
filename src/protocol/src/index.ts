// @ofac/protocol — the frozen v1 wire contract as a consumable package.
// The Workspace Link SDK consumes generated types from this root entry
// without remapping; the Link server uses KNOWN_COMMAND_TYPES as its
// fail-closed command table.
export * from "./generated/index.js";
export * from "./digest.js";
export {
  ALL_SCHEMAS,
  SCHEMA_IDS,
  schemaById,
  CONTROL_SCHEMA_ID,
  ENVELOPE_SCHEMA_ID,
  ENVELOPE_COMMAND_SCHEMA_ID,
  ENVELOPE_EVENT_SCHEMA_ID,
  RECEIPT_SCHEMA_ID,
  type JsonSchema,
} from "./schemas.js";
export {
  validateEnvelope,
  validateReceipt,
  validateControlFrame,
  validateAgainstSchemaId,
  enforceSizeLimit,
  negotiateProtocolVersion,
  KNOWN_COMMAND_TYPES,
  type ErrorClass,
  type ValidationResult,
} from "./validate.js";
