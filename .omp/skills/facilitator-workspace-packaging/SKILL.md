---
name: facilitator-workspace-packaging
description: Dev Container Features (workspace-link, gateway, harness-adapter), Gateway modes and trust reporting, HarnessAdapter lifecycle contract, and team configuration layout for the Open Facilitator Framework.
---

> **Status:** the Link client runtime is IMPLEMENTED (Plan 04): `src/workspace-link` (`@ofac/workspace-link`) + the `ofac-linkd` bin — the headless supervisor entrypoint (spec S:409/§8.2): enroll → capabilities POST → connect → serve, SQLite state for replay, echo/stub handler modes, `--transport signalr|http-poll`, exit codes 0/2/3/4. The workspace-link Feature starts the Link from the supervisor unit via `ofac-linkd run`, never relies on `postStartCommand`, and mounts the enrollment secret / link token at runtime. The Gateway runtime is IMPLEMENTED for ALL THREE modes (Plans 05+06): `src/gateway` (`@ofac/gateway`) + `ofac-gateway run --config gateway.yaml` — one endpoint `gw.<slug>`; `embedded` = one in-process child behind the serializable `ChildExecutor` seam; `shared`/`standalone` = a fleet of child PROCESSES over a unix socket (`childTransport.socket.path`, resolved against `stateDir`) with localhost TCP carrying the identical NDJSON frame protocol (`src/gateway/src/childwire.ts`: hello/register-ack/command/reply/event/heartbeat/goaway; child-side client `connectChild` exported from `@ofac/gateway`). Per-child tokens (`children[].token`, fallback `OFAC_CHILD_SECRET`), heartbeat lease, admission law, union-manifest re-POST, per-child `maxInFlight` quota, re-register reconcile probe. `gateway.sqlite3` bindings + `child_state` + `pending_actions` ledgers, `/status` diagnostics, exit codes 0/2/3/4 (linkd's law). Config keys, ledger schemas, and the trustMode table: `src/gateway/README.md` + `docs/advanced.md`. The TEAM POLICY ENGINE + CONFIRM LOOP are IMPLEMENTED (Plan 12): `gateway.yaml` `policy: {inline|path, confirmTimeoutSeconds}` (fail-closed YAML loader `policy-file.ts`, `policyId = owner/sha256-prefix`); `policy.ts` fleet-honesty pre-pass + first-match-wins `allow|deny|confirm` + implicit deny (resource law: `data.inputs.resource` else capabilityId, `*`/prefix-globs); `claims.ts` verifies `oidc:` evidence JWTs via issuer JWKS (signature/iss/aud/exp+30s skew/delegation) degrading to workload-only claims; a `confirm` parks a `pending_actions` row + publishes `approval.required` (kernel recomputes the §7.2 digest), the kernel's decided-push arrives as `event.approval.decided.v1`, the Gateway re-evaluates (§7.3) + consumes over the Link plane + executes once; `audit.ts` emits the §12.2 `authorization.evaluated` line. Laws + round-trip: `docs/security.md`. The HarnessAdapter layer is IMPLEMENTED (Plan 07): `src/adapters/harnesses/adapter-sdk` (`@ofac/adapter`) implements the lifecycle law over that exact 06 wire, profiled normatively by `protocol/json-schema/adapter.v1.json` (fixture class `adapter`); the worked-example worker is `src/adapters/harnesses/deterministic` (`@ofac/harness-deterministic`, bin `ofac-deterministic-worker`, config `kind: HarnessAdapter`); no-Docker walkthrough `examples/slim/deterministic-research-worker/` (§5b = the live confirm loop). The FEATURES are IMPLEMENTED (Plan 08): see "Feature family (as-built)" below — replace this paragraph's tail when a slice lands.

# Workspace Packaging & Gateways

Normative source: `docs/specs/open_facilitator_framework_architecture.md` §3.2, §6, §8, §9, §18. The onboarding promise: a team adds **one Feature, a URL, and a short-lived enrollment credential** to an *existing* `devcontainer.json` — no harness rewrites, no model/vendor changes, no new AGENTS.md or CI conventions.

## When to use

- Building or changing anything under `features/`, `src/gateway/`, `src/workspace-link/`, `src/adapters/harnesses/`, `examples/`.
- Deciding deployment topology for a team, or writing docs about trust/isolation.
- Implementing a harness wrapper for Pi/OpenCode/Codex/Claude/custom/deterministic runtimes.

## Feature family (as-built, Plan 08)

Layout `features/src/<name>/{devcontainer-feature.json,install.sh}` +
generated `pkg/runtime.tar.gz` (built by `features/build.mjs`: `pnpm deploy
--prod` per package → tarball + sha256 baked into the Feature layer — no npm
registry, no network, no toolchain at container-install time). Ids
`ofac-gateway`, `ofac-workspace-link`, `ofac-harness-adapter`; version
`1.0.0`; coordinates `ghcr.io/denisekart/open-facilitator/features/<name>:1`
(major tag re-tagged by the release features job).

| Feature | Installs | Options (startup defaults) | Unit |
|---|---|---|---|
| `gateway` | `ofac-gateway` bundle + supervisor | `configPath`, `log` | `units/10-gateway.sh` (exit >=2 terminal) |
| `workspace-link` | `ofac-linkd` bundle + supervisor | `facilitatorUrl`, `endpointId`, `team`, `manifest`, `mode` | `units/20-linkd.sh` (exit 2 terminal, 0 stops, else restart) |
| `harness-adapter` | `ofac-deterministic-worker` bundle + supervisor | `adapter` (=deterministic only), `configPath` | `units/30-harness-adapter.sh` (exit >=2 terminal) |

Contract: each install.sh hard-fails without Node >= 22 (bring
`ghcr.io/devcontainers/features/node` or a Node base image), checksums +
extracts to `/usr/local/lib/openfacilitator/<name>`, writes a PATH shim, and
is idempotent via `.installed-version`. ONE supervisor
(`features/shared/ofac-supervisor.sh`, staged into each `pkg/` because OCI
publish ships only the Feature subfolder) is every Feature's identical
`entrypoint`; logs to `${OFAC_LOG_DIR:-/tmp/openfacilitator/logs}/<unit>.log`;
units restart with 2 s backoff up to `OFAC_SUPERVISOR_MAX_RESTARTS` (10) and
forward TERM to the runtime child. No lifecycle hooks are declared (§8.2).
Options arrive as bare-UPPERCASE env (`configPath`→`CONFIGPATH` — the spec
adds NO underscore) and bake unit defaults; the runtime env surface
(`GATEWAY_CONFIG`, `WORKER_CONFIG`, `OFAC_*`) still wins at runtime; secrets
are env-only, never options. Tests: `features/test/<name>/`
(ubuntu+debian; Alpine excluded — better-sqlite3 musl prebuilds UNVERIFIED
as of 2026-10, only the shipped `prebuilds/linuxmusl-*.node` files exist),
combo scenario on gateway proves composition. Feature version ≠ protocol
major (§8.5).

Supervisor units read `${UNITS_DIR:-/usr/local/share/openfacilitator/units}`
— the compose `demo-workspace` service (reference env) bind-mounts
`reference/container/units/` + `reference/config/workspace/` and runs the
same unit law over the node-runtime image (`/app/gateway`, `/app/worker`);
the worker unit's kernel self-enroll block (OFAC_KERNEL_URL +
OFAC_ENROLL_SECRET → POST /api/link/v1/enroll role child → export
OFAC_KERNEL_TOKEN) is deliberately duplicated between the Feature template
and the reference copy. `kernelUrl` is a REQUIRED worker config key (Plan 15
kill rule): no optional path, no shim.

## Gateway modes & trust truth

- **Embedded**: Feature beside the agent, normally 1 worker; privileged connectors allowed at team discretion; persistence SQLite/mounted volume. Co-located policy is *operational guidance*, not a security boundary against arbitrary in-container code — say so in docs/manifests/audit metadata.
- **Shared**: same package, own container/VM, 0..N children, independently enforceable credentials, always-on or event-triggered.
- **Direct Link**: no Gateway; workspace holds team-approved credentials and does the same policy checks itself.
- Report `trustMode` (`shared-runtime` | `separate-process-same-host` | `isolated-executor`) honestly; admin surfaces show it; a stopped Gateway stays logically registered with deliveries parked in Facilitator Postgres. Restart reconciles accepted assignments against its local SQLite ledger.

Privileged action sequence (never short-circuit): authenticated command → exact capability/resource/actor extraction → policy check → approval evidence → credential ceiling check → scoped connector execution → action ledger + event. Gateway rejection of a routable request is a normal result.

## HarnessAdapter contract (§8.3) — as-built

The spec's six-method interface shipped as an ASYNC-CALLBACK API (the wire is
command/reply, not RPC-per-method): `createAdapter(opts)` from `@ofac/adapter`
wraps `connectChild` with the lifecycle law. You implement `HarnessHooks`:
`onExecute(ctx)` (the detached run; resolving `completed` means the HARNESS
already published `assignment.completed.v1` — the SDK publishes only
`assignment.failed.v1` retryable on fail/throw), optional `onSteer` (no hook
⇒ steer answers `unsupported`, report-don't-reinterpret), `onCancel`
(cooperative; `ctx.signal` aborts for you). `RunContext` carries
`executionId`/`executionGeneration`/assign envelope + `publishEvent` (relay
law: the `event` frame carries the FULL CloudEvent envelope, Gateway forwards
bytes unmodified) / `publishProgress` / `setSteered`. The in-memory execution
registry dedupes duplicate `start` (matched by `command.data.commandId`) and
answers `status` truthfully; terminal entries expire after `TERMINAL_TTL_MS`
(60 s) ⇒ `unknown` ⇒ Gateway declares lost, exactly once. Restart ownership:
`restartBackoffMs` loop (default `[100,250,500,1000,2000,5000]`, cap-loop;
`[]` ⇒ exit for a supervisor). Descriptor: `invocation: "sdk"` ONLY (a
library, not a spawned CLI); `resume: "restart-with-context"`; `SessionStore`
is the unimplemented persistence seam. Conformance gate:
`runAdapterLifecycleContract` from `@ofac/adapter/testing` (in-process
listener, no Facilitator). Walkthrough: `src/adapters/harnesses/adapter-sdk/README.md`;
frames/authority/interims: `docs/advanced.md`.

## Provisioning (§18)

`WorkspaceProvisioner.start(spec, assignmentId, operationId)/status/stop` — idempotent `operationId`. Flow: admission reserves capacity → launch with short-lived assignment-bound enrollment credential → worker connects outbound, verifies assignment, emits `ready` → only then execution goes active. Defaults (configurable): `minWorkers=0, maxWorkers=3, idleShutdownMinutes=15, startDeadlineMinutes=5, maxExecutionHours=4, maxStartRetries=2`. Never kill a silent long-running research run without declared heartbeat expectations. In the full reference the Docker socket belongs to `workspace-launcher` (allowlisted images/network/commands), **not** `team-gateway`.

## Team config layout

`.facilitator/{gateway.yaml,harness.yaml,capabilities.yaml,policies.yaml,memory.yaml}` beside the team's existing `agents/`, `AGENTS.md`, `src/`. Everything optional; a research-only worker may have no `src/` and no repo at all.
