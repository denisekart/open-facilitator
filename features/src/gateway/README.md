# Dev Container Feature: ofac-gateway

Installs the team-owned **Gateway** (`ofac-gateway`) into any container: a
self-contained Node runtime bundle (no npm registry, no network, no toolchain
at install time), the headless supervisor entrypoint, and a supervisor unit
that runs the Gateway with restart-with-backoff.

## Prerequisites

- Node **>= 22** on PATH (hard-fails otherwise). A Node-shipping base image
  (`mcr.microsoft.com/devcontainers/typescript-node`) or the official
  `ghcr.io/devcontainers/features/node` Feature satisfies this; the Feature
  deliberately does not download a Node runtime itself (spec §8 packaging).

## Options (startup-time defaults only)

|Option|Default|Meaning|
|---|---|---|
|`configPath`|`.facilitator/gateway.yaml`|Config the unit starts with; `GATEWAY_CONFIG` env overrides at runtime|
|`log`|`info`|`debug` exports `OFAC_GATEWAY_LOG=debug` in the unit; env still wins|

Options bake the unit script's defaults; the runtime env/config surface is
the truth. Secrets are **never** options.

## Runtime env surface (1:1 with the binary)

- `GATEWAY_CONFIG` — config path (beats the baked `configPath`).
- `OFAC_ENROLL_SECRET` / `OFAC_LINK_TOKEN` / `OFAC_CHILD_SECRET` —
  credentials; one of them, or an existing `<stateDir>/token` file, is
  required.
- `OFAC_GATEWAY_LOG=debug` — debug logging.
- `OFAC_LOG_DIR` — supervisor log dir (default `/tmp/openfacilitator/logs`).
- `OFAC_SUPERVISOR_MAX_RESTARTS` — per-unit restart budget (default 10).

Logs: `$OFAC_LOG_DIR/10-gateway.sh.log`. Ready signal:
`gateway ready endpointId=<id> epoch=<n> children=<k>`.

## State law

The Feature owns no state: point `stateDir` (and the child socket) at the
workspace mount, e.g. `/workspace/.ofac-gateway`.

## Troubleshooting

- Unit exits with code >= 2: config/protocol/enrollment error — terminal by
  design (no restart loop). Read the log header; fix the config.
- `Gateway config error: stateDir` — `stateDir` is required and resolves
  against the process CWD; use an absolute path under the workspace mount.
- Port already in use — diagnostics bind `127.0.0.1:<diagnostics.port>`;
  change `diagnostics.port` in the config.
