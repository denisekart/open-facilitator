# @ofac/workspace-link

The Workspace Link SDK: the client half of the frozen Link contract
(`protocol/openapi/link-v1.yaml` 1.0.1, `protocol/json-schema/control.v1.json`,
`protocol/cloudevents-schemas/link-delivery.v1.json`). Stack-agnostic core +
Node layer (SignalR reference transport, HTTP-poll fallback, SQLite replay
ledger) + the `ofac-linkd` headless supervisor.

## Entry points

|Specifier|Contents|
|---|---|
|`@ofac/workspace-link`|Core: `createLinkClient`, `enroll`/`EnrollmentError`, `ProtocolMismatchError`, handler + transport interfaces, `FaultInjector`, loggers|
|`@ofac/workspace-link/node`|Node layer: `SignalrTransport`, `HttpPollTransport`, `SqliteLedger`, `postCapabilities`/`digestOfManifest`|
|`ofac-linkd` (bin)|Headless supervisor: enroll → capabilities → connect → serve, state file replay|

## Quickstart (linkd)

```bash
# 1. enroll (bootstrap secret, NOT a bearer token) — prints the minted token
node dist/bin/linkd.js run \
  --url http://localhost:8080 --secret enroll-dev \
  --endpoint worker-1 --team default --manifest manifest.json

# 2. restart with the minted token (no re-enroll)
OFAC_LINK_TOKEN=... node dist/bin/linkd.js run \
  --url http://localhost:8080 --token "$OFAC_LINK_TOKEN" \
  --endpoint worker-1 --team default --manifest manifest.json \
  --state ./linkd.sqlite --mode stub --transport http-poll
```

`OFAC_ENROLL_SECRET` replaces `--secret` and is consulted FIRST — argv is
world-readable via `ps`, the environment is not.

`--mode echo` answers every command with an `accepted` receipt echoing the
envelope; `--mode stub` logs and accepts. SIGINT sends a goodbye and exits 0.
Exit codes: `0` clean · `2` config error · `3` protocol mismatch (terminal) ·
`4` enrollment rejected.

## SDK usage

```ts
import { createLinkClient, type LinkHandler } from "@ofac/workspace-link";
import { SignalrTransport, SqliteLedger, postCapabilities } from "@ofac/workspace-link/node";

const token = { getToken: async () => process.env.OFAC_LINK_TOKEN! };
const { digest } = await postCapabilities(manifest, { baseUrl, getToken: token.getToken });

const client = createLinkClient({
  transport: new SignalrTransport({ baseUrl, token }),
  ledger: new SqliteLedger("./state.sqlite"),
  endpointId: "worker-1",
  capabilityManifestDigest: digest,
  token,
  handler: myHandler, // LinkHandler
});
await client.start(); // enroll-state → handshaking → ready
```

`LinkHandler.onCommand(cmd, ctx)` returns a `CommandOutcome`
(`accepted` / `rejected` / `unsupported`); the client turns it into the
receipt. `ctx.publishEvent(env)` sends an outbound CloudEvent; on a dead
transport the envelope is buffered durably in the ledger outbox and the
call still resolves `accepted` — the buffer drains on the next `ready`.

### Delivery pipeline law

A delivery is persisted to the ledger **before** the `accepted` receipt
leaves the process, and receipt send failures NEVER suppress the result —
a lost receipt converges via the server's in-flight-lease redelivery, and
the ledger answers the re-push with `duplicate` without re-running the
handler. Deleting the state file forfeits exactly that replay guarantee.

### Test mode

`FaultInjector` (core export) wraps any `LinkTransport` and a `Ledger` to
script faults: drop the next receipt, cut the socket after N deliveries,
fail reconnects. The E2E tier (`test/e2e/`, env-gated) uses it for the
receipt-loss and partition scenarios.

### HTTP-poll session keeper (as-built asymmetry)

`HttpPollTransport` gets no WelcomeFrame from the server (enroll + bearer
token is the whole bootstrap, and every deliveries GET implicitly refreshes
the lease). The transport therefore keeps a hidden `LinkTransport` "keeper"
(SignalR hello) to hold the `link_sessions` row alive; a
`404 session-not-found` re-runs keeper connect + hello before the next
poll. The keeper socket is a tracked hub connection, so the server may
push `Deliver` to it instead of leaving the row for the poll; the
transport forwards keeper frames into the same pipeline as poll results
and the ledger dedupes either arrival. linkd wires this automatically for
`--transport http-poll`.

## Errors

|Class|When|Retry|
|---|---|---|
|`EnrollmentError`|enroll non-2xx (bad secret, bad request)|no (4xx) / per backoff (429, 5xx)|
|`ProtocolMismatchError`|hello intersection empty / server goodbye `protocol-mismatch`|no — terminal, exit 3|
|`LinkError("schema-violation")`|outbound frame fails the frozen schema|no — caller bug|
|`LinkError("unauthorized")`|token expired/revoked mid-session|re-enroll, then restart|

Reconnect follows the locked backoff (expjitter, base 1 s, cap 60 s, max
attempts configurable via `maxReconnectAttempts`).
