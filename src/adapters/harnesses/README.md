# Harness adapters

One directory per harness (Pi, OpenCode, Codex, custom) wrapping the harness
with a small HarnessAdapter implementation (workspace-packaging
skill). Shipped:

- `adapter-sdk/` — `@ofac/adapter`: the lifecycle law over the child wire
  (`adapter.v1.json`) + the `./testing` conformance kit. Library only: no
  config, no CLI.
- `cli/` — `@ofac/harness-cli` (bin `ofac-cli-worker`): the playbook-driven
  worker — a YAML playbook declares the capability face and the steps, the
  worker spawns any CLI tool, watches markers, captures outputs, and derives
  the advertised manifest from the playbook itself. The zero-randomness
  parity face (research.analyze + research.publish) rides the sample
  playbook + fake tool. Third-party implementers copy this package, or
  `examples/custom-hooks-worker` for the pure-hooks shape.
- `omp/` — `@ofac/harness-omp` (bin `ofac-omp-worker`): full TUI stand-in for
  the oh-my-pi CLI (`omp --mode rpc`), live steer, resume-aware, `ask_user`
  MCP relay. Shipped as the `ofac-omp-adapter` Dev Container Feature.
- `codex/` — `@ofac/harness-codex` (bin `ofac-codex-worker`): OpenAI `codex
  exec --json` stand-in, JSONL event relay, resume-aware, no live steer
  (`supportsSteering: false`), `ask_user` MCP relay. Shipped as the
  `ofac-codex-adapter` Dev Container Feature.

Provider harnesses (Pi et al.) land with their provider plans as sibling
packages built on `@ofac/adapter`.
