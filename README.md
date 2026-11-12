# Open Facilitator

**Portable, pluggable, multi-workspace orchestration of autonomous, assisted, and deterministic workloads — across independently operated execution environments.**

Deploy one persistent **Facilitator**: a durable coordination kernel, not a
privileged super-agent. Teams register a **Project**, connect their existing
tools, and attach **Gateways**/**Workspaces** that dial *out* to the
Facilitator. The Facilitator owns external communication (chat threads,
issues), durable work state, breakdown, routing, human approvals, governed
memory, and artifact references. Teams keep execution authority: their
harnesses, models, credentials, and every SCM/issue/deployment write stay
team-side. Work is not code-shaped by default — research reports, datasets,
documents, and test evidence are first-class outputs.

> The slim and full reference environments run end-to-end:
> `./referencectl up slim` boots the whole stack and
> `./referencectl verify` proves every hop unattended.
> All documentation lives in [`docs/`](docs/README.md).

## Why

- **Your execution stays yours.** No vendor lock-in, no credential
  surrender: the kernel coordinates, team-side harnesses run.
- **Recoverable by construction.** Transactional inbox/outbox, leases,
  fencing generations, at-least-once delivery with idempotent admission —
  nothing lives only in a socket or an ephemeral container. A kernel
  restart mid-approval resumes exactly.
- **Deterministic authority.** Models and memory propose; only deterministic
  policy and verified identity evidence authorize. Every gate decision —
  allow and deny — is audited.
- **Protocol, not product.** CloudEvents 1.0 + versioned JSON Schema +
  OpenAPI 3.1. SignalR is the reference transport; a plain HTTP fallback
  keeps every language first-class. A conformance suite with cross-language
  fixtures gates the wire.
- **Human-in-the-loop is a first-class citizen.** Approvals are durable,
  digest-pinned, single-use records with lazy expiry — not a chat poll.
- **Minimal footprint.** The slim reference is Facilitator + PostgreSQL +
  Keycloak + an S3 store + a dashboard on one laptop. Kafka, Temporal,
  vector DBs, and LLMs are optional adapters, never requirements.

## How it works

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

1. An event (chat mention, issue label, API call) creates a **Work** — a
   durable objective with a lifecycle, timeline, and approvals.
2. The kernel routes it to a capable **Workspace**, reached over an
   *outbound* Link: the team's Gateway authenticates, advertises its
   children's capabilities, and receives assignments.
3. A harness adapter (deterministic, oh-my-pi, Codex, or your own via
   `@ofac/adapter`) executes team-side; progress, artifacts, and results
   flow back as signed protocol events.
4. Policy held by the *team* decides what needs a human **approval** before
   anything privileged happens. Memory and artifacts are brokered, scoped,
   and ACL'd — but content stays owned by the team that produced it.

## Quick start

```bash
./referencectl up slim      # build + boot + seed the demo path
./referencectl verify       # acceptance run: every hop, 11 steps
open http://localhost:8088  # admin SPA (Keycloak login: alice/alice)
./referencectl down -v
```

Needs only Docker (+ Compose v2, ~4 GiB free RAM). The full walkthrough —
tokens, Works, approvals, artifacts, traces:
[Getting started (humans)](docs/getting-started.md) ·
[Getting started (AI agents)](docs/getting-started-agents.md)

## Components

| Piece | What it is | Where |
|---|---|---|
| **Facilitator kernel** | .NET 10 service: work lifecycle, identity/policy gates, Link hub, artifact + memory brokers, admin SPA, operator API + CLI | `src/facilitator/` |
| **Wire contract** | CloudEvents 1.0 envelopes, versioned JSON Schemas, OpenAPI, conformance fixtures | `protocol/` |
| **Gateway** | Team-owned Node runtime multiplexing Workspaces over one Link endpoint | `src/gateway/` (`@ofac/gateway`) |
| **Workspace Link SDK** | Client SDK + `ofac-linkd` supervisor (enroll, connect, replay) | `src/workspace-link/` (`@ofac/workspace-link`) |
| **Harness adapters** | Thin translators from Gateway child wire to one execution harness; `@ofac/adapter` SDK + deterministic / oh-my-pi / Codex reference workers | `src/adapters/harnesses/` |
| **Communication adapters** | Chat/forge legs (Mattermost, Telegram, Forgejo/GitHub/GitLab pairs, Kafka) normalizing into provider domain events | `src/adapters/communication/` |
| **Dev Container Features** | One-Feature onboarding: any team container becomes a linked workspace by adding a single feature | `features/` |
| **Reference environment** | Compose slim/full stacks, `referencectl` lifecycle + acceptance scripts | `reference/` |

## Documentation

| If you want to… | Read |
|---|---|
| Understand the model and vocabulary from zero | [docs/overview.md](docs/overview.md) |
| Run the whole demo stack yourself | [docs/getting-started.md](docs/getting-started.md) |
| Drive the kernel as an agent (API, tokens, links) | [docs/getting-started-agents.md](docs/getting-started-agents.md) |
| Implement a client, adapter, or service on the wire | [docs/protocol.md](docs/protocol.md) |
| Know who can do what, and how it's enforced | [docs/security.md](docs/security.md) |
| Deploy, connect a team, add a harness/provider | [docs/advanced.md](docs/advanced.md) |
| Operate it (when things go wrong) | [docs/runbooks/index.md](docs/runbooks/index.md) |
| Contribute | [docs/contributing.md](docs/contributing.md) |

Browsing remotely? This table renders as a searchable site under
[`site/`](site/README.md) — a GitHub Actions job publishes it to GitHub
Pages from these same files.

## Repository layout

```text
├── src/
│   ├── facilitator/       # .NET 10 kernel: ingress, work lifecycle, Link hub,
│   │                      #   artifact/memory brokers, admin SPA, CLI
│   ├── gateway/           # @ofac/gateway — team Gateway runtime
│   ├── workspace-link/    # @ofac/workspace-link — Link SDK + ofac-linkd
│   └── adapters/          # harness adapters + communication/provisioning adapters
├── protocol/              # JSON Schemas, CloudEvents schemas, OpenAPI, conformance fixtures
├── features/              # Dev Container Features (gateway, workspace-link, adapters)
├── examples/              # slim no-Docker walkthrough, full-profile workers, AWS CDK
├── reference/             # Compose slim/full, referencectl, seeds, acceptance checks
├── docs/                  # user-facing documentation
├── site/                  # open-facilitator.org — static site + docs browser (see site/README.md)
└── tests/                 # .NET integration + scenario + browser suites
```

## Tech stack

| Area | Choice |
|---|---|
| Facilitator | .NET 10, ASP.NET Core, SignalR, EF Core, OTLP |
| Gateway / Link / adapters | TypeScript on Node.js ≥ 22 (npm packages, Dev Container Features, OCI images) |
| Admin UI | Svelte 5 + Tailwind, embedded single-page app |
| Durable state | PostgreSQL 17 (also the default memory search backend via FTS) |
| Identity | OIDC/OAuth 2.0 — Keycloak in the reference, provider-swappable |
| Reference infra | Docker Compose (slim/full), SeaweedFS artifacts, Aspire dashboard, Mattermost, Forgejo, Kafka, Ollama |

## License

Dual-licensed: **AGPL-3.0-or-later** (free, including running a modified
version as a service — that's the point) or a **commercial license** with no
copyleft obligations. See [LICENSE](LICENSE); the license ships with every
npm tarball, Dev Container Feature, and container image.
