# Dev Container Feature: ofac-harness-adapter

Installs a **harness worker** — today the deterministic reference adapter
(`ofac-deterministic-worker`) — as a self-contained Node bundle plus a
supervisor unit. The worker is a Gateway *child*: it attaches over the child
transport (unix socket or 127.0.0.1 TCP), registers its capability manifest,
and executes assignments the Gateway routes to it.

## Prerequisites

- Node **>= 22** on PATH (hard-fails otherwise; a Node-shipping base image or
  the official `ghcr.io/devcontainers/features/node` Feature satisfies this).
- `adapter: deterministic` — the only adapter binary that exists; any other
  option value fails the install with exit 1.

## Options (startup-time defaults only)

|Option|Default|Meaning|
|---|---|---|
|`adapter`|`deterministic`|Which adapter binary to install (only value accepted)|
|`configPath`|`.facilitator/harness.yaml`|Worker config the unit starts with; `WORKER_CONFIG` env overrides|

## Socket coupling (the common mistake)

When the gateway and harness-adapter Features share a container, the
worker's `transport.socket.path` must point at the Gateway's
`<stateDir>/child.sock`. The worker resolves **relative paths against its
config file's directory**, the Gateway resolves its socket against
`stateDir` — so with configs in `.facilitator/` and `stateDir:
/workspace/.ofac-gateway`, use the **absolute** path
`/workspace/.ofac-gateway/child.sock` in the worker config (see
`examples/slim/deterministic-research-worker/.facilitator/harness.yaml`).

## Runtime env surface (1:1 with the binary)

- `WORKER_CONFIG` — config path (beats the baked `configPath`).
- `OFAC_CHILD_SECRET` — beats the config file's `token:`; must equal the
  Gateway's `OFAC_CHILD_SECRET`.
- `OFAC_ADAPTER_LOG` — log level (debug|info|warn|error).
- `OFAC_LOG_DIR` / `OFAC_SUPERVISOR_MAX_RESTARTS` — supervisor surface.

Logs: `$OFAC_LOG_DIR/30-harness-adapter.sh.log`. Ready signal:
`worker ready childId=<gatewayId>/<child> socket=...`.

Restart law mirrors the binary: 1 (runtime — e.g. Gateway down, socket not
yet there) restarts with 2 s backoff; 2 (config) and 3 (register denied with
backoff disabled) are terminal.

## State law

The worker writes artifacts under its config's `workDir` — keep it on the
workspace mount (e.g. `/workspace/.ofac-worker`).
