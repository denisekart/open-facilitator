# src/adapters

## Purpose
Home for communication adapters (Mattermost, Forgejo, Kafka) and harness
adapters. Every subdirectory is a spec-derived stub today — a README stating
its future role; no code has shipped.

## Truth anchors
- `../../docs/specs/open_facilitator_framework_architecture.md` §13 —
  communication adapter requirements each directory implements.
- `../../.omp/skills/facilitator-workspace-packaging/SKILL.md` —
  HarnessAdapter contract for `harnesses/`.

## Develop here
- One directory per adapter; the first real implementation replaces the stub
  README with a buildable package and updates this area's docs in the same
  change.

## Invariants
- Adapters translate between an external system and the frozen wire contract
  (`../../protocol/`); they never invent message types.
- Credentials stay team-side: an adapter holds outbound connections, not
  execution authority.
