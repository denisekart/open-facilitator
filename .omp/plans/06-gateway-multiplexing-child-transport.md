# Plan 06 — Gateway multiplexing: child transport, shared + standalone modes

**Status:** ⬜ not-started — implementation begins ONLY after its replanning gate flips this to 🔵.
**Phase:** B. **Depends on:** 05 (Gateway runtime core 🟢).
**Completes:** roadmap entry 05's full scope — this plan is the multiplexing + multi-mode half, split out to keep 05 a single-child slice.

## Normalization from prior plans (assumed state)

Plan 05's Gateway serves one embedded child through a serializable `ChildExecutor` message interface and owns the execution ledger + binding tuple + policy port. This plan assumes that interface is the exact message vocabulary the socket transport will carry — replanning verifies the 05 detail design landed socket-compatible (if 05 chose a non-serializable seam, widening it is this plan's first step). Assumes SDK `linkd` exists as the counter-example runtime (one process = one worker, no Gateway) and the `gateway-e2e` CI tier exists.

## Objective

Make the Gateway a true team-side multiplexer: child workspaces connect to the Gateway over a local child transport (Unix domain socket primary, localhost HTTP fallback for containers without socket mounts), register capability manifests, and receive routed commands; the Gateway serves N children concurrently, advertises the union capability manifest, enforces per-child quotas (`maxInFlight` fan-out), and runs in all three §9.1 modes from one codebase: `embedded` (05's, kept), `shared` (multiple children in one Gateway process via the socket), `standalone` (Gateway process with zero children at boot, children attach later — the zero-to-N preview for roadmap-era Plan 16).

## Replanning gate (mandatory initial research)

1. Spec §6.1 (binding tuple + routing law), §9.1 (mode semantics incl. what `shared` promises about isolation), §9.3 (Gateway restart law), appendix §4.2 (trustMode per child enrollment record — in `shared` mode children are NOT separately enrolled; the Gateway's enrollment carries the mode).
2. Plan 05 detail design + Implementation Status: the actual `ChildExecutor` message vocabulary, ledger schema, config format, diagnostics surface.
3. Child-transport research: Unix socket framing (length-prefixed JSON vs newline-delimited — pick boring), auth between child and Gateway on a shared host (peer-cred `SO_PEERCRED` vs shared-secret file; decision + rationale at gate), reconnection semantics child↔Gateway (Gateway restart: children reconnect and re-register, reconcile from Gateway ledger; child restart mid-execution: Gateway marks execution `lost` and reports honestly — §6.3).
4. Decide: manifest union + conflict rules (two children claiming the same `capabilityId` → precedence or rejection, deny-ambiguity), per-child capacity config, how a child disconnect mid-assignment behaves observably (execution `lost`, no silent re-route at Gateway level — re-decision is kernel routing's job).

## Scope

- Child transport server in Gateway: register/connect/disconnect lifecycle, heartbeat child→Gateway, framed command/event delivery both ways.
- CLI additions: `ofac-gateway serve` (standalone/shared), child-side connect helpers exported from `@ofac/gateway` (children are plain Node processes; Plan 07's adapter SDK will consume this, not reinvent it).
- Routing: `work.assign` → capability match → child binding; steer/cancel/status/nudge → bound child; unmatched → typed `unsupported` receipt.
- Aggregated capability manifest (union, digests per child recorded in Gateway state) POSTed to Facilitator; re-POST on child register/deregister.
- Ledger extension: `connectionGeneration` bump per child connection; a stale-generation message from a reconnected child cannot mutate a live execution.
- Mode matrix tested from one codebase; `linkd` path untouched.

## State after implementation

One Gateway serves two child workers with different handlers and capability manifests (e2e-proven); a child killed mid-assignment leaves the assignment honestly `lost` (visible via diagnostics + kernel dead-letter/timeline), the other child keeps flowing; a Gateway restart reconciles its ledger and reports real status for both children (roadmap 05's "State after", §26.1 item 7 fully landed; slice-3 exit behavior per §6.3).

## Testing

- vitest unit: routing table, union manifest construction + conflict denial, generation fencing, quota enforcement (mock children).
- e2e (extend `gateway-e2e`): two real child processes over the socket, kill/reattach one, Gateway SIGKILL/restart reconcile, zero-children idle heartbeat (standalone boot).
- Shared fixtures: no new wire types expected; if child-transport frames need schemas, they are Gateway-internal (NOT protocol/) — keep them out of `protocol/` unless replanning finds a cross-implementation need (then propose a contract amendment, §5.5 discipline).

## Documentation

- `docs/advanced.md`: Gateway modes completed (all three as-built), child-transport protocol reference, restart/reconcile semantics.
- `src/gateway/README.md`: multi-child config sample, child connect example.
- `.omp/skills/facilitator-workspace-packaging`: modes → implemented reality; child transport spec pointer.

## Architecture fit

§6.1–6.3 in full: the Facilitator sees one endpoint; the Gateway owns fan-out, per-child bindings, and reconcile. This is what makes the "team-owned execution authority" (§2.1) concrete: children can come and go, the Gateway's ledger is the execution truth the kernel reconciles against. Prerequisite for Plan 07 (adapters are the first real children) and the packaging story (Plan 08 Features run exactly these processes).

## Additional resources

- Spec §6.1–6.3, §9; appendix §4, §5.4–5.5; `.omp/skills/facilitator-workspace-packaging`.
- As-built: Plan 05 code (`src/gateway/`), SDK transport seam patterns (`src/workspace-link/src/node/`), `link-e2e`/`gateway-e2e` CI jobs.
- External: Node `net`/Unix socket + `SO_PEERCRED` docs (`node:net` `getSndBufSize`… concretely the `node-ipc`/`abstract-socket` prior-art discussion; decide boring).
