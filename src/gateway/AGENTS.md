# Gateway (`@ofac/gateway`)

Team-owned composition over `@ofac/workspace-link`: one Link endpoint
(`gw.<slug>`) fanning out to children — embedded (05, one in-process),
shared/standalone (06, a process fleet over a unix socket, NDJSON
childwire).

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
  would make reconcile's freshness law (fence below) lie.
- **Reconcile completes before the ready line**; redeliveries can't race it
  because the SDK ledger answers `duplicate` without touching bindings.
- **Exit codes are linkd's law:** 0 clean · 2 config · 3 protocol mismatch ·
  4 enrollment refused. Diagnostics bind failure is fatal (exit 1): a
  Gateway that can't report truth is broken.
- **Admission law (listen.ts):** token (per-child entry, else
  `OFAC_CHILD_SECRET`, else open + one warn) → `<gatewayId>/<name>`
  namespace → `too-many-children` (32) → manifest schema → first-wins
  `duplicate-id` (a LIVE holder is never displaced) → deny-ambiguity
  `manifest-conflict` (same (id,version), different JSON). Lease:
  `leaseMissedBeats` missed heartbeats kill the socket; close ⇒ offline,
  route survives so future assigns answer `child-disconnected` honestly.
- **Union law:** the kernel holds ONE manifest per endpoint, so child churn
  debounces a re-POST of the fleet union (ownerTeam = config team;
  byte-identical duplicate capabilities dedupe). Fleet modes POST NOTHING
  at boot — hello carries the empty-union placeholder digest.
- **Probe law:** a RE-registered child with durable `running` rows is
  asked `status` per row; `unknown` ⇒ lost + ONE retryable
  `assignment.failed(child-lost-execution)` — never re-executed.

## Anchors

- Startup order + token lifecycle: `src/runtime.ts` (`start`, `acquireToken`)
- Binding table + dedupe keys + `child_state`: `src/ledger.ts`
- Wire codec (NDJSON frames, size cap): `src/childwire.ts`
- Listener (admission + lease): `src/listen.ts`; child-side client:
  `src/connect.ts`; fleet routing/quota/union: `src/child-link.ts`
- Policy seam (deny-by-default): `src/policy.ts`
- Child seam (socket-compatible messages): `src/child.ts`
- Tests: `test/*/` unit tier (mock transport, socket pairs), `test/e2e/`
  (live kernel + real child processes via `child-runner.mjs`)
