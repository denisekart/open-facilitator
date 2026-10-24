# reference/

## Purpose
Reference deployment inputs for the slim/full Docker Compose environments: image
version pins, Keycloak realm inputs, Postgres init, smoke checks. The compose
files themselves and the `referencectl` CLI do not exist yet; the directories
here are the seeds they will consume.

## Truth anchors
- `versions.env` — the single pin source: every reference image tag and
  release-time digest lives here, nothing else.
- `.env.example` — host-side overrides (ports, hostnames) for compose.
- `bootstrap/`, `checks/`, `config/`, `keycloak/`, `postgres-init/` — see each
  directory's README for its role.
- Target design: `../docs/specs/open_facilitator_framework_architecture.md`
  §21–22 (reference environment).

## Develop here
- Change pins only in `versions.env`; a PR that adds a key must also extend the
  key-completeness list in the `meta` job of `../.github/workflows/ci.yml`.
- Do not invoke `referencectl` — it is not built yet.

## Invariants
- Pinned versions are law for every image this repo runs or references; CI and
  docs cite `versions.env`, never restate a tag.
- Empty `*_DIGEST` entries are filled by the release process, not by hand.
