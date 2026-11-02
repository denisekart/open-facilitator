# Harness adapters

One directory per harness (Pi, OpenCode, Codex, custom) wrapping the harness
with a small HarnessAdapter implementation (workspace-packaging
skill). Shipped:

- `adapter-sdk/` — `@ofac/adapter`: the lifecycle law over the child wire
  (`adapter.v1.json`) + the `./testing` conformance kit. Library only: no
  config, no CLI.
- `deterministic/` — `@ofac/harness-deterministic` (bin
  `ofac-deterministic-worker`): the zero-randomness worked example — two-step
  run, progress events, on-disk report with `sha256:` digest,
  `file://` external refs. Third-party implementers copy this package.

Provider harnesses (Pi et al.) land with their provider plans as sibling
packages built on `@ofac/adapter`.
