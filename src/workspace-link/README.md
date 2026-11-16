# @ofac/workspace-link

The Workspace Link SDK: the client half of the frozen Link contract
(`protocol/openapi/link-v1.yaml` 2.0.0, `protocol/json-schema/control.v1.json`,
`protocol/cloudevents-schemas/link-delivery.v1.json`). Stack-agnostic core +
Node layer (SignalR reference transport, HTTP-poll fallback, SQLite replay
ledger) + the `ofac-linkd` headless supervisor.

**The credential is a key, not a secret you carry.** On first boot the client
creates an Ed25519 key pair in its state dir, registers the public half with
`POST /enroll` (the body's `token` is a first-boot enrollment CODE — it
authorizes the join and retires; the response hands back a key fingerprint and
a replay fence, nothing to store), and from then on authenticates by proving
possession of that key against a kernel nonce. What rides
`Authorization: Bearer` is a 5-minute kernel-signed session JWT the client
mints for itself (`SessionProvider`: challenge → proof → session, re-proved on
`401`, never a re-enroll). There is no long-lived link token anywhere.

## Entry points

|Specifier|Contents|
|---|---|
|`@ofac/workspace-link`|Core: `createLinkClient`, `enroll`/`EnrollmentError`, `loadOrCreateIdentity` (the keystore), `buildProof`/`signProofForGateway`, `SessionProvider`, `ProtocolMismatchError`, handler + transport interfaces, `FaultInjector`, loggers|
|`@ofac/workspace-link/node`|Node layer: `SignalrTransport`, `HttpPollTransport`, `SqliteLedger`, `postCapabilities`/`digestOfManifest`|
|`ofac-linkd` (bin)|Headless supervisor: key → enroll (first boot) → prove → capabilities → connect → serve, ledger replay|

**The keystore law:** `stateDir` IS the logical instance. `identity.key` is a
0600 JSON file (plaintext, no sealing tiers), there is no backup and no
migration tool — moving an endpoint is `mv`, losing the key means rejoining
(re-enroll the new key, or `POST /enroll/{endpointId}/rotate` while the old one
still speaks). A corrupt key file throws; it never silently re-enrolls.

## Quickstart (linkd)

```bash
# 1. first boot: mint the identity, enroll it with the one-time code, prove it.
# --team is a kernel roster slug (config Link:Teams); --role defaults to child.
node dist/bin/linkd.js run \
  --url http://localhost:8080 --secret enroll-dev \
  --endpoint worker-1 --team team-alpha --manifest manifest.json \
  --state-dir ./ofac-link-state --mode stub --transport http-poll

# 2. every later boot: the code is spent, the key in --state-dir is the
# identity, so no credential at all is needed.
node dist/bin/linkd.js run \
  --url http://localhost:8080 \
  --endpoint worker-1 --team team-alpha --manifest manifest.json \
  --state-dir ./ofac-link-state --mode stub --transport http-poll
```

`OFAC_ENROLL_SECRET` replaces `--secret` and is consulted FIRST — argv is
world-readable via `ps`, the environment is not. Default state dir:
`./ofac-link-state` (the sqlite ledger lives inside it).

`--mode echo` answers every command with an `accepted` receipt echoing the
envelope; `--mode stub` logs and accepts. SIGINT sends a goodbye and exits 0.
Exit codes: `0` clean · `2` config error · `3` protocol mismatch (terminal) ·
`4` enrollment refused.

## SDK usage

```ts
import {
  createLinkClient, loadOrCreateIdentity, SessionProvider, type LinkHandler,
} from "@ofac/workspace-link";
import { SignalrTransport, SqliteLedger, postCapabilities } from "@ofac/workspace-link/node";

const identity = loadOrCreateIdentity("./state");            // the whole credential
const token = new SessionProvider({ baseUrl, endpointId: "worker-1", identity });
const { digest } = await postCapabilities(manifest, { baseUrl, getToken: () => token.getToken() });

const client = createLinkClient({
  transport: new SignalrTransport({ baseUrl, token }),
  ledger: new SqliteLedger("./state/ledger.sqlite3"),
  endpointId: "worker-1",
  capabilityManifestDigest: digest,
  token,
  handler: myHandler, // LinkHandler
});
await client.start(); // handshaking → ready (hello carries the proof)
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

`HttpPollTransport` gets no WelcomeFrame from the server (a challenge/proof
session is the whole bootstrap, and every deliveries GET implicitly refreshes
the lease). The transport therefore keeps a hidden `LinkTransport` "keeper"
(SignalR hello — whose frame carries the proof) to hold the `link_sessions` row
alive; a
`404 session-not-found` re-runs keeper connect + hello before the next
poll. The keeper socket is a tracked hub connection, so the server may
push `Deliver` to it instead of leaving the row for the poll; the
transport forwards keeper frames into the same pipeline as poll results
and the ledger dedupes either arrival. linkd wires this automatically for
`--transport http-poll`.

## Errors

|Class|When|Retry|
|---|---|---|
|`EnrollmentError`|enroll non-2xx (spent/invalid code, missing `publicKey`, bad request)|no (4xx) / per backoff (429, 5xx)|
|`ProtocolMismatchError`|hello intersection empty / server goodbye `protocol-mismatch`|no — terminal, exit 3|
|`LinkError("schema-violation")`|outbound frame fails the frozen schema|no — caller bug|
|`LinkError("unauthorized")`|session expired, or the key was revoked/rotated mid-session|`token.refresh()` — re-prove (challenge+proof); only a dead key means re-enroll|

Reconnect follows the locked backoff (expjitter, base 1 s, cap 60 s, max
attempts configurable via `maxReconnectAttempts`).
