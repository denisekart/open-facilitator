# src/adapters

## Purpose
Home for communication adapters (Mattermost, Forgejo, Kafka) and harness
adapters. Communication subdirectories are spec-derived stubs — a README
stating their future role. Harnesses have shipped: `harnesses/adapter-sdk`
(the HarnessAdapter lifecycle library) and `harnesses/deterministic` (the
worked-example worker).

## Truth anchors
- `../../docs/specs/open_facilitator_framework_architecture.md` §13 —
  communication adapter requirements each directory implements.
- `harnesses/adapter-sdk/README.md` — write-your-own-adapter walkthrough.
- `../../protocol/json-schema/adapter.v1.json` — the normative child wire.

## Develop here
- One directory per adapter; a new harness copies `harnesses/deterministic`
  and runs the conformance kit (`@ofac/adapter/testing`).

## Invariants
- Adapters translate between an external system and the frozen wire contract
  (`../../protocol/`); they never invent message types. Harnesses import
  frame/payload types from the shipped contract, never redefine them.
- Credentials stay team-side: an adapter holds outbound connections, not
  execution authority. The harness — not the adapter, never the Gateway —
  publishes `assignment.completed.v1`.
