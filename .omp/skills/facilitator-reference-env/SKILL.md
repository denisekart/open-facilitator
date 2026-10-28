---
name: facilitator-reference-env
description: Slim/full Docker Compose reference environments, versions.env pinning, referencectl bootstrap/seed/verify, localhost hostname wiring, and local debugging of the Open Facilitator Framework.
---

> **Status (as-built):** `./referencectl up slim` (repo root) boots + seeds the demo path: PostgreSQL + Keycloak + SeaweedFS + Aspire dashboard + the Facilitator container + `demo-workspace` (one container, two processes: standalone Gateway `gw.team-alpha` + the deterministic worker over an in-container unix child leg — the mode-(b) embedded story). `reference/bootstrap/bootstrap.sh` seeds project + Work + a `work.assign` outbox row (`pg_notify` wakes the dispatcher); the SQL outbox insert stands in until the routing slice assigns works itself. NOT built yet: `checks/` scripts, full-overlay seed data. Where this document describes those, it is spec-proposed target, not reality.

# Reference Environment & Dev Loop

Normative source: `docs/specs/open_facilitator_framework_architecture.md` §21–§22, §26–§27; appendix §10–§11. Both profiles run the **same** Facilitator source, schemas, Gateway package, identity model, and Features — they differ only in breadth.

## When to use

- Working under `reference/` (compose files, keycloak realm, postgres-init, config, bootstrap, checks), `examples/`, or CI for them.
- Running/verifying any acceptance flow; reproducing an integration bug locally.

## Profiles

**Slim** (laptop, few GiB): Facilitator (ASP.NET Core + embedded admin SPA), PostgreSQL 17 (default datastore even in slim; inbox/outbox/leases/memory FTS), Keycloak 26 (`start-dev --import-realm`), SeaweedFS `weed mini` (S3 artifacts), standalone Aspire dashboard (OTLP viewer, in-memory retention), Node/TS embedded Gateway + deterministic research worker, CLI/HTTP requester. No Kafka, no Temporal, no model download required.

**Full overlay** adds: Mattermost Team Edition (chat adapter target: `root_id` replies, reactions), Forgejo (SQLite; VCS **and** issues — one service), Kafka 4.x single-node KRaft (example ingest only), Ollama + `qwen2.5:1.5b` (optional semantic routing), coding + research worker fixtures, shared team-gateway + workspace-launcher.

Layer compose: `docker compose -f compose.slim.yaml -f compose.full.yaml`; ship one tested method, don't make newcomers invent network wiring.

## Hard requirements

- `reference/versions.env` pins every image tag/digest — **no `latest`** in CI/reproducibility.
- `.env` holds demo secrets (`DEMO_POSTGRES_PASSWORD` etc.); `reference/secrets/` generated + gitignored; enrollment tokens are short-lived, assignment-bound, written atomically by seed.
- Pre-seed runtime behavior: Gateways/Links start in nonprivileged `awaiting-enrollment` and retry — never crash, never accept anonymous commands.
- Identity: demo users/groups/scopes live in `keycloak/realm.json`; Mattermost↔Keycloak linkage is a single-use DM code challenge — never email/username matching.
- Loopback-bound ports + `start-dev` + plaintext = reference-only; document that.

## Hostnames & ports (one-host demo)

`http://id.localhost:8080` Keycloak · Facilitator `:8088` · `http://git.localhost:3000` Forgejo · `http://chat.localhost:8065` Mattermost · `:8333` SeaweedFS S3 · `:9092` Kafka · `:11434` Ollama · `:18888/:18890` Aspire dashboard/OTLP. Provider webhook callbacks point at the container-internal Facilitator URL (e.g. `http://facilitator:8088/api/integrations/forgejo/webhook`).

## Dev loop

```bash
make compose-slim          # from the repo root: cp -n .env.example .env, then
                           # docker compose --env-file versions.env --env-file .env \
                           #   -f compose.slim.yaml up -d --build
make compose-full          # + compose.full.yaml overlay
make compose-down          # down -v (FULL=1 to include the overlay)
```

Observe: `docker compose ... ps` (all healthy), kernel `http://localhost:8088/health`, realm `.well-known` at `http://id.localhost:8080/realms/facilitator`, bucket browse `http://localhost:8333`, traces at the dashboard `http://localhost:18888`. Operator bearer: `facilitator-cli`/`e2e-cli-secret` client_credentials against the realm token endpoint.

`referencectl` (built): `up slim [--no-seed]` (compose up --wait + bootstrap), `down [-v]`, `status` (compose ps + kernel `/health` + a `compose exec` probe of the Gateway's `127.0.0.1:8099/status` — diagnostics bind the demo-workspace container's loopback, never a published port), `token [--user U --password P]`, `work [--objective T]`. `verify` is reserved for the acceptance-check pass. Appendix ergonomics (`./dev up`, `./dev assign`, `./dev show deliveries`) share that unbuilt status.

## Debugging hooks

- MCP `postgres` (enable in `.omp/mcp.json` once seeded): inspect `inbox/outbox/dead_letters/link_sessions/work/assignments` — check `attempts`, `leased_until`, `session_epoch` before touching code.
- MCP `playwright` / omp `browser`: drive admin SPA, Keycloak login, Mattermost thread flows.
- Runbooks (spec §27): Gateway offline → link_sessions + last heartbeat; stuck work → heartbeat vs native progress, one status.request, ≤1 nudge; suspected duplicates → provider delivery ID + `(source,id)` + connector ledger, mark `uncertain` not cleared; DB outage → nothing claims accepted until persisted.
- Flaky chat/model output must never gate protocol tests: core acceptance routes a deterministic fixture (`referencectl verify`); local-model assertions are bounded and separate.
