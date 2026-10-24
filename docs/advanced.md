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

The seam's message vocabulary is wire-shaped on purpose: a cross-process
child transport (unix socket / localhost), N children per gateway behind a
union capability manifest, and the standalone deployment mode reuse it
unchanged — the Gateway's routing, policy, ledger, and fencing laws do not
move when the child hops the socket.

### trustMode

Every enrollment declares one of three modes (immutable for a session); the
Gateway config's `trustMode` is sent verbatim in the enroll request and
recorded server-side:

|Mode|Guarantee|Operator guidance|
|---|---|---|
|`shared-runtime`|Gateway and workspace share OS resources; no isolation|trusted single-team devcontainers|
|`separate-process-same-host`|separate processes, OS isolation may be limited|semi-trusted workloads|
|`isolated-executor`|strong isolation (VM / strict-seccomp container)|untrusted third-party or multi-tenant code|

The embedded-child Gateway honestly declares `shared-runtime`: the child
shares the Gateway's process and secrets.

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
|`child.name` `child.mode` `child.manifestFile`|single embedded child: `echo` \| `stub`, or an advertised manifest file|
|`diagnostics.port`|localhost `/status` endpoint; `0` disables|
|`heartbeatSeconds`|1..3600, default 30|
