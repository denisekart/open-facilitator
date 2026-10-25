# Dev Container Feature: ofac-workspace-link

Installs **`ofac-linkd`** — the headless Workspace Link runtime — into any
container: a self-contained Node bundle (no npm registry, no network, no
toolchain at install), the supervisor entrypoint, and a unit that enrolls,
publishes capabilities, and keeps a direct Link connection to a Facilitator.
This is the no-Gateway path: the workspace itself is the link endpoint.

## Prerequisites

- Node **>= 22** on PATH (hard-fails otherwise; a Node-shipping base image or
  the official `ghcr.io/devcontainers/features/node` Feature satisfies this).
- A reachable Facilitator and one of `OFAC_ENROLL_SECRET` / `OFAC_LINK_TOKEN`
  (env, never options). A minted token persists in the `--state` ledger, so a
  rebuilt container with a mounted workspace reuses it; a fresh state re-enrolls.

## Options (startup-time defaults only)

|Option|Default|Meaning|
|---|---|---|
|`facilitatorUrl`|`http://localhost:8088`|Kernel base URL|
|`endpointId`|`ws.dev`|Link endpoint identity|
|`team`|`default`|Enrollment team (must equal the manifest `ownerTeam`)|
|`manifest`|`.facilitator/capabilities.json`|Capability manifest JSON (linkd requires a readable file — missing ⇒ terminal config exit)|
|`mode`|`echo`|`echo` replies to deliveries, `stub` acks silently|

## Runtime env surface (1:1 with the binary)

- `OFAC_ENROLL_SECRET` / `OFAC_LINK_TOKEN` — credentials, beat argv.
- `OFAC_LINKD_LOG=debug` — debug logging.
- `OFAC_LOG_DIR` — supervisor log dir (default `/tmp/openfacilitator/logs`).
- `OFAC_SUPERVISOR_MAX_RESTARTS` — per-unit restart budget (default 10).

Logs: `$OFAC_LOG_DIR/20-linkd.sh.log`. Ready signal:
`linkd ready endpointId=<id> epoch=<n> transport=<t>`.

Restart law mirrors the binary: exit 2 (config) is terminal; clean 0 stops;
3/4/1 (protocol, enrollment, runtime — e.g. kernel was down at enroll)
restart with 2 s backoff up to the budget.

## State law

The Feature owns no state: the sqlite ledger defaults to
`.ofac-linkd.sqlite3` in the workspace working dir (`OFAC_LINKD_STATE` moves it).
