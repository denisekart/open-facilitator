# Plan 04 — Workspace Link SDK and runtime (TypeScript)

**Status:** ⬜ not-started
**Depends on:** 03

Status legend: ⬜ `not-started` → 🔵 `replanning` (mandatory gate) → 🟡 `in-progress` → 🟢 `done`. This plan starts with a replanning pass (flip to 🔵): re-verify assumptions against the specs and prior plans' state, run the research below, finalize this file's detail design, then flip to 🟡.

## Objective

Ship `@ofac/workspace-link`: the official TypeScript Link client — SignalR primary with HTTP long-poll fallback, token-factory auth, receipt ledger, envelope helpers — plus a small runtime binary (`linkd`) that connects a workspace outbound, registers a capability manifest, executes injected command handlers, and survives disconnect/reconnect with replay.

## Replanning gate

- Pin `@microsoft/signalr` version + fallback polling loop against Plan 03's actual endpoints.
- Decide SDK vs runtime package split (recommend: one package, two entry points).
- Confirm `@ofac/protocol` generated types are consumable by the client without re-mapping.

## Scope

- Client state machine (connecting→hello→welcome→ready, heartbeat lease refresh, epoch bump per reconnect).
- Durable local delivery ledger (SQLite via `better-sqlite3`) so a crashed process reconciles receipts on restart.
- Handler interface `onCommand(cmd) => Promise<Accept|Reject>`.
- `flow_control` honoring maxInFlight.
- Test mode simulating dropped receipts/partitions (appendix §12.3).

## State after

`linkd` pointed at a dev Facilitator receives a real `work.assign`, invokes a JS handler, and after a kill/restart replays nothing already receipted and redelivers nothing un-acked wrongly.

## Testing

vitest against a real Facilitator (dockerized via make target): §26.1 items 2–3 client side, plus partition simulation (dropped receipt, duplicated delivery, stale epoch ignored). Shared conformance fixtures run in TS via `ajv`.

## Documentation

`docs/protocol.md` client section; package README with quickstart.

## Architecture fit

§5, §6.3 (reconnect/reconcile), appendix §10.2 (SDK scope). First half of the team-owned side of the wire.

## Additional resources

Spec §5.3/§6.3, appendix §10.2/§12.3; `@microsoft/signalr` docs (accessTokenFactory, reconnect policies); SQLite ledger prior art: outbox pattern in worker processes.
