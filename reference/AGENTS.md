# reference/

## Purpose
Reference deployment inputs for the slim/full Docker Compose environments:
image version pins, Keycloak realm inputs, Postgres init, kernel config, smoke
checks. `make compose-slim` / `compose-full` / `compose-down` (repo root) boot
the stack; `ref-env-bringup` in `../.github/workflows/ci.yml` gates it per PR.

## Truth anchors
- `compose.slim.yaml` / `compose.full.yaml` — the stacks: providers only
  (gateway/launcher/worker services come with the node-runtime services);
  interpolation ONLY from `versions.env` + `.env` (two `--env-file` flags).
- `versions.env` — the single pin source: every reference image tag, the
  source-built `FRAMEWORK_TAG`, and release-time digests live here, nothing
  else. `.env.example` — every local secret, each naming its consumer.
- `config/facilitator.json` — mounted Production kernel config (no secrets);
  env vars override it. `keycloak/realm.json` — shared dev/test IdP fixture.
- `postgres-init/01-databases.sql` — side databases on first boot only.
- `bootstrap/`, `checks/` — planned seed/verify inputs, not built yet.
- Target design: `../docs/specs/open_facilitator_framework_architecture.md`
  §21–22 (reference environment).

## Develop here
- Change pins only in `versions.env`; a PR that adds a key must also extend the
  key-completeness list in the `meta` job of `../.github/workflows/ci.yml`.
- A bump or compose change must boot a healthy stack (`make compose-slim`) —
  the CI `ref-env-bringup` job re-runs exactly that.
- The Keycloak health probe and the kernel health probe are raw `/dev/tcp`
  bash probes: the UBI-micro and aspnet images ship no HTTP client.

## Invariants
- Pinned versions are law for every image this repo runs or references; CI and
  docs cite `versions.env`, never restate a tag.
- Empty `*_DIGEST` entries are filled by the release process, not by hand.
- `identity:oidc:authority` in `config/facilitator.json` must byte-match the
  issuer Keycloak publishes (`KC_HOSTNAME` + the `id.localhost` alias make
  `http://id.localhost:8080/realms/facilitator` that single URL).
