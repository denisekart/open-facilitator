# reference/

## Purpose
Reference deployment inputs for the slim/full Docker Compose environments:
pins, IdP realm, Postgres init, kernel + workspace configs, seeds, checks.
`./referencectl up slim` boots + seeds; `./referencectl verify` is the
acceptance run — a LOCAL gate (hosted ref-env CI is compute-gated off).

## Truth anchors
- `compose.slim.yaml` / `compose.provisioning.yaml` / `compose.full.yaml` —
  the stacks; the provisioning overlay (launcher + kernel `Provision__*` +
  the build-only `elastic-worker` image) rides EVERY compose invocation.
- `compose.replicas.yaml` (+ `.full.yaml`) — two-replica shape (`up slim
  --replicas 2`): `facilitator-b` + nginx `facilitator-lb` :8089; link legs
  ride the LB via `config/*-lb` mounts or `OFAC_KERNEL_URL`.
- Kernel env `config/facilitator.env` + provider arm
  `config/facilitator-providers.env` — ONE env_file each, shared with the
  replica twins (`extends` resolves against compose.slim.yaml only).
  Interpolation ONLY from `versions.env` (single pin source) + `.env`.
  `config/facilitator.json` — mounted kernel config (env overrides it);
  `keycloak/realm.json` — IdP fixture; `postgres-init/` — side DBs.
- `container/units/` + `container/coding-units/` + `config/coding-workspace/`
  + `config/workspace/` — supervisor units + Gateway/worker/policy/manifest
  configs for the demo and coding workspaces; `bootstrap/seed-lib.sh` — seed
  primitives shared by `bootstrap.sh` and the acceptance run.
- `checks/verify.sh` — slim acceptance run; `checks/verify-replicas.sh` —
  two-replica matrix (spec §7); `checks/verify-full.sh` — provider +
  provisioning live proof (`make provider-e2e`).
- Provider init is a one-shot per forge (`mattermost-init`, `forgejo-init`):
  gate on `service_completed_successfully`, bot credentials via the provision
  volume, never env. Target design:
  `../docs/specs/open_facilitator_framework_architecture.md` §21–22.

## Develop here
- Pins change only in `versions.env`; empty `*_DIGEST` are release-filled; a
  new pin key extends `../.github/workflows/ci.yml`'s `meta` key list.
- Any pin/compose change must pass locally — locally IS the gate:
  `./referencectl down -v && ./referencectl up slim --no-seed && ./referencectl verify`.
- `identity:oidc:authority` in `config/facilitator.json` must byte-match the
  Keycloak issuer (`http://id.localhost:8080/realms/facilitator`).
