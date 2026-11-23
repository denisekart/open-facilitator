# Dev Container Features (`features/`)

Six Dev Container Features that inject the team-plane runtime into any
team-owned container; read this when you compose a workspace container or
consume a Feature from an image instead of the repo source. Normative area
rules (install contract, supervisor law, option/state invariants) are
`AGENTS.md`; each Feature's own options, env surface, and restart law are in
its README.

## The six Features

|Feature|Installs|Readme|
|---|---|---|
|`gateway`|`ofac-gateway` — team-owned Gateway + supervisor unit|[src/gateway/README.md](src/gateway/README.md)|
|`workspace-link`|`ofac-linkd` — direct Link endpoint, no Gateway|[src/workspace-link/README.md](src/workspace-link/README.md)|
|`harness-adapter`|`ofac-cli-worker` — playbook-driven Gateway child|[src/harness-adapter/README.md](src/harness-adapter/README.md)|
|`omp-adapter`|`ofac-omp-worker` — omp CLI Gateway child|[src/omp-adapter/README.md](src/omp-adapter/README.md)|
|`codex-adapter`|`ofac-codex-worker` — Codex CLI Gateway child|[src/codex-adapter/README.md](src/codex-adapter/README.md)|
|`forgejo-provider`|`ofac-forgejo-connector` — forge-ops Gateway child|[src/forgejo-provider/README.md](src/forgejo-provider/README.md)|

Each directory also holds `devcontainer-feature.json` + `install.sh`, and
`test/<name>/{scenarios.json,<scenario>.sh}` for the Feature CLI scenarios.

## Consuming a published Feature

Publishing is OCI: each Feature ships as an OCI image tagged with its semver
plus a rolling major, so a `devcontainer.json` points at

```
ghcr.io/denisekart/open-facilitator/features/<name>:1
```

(`release.yml`'s features job builds `features/src` with the devcontainer CLI
against `--registry ghcr.io --namespace denisekart/open-facilitator/features`
and moves the `:1` tag). The repo-source coordinates `./features/src/<name>`
work identically for local composition — see
`../examples/slim/cli-research-worker/.devcontainer`. A Feature
version is not a protocol major.

## Build and test

```bash
node features/build.mjs   # generates the pkg/ payloads (tarball + sha256 + supervisor) the images carry
npx --yes @devcontainers/cli@0.90.0 features test -p features   # scenario tests (ubuntu+debian only)
```

Install-time needs no npm registry and no network; the supervisor entrypoint
runs the per-Feature units in numeric-prefix order.
