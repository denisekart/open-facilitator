---
name: facilitator-reference-env
description: Slim/full Docker Compose reference environments, versions.env pinning, referencectl bootstrap/seed/verify, localhost hostname wiring, and local debugging of the Open Facilitator Framework.
---

> **Status:** nothing exists yet — compose files, `referencectl`, seeds, and services below are spec-proposed targets to build. Update this skill the moment a slice lands so implemented reality replaces spec proposals.

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
cd reference && cp .env.example .env
./bootstrap/generate-secrets.sh
./referencectl up slim && ./referencectl seed slim && ./referencectl verify slim
./referencectl up full  && ./referencectl seed full  && ./referencectl verify full
./referencectl status   # waits for HTTP/readiness, not container start
```

`referencectl` is idempotent: provisions Forgejo org/repos/issues, Mattermost team/channel/bot token, linkage challenges, Kafka topic + sample event, worker enrollment grants, Ollama model; prints login links, never dumps tokens. Appendix ergonomics: `./dev up`, `./dev assign --work work-1 --capability research.analyze`, `./dev show deliveries --pending`, `./dev inspect delivery del-123`.

## Debugging hooks

- MCP `postgres` (enable in `.omp/mcp.json` once seeded): inspect `inbox/outbox/dead_letters/link_sessions/work/assignments` — check `attempts`, `leased_until`, `session_epoch` before touching code.
- MCP `playwright` / omp `browser`: drive admin SPA, Keycloak login, Mattermost thread flows.
- Runbooks (spec §27): Gateway offline → link_sessions + last heartbeat; stuck work → heartbeat vs native progress, one status.request, ≤1 nudge; suspected duplicates → provider delivery ID + `(source,id)` + connector ledger, mark `uncertain` not cleared; DB outage → nothing claims accepted until persisted.
- Flaky chat/model output must never gate protocol tests: core acceptance routes a deterministic fixture (`referencectl verify`); local-model assertions are bounded and separate.
