# features/

## Purpose
Dev Container Feature packages (gateway, workspace-link, harness-adapter) that
inject a Gateway or Workspace Link runtime into any team-owned container. The
three directories are spec-derived stubs today: each carries only a README; no
`feature.json` has shipped yet.

## Truth anchors
- Dev Container Features spec (containers.dev/features) — the format contract.
- `../.omp/skills/facilitator-workspace-packaging/SKILL.md` — the project's
  Feature layout, option naming, and provisioning rules.
- `../docs/specs/open_facilitator_framework_architecture.md` §8 — packaging
  requirements this area implements.
- `../src/workspace-link` and `../src/gateway` — the runtimes a Feature installs.

## Develop here
- One directory per Feature: `feature.json` + install scripts, published as an
  OCI image built from the repo root (`Dockerfile`s live beside the runtimes).
- Options must mirror the runtime's env-var surface 1:1; the runtime env vars
  are the truth, the Feature only projects them.

## Invariants
- Features never own state; durable state stays in the workspace volume or the
  Facilitator.
- A Feature may pin runtime versions only via `reference/versions.env`.
