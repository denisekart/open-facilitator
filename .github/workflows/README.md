# Workflows

|Workflow / Job|Proves|Registry target|Completed by|
|---|---|---|---|
|`ci.yml` / `dotnet`|Solution builds + health-endpoint test on the pinned SDK (global.json)|—|Plan 00|
|`ci.yml` / `typescript`|Workspace lint + build + vitest on Node 22 (engines floor)|—|Plan 00; `@ofac/protocol` joins in Plan 01|
|`ci.yml` / `meta`|actionlint + yamllint + `reference/versions.env` key completeness|—|Plan 00|
|`ci.yml` / `docker-build`|Facilitator image compiles on every PR (no push)|—|Plan 00|
|`ci.yml` / `protocol`|Codegen drift gate (generated TS + .NET + schemas) + OpenAPI stub validates|—|Plan 01|
|`ci.yml` / `ref-env-bringup` **(CI-disabled)**|Slim reference env boots authenticated, S3-backed, OTLP-exporting. Commented out in `ci.yml` (free-tier compute); **runs locally and is acceptance-required** — see `reference/AGENTS.md`|—|Plan 19|
|`ci.yml` / `ref-env-verify` **(CI-disabled)**|Acceptance run (`referencectl verify`, 11 steps) unattended on a booted-but-unseeded slim stack, amd64+arm64 matrix. Commented out in `ci.yml`; **runs locally and is acceptance-required**|—|Plan 19|
|`ci.yml` / `link-e2e`|SDK + linkd against a live Facilitator + Postgres service: handshake/supersede, delivery+dedupe, receipt-loss redelivery, partition buffering, HTTP-poll keeper revival, crash replay|—|Plan 04|
|`ci.yml` / `gateway-e2e`|Team Gateway against a live Facilitator + Postgres service: assign→echo-complete→inbox-processed, SIGKILL reconcile (lost + one retryable failed), graceful-restart duplicate, token rotation|—|Plan 05|
|`ci.yml` / `features`|Feature install contract per arch (amd64+arm64): ubuntu+debian scenarios per Feature + a combo scenario composing all three (PATH contract, unit files, idempotent rebuild)|—|Plan 08|
|`ci.yml` / `example-smoke`|Composed slim example end-to-end: Facilitator service + devcontainer with all three Features, ready lines, seeded assign → completion in inbox, live child on `/status`|—|Plan 08|
|`release.yml` / `publish-npm`|inert seam (`vars.ENABLE_NPM_PUBLISH` unset)|`https://npm.pkg.github.com/denisekart`|Plan 01 flip (`private: false` + publish)|
|`release.yml` / `containers`|Facilitator image → GHCR on `v*` tags (amd64+arm64)|`ghcr.io/denisekart/open-facilitator/facilitator`|gateway/workspace-link entries: Plans 04/05|
|`release.yml` / `features`|Dev Container Features → GHCR OCI on `v*` tags (`:1.0.0` + `:1` major tags)|`ghcr.io/denisekart/open-facilitator/features/<name>`|Plan 08|
|`release.yml` / sbom + digest-pin|commented plan only|—|Plan 18 (spec §22.6)|
