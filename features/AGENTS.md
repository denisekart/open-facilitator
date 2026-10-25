# features/

## Purpose
Dev Container Features (`src/gateway`, `src/workspace-link`,
`src/harness-adapter`) that inject a Gateway or Workspace Link runtime into
any team-owned container, published as OCI images
(`ghcr.io/denisekart/open-facilitator/features/<name>:1`).

## Layout & install contract
- `src/<name>/{devcontainer-feature.json,install.sh,README.md}` + generated
  `src/<name>/pkg/` (tarball + sha256 + supervisor; gitignored, built by
  `build.mjs` via `pnpm deploy --prod` — no npm registry or network at
  container-install time).
- Tests: `test/<name>/{scenarios.json,<scenario>.sh}` (`features test -p
  features`); ubuntu+debian only — better-sqlite3 musl prebuilds unverified.
- Each install.sh: node>=22 gate, checksum-verify + extract to
  `/usr/local/lib/openfacilitator/<name>`, PATH shim, idempotent via
  `.installed-version`.
- One supervisor (`shared/ofac-supervisor.sh`, staged into each `pkg/`) is
  every Feature's `entrypoint`; units `units/{10-gateway,20-linkd,30-harness-adapter}.sh`
  carry baked option defaults, numeric-prefix order, per-runtime
  restart-vs-terminal law, and forward TERM to the runtime child. No
  lifecycle hooks (spec §8.2: never rely on them).

## Invariants
- Options are startup-time defaults baked into the unit; the runtime
  env/config surface (`OFAC_*`, `GATEWAY_CONFIG`, `WORKER_CONFIG`) is the
  truth. Secrets are never options.
- Features never own state; configs point `stateDir`/`workDir` at the
  workspace mount.
- Feature version != protocol major (spec §8.5); tags come from
  `release.yml`'s features job.

## Truth anchors
- `../.omp/skills/facilitator-workspace-packaging/SKILL.md` — provisioning rules.
- `../docs/specs/open_facilitator_framework_architecture.md` §8 — requirements.
- `../examples/slim/deterministic-research-worker/.devcontainer` — composed example.
