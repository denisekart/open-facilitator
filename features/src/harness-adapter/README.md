# Dev Container Feature: ofac-harness-adapter

Installs a **harness worker** — today the playbook-driven CLI adapter
(`ofac-cli-worker`) — as a self-contained Node bundle plus a
supervisor unit; read this when a container runs a Gateway child. The worker
attaches over the child transport (unix socket or 127.0.0.1 TCP), registers
its capability manifest, and executes assignments the Gateway routes to it.

## Quickest path

1. Base image with Node **>= 22** on PATH (hard-fails otherwise; a
   Node-shipping base image or the official
   `ghcr.io/devcontainers/features/node` Feature satisfies this).
2. `adapter: cli` — the only adapter binary that exists; any other
   option value fails the install with exit 1.
3. A Gateway's child socket reachable at the config's transport path, and
   `OFAC_CHILD_SECRET` equal to the Gateway's.
4. Start the container: `grep 'worker ready' "$OFAC_LOG_DIR/30-harness-adapter.sh.log"`
   shows `worker ready childId=<gatewayId>/<child> socket=...`.

## Options (startup-time defaults only)

|Option|Default|Meaning|
|---|---|---|
|`adapter`|`cli`|Which adapter binary to install (only value accepted)|
|`configPath`|`.facilitator/harness.yaml`|Worker config the unit starts with; `WORKER_CONFIG` env overrides|

Options bake the unit script's defaults; the runtime env surface is the truth.
Secrets are **never** options.

## Socket coupling (the common mistake)

When the gateway and harness-adapter Features share a container, the
worker's `transport.socket.path` must point at the Gateway's
`<stateDir>/child.sock`. The worker resolves **relative paths against its
config file's directory**, the Gateway resolves its socket against
`stateDir` — so with configs in `.facilitator/` and `stateDir:
/workspace/.ofac-gateway`, use the **absolute** path
`/workspace/.ofac-gateway/child.sock` in the worker config (see
`examples/slim/cli-research-worker/.facilitator/harness.yaml`).

## Runtime env surface (1:1 with the binary)

|Variable|Meaning|
|---|---|
|`WORKER_CONFIG`|config path (beats the baked `configPath`)|
|`OFAC_CHILD_SECRET`|beats the config file's `token:`; must equal the Gateway's `OFAC_CHILD_SECRET`|
|`OFAC_ADAPTER_LOG`|log level — one of `debug`, `info`, `warn`, `error`|
|`OFAC_LOG_DIR` / `OFAC_SUPERVISOR_MAX_RESTARTS`|supervisor surface|

Logs: `$OFAC_LOG_DIR/30-harness-adapter.sh.log`. Ready signal:
`worker ready childId=<gatewayId>/<child> socket=...`.

Restart law mirrors the binary: 1 (runtime — e.g. Gateway down, socket not
yet there) restarts with 2 s backoff; 2 (config) and 3 (register denied with
backoff disabled) are terminal.

## State law

The worker writes artifacts under its config's `workDir` — keep it on the
workspace mount (e.g. `/workspace/.ofac-worker`).
