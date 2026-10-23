# Conformance fixtures

Golden wire-format fixtures consumed by both conformance harnesses (spec §26).
Implemented in Plan 01: `index.json` is the arbiter (limits + per-case expectations);
`pnpm --filter @ofac/protocol test` and
`dotnet run --project protocol/tools/Validate.Fixtures` run the same suite.
