# reference/

## Purpose
Reference deployment inputs for the slim/full Docker Compose environments:
image version pins, Keycloak realm inputs, Postgres init, kernel config, the
demo-workspace container payload, seeds, acceptance checks. `./referencectl up
slim` (repo root) boots + seeds the demo path; `./referencectl verify` is the
acceptance run — a LOCAL gate on every task (the hosted ref-env CI jobs are
compute-gated off; see `Develop here`).

## Truth anchors
- `compose.slim.yaml` / `compose.full.yaml` — the stacks: providers plus
  `demo-workspace` (node-runtime image, supervisor entrypoint);
  interpolation ONLY from `versions.env` + `.env` (two `--env-file` flags).
- `../referencectl` — the operator loop: up/down/status/token/work; it owns
  the compose invocation and the bootstrap call.
- `versions.env` — the single pin source: every reference image tag, the
  source-built `FRAMEWORK_TAG`, and release-time digests live here, nothing
  else. `.env.example` — every local secret, each naming its consumer.
- `config/facilitator.json` — mounted Production kernel config (no secrets);
  env vars override it. `keycloak/realm.json` — shared dev/test IdP fixture.
- `postgres-init/01-databases.sql` — side databases on first boot only.
- `container/units/` — supervisor units for `demo-workspace` (mirror the
  Feature-baked ones; duplication is deliberate); `config/workspace/` —
  Gateway/worker/policy/manifest configs; `bootstrap/seed-lib.sh` — the seed
  primitives (compose/psql/token/work) shared by `bootstrap.sh` (demo path)
  and the acceptance run — a raw-SQL `work.assign` outbox insert stands in
  for the routing slice; `checks/verify.sh` — the 11-step acceptance run,
  sourced by `referencectl verify`.
- Provider init is a one-shot per forge (`mattermost-init`, `forgejo-init` =
  `forgejo-adapter init`): consumers gate on `service_completed_successfully`
  and read bot credentials from the shared provision volume, never env.
  Provider live proof rides `checks/verify-full.sh` (`make provider-e2e`).
- Target design: `../docs/specs/open_facilitator_framework_architecture.md`
  §21–22 (reference environment).

## Develop here
- Change pins only in `versions.env`; a PR that adds a key must also extend the
  key-completeness list in the `meta` job of `../.github/workflows/ci.yml`.
- A bump or compose change must boot a healthy stack and pass the acceptance
  run locally: `./referencectl down -v && ./referencectl up slim --no-seed &&
  ./referencectl verify` (the CI jobs mirror this; they are commented out in
  `../.github/workflows/ci.yml` for free-tier compute, so locally IS the gate).
- The Keycloak health probe and the kernel health probe are raw `/dev/tcp`
  bash probes: the UBI-micro and aspnet images ship no HTTP client.

## Invariants
- Pinned versions are law for every image this repo runs or references; CI and
  docs cite `versions.env`, never restate a tag.
- Empty `*_DIGEST` entries are filled by the release process, not by hand.
- `identity:oidc:authority` in `config/facilitator.json` must byte-match the
  issuer Keycloak publishes (`KC_HOSTNAME` + the `id.localhost` alias make
  `http://id.localhost:8080/realms/facilitator` that single URL).
