# Plan 08 — Dev Container Feature family + slim example workspace

**Status:** ⬜ not-started — implementation begins ONLY after its replanning gate flips this to 🔵.
**Phase:** B (closer). **Depends on:** 07 (HarnessAdapter contract + deterministic worker 🟢).
**Part of:** roadmap entry 06 (split): the packaging half. Completes Phase B.

## Normalization from prior plans (assumed state)

Plans 05–07 produce three real, long-running processes: Gateway (embedded+shared+standalone), deterministic worker as a Gateway child via the adapter contract, and the dev Facilitator (+ Postgres 17). `linkd` exists as the no-Gateway minimal runtime. The `link-e2e`/`gateway-e2e` CI jobs already spawn Facilitator+postgres per run. This plan packages 05–07's reality — it introduces no new runtime behavior, only distribution.

## Objective

Ship the Dev Container Feature family — `features/gateway`, `features/workspace-link`, `features/harness-adapter` — per §8.2/§8.5, so that *adding one Feature to an existing devcontainer yields a linked, headless workspace* (spec §28's headline promise). Publish them OCI-first (GHCR, namespace pinned in `reference/versions.env` as `OCI_REGISTRY`/`…/features/<name>`), version decoupled from protocol major. Ship `examples/slim/deterministic-research-worker/` as the composed example devcontainer (replacing Plan 07's plain-process README wiring with the Feature-based one, keeping the process walkthrough as the "no Docker" alternative).

## Replanning gate (mandatory initial research)

1. Dev Container Features spec (devcontainers.com) — current schema: `feature.json` layout, `options`, `containerEnv`, entrypoint/mutability rules, **multiple-Features-in-one-image behavior**, lifecycle hooks; `@devcontainers/cli` current flags for `up`/`build`/`features print`.
2. §8.2 (headless supervisor startup law — no TTY dependency, no first-use secrets at image-build), §8.4 (team layout: where a workspace's config/manifest/state-dir live so Features + Plan 05 config format agree), §8.5 (Feature versioning + OCI publishing rules), appendix §11 (one-command dev loop expectations).
3. As-built: Plan 05 config/state-dir conventions, Plan 06 child-transport socket paths (the Features must mount the Gateway↔child socket in one container for `shared` mode, or document sidecar layout), Plan 07 adapter env expectations.
4. Decide: supervisor mechanism for headless start (boring option: the Feature installs a `linkd`-style supervisor script + documented entrypoint composition; no new process manager unless the gate proves a need), Feature↔package install source (npm from GHCR — requires the `@ofac/*` publish flip, currently `private: true` with `publishConfig` staged in Plan 00; either flip publish now for `@ofac/protocol|workspace-link|gateway|adapter`, or install from packed tarballs during CI in this plan and defer publish — gate decides, publish flip preferred since consumers outside the repo need it), Feature test-matrix OSes (Ubuntu + Debian minimum; Alpine only if `better-sqlite3` prebuilds exist there — Plan 04 learned the prebuild reality, record it).

## Scope

- Three Feature directories with `feature.json` (id, version, options: `mode`, `endpointId`, `facilitatorUrl` — no secret values in options, env-var indirection only), `install.sh` (idempotent, no network at `devcontainer build` beyond package fetch, amd64+arm64), README each.
- Startup: headless supervisor wiring Gateway/worker per Feature options; logs to a known path; graceful shutdown.
- `examples/slim/deterministic-research-worker/`: `.devcontainer/` combining the three Features + example manifest/config from Plan 07; README with expected-output walkthrough.
- CI: Feature matrix job — `devcontainer build` per Feature (Ubuntu, Debian) + a bounded `devcontainer up` smoke of the example against a Facilitator service (mirror `link-e2e` service pattern; time-box the job).
- `release.yml`: Feature OCI publish steps (publish-npm flip job goes live if the gate chose publish); digest-pinning stays Plan-era-20 (hardening).
- `Makefile`/docs hooks: `example-slim-up` convenience target (pointer to devcontainer-cli).

## State after implementation

A contributor copies `examples/slim/deterministic-research-worker/`, `devcontainer up`, and the workspace auto-enrolls, connects outbound, idles ready for a `work.assign` (full acceptance loop still needs the slim Compose env — roadmap-era 13 — but link+handshake+echo-capable ready state proves here). §8's packaging story exists as implemented artifacts; every Phase B component is distributable without a repo clone.

## Testing

- Feature matrix CI job: install succeeds Ubuntu/Debian × amd64/arm64 (build-only where full smoke is too slow — record which tier each OS runs).
- Idempotency: install twice ⇒ no error, same state (script-level assertion).
- Smoke e2e: example devcontainer boots → Gateway diagnostics + Facilitator `link_sessions` show a live session → seeded assign completes (reuse the Plan 07 lifecycle test kit inside the container).
- No new wire types/fixtures expected.

## Documentation

- `docs/getting-started.md`: "connect an existing agent by adding one Feature" section per spec §28 (exact `devcontainer.json` snippet + expected output); example README.
- Feature READMEs (options table, env vars, troubleshooting, log locations).
- `.omp/skills/facilitator-workspace-packaging`: Features → implemented reality (registry coordinates, versions, install contract).

## Architecture fit

§8 entirely: packaging makes workspaces disposable and team-side; the Features are how a team adopts the framework without owning the runtime internals — they install/admit/launch 05–07's binaries and nothing else. This closes Phase B: after this plan the team-owned execution side (Gateway ↔ children ↔ adapters ↔ packaged workspaces) is real, and Phase C (identity/policy) starts replacing the interim trust assumptions (bootstrap tokens, deny-by-default stub) that 02/03/05 deliberately left as single seams.

## Additional resources

- Spec §8.2/§8.4/§8.5, §28; appendix §11; `.omp/skills/facilitator-workspace-packaging` + `facilitator-reference-env` (versions.env/OCI registry conventions).
- As-built: 05 config conventions, 06 modes, 07 example + lifecycle kit, Plan 00's `release.yml` publish seams, Plan 04's better-sqlite3 prebuild experience.
- External: Dev Container Features spec + `@devcontainers/cli` docs; GHCR OCI artifact publishing (features are OCI image artifacts).
