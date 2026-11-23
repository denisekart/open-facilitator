# Example: research worker (full)

Full-profile workspace example for artifact/memory integration; read by
anyone looking for a shipped research-workspace container or building this
one. The directory holds this page only — the reference stack deploys no
image from here, so nothing in it is a live leg.

What exists today: the full profile's research lane runs as the
`demo-workspace` compose service (`../../reference/compose.full.yaml`), and
memory behaviour follows the memory-broker law in `docs/advanced.md` (the
`.facilitator/memory.yaml` profile, the `memory.*` operations). The normative
design for a workspace of this shape is the `research-worker` service in
`docs/specs/open_facilitator_framework_architecture.md`. Closest working
code to build on: `examples/full/coding-worker` (a full-profile workspace
container) and `examples/slim/cli-research-worker` (the
playbook-driven worker's end-to-end contract).
