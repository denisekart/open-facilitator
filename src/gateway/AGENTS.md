# Gateway (`@ofac/gateway`)

Team-owned composition over `@ofac/workspace-link`: one Link endpoint
(`gw.<slug>`), one embedded child, the execution truth between kernel and
child.

## Invariants invisible in code

- **Digest law:** the hello `capabilityManifestDigest` is the digest over the
  EXACT bytes POSTed to `/capabilities` (`aggregatedManifest` = child
  manifest, `ownerTeam` rewritten to the config team — the kernel 403s any
  other team).
- **The Gateway never fabricates outcomes.** Children publish their own
  terminal events; the Gateway forwards bytes unmodified and settles
  bindings from what the child itself said. The one event the Gateway
  authors is the reconcile `assignment.failed` (`retryable: true`, the
  generation the execution ran under) — it is the reporting authority there
  precisely because the child is gone.
- **Fencing before everything:** any row whose `connectionGeneration` !=
  the live `leaseEpoch` is stale: no settle, no forward (`fence()`).
- **Stamp law:** a binding's `connectionGeneration` is the accepting session's
  leaseEpoch — `onAssign` awaits the first welcome (`awaitSessionEpoch()`)
  because a delivery frame can beat the handshake continuation; an epoch-0 row
  would make reconcile's freshness law (§6.3, fence below) lie.
- **Reconcile completes before the ready line**; redeliveries can't race it
  because the SDK ledger answers `duplicate` without touching bindings.
- **Exit codes are linkd's law:** 0 clean · 2 config · 3 protocol mismatch ·
  4 enrollment refused. Diagnostics bind failure is fatal (exit 1): a
  Gateway that can't report truth is broken.

## Anchors

- Startup order + token lifecycle: `src/runtime.ts` (`start`, `acquireToken`)
- Binding table + dedupe keys: `src/ledger.ts` (`executions`, `bindings`)
- Policy seam (deny-by-default): `src/policy.ts`
- Child seam (socket-compatible messages; cross-process transport is the
  next step): `src/child.ts`
- Tests: `test/*/` unit tier (mock transport), `test/e2e/` (live kernel,
  reuses the workspace-link harness)
