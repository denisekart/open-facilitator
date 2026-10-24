# @ofac/gateway

The team Gateway: a composition over `@ofac/workspace-link` that runs ONE
Link endpoint (`gw.<slug>`) for a team and owns the execution truth between
the Facilitator kernel and an embedded child. Minimal one-worker runtime
stays `ofac-linkd`'s; the Gateway is the multiplexer seam — today one child,
child-per-socket with the same message vocabulary is the next step.

## Quickstart

```yaml
# gateway.yaml
apiVersion: facilitator.framework/v1
kind: Gateway
gatewayId: gw.team-a          # ^gw\.[a-z0-9]([a-z0-9-]*[a-z0-9])?$
team: default                 # must equal the kernel's enrollment team slug
facilitatorUrl: http://localhost:8080
enrollmentSecret: enroll-dev  # or OFAC_ENROLL_SECRET (env wins); optional once <stateDir>/token exists
trustMode: shared-runtime
stateDir: ./gw-state          # link.sqlite3 + gateway.sqlite3 + token, mode 0700
child:
  name: echo                  # single embedded child
  mode: echo                  # echo | stub (or manifestFile: child.json)
diagnostics:
  port: 9701                  # 0 disables
heartbeatSeconds: 30
```

```bash
node dist/bin/gateway.js run --config gateway.yaml
# gateway ready endpointId=gw.team-a epoch=1 child=gw.team-a/echo
```

Exit codes (linkd's law): `0` clean · `2` config error · `3` protocol
mismatch · `4` enrollment refused. `OFAC_GATEWAY_LOG=debug` for verbose logs.

## Lifecycle

enroll (or mount `<stateDir>/token`) → POST the aggregated manifest (the
child's, `ownerTeam` rewritten to `team`; the hello digest covers these exact
bytes) → connect + hello → **reconcile** (every `running` binding row is
queried against the fresh child: `unknown` ⇒ `lost` + ONE retryable
`assignment.failed`, the Gateway as reporting authority) → print the ready
line → serve. Commands route through the deny-by-default policy; the child's
events are forwarded verbatim — the Gateway never fabricates an outcome.
SIGTERM/SIGINT: one `draining` heartbeat, goodbye, ledgers closed, exit 0.

## Diagnostics

`GET http://127.0.0.1:<port>/status` (no auth, dev-tier honesty, mirroring
the kernel's `/api/dev`): `{ gatewayId, state, reconciled, session?,
executions: {running, completed, failed, lost}, bindings: [...],
child, linkStats }`. Bind failure is fatal at startup (exit 1) — a Gateway
that cannot report truth is broken.

## Ledgers (`<stateDir>`, created 0700)

|File|Table|Purpose|
|---|---|---|
|`link.sqlite3`|`ledger` (SDK)|Delivery inbox: dedupe by `delivery_id` OR `dedupe_key` (= `commandId` for commands) — the replay/duplicate law|
|`gateway.sqlite3`|`bindings`|§6.1 tuples: `delivery_id, dedupe_key, gateway_id, child_id, assignment_id, execution_id, work_id, state, connection_generation, error` — `execution_id` is primary identity; `dedupe_key` UNIQUE, so a redelivered command re-answers instead of re-executing|

## Tests

`pnpm test` — unit tier: mock transport + injected fetch + temp ledgers, no
network. `pnpm test:e2e` (`make gateway-e2e`) — live Facilitator per file via
the workspace-link harness: assign→echo-complete→inbox-processed,
SIGKILL reconcile, graceful-restart duplicate, token rotation.
