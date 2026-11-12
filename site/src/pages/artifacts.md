# Artifacts & installation

Everything ships as published, versioned artifacts. No source builds
required, no registry login required for the base image stream —
consume them like any other container image or package.

## The kernel image

```bash
docker pull ghcr.io/denisekart/open-facilitator/facilitator:v0.1.2
```

One stateless container, multi-architecture (amd64 + arm64), tagged per
release and pinned by digest in every release's provenance. It needs
three things to start, all injected as configuration — and it is
deliberately fail-closed: missing or ambiguous config stops startup with
a readable error instead of guessing defaults:

| Needs | How |
|---|---|
| PostgreSQL | `ConnectionStrings__Facilitator` — schema migrations run at startup |
| An OIDC provider | config file block: authority, audience, and the claim mapping your provider emits |
| S3-compatible object storage | `Storage__S3__*` env block (`Storage__Provider=filesystem` + `Storage:FileSystem:RootPath` for a toy) |

```bash
docker run -d -p 8080:8080 \
  -e ConnectionStrings__Facilitator="Host=db;Database=facilitator;Username=ofac;Password=…" \
  -e Storage__Provider=s3 \
  -e Storage__S3__Endpoint=https://s3.example \
  -e Storage__S3__Bucket=facilitator \
  -e Storage__S3__AccessKey=… -e Storage__S3__SecretKey=… \
  -v $PWD/facilitator.json:/app/config/facilitator.json:ro \
  ghcr.io/denisekart/open-facilitator/facilitator:v0.1.2
```

Serving that URL is your whole control plane: the console and the API
share the origin. Scale by running more copies against the same
PostgreSQL — the coordination is in the database, not in the process.

## Node packages (team side)

From the `@ofac` scope on npm:

| Package | What you get |
|---|---|
| `@ofac/gateway` | the Gateway runtime (`ofac-gateway`) — one process, many workspaces |
| `@ofac/workspace-link` | Link SDK + `ofac-linkd` supervisor for custom runtimes |
| `@ofac/protocol` | typed contract: envelope builders + schema validators for your own services |
| `@ofac/adapter` | harness-adapter SDK + conformance kit |
| `@ofac/harness-omp`, `@ofac/harness-codex`, `@ofac/harness-deterministic` | ready-made harnesses |
| `@ofac/mattermost-adapter`, `@ofac/telegram-adapter` | chat provider services |
| `@ofac/github-adapter` + `@ofac/github-connector` (and `forgejo-*`, `gitlab-*`) | forge pairs: inbound normalization + approval-gated outbound writes |
| `@ofac/provisioning-oci` | workspace-provisioning launcher |

Everything is public: `npm install` works anywhere, without registry
auth. Pin the version you consume; upgrades are your decision.

## Dev Container Features

Turn any team container into a linked workspace with one line of
`devcontainer.json` — OCI images on the same GHCR namespace, semver
tagged (`:1.0.3` and major alias `:1`):

```jsonc
"features": {
  "ghcr.io/denisekart/open-facilitator/features/gateway:1": {},
  "ghcr.io/denisekart/open-facilitator/features/workspace-link:1": {},
  "ghcr.io/denisekart/open-facilitator/features/harness-adapter:1": {}
}
```

Available features: `gateway`, `workspace-link`, `harness-adapter`,
`omp-adapter`, `codex-adapter`, `forgejo-provider` — composable, each
idempotent, supervised with crash-restart by the feature supervisor.

## The node-runtime base

`ghcr.io/denisekart/open-facilitator/node-runtime` is the recommended
base image for team-side services (Gateway, adapters, workers): the
supervisor, log rotation, and credential-broker client are prebaked.
Building your team image on it is convenience, not coupling — anything
that speaks the wire works.

## Release discipline you can rely on

- Every release cuts from a tag, builds multi-arch, and attaches SBOMs.
- Third-party base images are digest-pinned by the pipeline.
- The wire contract is versioned in its schema names
  (`org.facilitator.*.v1`): breaking changes get a new version segment,
  never a silent edit — two independent implementations must pass the
  same conformance fixtures before a change lands.
- The acceptance suite that gates every release is the same one you can
  run yourself ([guided tour](/docs/getting-started)).

## Current release

The pipeline publishes on every version tag: multi-arch images (amd64 +
arm64) and semver-tagged features, `latest` tracking the newest release.
The current release line is `v0.1.2` — pin a tag or a digest, never
`latest`, in anything you operate.
