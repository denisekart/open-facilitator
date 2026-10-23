# Open Facilitator Framework

**Portable, pluggable, multi-workspace orchestration of autonomous, assisted, and deterministic workloads across independently operated execution environments.**

> **Status: greenfield.** The repository currently holds the specification and a tailored [omp](https://github.com/can1357/oh-my-pi) agent workspace. No framework code exists yet — everything named in the specs (modules, schemas, endpoints, CLIs, compose services) is a target, not a reality.

## The idea in one paragraph

Deploy one persistent **Facilitator** — a durable coordination kernel, not a privileged super-agent. Teams register a **Project**, connect their existing tools, and optionally attach **Gateways** or **Workspaces** that dial *out* to the Facilitator. The Facilitator handles external communication (chat threads, issues), durable work state, breakdown, routing, human approvals, governed memory, and artifact references. Teams keep execution authority: their harnesses (Pi, OpenCode, Codex, custom, deterministic), models, credentials, and every SCM/issue/deployment write stay team-side. Work is not code-shaped by default — research reports, datasets, documents, and test evidence are first-class outputs.

## Architecture at a glance

```mermaid
flowchart TB
  CHAT[Chat / Issues / Webhooks] -->|events| FAC
  subgraph FAC[Facilitator - deploy once]
    ING[Ingress + adapters] --> ROUTE[Routing + decisions]
    ROUTE --> WORK[Work coordinator]
    WORK <--> DB[(PostgreSQL - durable state)]
    WORK --> MEM[Memory + artifact broker]
    WORK --> HUB[Link hub + admin SPA]
  end
  HUB <--"outbound link (SignalR / HTTP)"--- GW[Team Gateway]
  HUB <---direct link--- W2[Direct workspace]
  GW --> W1[Workspace + harness adapter]
  GW --> W3[Workspace + different harness]
  GW -->|team-owned credentials| SCM[Git / issues / deploys]
```

Key properties, normatively defined in the specs:

- **Protocol, not product**: CloudEvents 1.0 + versioned JSON Schema + OpenAPI 3.1; SignalR is the reference transport, a plain HTTP long-poll fallback keeps every language first-class.
- **Recoverable by construction**: transactional inbox/outbox, leases, fencing generations, at-least-once + idempotent admission — nothing lives only in a socket or an ephemeral container.
- **Deterministic authority**: models and memory propose; only deterministic policy and verified identity evidence authorize.
- **Minimal footprint**: the slim reference is Facilitator + PostgreSQL + Keycloak + SeaweedFS + Aspire dashboard on a laptop. Kafka, Temporal, vector DBs, and models are optional adapters.

## Where to read more

| Document | Contents |
|---|---|
| [`docs/specs/open_facilitator_framework_architecture.md`](docs/specs/open_facilitator_framework_architecture.md) | Normative architecture: domain model, wire contract, scaling, packaging, identity, policy, adapters, memory, artifacts, data model, reference Compose, delivery slices, conformance suite |
| [`docs/specs/open-facilitator-appendix.md`](docs/specs/open-facilitator-appendix.md) | Normative clarifications: trust modes, evidence validation, approval canonicalization (RFC 8785), outbox/inbox patterns, memory ACLs, acceptance checklist |
| [`docs/protocol.md`](docs/protocol.md) | As-built v1 wire contract: envelope, type registry, namespacing/versioning rules, conformance fixtures, payload limits |
| [`AGENTS.md`](AGENTS.md) | Agent working rules: stack decisions, spec pointers, delegation routing, repository layout |

## Repository layout

Repo root **is** the spec §22.1 `framework/` tree (Plan 00). Every directory carries a README stating what lands there and in which plan:

```text
├── src/
│   ├── facilitator/       # .NET 10: /health stub now; HTTP, SignalR Link hub, event loop (Plans 02-03); admin SPA wwwroot embed (Plan 12)
│   ├── gateway/           # @ofac/gateway stub now; runtime, connectors, provisioner ports (Plan 05)
│   ├── workspace-link/    # @ofac/workspace-link stub now; Link runtime + SDK (Plan 04)
│   └── adapters/          # mattermost (13) / forgejo (14) / kafka (15) + harnesses/ (06+)
├── protocol/              # CloudEvents schemas, JSON Schema, OpenAPI, conformance fixtures (Plan 01)
├── features/              # Dev Container Features: gateway, workspace-link, harness-adapter (Plan 06)
├── examples/              # slim/deterministic-research-worker (06); full/coding-worker (14), research-worker + seed-repositories (14/15)
├── reference/             # versions.env + .env.example now; Compose, referencectl, seeds, checks (Plan 11)
├── docs/specs/            # normative architecture (single source of truth)
├── .github/workflows/     # CI + release spine (see workflows/README.md)
└── Makefile               # restore/build/test/lint/format + compose placeholders
```

Delivery is vertical slices with runnable exit tests (spec §26): transactional kernel → workspace link → gateway/adapters → identity/policy → artifacts/memory → slim reference → full providers → multi-work provisioning → horizontal replicas → hardening. Track via `.omp/plans/IMPLEMENTATION_ROADMAP_PLAN.md`.

## Technology stack

| Area | Choice |
|---|---|
| Facilitator | .NET 10, ASP.NET Core, SignalR, EF Core, OTLP |
| Gateway / Link / adapters | TypeScript on Node.js, shipped as npm packages, Dev Container Features, OCI images |
| Admin UI | Svelte 5 + Tailwind, embedded single-page app |
| Durable state | PostgreSQL 17 (also the default memory search backend via FTS) |
| Identity | OIDC/OAuth 2.0 — Keycloak in the reference, provider-swappable |
| Reference infra | Docker Compose (slim/full), SeaweedFS artifacts, Aspire dashboard, Mattermost, Forgejo, Kafka, Ollama |

## Development environment

1. **Recommended: devcontainer.** Open the repo in the included `.devcontainer` — Docker-in-Docker, .NET SDK 10, Node 24, pnpm 12 preinstalled; nothing to install.
2. **Manual path:** .NET SDK per `global.json` (`10.0.40x`), Node ≥ 22, then `corepack enable && corepack install` (honors `packageManager: pnpm@12.5.1`). Docker is optional, for image builds.
3. **Daily loop:** `make restore` → `make build` → `make test` → `make lint`; `make format` before committing.
4. **Running tests:** .NET — `dotnet test OpenFacilitator.slnx [-c Release] [--filter FullyQualifiedName~HealthEndpointTests]`; TypeScript — per package `pnpm --filter @ofac/gateway test`, workspace-wide `pnpm -r test`, single file `pnpm --filter @ofac/workspace-link exec vitest run test/package.test.ts`. Behavior/integration tests (Testcontainers, dockerized Facilitator) appear from Plan 02 onward.
5. **CI is the merge gate** — see [`.github/workflows/README.md`](.github/workflows/README.md). Release = push a `v*` tag → images to GHCR.

This repo is also an omp agent workspace: project skills in `.omp/skills/` encode the spec rules agents must not violate (every implementation slice updates the matching skill in the same change), MCP servers are declared in `.omp/mcp.json`, plans land in `.omp/plans/`, and demo secrets are never committed (`.env`, `secrets/` are gitignored).

Once the slim reference environment exists (Plan 11), the demo entry point will be:

```bash
cd reference && cp .env.example .env
./referencectl up slim && ./referencectl seed slim && ./referencectl verify slim
```

## License

To be chosen by the maintainers; the spec targets open-source distribution of all framework modules.
