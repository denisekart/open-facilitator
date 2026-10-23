# Plan 01 — Protocol contract package: schemas, envelope, conformance fixtures

**Status:** ⬜ not-started
**Depends on:** 00

Status legend: ⬜ `not-started` → 🔵 `replanning` (mandatory gate) → 🟡 `in-progress` → 🟢 `done`. This plan starts with a replanning pass (flip to 🔵): re-verify assumptions against the specs and prior plans' state, run the research below, finalize this file's detail design, then flip to 🟡.

## Objective

Publish the normative wire contract as machine-readable artifacts before any runtime code: CloudEvents 1.0 envelope JSON Schemas for the four message classes (command/event/receipt/control, §5.2), JSON Schemas per message type (`org.facilitator.*.v1` namespace), canonical-serialization fixtures, and a schema-validation conformance harness consumed by later plans in .NET and TypeScript.

## Replanning gate

- Fix the exact type-name list:
  - commands: `work.assign|steer|cancel|status.request|nudge`, `context.fetch`, `approval.respond`
  - events: `work.accepted|progress|input_required`, `assignment.completed|failed`, `workspace.heartbeat`, `artifact.available`, `memory.candidate`
  - receipts: `accepted|duplicate|rejected|unsupported`
  - control: `hello|welcome|heartbeat|flow_control|goodbye`
- Fix required fields per §5.2 (commandId, workId, assignmentId, targetId, deadline, expectedContextVersion, actorEvidenceRef, capabilityId) and appendix §16 canonicalization rules.
- Choose codegen tool: `quicktype` or hand-rolled with `JsonSchema.Net`/`ajv`.

## Scope

- `protocol/json-schema/*.json` (one schema per type, `$id` under `https://schemas.openfacilitator.org/...`).
- `protocol/cloudevents-schemas/envelope.json`.
- `protocol/conformance-fixtures/*.json` (valid/invalid pairs, incl. unknown-command-rejection and payload-limit cases).
- `protocol/openapi/link-v1.yaml` stub for the HTTP fallback surface (§5.3).
- Generated C# DTOs project + TS types package (`@ofac/protocol`) from the schemas.
- Validation CLI (`dotnet run --project protocol/tools/Validate.Fixtures`) + vitest fixture suite.

## State after

Any runtime component can reference versioned schemas + typed DTOs; CI validates every fixture against every schema; §26.1 item 1 (schema validation, payload limits, major-version negotiation, unknown command rejection) has its fixture basis.

## Testing

Fixture harness: all valid fixtures validate, all invalid fixtures fail with the expected error class; DTO/codegen drift check in CI.

## Documentation

`docs/protocol.md` — envelope, message classes, namespacing/versioning rules (§5.5), how to add a v2 type.

## Architecture fit

§5 entirely; appendix §5 (outbox/inbox payload shapes), §16 (canonical JSON). The contract every other plan implements against — this is why it precedes all runtime code.

## Cross-plan rule

Shared conformance fixtures live in `protocol/conformance-fixtures/` from here onward; every later plan adds fixtures for its behavior. Anything asserted about wire behavior must exist as a fixture entry, run by both .NET and TS harnesses.

## Additional resources

CloudEvents 1.0 spec + JSON schema binding; JSON Schema 2020-12; RFC 8785; spec §5.2/§5.5, appendix §16.
