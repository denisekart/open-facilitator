# Workflows

| Workflow / Job | Proves | Registry target |
|---|---|---|
| `ci.yml` / `dotnet` | Solution builds + health-endpoint test on the pinned SDK (global.json) | — |
| `ci.yml` / `typescript` | Workspace lint + build + vitest on Node 22 (engines floor) | — |
| `ci.yml` / `meta` | actionlint + yamllint + `reference/versions.env` key completeness | — |
| `ci.yml` / `docker-build` | Facilitator and node-runtime images compile on every PR (no push) | — |
| `ci.yml` / `protocol` | Codegen drift gate (generated TS + .NET + schemas) + OpenAPI stub validates | — |
| `ci.yml` / `conformance` **(CI-disabled)** | Config-driven kernel conformance runner (items 1-5) + the foreign-shape link client against a live slim stack; JSON report uploaded as an artifact. Commented out in `ci.yml` — the first hosted run died on the non-executable `referencectl` (committed mode 100644); the exec bit is now recorded in git, uncomment to re-enable | — |
| `ci.yml` / `ref-env-bringup` **(CI-disabled)** | Slim reference env boots authenticated, S3-backed, OTLP-exporting. Commented out in `ci.yml` (free-tier compute); **runs locally and is acceptance-required** — see `reference/AGENTS.md` | — |
| `ci.yml` / `ref-env-verify` **(CI-disabled)** | Acceptance run (`referencectl verify`, 11 steps) unattended on a booted-but-unseeded slim stack, amd64+arm64 matrix. Commented out in `ci.yml`; **runs locally and is acceptance-required** | — |
| `ci.yml` / `link-e2e` | SDK + linkd against a live Facilitator + Postgres service: handshake/supersede, delivery+dedupe, receipt-loss redelivery, partition buffering, HTTP-poll keeper revival, crash replay | — |
| `ci.yml` / `gateway-e2e` | Team Gateway against a live Facilitator + Postgres service: assign→echo-complete→inbox-processed, SIGKILL reconcile (lost + one retryable failed), graceful-restart duplicate, token rotation | — |
| `ci.yml` / `admin-e2e` | Embedded admin SPA (Playwright chromium) against a real-Kestrel kernel on Testcontainers Postgres+Keycloak: anonymous redirect, identity chip, search→inspector→timeline, links online/offline, dead-letter replay + ledger notice, config PATCH + 409 conflict | — |
| `ci.yml` / `features` | Feature install contract per arch (amd64+arm64): ubuntu+debian scenarios per Feature + a combo scenario composing all three (PATH contract, unit files, idempotent rebuild) | — |
| `ci.yml` / `example-smoke` | Composed slim example end-to-end: Facilitator service + devcontainer with all three Features, ready lines, seeded assign → completion in inbox, live child on `/status` | — |
| `release.yml` / `containers` | Facilitator + node-runtime images → GHCR on `v*` tags (amd64+arm64) | `ghcr.io/denisekart/open-facilitator/{facilitator,node-runtime}` |
| `release.yml` / `features` | Dev Container Features → GHCR OCI on `v*` tags (`:1.0.0` + `:1` major tags) | `ghcr.io/denisekart/open-facilitator/features/<name>` |
| `release.yml` / `publish-npm` | inert seam (`vars.ENABLE_NPM_PUBLISH` unset) | `https://npm.pkg.github.com/denisekart` |
| `release.yml` / `sbom` | `dotnet-CycloneDX` SBOM for the solution + pnpm's native lockfile SBOM for the workspace, attached to the GitHub release | — |
| `release.yml` / `digest-pin` | Resolves pushed + third-party image digests and commits changed `*_DIGEST` keys back to `reference/versions.env` (also runs on `workflow_dispatch` for a re-pin) | — |
