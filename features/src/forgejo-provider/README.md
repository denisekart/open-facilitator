# Dev Container Feature: ofac-forgejo-provider

Installs the **forgejo ops connector** (`ofac-forgejo-connector`) — the
team-side child that executes privileged forge writes (PR create/comment,
branch push, CI trigger) for assignments the team Gateway routes — as a
self-contained Node bundle plus supervisor unit `40-forgejo-connector.sh`.
The connector is a Gateway *child*: it attaches over the child transport
(unix socket or 127.0.0.1 TCP) and registers the flag-filtered `forgejo.*`
capability manifest.

## Prerequisites

- Node **>= 22** on PATH (hard-fails otherwise).
- A Gateway in the same container or reachable over TCP — typically the
  `ofac-gateway` Feature sharing the supervisor + child socket.

## Opt-in by existence

Without `FORGEJO_URL` in the container environment the unit **exits 0
immediately**: installing this Feature in a chat-only workspace changes
nothing observable (the supervisor runs one unit fewer). Everything else is
opt-in by config presence.

## Options (startup-time defaults only)

|Option|Default|Meaning|
|---|---|---|
|`configPath`|`.facilitator/forgejo-connector.yaml`|Connector config the unit starts with; `FORGEJO_CONNECTOR_CONFIG` env overrides|

## Socket coupling (the common mistake)

Same law as ofac-harness-adapter: the connector's `transport.socket.path`
must point at the Gateway's `<stateDir>/child.sock`, and relative paths
resolve against the CONFIG file's directory — with a Gateway `stateDir`
outside the config dir, use the absolute socket path.

## Runtime env surface (1:1 with the binary)

- `FORGEJO_CONNECTOR_CONFIG` — config path (beats the baked `configPath`).
- `FORGEJO_URL` — the forge the ops execute against; its absence disables the unit.
- `FORGEJO_TOKEN` / `FORGEJO_TOKEN_FILE` — bot credential (exactly one required).
- `FORGEJO_ENABLE_ISSUES` / `FORGEJO_ENABLE_VCS` / `FORGEJO_ENABLE_CI` — advertise those op domains (at least one).
- `OFAC_CHILD_SECRET` — beats the config file's `token:`; must equal the Gateway's.
- `OFAC_KERNEL_URL`, `OFAC_ADAPTER_LOG`, `OFAC_LOG_DIR`, `OFAC_SUPERVISOR_MAX_RESTARTS`.

Logs: `$OFAC_LOG_DIR/40-forgejo-connector.sh.log`. Restart law mirrors the
binary: 1 (runtime) restarts with 2 s backoff; 2 (config) and 3 (register
denied, backoff disabled) are terminal.

## State law

The connector's action ledger (`statePath`) is idempotency state for
replayed assignments — keep it on the workspace mount, never in an ephemeral
tmpfs.
