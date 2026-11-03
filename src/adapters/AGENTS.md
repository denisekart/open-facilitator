# src/adapters

## Purpose
Adapters grouped by function: `communication/` holds provider adapters
(each speaks the link plane, never the kernel core), `harnesses/`
holds execution-harness adapters. Place a new adapter by what it talks to:
an external system that feeds events in or takes messages out ⇒
`communication/<name>/`; a workspace execution harness (runs assignments) ⇒
`harnesses/<name>/`. Shipped: `mattermost` (facilitator-owned chat leg),
`forgejo` + `forgejo-connector` (team-owned forge: fan-out normalizer +
Gateway-child ops executor), the same pair shape for `github{,-connector}`
and `gitlab{,-connector}` (GitHub unit/e2e-only via an exported fake; GitLab
additionally on-demand via `make gitlab-up` + `reference/checks/verify-gitlab.sh`),
`provider-core` (the shared scaffolding BOTH faces ride — single way to
integrate a provider), `harnesses/adapter-sdk` (the HarnessAdapter lifecycle
library), and `harnesses/deterministic` (the worked-example worker).
`kafka` remains a documented stub — a README stating its future role.

## Truth anchors
- `../../docs/specs/open_facilitator_framework_architecture.md` —
  communication adapter requirements each directory implements.
- `harnesses/adapter-sdk/README.md` — write-your-own-adapter walkthrough.
- `../../protocol/json-schema/adapter.v1.json` — the normative child wire.

## Develop here
- One directory per adapter, under the function group it belongs to.
- New communication adapter: one directory pair per provider under
  `communication/` consuming `@ofac/provider-core` (adapter face
  `runProviderAdapter` + the webhook-receiver seam; connector face config-law
  /action-ledger/hooks/ops — provider-local code stays REST client, signature
  scheme, vendor→domain mapping, init seeding; pattern:
  `communication/forgejo{,-connector}`); register it in `pnpm-workspace.yaml`
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
