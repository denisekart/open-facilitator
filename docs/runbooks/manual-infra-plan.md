# Manual infra plan (the legs demoted from CI)

**Why this exists.** The scenario gate (`make scenario-gate`) must run in CI
without a single external container, so the legs that need the compose
stack, the dev IdP port-map, or a clean working tree were demoted from every
`make test` to this recorded manual plan. Demoted is not deleted: **the law
still binds for any change that touches the reference env inputs**
(`reference/config/*`, `reference/keycloak/*`, `examples/**`,
`src/facilitator/Migrations/**`, the Dockerfiles, and the compose files).
Before landing such a change, run the legs below and record the date here.

## The legs

1. **Slim compose acceptance** — `make compose-slim`, then from a clean
   tree `./referencectl up slim --no-seed && ./referencectl verify`.
   Proves spec 21.1's hop chain (`reference/checks/verify.sh`) against the
   real stack: no product-code hooks, waits ride timeline kinds.
2. **Full compose provider legs** — `make compose-full`, then
   `make provider-e2e` (runs `reference/checks/verify-full.sh`: Mattermost +
   Forgejo + coding-worker + provisioning legs) and, with GitLab booted
   (`make gitlab-up`), `reference/checks/verify-gitlab.sh`.
3. **Link/replica tiers** — `make link-e2e` (workspace-link against a live
   kernel + dev IdP, `reference/checks/verify-replicas.sh` shape),
   `make worker-e2e`, and `make admin-e2e` (the browser tier over the SPA).
4. **Example smokes** — `make example-slim-up` and `make example-local-smoke`
   (the local template provisioning + `localctl` legs).

## Recording a run

Append one line per leg: date, tree SHA, leg, outcome, notes. A failed leg
blocks the landing of the change that broke it (the leg is the gate for
that input surface); the scenario gate stays green regardless — that
separation is the point of this file.

| Date | Tree SHA | Leg | Outcome | Notes |
|---|---|---|---|---|
| 2026-11-11 | (scenario-gap-closure branch tip) | — | recorded-not-run | the legs were demoted by the gap-closure plan; the next reference-env change must run them and fill this table |
