# protocol (this package + `../../protocol/`)

## Purpose
Wire-contract source of truth. `../../protocol/` holds the normative artifacts:
CloudEvents envelope + payload JSON Schemas (`protocol/json-schema/`,
`protocol/cloudevents-schemas/`), the OpenAPI description of the Link HTTP
surface (`protocol/openapi/link-v1.yaml`), the dual-harness conformance fixtures
(`protocol/conformance-fixtures/`), the .NET wire library (`protocol/dotnet/Ofac.Protocol`), and
the codegen pipeline (`protocol/tools/codegen.mjs`). This directory is the TypeScript
package (`@ofac/protocol`) that consumes them.

## Truth anchors
- Schema files are the wire law: message shapes and error URIs exist because a
  schema says so, nothing else.
- `../../protocol/tools/codegen.mjs` renders `src/generated/` and
  `../../protocol/dotnet/Ofac.Protocol/Generated` plus the `schemas/` mirror —
  all read-only.
- `../../protocol/conformance-fixtures/` + both harnesses: the TS validator in
  `test/fixtures.test.ts` here and `../../protocol/tools/Validate.Fixtures`
  (.NET).

## Develop here
- After any schema edit: `pnpm protocol:codegen` (repo root), then
  `make protocol` — it re-validates the OpenAPI and fails on output drift.
  CI's protocol job runs the same `git diff --exit-code` gate.
- A wire change is ONE change: schemas + codegen + both validators + fixtures.
- `pnpm -C src/protocol test` runs the validator + fixture suite;
  `pnpm -C src/protocol typecheck` gates the package.
- The .NET library builds with `Nullable=enable`; the generated file suppresses
  only warning-context via a banner directive — never loosen the csproj.

## Invariants
- NEVER hand-edit the generated dirs; regenerate or the drift gate fails.
- Fixture `expect` verdicts are the cross-implementation contract — changing
  one means the wire contract itself changed.
- Runtime validation here is ajv against the mirrored schemas; no hand-rolled
  shape checks anywhere downstream.
