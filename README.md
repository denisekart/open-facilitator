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
| [`AGENTS.md`](AGENTS.md) | Agent working rules: stack decisions, spec pointers, delegation routing, planned repository layout |

## Planned repository layout

Outline from spec §22.1 — created as implementation slices land, not before:

```text
framework/
├── src/
│   ├── facilitator/       # .NET 10: HTTP, SignalR Link hub, event loop, admin SPA
│   ├── gateway/           # TypeScript runtime, connectors, provisioner ports
│   ├── workspace-link/    # TypeScript Link runtime + SDK
│   └── adapters/          # mattermost / forgejo / kafka + harnesses/
├── protocol/              # JSON Schema, OpenAPI, CloudEvents schemas, conformance fixtures
├── features/              # Dev Container Features: gateway, workspace-link, harness-adapter
├── examples/              # deterministic research worker, coding worker, research worker
├── reference/             # slim + full Compose, versions.env, seeds, bootstrap, checks
└── Makefile
```

Delivery is vertical slices with runnable exit tests (spec §26): transactional kernel → workspace link → gateway/adapters → identity/policy → artifacts/memory → slim reference → full providers → multi-work provisioning → horizontal replicas → hardening.

## Technology stack

| Area | Choice |
|---|---|
| Facilitator | .NET 10, ASP.NET Core, SignalR, EF Core, OTLP |
| Gateway / Link / adapters | TypeScript on Node.js, shipped as npm packages, Dev Container Features, OCI images |
| Admin UI | Svelte 5 + Tailwind, embedded single-page app |
| Durable state | PostgreSQL 17 (also the default memory search backend via FTS) |
| Identity | OIDC/OAuth 2.0 — Keycloak in the reference, provider-swappable |
| Reference infra | Docker Compose (slim/full), SeaweedFS artifacts, Aspire dashboard, Mattermost, Forgejo, Kafka, Ollama |

## Working in this workspace

This repo is set up as an omp agent workspace:

- **Project skills** in `.omp/skills/` (`facilitator-architecture`, `facilitator-protocol`, `facilitator-workspace-packaging`, `facilitator-reference-env`) encode the spec rules agents must not violate. They are spec-derived projections of unbuilt code — every implementation slice updates the matching skill in the same change.
- **MCP servers** in `.omp/mcp.json`: Playwright for admin-UI/reference-service E2E; a read-only Postgres inspector, enabled once the slim reference environment is seeded.
- **Plans** land in `.omp/plans/`; demo secrets and generated credentials are never committed (`.env`, `secrets/` are gitignored).

Once the reference environment exists, the entry point will be:

```bash
cd reference && cp .env.example .env
./bootstrap/generate-secrets.sh
./referencectl up slim && ./referencectl seed slim && ./referencectl verify slim
```

## License

To be chosen by the maintainers; the spec targets open-source distribution of all framework modules.
