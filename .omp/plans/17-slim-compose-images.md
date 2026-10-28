# Plan 17 — Slim reference environment, leg 1: images and kernel-side Compose stack
**Status:** ⬜ not-started (flips 🔵 at the replanning gate, then 🟡)
**Depends on:** 08, 09, 13, 14, 15, 16
**Phase E leg 1 of 5.** Chain: 17 → 18 → 19 (reference env), 20 → 21 (operator surface).

## Objective
Stand up the kernel half of spec §21.1's slim profile as real, tested infrastructure:
container images for every framework runtime that needs one, and
`reference/compose.slim.yaml` booting PostgreSQL + Keycloak + SeaweedFS + Aspire
dashboard + the Facilitator container, wired from `reference/versions.env` and
`reference/.env.example` verbatim, with healthchecks. After this plan
`docker compose up` gets a healthy, authenticated, S3-backed, OTLP-exporting
Facilitator — no Gateway/worker yet (that is Plan 18).

## Known facts (as-built anchors; code wins, divergence goes in the status table)
- Only one Dockerfile exists: `src/facilitator/Dockerfile` (multi-stage, aspnet:10.0, EXPOSE 8080). No images for gateway, workspace-link/linkd, or the deterministic worker; everything TS runs from source, Features, or `node dist/`.
- `release.yml` `containers` job publishes only the facilitator image (amd64+arm64); a comment at `release.yml:23-25` says gateway/workspace-link entries are "to be added". CI `docker-build` job builds the kernel image per PR but never pushes.
- `reference/versions.env` pins `POSTGRES_TAG=17-alpine`, `KEYCLOAK_TAG=26.7.5`, `SEAWEEDFS_TAG=4.48`, `ASPIRE_TAG=13.4.2` (+ full-profile pins), `OCI_REGISTRY=ghcr.io/denisekart/open-facilitator`, nine empty `*_DIGEST` keys; CI `meta` job enforces key completeness. No compose file consumes any of it yet; `make compose-slim|compose-full` are echo + `exit 2` placeholders (`Makefile:67-69`).
- No compose is used by any test tier today: TS e2e spawns the kernel from `dotnet` binaries against external Postgres/Keycloak; .NET tests use Testcontainers (Postgres, Keycloak importing `reference/keycloak/realm.json`, SeaweedFS 4.48 generic container, bucket `ofac-test`).
- Kernel OTLP export is config-only: exporters activate when `Otel:Endpoint` is set (`Kernel/KernelServiceCollection.cs:192-209`). `/health` exists anonymous (`Program.cs:27`).
- S3 backend fully implemented (`Storage:Provider=s3`, `Storage:S3.{Endpoint,Bucket,Region,AccessKey,SecretKey,ForcePathStyle}`), exercised only by the .NET fixture; dev default is the filesystem provider.
- Keycloak realm `reference/keycloak/realm.json` imports cleanly on 26.7.5 `start-dev --import-realm` (Plan 11 fixed the group-mapper `claim.name` gap; KC 26 needs `jsonType.label: JSON` for boolean delegation claims).
- `GET /api/dead-letters` (newest-50) is the only admin-ish read surface; no `/api/admin/*` routes exist.
- Spec §22.2 ships a slim Compose template (`docs/specs/…architecture.md:1530-1627`) grounded in official container interfaces; §22.5 fixes loopback `.localhost` hostnames.

## Locked decisions (from planning; replanning may only amend with recorded reason)
- **Image shape: source-built.** New `Dockerfile`s for gateway and linkd/worker on slim `node:22`, built from this tree like the kernel Dockerfile. No dependency on the inert npm-publish flip. `release.yml` gains the gateway image entry (replacing the stale "to be added" comment); worker rides the gateway image or its own — decided at the gate.
- **Compose consumes `versions.env` + `.env.example` verbatim** via `env_file`/`${VAR:?}`; zero hardcoded tags or registry hosts.
- **Aspire = standalone dashboard container** (`ASPIRE_TAG`), no AppHost project; kernel+gateway get `Otel:Endpoint` pointing at its OTLP port.
- Healthchecks on every service; `depends_on: condition: service_healthy` ordering.
- This plan does **not** ship `referencectl` or seeds (Plan 18) nor acceptance verify (Plan 19).

## Steps
0. **Research (mandatory):** re-verify §22.2 template against the actual config surface of 02–16 (`appsettings` shapes, `ArtifactOptions`, realm clients, `Link:Teams` roster); verify SeaweedFS `weed mini` vs the fixture's `server -s3` invocation and the S3 config-file mount shape; verify the Aspire standalone dashboard image's OTLP ingest ports; verify Keycloak 26.7.5 hostname env (`KC_HOSTNAME` behavior per §22.5).
1. `src/gateway/Dockerfile` + `src/workspace-link/Dockerfile` (or one node image with two entrypoints), dist-only layer, non-root, 8080-style HTTP ports per the gateway diagnostics surface.
2. `reference/compose.slim.yaml` per §22.2: postgres (+`./postgres-init`), keycloak (`start-dev --import-realm`, `KC_HOSTNAME=http://id.localhost:8080`), seaweedfs (`weed mini` or fixture-proven invocation, bucket init), aspire-dashboard, facilitator (`Storage:Provider=s3`, `Otel:Endpoint`, Keycloak authority, `/health` healthcheck).
3. `reference/config/facilitator.yaml` (mounted kernel config), postgres-init SQL (databases for facilitator + keycloak), seaweedfs s3 config json — replacing the placeholder README stubs.
4. Makefile: `compose-slim`/`compose-full` become real (full = overlay file exists but may only pin services until the full-profile plans). Replace "Lands with Plan 11" prose in the touched stubs with factual statements (no plan numbers per the cross-plan rule).
5. Release/CI: gateway image entry in `release.yml` `containers` job; PR `docker-build` extended to build (not push) the node image.
6. Local proof: `make compose-slim` boots healthy on WSL arm64; kernel `/health` 200, realm imported, bucket exists, OTLP spans land in dashboard (`/api/v0` health or logs).

## State after
Fresh clone → `make compose-slim` (after `.env` copy) → healthy slim kernel stack with S3 artifacts, OIDC, OTLP→Aspire. Images buildable and CI-gated; release publishes a gateway image.

## Testing
- Image builds gated in CI (extend `docker-build`).
- Bring-up smoke in CI (new `ref-env-bringup` job, per-tier decision below): `make compose-slim` → all services healthy → `/health` 200 → Keycloak well-known reachable → `GET /api/dead-letters` answers with a kernel token. Teardown. PR-tier only (bring-up + healthchecks); no acceptance run yet.
- Provider behavior (SeaweedFS S3 semantics) stays covered by the existing .NET fixture matrix; compose adds no new S3 tests.

## CI tier (locked)
PR gate = bring-up + healthchecks. Full `referencectl verify` (Plan 19) runs on main-push + nightly, linux/arm64 + amd64.

## Documentation
- `reference/README.md` written for real (port map, `.env`, up/down, what each service is).
- Fix the stale "Plan 11" landing prose in `reference/*/README.md`, `Makefile`, `docs/getting-started.md` header, `docs/contributing.md` — rewrite as statements of fact.
- `.omp/skills/facilitator-reference-env` updated to as-built (AGENTS.md maintenance rule).

## Architecture fit
§22.1/§22.2/§22.4 — the static reference skeleton gets its runtime half. §21.1's kernel-side services first; the workspace-side services and the single-command story are Plans 18–19.

## Assumptions & contingencies (replanning caveat)
- If `weed mini` diverges from the fixture-proven `server -s3` flags, keep the fixture invocation (code-proven) and record the deviation.
- If the Aspire dashboard image's ARM64 tag lags `ASPIRE_TAG`, re-pin and note it in `versions.env`.
- Cross-container child-transport shape (06's contingency: TCP `0.0.0.0` bind vs sidecar socket volume) is deferred to Plan 18, where gateway+worker enter compose.
- Replanning or optimization may be required before implementation begins; implementation begins with additional research (Step 0).
- **Questions may be asked during planning, but not during implementation.**

## Implementation Status
| Step | Status | Notes |
|---|---|---|
| 0 Research | Not started | |
| 1 Node images | Not started | |
| 2 compose.slim.yaml | Not started | |
| 3 config/init/seeds-input | Not started | |
| 4 Makefile cutover | Not started | |
| 5 release/CI images | Not started | |
| 6 Local proof | Not started | |

## Verification
`make compose-slim` healthy + the CI `ref-env-bringup` job green + `make docs-check`/`make lint` green.
