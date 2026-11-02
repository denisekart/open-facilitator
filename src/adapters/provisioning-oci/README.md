# @ofac/provisioning-oci

Local OCI provisioning launcher: one Node process that is a
link-plane endpoint (enroll → capabilities → SignalR, the forgejo-adapter
shape) and executes the kernel's provisioning decisions against the **local
Docker Engine socket** — so a killed/idle worker container auto-relaunches and
rebinds the same assignment at a new generation (the continuity law). No
docker SDK: a tiny `http.request`-over-unix-socket client speaks the Engine
API directly.

## Command

```
oci-launcher run --config <adapter.yaml>
```

Exit codes: 0 clean · 1 runtime failure · 2 config error.

## Commands executed (handler law, src/handler.ts)

Routing mechanism: the delivered provision envelopes carry **no
capabilityId** — the kernel routes by recipient (`link.<Provisioning:…>`), and
the workspace-link client hands EVERY delivered command to the mounted
handler's `onCommand` (`core/command-dispatcher.ts` gates only on
`KNOWN_COMMAND_TYPES`, never on capability ids). The handler therefore
dispatches on `envelope.type`:

- `org.facilitator.command.provision.start.v1` → read-before-act, then
  `DELETE` the workload's name-holding exited containers (the kill-9 path:
  the real engine answers a fixed-name create with 409), then the
  inspect-first image gate — `GET /images/{ref}/json`, and
  `POST /images/create` (pull, bounded by the envelope deadline) ONLY when
  the image is absent: the reference env's worker images are locally built
  and never pushed — then `POST /containers/create` +
  `POST /containers/{id}/start`. A failed pull with the image still absent ⇒
  rejected receipt `image-missing`; a deadline abort ⇒ `start-deadline`.
- `org.facilitator.command.provision.stop.v1` → `POST /containers/{id}/stop?t=<grace>`
  + `DELETE /containers/{id}` for EVERY container with the workload label,
  then the GC sweep. Stop envelopes carry no spec: the config
  `stopGraceSeconds` applies.
- Anything else → honest `unsupported` receipt.

**Reconcile-by-label law:** every created container carries labels
`ofac.workload=<workloadId>` + `ofac.operation=<operationId>`. A start whose
workload already has a RUNNING labeled container is the idempotent re-offer ⇒
no-op `accepted`. A stop with nothing to stop is `accepted` too. A workload
whose container EXITED (crash/idle-kill) has the corpse removed (it holds the
fixed container name) and gets a fresh container labeled with the new
operation — the same assignment, a new generation. Containers without an
`ofac.workload` label (compose-managed or foreign) are never touched.

**Create payload:** `Image`, `Entrypoint` argv, `Hostname: ofac-<slug>`,
`Env: KEY=VALUE`, `Labels` merge (spec labels ride alongside the ofac ones),
`HostConfig.NetworkMode` when `spec.network` is set (attaches to the EXISTING
network — the launcher never creates one), `HostConfig.Binds` from
`spec.volumes` (`source:target[:ro]`), `AutoRemove: false`,
`RestartPolicy: no`.

**Env-expansion law:** a `spec.env` VALUE consisting solely of `$VARNAME` is
expanded from the launcher's OWN process env at create time (kernel config is
static JSON and cannot interpolate secrets). A missing/empty var fails the
command with the honest receipt reason `env-unresolved:VARNAME` — never an
empty secret.

**GC law:** ofac-labeled containers in state `exited`/`dead` older than
`OFAC_PROV_GC_MINUTES` (default 60) are removed — on every stop, once at
startup, and every 60 s from the bin's REF'D periodic timer (that timer is
also the daemon's liveness anchor: the SDK's heartbeat/backoff timers are
unref'd and this process owns no server, so without it a transport blip
would let Node exit 0 mid-reconnect).

**Approved gate:** `spec.image` / `spec.network` outside the allowlists ⇒
rejected receipt (`image-not-allowed` / `network-not-allowed`) with a log,
before any docker call. An unset allowlist denies EVERYTHING.

## adapter.yaml

Secrets travel ONLY by env indirection (`enrollSecretEnv` NAMES the var).
Unknown keys are rejected by name.

```yaml
kernelUrl: http://kernel:9000
enrollSecretEnv: OFAC_ENROLL_SECRET
endpointId: oci-launcher # link identity
team: team-alpha # enrollment team
statePath: /var/lib/ofac/oci-launcher.sqlite3 # delivery ledger
gcMinutes: 60 # optional (env OFAC_PROV_GC_MINUTES wins)
stopGraceSeconds: 10 # optional
```

## Environment (run)

| Var                          | Meaning                                                              |
| ---------------------------- | -------------------------------------------------------------------- |
| `OFAC_ENROLL_SECRET`         | Enrollment secret (named by `enrollSecretEnv`)                        |
| `OFAC_ENDPOINT_ID`           | Link endpoint id override (default: the yaml, conventionally `oci-launcher`) |
| `OFAC_TEAM`                  | Team override (default: the yaml)                                    |
| `DOCKER_HOST`                | Engine socket (default `unix:///var/run/docker.sock`; unix-only)     |
| `OFAC_PROV_ALLOWED_IMAGES`   | Comma list; exact refs + `prefix/*` wildcards. Unset ⇒ deny all      |
| `OFAC_PROV_ALLOWED_NETWORKS` | Comma list, same matcher. Unset ⇒ any `spec.network` is rejected     |
| `OFAC_PROV_GC_MINUTES`       | GC age threshold (default 60)                                        |
| `OFAC_STATE_PATH`            | SQLite ledger path (default: the yaml `statePath`)                   |
| `OFAC_ADAPTER_LOG`           | debug\|info\|warn\|error (default info)                              |

The `$VARNAME` values inside `spec.env` resolve against THIS process's
environment — compose injects the secrets the workloads need, and they never
ride the kernel's wire.

## Receipts

Honest, via the workspace-link client law (identical to forgejo-adapter): the
handler returns `accepted` / `rejected{reason}` / `unsupported{reason}` and
the client ships the `ReceiptV1`; the SQLite ledger dedupes by `commandId`, so
a delivery replay answers `duplicate` and never double-creates.

## Tests

`pnpm test` — no real docker: a fake Engine (`test/fake-docker.ts`) is a real
`http.Server` on a unix socket in `os.tmpdir()`, driven by the production
`DockerClient`. Pins the exact create payload (labels/env/binds/network), the
re-offer no-op, stop-set semantics, the GC age/state/label filter, the
allowlist + `env-unresolved` + `start-deadline` receipts, and the config law.
