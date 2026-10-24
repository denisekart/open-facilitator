# Advanced Topics

## Gateway modes

`@ofac/gateway` is the team-owned composition over the Workspace Link SDK
(`@ofac/workspace-link`): one Link endpoint per team gateway
(`gatewayId: gw.<slug>`), the execution ledger between kernel and children.

### As-built: embedded child

One process, one child: the Gateway enrolls (or mounts a stored token),
POSTs the child's capability manifest rewritten to the team's ownership,
connects, reconciles any `running` executions against the fresh child, and
serves command deliveries. The child sits behind the `ChildExecutor` seam —
serializable messages (`register`/`start`/`steer`/`status`/`stop`) with
JSON replies, in-process today. Modes `echo` and `stub` are the builtin
child manifests; `child.manifestFile` advertises any other capability set
while the builtin handlers answer.

### As-built: shared + standalone fleets

`mode: shared` and `mode: standalone` replace the single `child:` with a
`children:` list and a `childTransport:` block. Children are separate
processes attaching over a unix socket (optional localhost TCP fallback,
same protocol): NDJSON frames (`hello`/`register-ack`/`command`/`reply`/
`event`/`heartbeat`/`goaway`), per-child tokens, a heartbeat lease
(`leaseMissedBeats` missed beats kill the socket), and an admission law —
namespace, frame-size, `too-many-children` (32), manifest-schema,
first-wins `duplicate-id`, deny-ambiguity `manifest-conflict`. The kernel
sees ONE endpoint: child churn debounces a re-POST of the fleet UNION
manifest (ownerTeam = the gateway's team); nothing POSTs at boot, so a
standalone gateway is legal with zero children. Commands route to the
advertising child under its `maxInFlight` quota (over-quota answers
`child-busy`); an offline advertiser answers `child-disconnected`. A
re-registering child is probed with `status` for every durable `running`
row it owns: `unknown` means the child died mid-flight — the row goes
`lost` with ONE retryable `assignment.failed(child-lost-execution)`,
never re-executed. `standalone` is the same machinery for a
workspace-isolated deployment where the gateway owns no embedded work.

### trustMode

Every enrollment declares one of three modes (immutable for a session); the
Gateway config's `trustMode` is sent verbatim in the enroll request and
recorded server-side:

|Mode|Guarantee|Operator guidance|
|---|---|---|
|`shared-runtime`|Gateway and workspace share OS resources; no isolation|trusted single-team devcontainers|
|`separate-process-same-host`|separate processes, OS isolation may be limited|semi-trusted workloads|
|`isolated-executor`|strong isolation (VM / strict-seccomp container)|untrusted third-party or multi-tenant code|

`shared-runtime` is the honest declaration for embedded and shared fleets
(children share the host, no isolation). The validator rejects the one
dishonest combination — `embedded` cannot claim `isolated-executor`, since
an in-process child has no isolation boundary to claim.

### Configuration keys

The locked YAML surface (`loadConfigFile` rejects any other key, naming it):

|Key|Notes|
|---|---|
|`apiVersion`|`facilitator.framework/v1`|
|`kind`|`Gateway`|
|`gatewayId`|`^gw\.[a-z0-9]([a-z0-9-]*[a-z0-9])?$`, ≤128 chars — the Link endpoint id|
|`team`|must equal the kernel's enrollment team slug (else capabilities POST 403s)|
|`facilitatorUrl`|kernel base URL|
|`enrollmentSecret`|bootstrap secret; `OFAC_ENROLL_SECRET` env wins; optional once `<stateDir>/token` exists|
|`linkToken`|static mount; `OFAC_LINK_TOKEN` env wins|
|`trustMode`|see table above|
|`stateDir`|created 0700; holds `link.sqlite3`, `gateway.sqlite3`, `token`|
|`mode`|`embedded` \| `shared` \| `standalone`; default `embedded`|
|`child.name` `child.mode` `child.manifestFile`|embedded only: single in-process child: `echo` \| `stub`, or an advertised manifest file|
|`children[]`|fleet only: `name` (`^[a-z0-9]([a-z0-9-]*[a-z0-9])?$`), optional `token`, `maxInFlight` (≥1), `timeoutSeconds` (1..86400, default 120)|
|`childTransport.socket.path`|fleet only; resolved against `stateDir`; optional `allowedUid`|
|`childTransport.tcp.port`|optional localhost fallback, same wire protocol|
|`childTransport.handshakeTimeoutSeconds` `childTransport.leaseMissedBeats`|hello deadline; lease (default 5 / 2)|
|`diagnostics.port`|localhost `/status` endpoint; `0` disables|
|`heartbeatSeconds`|1..3600, default 30|
