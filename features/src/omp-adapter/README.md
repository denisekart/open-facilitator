# Dev Container Feature: ofac-omp-adapter

Installs the **omp harness worker** (`ofac-omp-worker`) — the oh-my-pi CLI
stand-in — as a self-contained Node bundle plus a supervisor unit. The worker
is a Gateway *child*: it attaches over the child transport (unix socket or
127.0.0.1 TCP), registers its `coding.omp` capability manifest, and executes
assignments the Gateway routes to it by spawning `omp --mode rpc` per
execution.

## Prerequisites

- Node **>= 22** on PATH (hard-fails otherwise; a Node-shipping base image or
  the official `ghcr.io/devcontainers/features/node` Feature satisfies this).
- The native **`omp` CLI on PATH** — the Feature does NOT install it. The
  install hard-fails with an ERROR naming the base-image hint when
  `omp` is missing; set `requireCli: false` to install the worker bundle
  anyway (e.g. for feature scenario tests).

## Options (startup-time defaults only)

|Option|Default|Meaning|
|---|---|---|
|`configPath`|`.facilitator/omp-harness.yaml`|Worker config the unit starts with; `WORKER_CONFIG` env overrides|
|`requireCli`|`true`|Fail the install when `omp` is not on PATH; `false` skips the CLI gate|

## Agentic-mode law

The worker runs the harness **fully autonomous**: it forces
`omp --mode rpc --auto-approve` so no per-tool-call approval can stall a
headless run. Override via the worker config's `extraArgs` (appended
verbatim) when the base image's omp version spells the flag differently.
Genuine interaction — a question to the human — is *not* a native prompt:
the worker injects an `ask_user` MCP tool into the harness, and a tool call
relays the question through the Facilitator to the work's owner; the live
run resumes when the answer arrives.

## Runtime env surface (1:1 with the binary)

- `WORKER_CONFIG` — config path (beats the baked `configPath`).
- `OFAC_CHILD_SECRET` — beats the config file's `token:`; must equal the
  Gateway's `OFAC_CHILD_SECRET`.
- `OFAC_ADAPTER_LOG` — log level (debug|info|warn|error).
- `OFAC_LOG_DIR` / `OFAC_SUPERVISOR_MAX_RESTARTS` — supervisor surface.

Logs: `$OFAC_LOG_DIR/31-omp-adapter.sh.log`. Ready signal:
`worker ready childId=<gatewayId>/<child> socket=...`.

Restart law mirrors the binary: 1 (runtime — e.g. Gateway down, socket not
yet there) restarts with 2 s backoff; 2 (config) and 3 (register denied with
backoff disabled) are terminal.

## State law

The worker clones + runs each execution under its config's `workDir` — keep
it on the workspace mount (e.g. `/workspace/.ofac-omp-worker`).
