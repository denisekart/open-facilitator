# src/adapters

## Purpose
Adapters grouped by function: `communication/` holds provider adapters
(spec §13; each speaks the link plane, never the kernel core), `harnesses/`
holds execution-harness adapters. Place a new adapter by what it talks to:
an external system that feeds events in or takes messages out ⇒
`communication/<name>/`; a workspace execution harness (runs assignments) ⇒
`harnesses/<name>/`. Shipped: `mattermost` (facilitator-owned chat leg),
`forgejo` + `forgejo-connector` (team-owned forge: webhook normalizer +
Gateway-child ops executor), `harnesses/adapter-sdk` (the HarnessAdapter
lifecycle library), and `harnesses/deterministic` (the worked-example
worker). `kafka` is a spec-derived stub — a README stating its future role.

## Truth anchors
- `../../docs/specs/open_facilitator_framework_architecture.md` §13 —
  communication adapter requirements each directory implements.
- `harnesses/adapter-sdk/README.md` — write-your-own-adapter walkthrough.
- `../../protocol/json-schema/adapter.v1.json` — the normative child wire.

## Develop here
- One directory per adapter, under the function group it belongs to.
- New communication adapter: one directory per provider under
  `communication/` (pattern: `communication/mattermost` — link-plane consumer,
  `@ofac/workspace-link` as a library); register it in `pnpm-workspace.yaml`
  and, when it ships in the node image, in `Dockerfile.node`'s deploy list.
- New harness adapter: copy `harnesses/deterministic` and run the conformance
  kit (`@ofac/adapter/testing`).

## Invariants
- Adapters translate between an external system and the frozen wire contract
  (`../../protocol/`); they never invent message types. Harnesses import
  frame/payload types from the shipped contract, never redefine them.
- Credentials stay team-side: an adapter holds outbound connections, not
  execution authority. The harness — not the adapter, never the Gateway —
  publishes `assignment.completed.v1`.
