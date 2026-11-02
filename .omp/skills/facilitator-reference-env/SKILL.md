---
name: facilitator-reference-env
description: Slim/full Docker Compose reference environments, versions.env pinning, referencectl bootstrap/seed/verify, localhost hostname wiring, and local debugging of the Open Facilitator Framework.
---

> **Status (as-built):** `./referencectl up slim` (repo root) boots + seeds the demo path: PostgreSQL + Keycloak + SeaweedFS + Aspire dashboard + the Facilitator container + `demo-workspace` (one container, two processes: standalone Gateway `gw.team-alpha` + the deterministic worker over an in-container unix child leg — the mode-(b) embedded story). `reference/bootstrap/seed-lib.sh` holds the seed primitives (compose/psql/token/project/work/dispatch), shared by `bootstrap.sh` (demo path) and the acceptance run; `seed_work` POSTs `/api/works`; the kernel routing funnel writes the `work.assign` effect. `reference/checks/verify.sh` is the 11-step acceptance run (`referencectl verify`). Full-overlay provider seed data lands with the init one-shots (`mattermost-init`, `forgejo-init`), not the slim bootstrap.

# Reference Environment & Dev Loop

Normative source: `docs/specs/open_facilitator_framework_architecture.md` §21–§22, §26–§27; appendix §10–§11. Both profiles run the **same** Facilitator source, schemas, Gateway package, identity model, and Features — they differ only in breadth.

## When to use

- Working under `reference/` (compose files, keycloak realm, postgres-init, config, bootstrap, checks), `examples/`, or CI for them.
- Running/verifying any acceptance flow; reproducing an integration bug locally.

## Profiles

**Slim** (laptop, few GiB): Facilitator (ASP.NET Core + embedded admin SPA), PostgreSQL 17 (default datastore even in slim; inbox/outbox/leases/memory FTS), Keycloak 26 (`start-dev --import-realm`), SeaweedFS `weed mini` (S3 artifacts), standalone Aspire dashboard (OTLP viewer, in-memory retention), Node/TS embedded Gateway + deterministic research worker, CLI/HTTP requester. No Kafka, no Temporal, no model download required.


 **Full overlay** adds: Mattermost Team Edition + the Plan 22 provider pair (`mattermost-init` one-shot provisions bot+PAT via REST onto the `mm_provision` volume — 11.11.1 has NO unauthenticated setup route, so bootstrap rides OPEN SIGNUP; `mattermost-adapter` runs from node-runtime, `/healthz` :8066), Forgejo (SQLite; VCS **and** issues — one service) + the Plan 23 pair (`forgejo-init` — `forgejo-adapter init` — signs up the first admin via `POST /user/sign_up` when none exists, seeds org/repo/labels/sample issues/webhook + bot PAT onto `forgejo_provision`; `forgejo-adapter` webhook normalizer `/healthz`+`/hooks/forgejo` :8067; the `forgejo-connector` runs as a supervisor unit inside `demo-workspace`, env-gated by `FORGEJO_URL`), a **second workspace container for the coding lane** (`coding-workspace`: same node-runtime, `CODING_CHILD_SECRET` child-wire fleet key, `OFAC_WORKER_TEAM=team-coding`), Kafka 4.x single-node KRaft (example ingest only), Ollama + `qwen2.5:1.5b` (optional semantic routing), shared team-gateway + workspace-launcher. The facilitator's provider sections ride `config/facilitator-providers.env` (full-profile `env_file` only, both replicas — see the twin law below), so the slim stack stays provider-less from the same mounted config; the coding lane is `Providers__Forgejo__AssignRules__N__*` (label→endpoint/capability/team) + `PrRequestCapability`. The PROVISIONING overlay (`compose.provisioning.yaml`, slim+full, plan 35 Phase C) adds the `provisioning-launcher` link endpoint (Docker socket, allowlisted image+network) + the kernel's `Provision__*` config; the `elastic-worker` (team-beta, `gw.team-beta`) is a build-only image the KERNEL launches — never a compose service. Live proof: `make provider-e2e` (20 steps: 6 mattermost + 7 forgejo + 4 coding `coding-*` + 1 two-repo DAG leg + 2 provisioning `provision-*` legs, `reference/checks/verify-full.sh`; the CI leg synthesizes a signed `status` delivery straight to the adapter — Forgejo 16.0.7 does not fire status webhooks, open feature request #9325).

Layer compose: `docker compose -f compose.slim.yaml -f compose.provisioning.yaml [-f compose.full.yaml]`; ship one tested method, don't make newcomers invent network wiring. The two-replica shape (`up slim --replicas 2`, full: `FULL=1`) appends `-f compose.replicas.yaml [-f compose.replicas.full.yaml]`: a `facilitator-b` twin (`extends` the primary — distinct `Kernel__ReplicaId`, :8090) behind nginx `facilitator-lb` :8089 (least_conn + passive eviction; ip_hash degenerates behind docker's userland proxy). EVERY link leg rides the LB: the `config/workspace-lb`/`coding-workspace-lb`/`launcher-lb` mount variants point gateways/launcher at `http://facilitator-lb`, adapter + child URLs via env (a Gateway's `facilitatorUrl` honors the `OFAC_KERNEL_URL` env overlay — how the elastic worker's BAKED config repoints at the LB). The twin's `extends` resolves against compose.slim.yaml ONLY, so the full-profile provider arm lives in `config/facilitator-providers.env` and BOTH replica blocks load it as `env_file` (an inline block in compose.full.yaml would leave B provider-less → typed `unsupported` receipts when an adapter session lands there). The LB healthcheck PROXIES `/health`: a stale cached upstream IP after a replica recreate must surface as an unhealthy LB (restart it), not silent 502s. Single-replica bring-up never loads these files.

## Hard requirements

- `reference/versions.env` pins every image tag/digest — **no `latest`** in CI/reproducibility.
- `.env` holds demo secrets (`DEMO_POSTGRES_PASSWORD` etc.); `reference/secrets/` generated + gitignored; enrollment tokens are short-lived, assignment-bound, written atomically by seed.
- Pre-seed runtime behavior: Gateways/Links start in nonprivileged `awaiting-enrollment` and retry — never crash, never accept anonymous commands.
- Identity: demo users/groups/scopes live in `keycloak/realm.json`; Mattermost↔Keycloak linkage is a single-use DM code challenge — never email/username matching.
- Loopback-bound ports + `start-dev` + plaintext = reference-only; document that.

## Hostnames & ports (one-host demo)

`http://id.localhost:8080` Keycloak · Facilitator `:8088` · `http://git.localhost:3000` Forgejo · `http://chat.localhost:8065` Mattermost · `:8333` SeaweedFS S3 · `:9092` Kafka · `:11434` Ollama · `:18888/:18890` Aspire dashboard/OTLP · `:8066` mattermost-adapter healthz · `:8067` forgejo-adapter (`/healthz` + `/hooks/forgejo` — the Forgejo webhook posts container-internal to the adapter, loopback-published so verify-full can synthesize deliveries). Both adapters dial OUT (bot WS / link plane, D1); the only provider callback in the system is forgejo→forgejo-adapter.

## Dev loop

```bash
make compose-slim          # from the repo root: cp -n .env.example .env, then
                           # docker compose --env-file versions.env --env-file .env \
                           #   -f compose.slim.yaml up -d --build
make compose-full          # + compose.full.yaml overlay
make compose-down          # down -v (FULL=1 to include the overlay)
```

Observe: `docker compose ... ps` (all healthy), kernel `http://localhost:8088/health`, realm `.well-known` at `http://id.localhost:8080/realms/facilitator`, bucket browse `http://localhost:8333`, traces at the dashboard `http://localhost:18888`. Operator bearer: `facilitator-cli`/`e2e-cli-secret` client_credentials against the realm token endpoint.

Operator UI: `http://localhost:8088` serves the embedded admin SPA (Plan 21) — login `alice`/`alice`; the browser tier is `make admin-e2e`, so a psql-free walkthrough of the acceptance-run Work (search → inspector → timeline → trace chip, dead-letter replay, one config PATCH) is the manual acceptance leg.

`referencectl` (built): `up slim [--no-seed] [--replicas 2]` (compose up --wait + bootstrap), `down [-v]`, `status` (compose ps + kernel `/health` + a `compose exec` probe of the Gateway's `127.0.0.1:8099/status` — diagnostics bind the demo-workspace container's loopback, never a published port; when an LB answers at :8089 the fleet view `/api/admin/replicas` prints too), `token [--user U --password P]`, `work [--objective T]`, `verify [--json] [--only ids]` — the acceptance run (see below), `verify --replicas` — the two-replica matrix, `verify --full [--json]` — every tier (slim + full-profile + replica matrix) as one merged JSON report. Appendix ergonomics (`./dev up`, `./dev assign`, `./dev show deliveries`) share that unbuilt status.

## Acceptance run (`reference/checks/verify.sh`)

`./referencectl verify` proves every spec 21.1 hop against a booted-but-
unseeded stack, seeds its own works, exit 0 iff all 11 steps pass:
idp-login, work-create, dispatch-link, progress, artifact-stored,
memory-candidate, approval-roundtrip, worker-restart-resume,
kernel-restart-resume, trace-correlation, dead-letters-empty. Laws the
script encodes (keep them when editing):
- Waits ride inbox/timeline event kinds (`poll_until`), never wall-clock
  sleeps; unrequested dependency steps run as prerequisites (steps own
  run-global state: tokens, WORK_MAIN).
- Restart checkpoints: approval parked across a `restart facilitator`
  (durable await); worker leg = the plan 35 Phase C PROVISIONING law — the
  killed worker is the kernel-launched elastic container (`docker kill
  ofac-gw-team-beta`), and a mid-execution kill yields exactly ONE honest
  `assignment.failed(retryable)`, then the kernel's worker-lost arm
  relaunches AND re-dispatches (NO operator verb) to completion with
  exactly 1 manifest, revision 1, revisionOf null, and a
  `provisioning_operations` row `reason like 'worker-lost%'`. The child
  quota is `maxInFlight: 1`: seed only after the fleet is idle
  (`poll_elastic_idle` — session lease + zero in-flight over the
  container's own diagnostics port).
- The provisioning overlay (compose.provisioning.yaml) rides EVERY compose
  invocation (seed-lib, verify.sh, verify-full, referencectl, Makefile):
  the scale law is part of the acceptance env. The legs OWN the policy via
  `seed_workload_scale_policy` (PATCH /api/admin/config/workload-scale,
  If-Match:*) — min1 at leg start, and bootstrap.sh seeds {1,1,10} at
  stack up so a bare stack self-boots the elastic container.
- Trace law: the dispatcher extracts the carrier parent, runs
  `outbox.dispatch` under it, and restamps every link-channel row before
  delivery (`RestampEnvelopeAsync`, lease-guarded); the worker echoes the
  stamped `traceparent` verbatim, so outbox stamp == progress/completed
  envelope traceparent == the Aspire span's trace id. Read the stamp out
  of the outbox row — never a timing join.
- Debug a leg: `verify --only <id>` (prereqs auto-run); the human plane
  dumps kernel+workspace log tails on failure.
- Two-replica matrix (`reference/checks/verify-replicas.sh`,
  `verify --replicas`, spec §7 cases a–e): LB answers from both replicas;
  concurrent duplicate event POSTs settle to one inbox row (DB arbiter);
  SIGKILL the session-owning replica → lease expiry (15s: heartbeat 5×3 —
  the overlay's timing law) → the gateway reconnects through the LB onto
  the survivor at a bumped epoch, acked deliveries never shrink; an
  expired-lease outbox row stamped by a GHOST replica is stolen by a live
  dispatcher and settled exactly once; parallel steers at one
  expectedVersion (distinct commandIds) → one 200 + one 409 + fresh-version
  retry wins (target: a parked research.publish work — steer on completed
  work is a terminal-state refusal); the full slim suite nested through the
  LB with both replicas running every loop. The kill leg RESTORES the
  victim (`up -d --no-deps`): docker counts a user KILL as an intentional
  stop (on-failure restarts crashes, not kills), and later cases need both
  replicas. The restore loads the SAME overlays as the running stack (the
  script auto-detects the full profile from a running
  `facilitator-demo-mattermost-1`): a full stack restored without
  `compose.full.yaml` recreates the primary stripped of its provider arm,
  and the adapter's DM events then type `unsupported`.

## Debugging hooks

- MCP `postgres` (enable in `.omp/mcp.json` once seeded): inspect `inbox/outbox/dead_letters/link_sessions/work/assignments` — check `attempts`, `leased_until`, `session_epoch` before touching code.
- MCP `playwright` / omp `browser`: drive admin SPA, Keycloak login, Mattermost thread flows.
- Runbooks (spec §27): Gateway offline → link_sessions + last heartbeat; stuck work → heartbeat vs native progress, one status.request, ≤1 nudge; suspected duplicates → provider delivery ID + `(source,id)` + connector ledger, mark `uncertain` not cleared; DB outage → nothing claims accepted until persisted.
- Flaky chat/model output must never gate protocol tests: core acceptance routes a deterministic fixture (`referencectl verify`); local-model assertions are bounded and separate.
