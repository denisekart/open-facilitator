# Workflows

|Workflow / Job|Proves|Registry target|Completed by|
|---|---|---|---|
|`ci.yml` / `dotnet`|Solution builds + health-endpoint test on the pinned SDK (global.json)|—|Plan 00|
|`ci.yml` / `typescript`|Workspace lint + build + vitest on Node 22 (engines floor)|—|Plan 00; `@ofac/protocol` joins in Plan 01|
|`ci.yml` / `meta`|actionlint + yamllint + `reference/versions.env` key completeness|—|Plan 00|
|`ci.yml` / `docker-build`|Facilitator image compiles on every PR (no push)|—|Plan 00|
|`ci.yml` / `link-e2e`|SDK + linkd against a live Facilitator + Postgres service: handshake/supersede, delivery+dedupe, receipt-loss redelivery, partition buffering, HTTP-poll keeper revival, crash replay|—|Plan 04|
|`ci.yml` / `gateway-e2e`|Team Gateway against a live Facilitator + Postgres service: assign→echo-complete→inbox-processed, SIGKILL reconcile (lost + one retryable failed), graceful-restart duplicate, token rotation|—|Plan 05|
|`release.yml` / `publish-npm`|inert seam (`vars.ENABLE_NPM_PUBLISH` unset)|`https://npm.pkg.github.com/denisekart`|Plan 01 flip (`private: false` + publish)|
|`release.yml` / `containers`|Facilitator image → GHCR on `v*` tags (amd64+arm64)|`ghcr.io/denisekart/open-facilitator/facilitator`|gateway/workspace-link entries: Plans 04/05|
|`release.yml` / sbom + digest-pin|commented plan only|—|Plan 18 (spec §22.6)|
