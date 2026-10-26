# @ofac/gateway

The team Gateway: a composition over `@ofac/workspace-link` that runs ONE
Link endpoint (`gw.<slug>`) for a team and owns the execution truth between
the Facilitator kernel and its children. Three modes: `embedded` (05: one
in-process child), `shared` and `standalone` (06: a fleet of child
processes over a unix socket, `connectChild` + NDJSON childwire). The
Gateway is the multiplexer seam: per-child routing, quota, union manifests.

## Quickstart

```yaml
# gateway.yaml
apiVersion: facilitator.framework/v1
kind: Gateway
gatewayId: gw.team-a          # ^gw\.[a-z0-9]([a-z0-9-]*[a-z0-9])?$
team: team-alpha              # kernel roster slug (config Link:Teams); 403 team-unknown otherwise
facilitatorUrl: http://localhost:8080
enrollmentSecret: enroll-dev  # or OFAC_ENROLL_SECRET (env wins); optional once <stateDir>/token exists
role: gateway                 # sent at enrollment (default gateway; gw.* endpoints MUST be gateway)
# workload:                     # mode-(b) enrollment: OIDC client-credentials instead of the secret
#   tokenUrl: https://idp/realms/facilitator/protocol/openid-connect/token
#   clientId: gw-team-a           # kernel must pre-register this client's issuer|subject
#   clientSecret: s3cret        # OFAC_OIDC_TOKEN_URL/CLIENT_ID/CLIENT_SECRET env wins per-key
trustMode: shared-runtime
stateDir: ./gw-state          # link.sqlite3 + gateway.sqlite3 + token, mode 0700
mode: embedded                # embedded | shared | standalone (default: embedded)
child:
  name: echo                  # embedded only: single in-process child
  mode: echo                  # echo | stub (or manifestFile: child.json)
diagnostics:
  port: 9701                  # 0 disables
heartbeatSeconds: 30
```

Fleet modes replace `child:` with a `children:` fleet list + a
`childTransport:` block (socket path resolves against `stateDir`):

```yaml
mode: shared
children:
  - name: builder             # childWorkspaceId = gw.team-a/builder
    token: s3cret             # per-child auth; fallback: OFAC_CHILD_SECRET
    maxInFlight: 1            # quota: over-quota assigns answer child-busy
    timeoutSeconds: 120
childTransport:
  socket:
    path: child.sock          # primary; allowedUid optional
  tcp: { port: 8899 }         # optional localhost fallback, same protocol
  handshakeTimeoutSeconds: 5  # hello deadline
  leaseMissedBeats: 2         # lease: N missed heartbeats ⇒ offline
```

```bash
node dist/bin/gateway.js run --config gateway.yaml
# gateway ready endpointId=gw.team-a epoch=1 children=2
```

Exit codes (linkd's law): `0` clean · `2` config error · `3` protocol
mismatch · `4` enrollment refused. `OFAC_GATEWAY_LOG=debug` for verbose logs.

## Lifecycle

enroll (or mount `<stateDir>/token`) → embedded: POST the child's manifest
(ownerTeam rewritten to `team`, hello digest over these exact bytes) —
fleet modes POST NOTHING at boot → connect + hello → **reconcile** (every
`running` binding row is queried against its child: offline or `unknown`
⇒ `lost` + ONE retryable `assignment.failed`; rows of children that
reconnect later are probed on registration) → print the ready line →
serve. Fleet: each child registers over the socket (token, namespace,
manifest-conflict, duplicate-id, too-many-children admission law); child
churn debounces a UNION re-POST (the kernel holds one manifest per
endpoint). Commands route through the deny-by-default policy to the
advertising child; the child's events are forwarded verbatim — the Gateway
never fabricates an outcome. SIGTERM/SIGINT: one `draining` heartbeat,
goodbye, ledgers closed, exit 0.

## Diagnostics

`GET http://127.0.0.1:<port>/status` (no auth, dev-tier honesty, mirroring
the kernel's `/api/dev`): `{ gatewayId, state, mode, reconciled, session?,
executions: {running, completed, failed, lost}, bindings: [...],
children: [{childWorkspaceId, state, childGeneration, manifestDigest,
inFlight}], linkStats }`. Bind failure is fatal at startup (exit 1) — a
Gateway that cannot report truth is broken.

## Ledgers (`<stateDir>`, created 0700)

|File|Table|Purpose|
|---|---|---|
|`link.sqlite3`|`ledger` (SDK)|Delivery inbox: dedupe by `delivery_id` OR `dedupe_key` (= `commandId` for commands) — the replay/duplicate law|
|`gateway.sqlite3`|`bindings`|§6.1 tuples: `delivery_id, dedupe_key, gateway_id, child_id, assignment_id, execution_id, work_id, state, connection_generation, child_generation, error` — `execution_id` is primary identity; `dedupe_key` UNIQUE, so a redelivered command re-answers instead of re-executing|
|`gateway.sqlite3`|`child_state`|Fleet durability: per-child last manifest + digest, online/offline, generation fence — survives restarts so /status + reconcile stay honest before any child reconnects|

## Tests

`pnpm test` — unit tier: mock transport + injected fetch + temp ledgers +
real unix-socket listener/child pairs, no kernel network. `pnpm test:e2e`
(`make gateway-e2e`) — live Facilitator per file via the workspace-link
harness: assign→echo-complete→inbox-processed, SIGKILL reconcile,
graceful-restart duplicate, token rotation; 06: two-process fleet assign +
union POST, child SIGKILL → probe-lost + survivor keeps flowing,
standalone zero-child boot + late child.
